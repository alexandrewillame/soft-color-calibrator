using AwesomeAssertions;
using SoftCal.Instruments.Webcam.Controls;
using SoftCal.Windows.Safety;
using Xunit;

namespace SoftCal.Instruments.Webcam.Tests;

public sealed class CameraControlsRestorerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "softcal-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_journaled_snapshot_goes_back_to_the_connected_camera_and_waits_when_it_is_unplugged()
    {
        var journal = new StateJournal(_directory);
        var camera = new WebcamDevice("Logi Webcam C920e", @"\\?\usb#vid_046d&pid_08b6&mi_00#8&12999464&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global");
        CameraControlsRestorer.Journal(journal, camera, new ControlSnapshot(camera.StableId, DateTimeOffset.UtcNow, []));
        var entry = journal.Pending(CameraControlsRestorer.JournalKind).Single();
        ControlSnapshot? restored = null;

        var connected = new CameraControlsRestorer(() => [camera], (_, snapshot) => { restored = snapshot; return []; }).Restore(entry);
        var unplugged = new CameraControlsRestorer(() => [], (_, _) => []).Restore(entry);
        var stubborn = new CameraControlsRestorer(() => [camera], (_, _) => ["WhiteBalance"]).Restore(entry);

        entry.Key.Should().Be("usb-046d-08b6");
        connected.Outcome.Should().Be(RestoreOutcome.Restored);
        restored!.DeviceId.Should().Be(camera.StableId);
        unplugged.Outcome.Should().Be(RestoreOutcome.Deferred);
        stubborn.Should().Match<RestoreResult>(r => r.Outcome == RestoreOutcome.Failed && r.Message.Contains("WhiteBalance"));
    }
}
