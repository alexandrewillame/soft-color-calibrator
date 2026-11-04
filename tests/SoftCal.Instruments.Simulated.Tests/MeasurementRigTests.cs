using AwesomeAssertions;
using SoftCal.Core.Analysis;
using SoftCal.Core.Color;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using Xunit;

namespace SoftCal.Instruments.Simulated.Tests;

/// <summary>The instrument-agnostic measurement core, run end to end against the simulated rig.</summary>
public sealed class MeasurementRigTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static (SimulatedRig Rig, MeasurementRig Measure) Create(
        SimulatedDisplayOptions? display = null,
        SimulatedCameraOptions? camera = null,
        SimulatedRigOptions? rig = null)
    {
        var sim = new SimulatedRig(new SimulatedDisplay(display), new SimulatedCamera(camera), rig);
        return (sim, new MeasurementRig(sim, sim, new RoiSpec()));
    }

    [Fact]
    public async Task A_reading_waits_for_the_patch_to_settle_and_averages_accepted_frames()
    {
        var (sim, measure) = Create(rig: new SimulatedRigOptions { LatencySeconds = 0.1 });
        await using var _ = measure;
        measure.LatencySeconds = 0.1;
        await measure.SetExposureAsync(1.0 / 64, Token);
        await measure.ReadAsync(new SolidPatch(DeviceRgb.Black), null, Token);

        var reading = await measure.ReadAsync(new SolidPatch(DeviceRgb.Gray(200)), ReadPolicy.Default with { Frames = 10 }, Token);

        reading.Frames.Should().Be(10);
        reading.FirstFrameSeconds.Should().BeGreaterThanOrEqualTo(reading.DisplayTimeSeconds + 0.1 + (1.0 / 64));
        var expected = sim.Camera.ExpectedCodes(sim.Display.Emit(DeviceRgb.Gray(200)), sim.Camera.ActualExposure(1.0 / 64));
        reading.Mean.Y.Should().BeApproximately(expected.Y, 0.05, "every averaged frame shows the new patch");
        reading.StdError.Y.Should().BeLessThan(0.05);
    }

    [Fact]
    public async Task Frames_from_before_an_exposure_change_are_skipped()
    {
        var (_, measure) = Create(rig: new SimulatedRigOptions { ExposureApplyFrames = 2 });
        await using var _ = measure;
        await measure.SetExposureAsync(1.0 / 32, Token);
        var before = await measure.ReadAsync(new SolidPatch(DeviceRgb.Gray(128)), null, Token);

        await measure.SetExposureAsync(1.0 / 128, Token);
        var after = await measure.ReadShownAsync(measure.LastShown!, ReadPolicy.Default with { Frames = 5, SkipFramesAfterExposureChange = 3 }, Token);

        after.ExposureEpoch.Should().Be(measure.Exposure!.Epoch);
        after.FrameStdDev.Y.Should().BeLessThan(1, "no frame of the old exposure is mixed in");
        after.Mean.Y.Should().BeLessThan(before.Mean.Y - 20);
    }

    [Fact]
    public async Task Auto_exposure_lands_near_the_target_without_clipping()
    {
        var (_, measure) = Create();
        await using var _ = measure;

        var state = await measure.AutoExposeAsync(new SolidPatch(DeviceRgb.White), 150, Token);
        var reading = await measure.ReadAsync(new SolidPatch(DeviceRgb.White), null, Token);

        state.NominalSeconds.Should().BeOneOf(measure.ExposureSteps);
        reading.MaxClippedFraction.Should().BeLessThan(0.0005);
        Math.Max(reading.Mean.X, Math.Max(reading.Mean.Y, reading.Mean.Z)).Should().BeInRange(75, 210);
    }

    [Fact]
    public async Task Auto_exposure_on_a_linear_camera_lands_near_the_target_without_clipping()
    {
        // As a phone measuring from its sensor's raw data: one exposure step doubles the reading.
        var (sim, measure) = Create(camera: new SimulatedCameraOptions { Response = CameraResponse.Linear });
        await using var _ = measure;

        var state = await measure.AutoExposeAsync(new SolidPatch(DeviceRgb.White), 150, Token);
        var reading = await measure.ReadAsync(new SolidPatch(DeviceRgb.White), null, Token);

        sim.Capabilities.OutputIsLinear.Should().BeTrue();
        state.NominalSeconds.Should().BeOneOf(measure.ExposureSteps);
        reading.MaxClippedFraction.Should().BeLessThan(0.0005);
        Math.Max(reading.Mean.X, Math.Max(reading.Mean.Y, reading.Mean.Z)).Should().BeInRange(75, 215);
    }

    [Fact]
    public async Task The_null_matches_a_checkerboard_whatever_the_camera_curve()
    {
        var truth = 255 * Math.Pow(0.5, 1 / 2.2);
        foreach (var response in new[] { CameraResponse.Srgb, CameraResponse.Filmic(3), CameraResponse.Power(1.6), CameraResponse.Linear })
        {
            var (_, measure) = Create(camera: new SimulatedCameraOptions { Response = response, Seed = 11 });
            await using var _ = measure;
            var matcher = new NullMatcher(measure);

            var result = await matcher.MatchAsync(
                "checker",
                new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard),
                c => new SolidPatch(DeviceRgb.Gray((byte)c)),
                180,
                new NullOptions { Repeats = 3 },
                Token);

            result.MatchedCode.Should().BeApproximately(truth, 0.3);
            result.MatchedCodeStdDev.Should().BeLessThan(0.3, "the gate's matched-reading σ");
            result.Repeats.Should().HaveCount(3);
            result.LowerCode.Should().Be(186);
        }
    }

    [Fact]
    public async Task The_null_is_unaffected_by_linear_drift_between_readings()
    {
        // A reading order of reference, low, high, high, low, reference cancels any linear drift.
        var (sim, measure) = Create();
        await using var _ = measure;
        var drifting = new DriftingInstrument(sim, perFrame: 0.0005);
        await using var driftMeasure = new MeasurementRig(drifting, sim, new RoiSpec());
        var matcher = new NullMatcher(driftMeasure);

        var result = await matcher.MatchAsync(
            "checker",
            new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard),
            c => new SolidPatch(DeviceRgb.Gray((byte)c)),
            186,
            new NullOptions { Repeats = 2 },
            Token);

        result.MatchedCode.Should().BeApproximately(255 * Math.Pow(0.5, 1 / 2.2), 0.3);
    }

    [Fact]
    public async Task The_midpoint_tree_reaches_about_code_21_and_follows_the_display_curve()
    {
        var (_, measure) = Create(display: new SimulatedDisplayOptions { Black = Vec3.Zero });
        await using var _ = measure;
        var matcher = new NullMatcher(measure);

        var levels = await MidpointTree.RunAsync(matcher, 8, new NullOptions { Repeats = 1 }, DeviceRgb.Gray, null, Token);

        levels.Should().HaveCount(8);
        foreach (var level in levels)
        {
            var truth = 255 * Math.Pow(level.Fraction, 1 / 2.2);
            level.Null.MatchedCode.Should().BeApproximately(truth, 0.5, $"level 1/{1 << level.Level}");
        }

        levels[^1].Null.MatchedCode.Should().BeInRange(19, 23);
        levels[3].Null.MatchedCode.Should().BeInRange(70, 75);
        levels[5].Null.MatchedCode.Should().BeInRange(36, 41);
    }

    [Fact]
    public async Task A_steep_display_needs_more_halvings_and_the_tree_stops_near_code_21()
    {
        // γ 3 puts 1/256 at code 40: the tree keeps halving until the matched code reaches ~22.
        var (_, measure) = Create(display: new SimulatedDisplayOptions { Gamma = new(3, 3, 3), Black = Vec3.Zero }, camera: new SimulatedCameraOptions { Sensitivity = 400 });
        await using var _ = measure;
        var matcher = new NullMatcher(measure);

        var levels = await MidpointTree.RunAsync(matcher, 14, new NullOptions { Repeats = 1 }, DeviceRgb.Gray, null, Token, stopBelowCode: 22, geometry: HalftoneGeometry.HorizontalLines);

        levels.Count.Should().BeGreaterThan(8);
        levels[^1].Null.MatchedCode.Should().BeLessThanOrEqualTo(22);
        levels.Should().OnlyContain(l => ((HalftonePatch)l.Null.Reference).Geometry == HalftoneGeometry.HorizontalLines);
        var nearest = MidpointTree.LevelsNearest([levels], [21, 38, 72]);
        foreach (var (target, level) in nearest)
        {
            Math.Abs(levels.Single(l => l.Level == level).Null.MatchedCode - target).Should().BeLessThan(target * 0.25, $"code ~{target}");
        }
    }

    [Fact]
    public async Task Pwm_flicker_shows_up_in_the_simulated_row_bands()
    {
        var (_, measure) = Create(rig: new SimulatedRigOptions { PwmFrequencyHz = 300, PwmDepth = 0.3, RowLineTimeSeconds = 50e-6 });
        await using var _ = measure;
        await using var rows = new MeasurementRig(measure.Instrument, measure.Presenter, new RoiSpec { RowBands = 432 });
        await rows.SetExposureAsync(rows.ExposureSteps[0], Token);
        await rows.ReadAsync(new SolidPatch(DeviceRgb.White), ReadPolicy.Default with { Frames = 2, MaxClippedFraction = 1 }, Token);

        var frames = await rows.CollectAsync(1, Token);
        var result = FlickerAnalysis.Analyze([.. frames.Select(f => f.RowBandMeans!)], 50e-6);

        result.Detected.Should().BeTrue();
        result.FrequencyHz.Should().BeApproximately(300, 10);
    }

    [Fact]
    public async Task A_reading_the_presenter_saw_disturbed_is_taken_again_once_the_patch_is_clear()
    {
        var sim = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera());
        var watch = new WatchedPresenter(sim, disturbedReadings: 1);
        await using var measure = new MeasurementRig(sim, watch, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };
        var disturbed = new List<string>();
        measure.PatchDisturbed += (_, what) => disturbed.Add(what);
        await measure.SetExposureAsync(1.0 / 64, Token);

        var reading = await measure.ReadAsync(new SolidPatch(DeviceRgb.Gray(200)), ReadPolicy.Default with { Frames = 5 }, Token);

        disturbed.Should().Equal("a window over the patch");
        watch.Asked.Should().Be(2, "the reading taken again was checked too");
        reading.FirstFrameSeconds.Should().BeGreaterThanOrEqualTo(watch.ClearFrom + measure.LatencySeconds + (1.0 / 64), "only frames that saw the patch clear count");
    }

    [Fact]
    public async Task A_patch_that_stays_covered_stops_the_measurement()
    {
        var sim = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera());
        await using var measure = new MeasurementRig(sim, new WatchedPresenter(sim, disturbedReadings: int.MaxValue), new RoiSpec());
        await measure.SetExposureAsync(1.0 / 64, Token);

        var read = () => measure.ReadAsync(new SolidPatch(DeviceRgb.White), ReadPolicy.Default with { Frames = 3 }, Token);

        (await read.Should().ThrowAsync<MeasurementException>()).WithMessage("*disturbed 6 times*");
    }

    [Fact]
    public async Task A_patch_never_clear_again_stops_the_measurement()
    {
        var sim = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera());
        await using var measure = new MeasurementRig(sim, new WatchedPresenter(sim, disturbedReadings: 1, clearsAgain: false), new RoiSpec());
        await measure.SetExposureAsync(1.0 / 64, Token);

        var read = () => measure.ReadAsync(new SolidPatch(DeviceRgb.White), ReadPolicy.Default with { Frames = 3 }, Token);

        (await read.Should().ThrowAsync<MeasurementException>()).WithMessage("*kept covering*");
    }

    /// <summary>Shows patches on the simulator and says the first readings were disturbed; the patch is clear again a
    /// little after each such reading (or never).</summary>
    private sealed class WatchedPresenter(SimulatedRig sim, int disturbedReadings, bool clearsAgain = true) : IPatchPresenter, IPatchWatch
    {
        public int Asked { get; private set; }

        public double ClearFrom { get; private set; } = double.NegativeInfinity;

        public Task<PresentedPatch> ShowAsync(PatchSpec patch, CancellationToken cancellationToken) => sim.ShowAsync(patch, cancellationToken);

        public Task<string?> DisturbanceAsync(double fromSeconds, double untilSeconds, CancellationToken cancellationToken)
        {
            if (++Asked > disturbedReadings)
            {
                return Task.FromResult<string?>(fromSeconds < ClearFrom ? "a reading from before the patch was clear" : null);
            }

            ClearFrom = untilSeconds + 0.2;
            return Task.FromResult<string?>("a window over the patch");
        }

        public Task<double?> WaitUntilClearAsync(double clearSeconds, double timeoutSeconds, CancellationToken cancellationToken) =>
            Task.FromResult<double?>(clearsAgain ? ClearFrom : null);
    }

    /// <summary>Wraps an instrument and lets every frame read brighter than the last, like a warming display.</summary>
    private sealed class DriftingInstrument(SimulatedRig inner, double perFrame) : IMeasurementInstrument
    {
        public event EventHandler<InstrumentFault>? Faulted
        {
            add => inner.Faulted += value;
            remove => inner.Faulted -= value;
        }

        public InstrumentInfo Info => inner.Info;

        public InstrumentCapabilities Capabilities => inner.Capabilities;

        public Task<LockReport> PrepareAsync(LockRequest request, CancellationToken cancellationToken) => inner.PrepareAsync(request, cancellationToken);

        public Task<ExposureState> SetExposureAsync(ExposureRequest request, CancellationToken cancellationToken) => inner.SetExposureAsync(request, cancellationToken);

        public async IAsyncEnumerable<FrameStats> StreamAsync(RoiSpec roi, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var f in inner.StreamAsync(roi, cancellationToken))
            {
                var gain = 1 + (perFrame * f.Sequence);
                ChannelStats Scale(ChannelStats c) => c with { Mean = c.Mean * gain };
                yield return f with { Red = Scale(f.Red), Green = Scale(f.Green), Blue = Scale(f.Blue) };
            }
        }

        public Task RestoreAsync(CancellationToken cancellationToken) => inner.RestoreAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
