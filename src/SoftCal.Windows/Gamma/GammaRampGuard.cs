using SoftCal.Windows.Safety;

namespace SoftCal.Windows.Gamma;

/// <summary>
/// Changes a display's gamma ramp safely: the original is journaled before the first write and written back (and
/// verified) on <see cref="Restore"/> or <see cref="Dispose"/>. If the process dies in between, the journal entry
/// stays and a guardian or the next launch calls <see cref="RestorePending"/>.
/// </summary>
public sealed class GammaRampGuard : IDisposable
{
    public const string JournalKind = "gamma";

    private readonly IGammaRampDevice _device;
    private readonly StateJournal _journal;
    private bool _changed;

    private GammaRampGuard(IGammaRampDevice device, StateJournal journal, GammaRamp original)
    {
        _device = device;
        _journal = journal;
        Original = original;
    }

    public GammaRamp Original { get; }

    public string DeviceName => _device.DeviceName;

    /// <summary>Reads the current ramp and journals it. Refuses when an older journaled ramp for this display is still
    /// pending, because the current ramp would then not be the user's.</summary>
    public static GammaRampGuard Begin(IGammaRampDevice device, StateJournal journal)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Pending(JournalKind).Any(e => e.Key.Equals(device.DeviceName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A gamma ramp for {device.DeviceName} from an earlier run is still journaled; restore it first (harness 'recover').");
        }

        var original = device.Read();
        journal.Save(JournalKind, device.DeviceName, original);
        return new GammaRampGuard(device, journal, original);
    }

    /// <summary>Writes a ramp; false when the driver rejected it.</summary>
    public bool Apply(GammaRamp ramp)
    {
        _changed = true;
        return _device.Write(ramp);
    }

    /// <summary>Writes the original back and checks it; the journal entry is removed only when the read-back matches.</summary>
    public bool Restore()
    {
        var restored = !_changed || (_device.Write(Original) && _device.Read().SameAs(Original));
        if (!_changed || restored)
        {
            _journal.Remove(JournalKind, _device.DeviceName);
            _changed = false;
        }

        return restored;
    }

    public void Dispose() => Restore();

    /// <summary>Restores every journaled ramp (after a crash). Returns the display names restored and failed.</summary>
    public static (IReadOnlyList<string> Restored, IReadOnlyList<string> Failed) RestorePending(
        StateJournal journal,
        Func<string, IGammaRampDevice> open,
        Func<JournalEntry, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(open);
        var restored = new List<string>();
        var failed = new List<string>();
        foreach (var entry in journal.Pending(JournalKind).Where(e => include?.Invoke(e) ?? true))
        {
            try
            {
                var original = StateJournal.Read<GammaRamp>(entry) ?? throw new InvalidDataException("Empty journal entry.");
                var device = open(entry.Key);
                if (device.Write(original) && device.Read().SameAs(original))
                {
                    journal.Remove(JournalKind, entry.Key);
                    restored.Add(entry.Key);
                }
                else
                {
                    failed.Add(entry.Key);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
            {
                failed.Add(entry.Key);
            }
        }

        return (restored, failed);
    }
}
