using AwesomeAssertions;
using SoftCal.Core.Instruments;
using SoftCal.Instruments.Webcam.Controls;
using Xunit;

namespace SoftCal.Instruments.Webcam.Tests;

/// <summary>
/// Tests against the real camera. They skip unless SOFTCAL_HARDWARE=1; the ones that change camera controls (always
/// restored) also need SOFTCAL_HARDWARE_WRITE=1. Run them with
/// <c>dotnet test -- --filter-trait "Category=Hardware"</c> after setting the variables.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class HardwareTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebcamDevice RequireCamera(bool writes = false)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SOFTCAL_HARDWARE") == "1", "Set SOFTCAL_HARDWARE=1 to run hardware tests.");
        if (writes)
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("SOFTCAL_HARDWARE_WRITE") == "1", "Set SOFTCAL_HARDWARE_WRITE=1 to allow camera control changes.");
        }

        var device = WebcamDevice.Enumerate().FirstOrDefault(d => d.IsUsbHardware);
        Assert.SkipWhen(device is null, "No USB camera is connected.");
        return device!;
    }

    [Fact]
    public async Task The_camera_opens_and_reports_its_controls_and_an_uncompressed_format()
    {
        var device = RequireCamera();

        await using var camera = WebcamInstrument.Open(device);

        camera.Format.Layout.Should().NotBeNull("the source reader must deliver YUY2 or NV12 with converters disabled");
        camera.Snapshot.Find(UvcControlCatalog.WhiteBalance)!.Range.Should().NotBeNull();
        camera.Snapshot.Find(UvcControlCatalog.Exposure)!.Range.Should().NotBeNull();
        camera.Capabilities.ExposureSteps.Should().NotBeEmpty();
        camera.Capabilities.ExposureModel.Should().Be(ExposureModel.DiscreteSteps);
    }

    [Fact]
    public async Task Locking_streams_frames_and_restoring_puts_every_control_back()
    {
        var device = RequireCamera(writes: true);
        var before = await Snapshot(device);

        await using (var camera = WebcamInstrument.Open(device))
        {
            var report = await camera.PrepareAsync(new LockRequest(), Token);
            report.CriticalControlsLocked.Should().BeTrue();
            var frames = new List<FrameStats>();
            await foreach (var frame in camera.StreamAsync(new RoiSpec(), Token))
            {
                frames.Add(frame);
                if (frames.Count == 10)
                {
                    break;
                }
            }

            frames.Select(f => f.Sequence).Should().BeInAscendingOrder();
            frames.Select(f => f.TimestampSeconds).Should().BeInAscendingOrder();
            frames.Should().OnlyContain(f => f.PixelCount > 1000 && f.RowBandMeans!.Count == 16);
            await camera.RestoreAsync(Token);
        }

        // Restored means: every control back in its mode, and manual ones at their value. A control in auto mode
        // reports whatever its automatic algorithm holds at the moment, which a restore cannot (and must not) pin.
        var after = await Snapshot(device);
        foreach (var (b, a) in before.Entries.Zip(after.Entries))
        {
            if (b.Value is not { } original)
            {
                continue;
            }

            a.Value.Should().NotBeNull(b.Name);
            a.Value!.Value.IsAuto.Should().Be(original.IsAuto, b.Name);
            if (!original.IsAuto)
            {
                a.Value.Value.Value.Should().Be(original.Value, b.Name);
            }
        }
    }

    private static async Task<ControlSnapshot> Snapshot(WebcamDevice device)
    {
        await using var camera = WebcamInstrument.Open(device);
        return camera.Snapshot;
    }
}
