using SoftCal.Core.Instruments;
using SoftCal.FeasibilityHarness.Infrastructure;
using SoftCal.Instruments.Webcam;
using SoftCal.Instruments.Webcam.Controls;

namespace SoftCal.FeasibilityHarness.Commands;

/// <summary>Shows a camera's controls (read-only) and can put them back to automatic for everyday use; with --watch it
/// then streams for a few seconds, changing nothing, to show its automatic adjustments at work; --wb-test compares the
/// picture's colour at fixed white balances and in automatic (then resets); --lock-test locks it as a measurement does
/// and restores it as at a run's end.</summary>
internal static class CameraCommands
{
    public static int Run(Arguments args)
    {
        var device = WebcamDevice.Enumerate().FirstOrDefault(d => d.IsUsbHardware)
            ?? throw new InvalidOperationException("No USB camera found.");
        if (args.Has("auto"))
        {
            Ui.Title($"Resetting {device.FriendlyName} to automatic / default settings");
            var failed = WebcamInstrument.ResetToAutomatic(device);
            if (failed.Count > 0)
            {
                Ui.Warn($"could not reset: {string.Join(", ", failed)}");
            }
        }

        Ui.Title($"{device.FriendlyName} controls (read-only)");
        var snapshot = WebcamInstrument.ReadControls(device);
        var suspicious = 0;
        foreach (var e in snapshot.Entries)
        {
            if (e.Value is not { } v)
            {
                continue;
            }

            var range = e.Range is { } r ? $"default {r.Default}, range {r.Min}–{r.Max}, auto {(r.SupportsAuto ? "supported" : "no")}" : "no range";
            var flag = e.Range is { SupportsAuto: true } && !v.IsAuto ? "  ← manual (normally automatic)"
                : e.Range is { } rr && !rr.SupportsAuto && v.Value != rr.Default ? "  ← not at default"
                : string.Empty;
            suspicious += flag.Length > 0 ? 1 : 0;
            Ui.Info($"{e.Name,-28} {v.Value,6} {(v.IsAuto ? "auto  " : "manual")}  ({range}){flag}");
        }

        if (args.Has("wb-test"))
        {
            WhiteBalanceTest(device);
        }

        if (args.Has("lock-test"))
        {
            LockTest(device);
        }

        if (args.Has("watch"))
        {
            Watch(device);
        }

        return suspicious == 0 ? 0 : 1;
    }

    /// <summary>The picture's colour at a warm, a cool and the automatic white balance (about 10 s), then every control
    /// back to automatic/defaults: tells a stuck automatic white balance from a scene that really is warm.</summary>
    private static void WhiteBalanceTest(WebcamDevice device)
    {
        Ui.Title($"{device.FriendlyName}: the picture's colour at each white balance (the camera stays where it is)");
        int?[] settings = [2800, 6500, null];
        var results = WebcamInstrument.CompareWhiteBalanceAsync(device, settings, TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
        foreach (var (kelvin, p) in results)
        {
            Ui.Info($"{(kelvin is { } k ? $"{k} K manual" : "automatic"),-14}  mean R G B {p.X,5:0.0} {p.Y,5:0.0} {p.Z,5:0.0}   red {p.X / p.Y:0.00}, blue {p.Z / p.Y:0.00} times green");
        }

        var failed = WebcamInstrument.ResetToAutomatic(device);
        Ui.Info(failed.Count == 0 ? "Put back to automatic/defaults." : $"could not reset: {string.Join(", ", failed)}");
    }

    /// <summary>A measurement's lock and restore, without the measurement: SoftCal's lock (white balance 6500 K, exposure,
    /// gain, focus…) held for 2 s, then put back as at the end of a run. Follow it with --watch to see whether the camera
    /// really went back to adjusting itself.</summary>
    private static void LockTest(WebcamDevice device)
    {
        Ui.Title($"{device.FriendlyName}: locked as for a measurement, then restored as at its end");
        var camera = WebcamInstrument.Open(device, new WebcamOptions { WatchControls = false, WatchSensorActivity = false });
        try
        {
            var report = camera.PrepareAsync(new LockRequest { WhiteBalanceKelvin = 6500, FocusValue = 50 }, CancellationToken.None).GetAwaiter().GetResult();
            Ui.Info($"locked: {string.Join(", ", report.Controls.Where(c => c.Requested is not null).Select(c => $"{c.Control} {c.Requested}"))}");
            Thread.Sleep(2000);
        }
        finally
        {
            camera.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        Ui.Info("restored.");
    }

    /// <summary>Five seconds of stream, nothing changed: white balance, exposure and gain as the camera moves them.</summary>
    private static void Watch(WebcamDevice device)
    {
        Ui.Title($"{device.FriendlyName} streaming for 5 s, nothing changed (its automatic adjustments only run while it streams)");
        var samples = WebcamInstrument.WatchAutomaticAsync(device, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(0.5), CancellationToken.None).GetAwaiter().GetResult();
        static string Show(UvcControlValue? v) => v is { } x ? $"{x.Value,5} {(x.IsAuto ? "auto  " : "manual")}" : "    ? ";
        static string Colour(SoftCal.Core.Color.Vec3? c) => c is { } p ? $"{p.X,5:0.0} {p.Y,5:0.0} {p.Z,5:0.0}" : "    no frame yet";
        Ui.Info($"{"s",5}  {"WhiteBalance",-13}  {"Exposure",-13}  {"Gain",-13}  picture mean R G B");
        foreach (var s in samples)
        {
            Ui.Info($"{s.Seconds,5:0.0}  {Show(s.WhiteBalance),-13}  {Show(s.Exposure),-13}  {Show(s.Gain),-13}  {Colour(s.Picture)}");
        }

        if (samples.LastOrDefault(s => s.Picture is not null)?.Picture is { } last)
        {
            var level = (last.X + last.Y + last.Z) / 3;
            Ui.Info(level < 20
                ? $"Picture: dark (mean {level:0.0} of 255): the camera sees almost nothing (covered, or facing something dark)."
                : $"Picture: mean {level:0.0} of 255; red {last.X / last.Y:0.00} and blue {last.Z / last.Y:0.00} times green (about 1 for a gray scene).");
        }

        void Verdict(string name, Func<AutomaticSample, UvcControlValue?> read)
        {
            var values = samples.Select(read).OfType<UvcControlValue>().ToList();
            if (values.Count == 0)
            {
                Ui.Warn($"{name}: unreadable");
                return;
            }

            var (first, low, high) = (values[0].Value, values.Min(v => v.Value), values.Max(v => v.Value));
            Ui.Info(low == high
                ? $"{name}: stayed at {first}{(values[0].IsAuto ? " (automatic: stuck, or already right for this light)" : string.Empty)}"
                : $"{name}: moved from {first} to {values[^1].Value} (between {low} and {high}): the camera adjusts it");
        }

        Verdict("WhiteBalance", s => s.WhiteBalance);
        Verdict("Exposure", s => s.Exposure);
        Verdict("Gain", s => s.Gain);
    }
}
