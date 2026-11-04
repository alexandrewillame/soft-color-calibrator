using System.Globalization;

namespace SoftCal.Windows.Gamma;

/// <summary>A calibration ramp to keep in one display's GPU.</summary>
/// <param name="Key">Names the calibration in the log (its profile file).</param>
public sealed record KeptRamp(string DeviceName, GammaRamp Ramp, string Key);

public enum KeepOutcome
{
    /// <summary>The first check of this calibration: the ramp is in the GPU now (written, or already there).</summary>
    Loaded,

    /// <summary>Something had changed the ramp; it was written back.</summary>
    Reapplied,

    /// <summary>The ramp could not be read or written (reported once until it works again).</summary>
    Failed,

    /// <summary>Something keeps undoing the ramp: it is left alone for <see cref="RampKeeper.BackOff"/>.</summary>
    BackingOff,
}

public sealed record KeepEvent(DateTimeOffset At, KeptRamp Target, KeepOutcome Outcome, int Difference, string Message);

/// <summary>
/// Keeps calibration ramps in the GPU. Each <see cref="Check"/> reads every target display's ramp and writes the
/// calibration back when it changed: Windows resets ramps at sign-in, at Switch user, on display and profile changes
/// and after sleep. When a ramp
/// keeps being undone, another program owns the curves (f.lux, a GPU tool, a game): the keeper backs off for a while
/// instead of fighting it, which would flicker.
/// </summary>
public sealed class RampKeeper(Func<string, IGammaRampDevice> deviceFor, TimeProvider? clock = null)
{
    /// <summary>Differences up to this (16-bit units) are rounding: some drivers keep only 8 bits.</summary>
    public const int Tolerance = 256;

    private readonly Func<string, IGammaRampDevice> _deviceFor = deviceFor ?? throw new ArgumentNullException(nameof(deviceFor));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _reapplied = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _backOffUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>More write-backs than this within <see cref="FightWindow"/> means something else is changing the ramp.</summary>
    public int MaxReapplies { get; init; } = 3;

    public TimeSpan FightWindow { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan BackOff { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Largest difference between two ramps, in 16-bit units.</summary>
    public static int Difference(GammaRamp a, GammaRamp b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var max = 0;
        for (var i = 0; i < GammaRamp.Size; i++)
        {
            max = Math.Max(max, Math.Max(Math.Abs(a.Red[i] - b.Red[i]), Math.Max(Math.Abs(a.Green[i] - b.Green[i]), Math.Abs(a.Blue[i] - b.Blue[i]))));
        }

        return max;
    }

    /// <summary>Checks every target once; returns only what changed (nothing while all is well).</summary>
    public IReadOnlyList<KeepEvent> Check(IReadOnlyList<KeptRamp> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var now = _clock.GetUtcNow();
        var events = new List<KeepEvent>();
        foreach (var target in targets)
        {
            var id = target.DeviceName + "|" + target.Key;
            if (_backOffUntil.TryGetValue(id, out var until) && now < until)
            {
                continue;
            }

            var device = _deviceFor(target.DeviceName);
            int difference;
            try
            {
                difference = Difference(device.Read(), target.Ramp);
            }
            catch (InvalidOperationException ex)
            {
                Fail(events, now, id, target, -1, $"{target.DeviceName}: its curves could not be read ({ex.Message}).");
                continue;
            }

            // Counted as loaded only once it is: a calibration that failed at first and then works is "loaded".
            var first = !_seen.Contains(id);
            if (difference <= Tolerance)
            {
                _failing.Remove(id);
                _seen.Add(id);
                if (first)
                {
                    events.Add(new(now, target, KeepOutcome.Loaded, difference, $"{target.DeviceName}: the calibration curves are loaded ({target.Key})."));
                }

                continue;
            }

            if (!Write(device, target.Ramp))
            {
                Fail(events, now, id, target, difference, $"{target.DeviceName}: the graphics driver refused the calibration curves ({target.Key}).");
                continue;
            }

            _failing.Remove(id);
            _seen.Add(id);
            if (first)
            {
                events.Add(new(now, target, KeepOutcome.Loaded, difference, $"{target.DeviceName}: loaded the calibration curves ({target.Key})."));
                continue;
            }

            if (!_reapplied.TryGetValue(id, out var times))
            {
                times = new Queue<DateTimeOffset>();
                _reapplied[id] = times;
            }

            times.Enqueue(now);
            while (now - times.Peek() > FightWindow)
            {
                times.Dequeue();
            }

            if (times.Count > MaxReapplies)
            {
                times.Clear();
                _backOffUntil[id] = now + BackOff;
                events.Add(new(now, target, KeepOutcome.BackingOff, difference, string.Create(
                    CultureInfo.InvariantCulture,
                    $"{target.DeviceName}: the curves were changed {MaxReapplies + 1} times within {FightWindow.TotalMinutes:0.#} min; another program seems to be adjusting them, so SoftCal leaves them alone for {BackOff.TotalMinutes:0} min.")));
            }
            else
            {
                events.Add(new(now, target, KeepOutcome.Reapplied, difference, string.Create(
                    CultureInfo.InvariantCulture,
                    $"{target.DeviceName}: the curves had been changed (largest difference {difference} of 65535); put the calibration back.")));
            }
        }

        return events;
    }

    /// <summary>Writes, then reads back: SetDeviceGammaRamp can report success and keep the old ramp.</summary>
    private static bool Write(IGammaRampDevice device, GammaRamp ramp)
    {
        try
        {
            return device.Write(ramp) && Difference(device.Read(), ramp) <= Tolerance;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void Fail(List<KeepEvent> events, DateTimeOffset now, string id, KeptRamp target, int difference, string message)
    {
        if (_failing.Add(id))
        {
            events.Add(new(now, target, KeepOutcome.Failed, difference, message));
        }
    }
}
