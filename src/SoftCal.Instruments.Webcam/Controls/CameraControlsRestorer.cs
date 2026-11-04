using SoftCal.Windows.Safety;

namespace SoftCal.Instruments.Webcam.Controls;

/// <summary>
/// Restores journaled camera controls (journal kind <see cref="JournalKind"/>, key = <see cref="WebcamDevice.StableId"/>).
/// UVC settings persist in the camera, so a crashed run would otherwise leave the user's video calls with manual white
/// balance and exposure.
/// </summary>
public sealed class CameraControlsRestorer(
    Func<IReadOnlyList<WebcamDevice>>? enumerate = null,
    Func<WebcamDevice, ControlSnapshot, IReadOnlyList<string>>? restore = null) : IStateRestorer
{
    public const string JournalKind = "camera";

    private readonly Func<IReadOnlyList<WebcamDevice>> _enumerate = enumerate ?? WebcamDevice.Enumerate;
    private readonly Func<WebcamDevice, ControlSnapshot, IReadOnlyList<string>> _restore = restore ?? WebcamInstrument.RestoreSnapshot;

    public string Kind => JournalKind;

    /// <summary>Journals a camera's snapshot before its controls are changed.</summary>
    public static void Journal(StateJournal journal, WebcamDevice device, ControlSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(device);
        journal.Save(JournalKind, device.StableId, snapshot);
    }

    public RestoreResult Restore(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var snapshot = StateJournal.Read<ControlSnapshot>(entry) ?? throw new InvalidDataException("Empty camera journal entry.");
        var device = _enumerate().FirstOrDefault(d => d.StableId == entry.Key);
        if (device is null)
        {
            return RestoreResult.Deferred("camera not connected; its controls will be restored when it is");
        }

        var failures = _restore(device, snapshot);
        return failures.Count == 0
            ? RestoreResult.Restored("camera controls restored")
            : RestoreResult.Failed("could not restore " + string.Join(", ", failures));
    }
}
