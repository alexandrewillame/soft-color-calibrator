using System.Runtime.CompilerServices;
using AwesomeAssertions;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using Xunit;

namespace SoftCal.Instruments.Simulated.Tests;

/// <summary>Whether the camera sees the screen blurred, as the halftone readings need, judged from the picture.</summary>
public sealed class DefocusCheckTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_camera_that_mixes_the_lines_passes()
    {
        var sim = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera());
        await using var rig = new MeasurementRig(sim, sim, new RoiSpec());

        var result = await DefocusCheck.RunAsync(rig, Token);

        result.Blurred.Should().BeTrue();
        result.Ripple.Should().BeLessThan(0.02);
        result.LinesMean.Should().BeApproximately(result.SolidMean, result.SolidMean * 0.15, "the lines emit about the solid gray's light");
    }

    [Fact]
    public async Task A_linear_camera_that_mixes_the_lines_passes_too()
    {
        // As a phone measuring from its raw data.
        var sim = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera(new SimulatedCameraOptions { Response = CameraResponse.Linear }));
        await using var rig = new MeasurementRig(sim, sim, new RoiSpec());

        var result = await DefocusCheck.RunAsync(rig, Token);

        result.Linear.Should().BeTrue();
        result.Blurred.Should().BeTrue();
        result.LinesMean.Should().BeApproximately(result.SolidMean, result.SolidMean * 0.15, "the lines emit about the solid gray's light");
    }

    [Fact]
    public void The_same_blur_gives_the_same_ripple_on_a_linear_camera()
    {
        // A light ripple of 22 % shows as about 10 % through a γ 1/2.2 curve.
        var encoded = new DefocusResult(140, 1, 140, Math.Sqrt(1 + Math.Pow(140 * 0.10, 2)), 0);
        var linear = new DefocusResult(100, 1, 100, Math.Sqrt(1 + Math.Pow(100 * 0.22, 2)), 0, Linear: true);

        encoded.Ripple.Should().BeApproximately(0.10, 1e-9);
        linear.MeasuredRipple.Should().BeApproximately(0.22, 1e-9);
        linear.Ripple.Should().BeApproximately(0.10, 1e-9);
        linear.Blurred.Should().Be(encoded.Blurred);
    }

    [Fact]
    public async Task A_camera_that_sees_the_lines_is_caught()
    {
        // As a phone whose lens stayed on the screen: the lines' pixels spread far beyond the noise, the dark ones clip.
        var sharp = new SharpCamera(new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera()));
        await using var rig = new MeasurementRig(sharp, sharp, new RoiSpec());

        var result = await DefocusCheck.RunAsync(rig, Token);

        result.Blurred.Should().BeFalse();
        result.Ripple.Should().BeGreaterThan(DefocusResult.MaxRipple);
    }

    [Fact]
    public async Task The_search_keeps_the_focus_that_blurs_the_lines()
    {
        // Sharp at the far settings (a lens that does not go to the farthest), blurred at 2 D.
        var camera = new FocusableCamera(new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera()), blurredAt: 2);
        await using var rig = new MeasurementRig(camera, camera, new RoiSpec());

        var result = await DefocusSearch.RunAsync(rig, camera, Token);

        result.Best.Should().Be(2);
        result.BestCheck.Blurred.Should().BeTrue();
        result.Trials.Select(t => t.Focus).Should().Equal(0.05, 0.5, 1, 2);
        result.Trials[0].Ripple.Should().BeGreaterThan(DefocusResult.MaxRipple, "the far setting leaves the lines sharp");
        result.Trials.Take(3).Should().OnlyContain(t => t.Ripple > result.BestCheck.Ripple, "a setting that merely passes does not stop the search");
        camera.Focus.Should().Be(2);
    }

    [Fact]
    public async Task When_no_focus_blurs_the_least_bad_is_kept_and_said_to_fail()
    {
        var camera = new FocusableCamera(new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera()), blurredAt: 40);
        await using var rig = new MeasurementRig(camera, camera, new RoiSpec());

        var result = await DefocusSearch.RunAsync(rig, camera, Token);

        result.Trials.Should().HaveCount(camera.FocusCandidates.Count);
        result.Best.Should().Be(10, "the setting nearest the blur, of those the camera offers");
        result.BestCheck.Blurred.Should().BeFalse();
        camera.Focus.Should().Be(10);
    }

    /// <summary>The simulated rig with a focus: halftones reach the camera with a ripple that grows with the distance (in
    /// diopters) from <paramref name="blurredAt"/>, clipping once it is large.</summary>
    private sealed class FocusableCamera(SimulatedRig rig, double blurredAt) : IMeasurementInstrument, IPatchPresenter, IFocusControl
    {
        private volatile PatchSpec? _shown;

        public event EventHandler<InstrumentFault>? Faulted
        {
            add => rig.Faulted += value;
            remove => rig.Faulted -= value;
        }

        public InstrumentInfo Info => rig.Info;

        public InstrumentCapabilities Capabilities => rig.Capabilities;

        public IReadOnlyList<double> FocusCandidates { get; } = [0.05, 0.5, 1, 2, 5, 10];

        public double Focus { get; private set; }

        public string DescribeFocus(double value) => $"{value} D";

        public Task<double> SetFocusAsync(double value, CancellationToken cancellationToken)
        {
            Focus = value;
            return Task.FromResult(rig.Now);
        }

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
                if (_shown is not HalftonePatch)
                {
                    yield return frame;
                    continue;
                }

                var ripple = 0.01 + (0.08 * Math.Abs(Focus - blurredAt));
                ChannelStats Rippled(ChannelStats c) => c with
                {
                    StdDev = Math.Sqrt((c.StdDev * c.StdDev) + Math.Pow(c.Mean * ripple, 2)),
                    ClippedFraction = ripple > 0.4 ? 0.05 : c.ClippedFraction,
                };
                yield return frame with { Red = Rippled(frame.Red), Green = Rippled(frame.Green), Blue = Rippled(frame.Blue) };
            }
        }

        public Task RestoreAsync(CancellationToken cancellationToken) => rig.RestoreAsync(cancellationToken);

        public ValueTask DisposeAsync() => rig.DisposeAsync();
    }

    /// <summary>The simulated rig, except that a halftone reaches the camera as lines: its pixels spread as widely as
    /// their mean, and a fifth of them clip.</summary>
    private sealed class SharpCamera(SimulatedRig rig) : IMeasurementInstrument, IPatchPresenter
    {
        private volatile PatchSpec? _shown;

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
                yield return _shown is HalftonePatch
                    ? frame with { Red = Sharp(frame.Red), Green = Sharp(frame.Green), Blue = Sharp(frame.Blue) }
                    : frame;
            }
        }

        public Task RestoreAsync(CancellationToken cancellationToken) => rig.RestoreAsync(cancellationToken);

        public ValueTask DisposeAsync() => rig.DisposeAsync();

        private static ChannelStats Sharp(ChannelStats channel) => channel with { StdDev = channel.Mean, ClippedFraction = 0.2 };
    }
}
