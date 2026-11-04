using System.Runtime.CompilerServices;
using AwesomeAssertions;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using Xunit;

namespace SoftCal.Instruments.Simulated.Tests;

/// <summary>Whether the monitor's light follows the picture, told apart from a camera that adapts to what it sees.</summary>
public sealed class ContentCheckTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.3, true)]
    public async Task A_backlight_that_follows_the_picture_is_caught(double dynamicContrast, bool expected)
    {
        var sim = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera(), new SimulatedRigOptions { DynamicContrast = dynamicContrast });
        await using var rig = new MeasurementRig(sim, sim, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };

        var content = await ContentCheck.RunAsync(rig, new QuickPlanOptions().Readings, Token);

        content.ChangesWithContent.Should().Be(expected, $"first difference {content.FirstDifference:P2}, drift {content.Drift:P2}");
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.3, true)]
    public async Task A_linear_camera_judges_the_same_light(double dynamicContrast, bool expected)
    {
        // As a phone measuring from its raw data: a change of the light shows 2.2 times larger than through a γ curve.
        var sim = new SimulatedRig(
            new SimulatedDisplay(),
            new SimulatedCamera(new SimulatedCameraOptions { Response = CameraResponse.Linear }),
            new SimulatedRigOptions { DynamicContrast = dynamicContrast });
        await using var rig = new MeasurementRig(sim, sim, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };

        var content = await ContentCheck.RunAsync(rig, new QuickPlanOptions().Readings, Token);

        content.Linear.Should().BeTrue();
        content.ChangesWithContent.Should().Be(expected, $"first difference {content.FirstDifference:P2}, drift {content.Drift:P2}");
    }

    [Fact]
    public void A_change_of_the_light_counts_the_same_whatever_the_cameras_curve()
    {
        // 3 % more light: about 1.4 % on an encoded reading, 3 % on a linear one; neither reaches the limit.
        var encoded = new ContentResponse([101.4, 101.4], [100, 100], 0.01);
        var linear = new ContentResponse([103, 103], [100, 100], 0.01, Linear: true);

        encoded.FirstDifference.Should().BeApproximately(0.03, 0.002);
        linear.FirstDifference.Should().BeApproximately(0.03, 0.002);
        encoded.ChangesWithContent.Should().BeFalse();
        linear.ChangesWithContent.Should().BeFalse();
    }

    [Fact]
    public async Task A_camera_that_adapts_to_what_it_sees_is_not_taken_for_the_monitor()
    {
        // As a phone whose tone curve follows the scene: after seeing white it reads darker for a while.
        var camera = new AdaptiveCamera(new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera()));
        await using var rig = new MeasurementRig(camera, camera, new RoiSpec());

        var content = await ContentCheck.RunAsync(rig, new QuickPlanOptions().Readings, Token);

        content.ChangesWithContent.Should().BeFalse($"the camera only ever sees the gray (first difference {content.FirstDifference:P2}, drift {content.Drift:P2})");
    }

    [Fact]
    public async Task Such_a_camera_does_drift_after_seeing_white()
    {
        // The reason for the check's design: had the camera seen a white screen, its next readings would drift.
        var camera = new AdaptiveCamera(new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera()));
        await using var rig = new MeasurementRig(camera, camera, new RoiSpec());
        var gray = new SolidPatch(DeviceRgb.Gray(160));
        await rig.AutoExposeAsync(gray, 140, Token);
        await rig.ReadAsync(new SolidPatch(DeviceRgb.White) { Layout = new PatchLayout(0.5, 0.5, 10, DeviceRgb.White) }, ReadPolicy.Default with { MaxClippedFraction = 1 }, Token);

        var first = (await rig.ReadAsync(gray, ReadPolicy.Default with { Frames = 4 }, Token)).Channel(3);
        var later = (await rig.ReadAsync(gray, ReadPolicy.Default with { Frames = 4, SettleMarginSeconds = 1 }, Token)).Channel(3);

        (later / first).Should().BeGreaterThan(1.02);
    }

    /// <summary>The simulated rig seen through a camera whose gain follows what its region sees, with a lag of about
    /// half a second: darker after something bright, brighter after something dark.</summary>
    private sealed class AdaptiveCamera(SimulatedRig rig) : IMeasurementInstrument, IPatchPresenter
    {
        private volatile PatchSpec? _shown;
        private double _seen = 0.6;

        public event EventHandler<InstrumentFault>? Faulted
        {
            add => rig.Faulted += value;
            remove => rig.Faulted -= value;
        }

        public InstrumentInfo Info => rig.Info;

        public InstrumentCapabilities Capabilities => rig.Capabilities;

        public Task<PresentedPatch> ShowAsync(PatchSpec patch, CancellationToken cancellationToken)
        {
            _shown = patch;
            return rig.ShowAsync(patch, cancellationToken);
        }

        public Task<LockReport> PrepareAsync(LockRequest request, CancellationToken cancellationToken) => rig.PrepareAsync(request, cancellationToken);

        public Task<ExposureState> SetExposureAsync(ExposureRequest request, CancellationToken cancellationToken) => rig.SetExposureAsync(request, cancellationToken);

        public async IAsyncEnumerable<FrameStats> StreamAsync(RoiSpec roi, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in rig.StreamAsync(roi, cancellationToken).ConfigureAwait(false))
            {
                // What the camera's region sees is the patch's own color, whatever surrounds it.
                var level = _shown is SolidPatch solid ? (solid.Color.R + solid.Color.G + solid.Color.B) / (3 * 255.0) : 0.5;
                _seen += (level - _seen) * 0.06;
                var gain = 1 + (0.5 * (0.6 - _seen));
                ChannelStats Adapted(ChannelStats c) => c with { Mean = c.Mean * gain, StdDev = c.StdDev * gain };
                yield return frame with { Red = Adapted(frame.Red), Green = Adapted(frame.Green), Blue = Adapted(frame.Blue) };
            }
        }

        public Task RestoreAsync(CancellationToken cancellationToken) => rig.RestoreAsync(cancellationToken);

        public ValueTask DisposeAsync() => rig.DisposeAsync();
    }
}
