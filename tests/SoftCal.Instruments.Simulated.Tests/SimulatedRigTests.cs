using AwesomeAssertions;
using SoftCal.Core.Color;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using Xunit;

namespace SoftCal.Instruments.Simulated.Tests;

public sealed class SimulatedRigTests
{
    [Fact]
    public void A_halftone_emits_the_average_light_of_its_two_colors()
    {
        var display = new SimulatedDisplay();

        var white = display.Emit(DeviceRgb.White);
        var black = display.Emit(DeviceRgb.Black);
        var checker = display.Emit(new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard));
        var quarter = display.Emit(new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 4));

        checker.Y.Should().BeApproximately((white.Y + black.Y) / 2, 1e-12);
        quarter.Y.Should().BeApproximately((0.25 * white.Y) + (0.75 * black.Y), 1e-12);
    }

    [Fact]
    public void Full_range_sent_to_a_limited_range_monitor_crushes_blacks_and_clips_whites()
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions { RangeMismatch = RangeMismatch.FullSentLimitedExpected });

        display.Emit(DeviceRgb.Gray(10)).Should().Be(display.Emit(DeviceRgb.Black));
        display.Emit(DeviceRgb.Gray(240)).Should().Be(display.Emit(DeviceRgb.White));
        display.Emit(DeviceRgb.Gray(128)).Y.Should().BeGreaterThan(display.Emit(DeviceRgb.Gray(100)).Y);
    }

    [Fact]
    public void The_gpu_lut_is_applied_and_quantized_to_the_output_bit_depth()
    {
        var display = new SimulatedDisplay();
        var halfRamp = Enumerable.Range(0, 256).Select(i => (ushort)(i * 257 / 2)).ToArray();

        display.SetGpuLut([halfRamp, halfRamp, halfRamp]);

        // Code 255 now leaves the GPU as 32767/65535 × 255 = 127.498, which an 8-bit link rounds to 127.
        display.Emit(DeviceRgb.White).Y.Should().BeApproximately(display.ChannelResponse(1, 127), 1e-12);
        display.ResetGpuLut();
        display.Emit(DeviceRgb.White).Y.Should().BeApproximately(display.ChannelResponse(1, 255), 1e-12);
    }

    [Fact]
    public void Doubling_the_exposure_doubles_the_linear_signal()
    {
        var camera = new SimulatedCamera();
        var light = new Vec3(0.2, 0.2, 0.2);

        var single = camera.ExpectedCodes(light, 1.0 / 64);
        var dbl = camera.ExpectedCodes(light, 1.0 / 32);

        double Linear(double code) => Core.ToneCurves.SrgbCurve.Instance.ToLinear(code / 255);
        (Linear(dbl.Y) / Linear(single.Y)).Should().BeApproximately(2, 1e-9);
    }

    [Fact]
    public void Exposure_snaps_to_powers_of_two()
    {
        var camera = new SimulatedCamera();

        camera.SnapExposure(0.03).Should().Be(1.0 / 32);
        camera.SnapExposure(10).Should().Be(0.25);
        camera.SnapExposure(1e-6).Should().Be(Math.Pow(2, -11));
    }

    [Fact]
    public async Task Patches_are_seen_only_after_the_pipeline_latency()
    {
        var rig = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera(), new SimulatedRigOptions { LatencySeconds = 0.1 });
        var dark = rig.CaptureFrame().Green.Mean;

        var shown = await rig.ShowAsync(new SolidPatch(DeviceRgb.White), TestContext.Current.CancellationToken);
        var frames = Enumerable.Range(0, 6).Select(_ => rig.CaptureFrame()).ToList();

        foreach (var frame in frames)
        {
            var seen = frame.TimestampSeconds >= shown.PresentTimeSeconds + 0.1 - 1e-9;
            (frame.Green.Mean > dark + 50).Should().Be(seen, $"frame at {frame.TimestampSeconds:F3}s");
        }
    }

    [Fact]
    public async Task A_new_exposure_takes_effect_a_few_frames_after_its_epoch_starts()
    {
        var rig = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera(), new SimulatedRigOptions { ExposureApplyFrames = 2 });
        await rig.ShowAsync(new SolidPatch(DeviceRgb.Gray(128)), TestContext.Current.CancellationToken);
        Skip(rig, 5);
        var before = rig.CaptureFrame();

        var state = await rig.SetExposureAsync(new ExposureRequest(1.0 / 64), TestContext.Current.CancellationToken);
        var frames = Enumerable.Range(0, 4).Select(_ => rig.CaptureFrame()).ToList();

        state.Epoch.Should().Be(before.ExposureEpoch + 1);
        state.NominalSeconds.Should().Be(1.0 / 64);
        frames.Should().OnlyContain(f => f.ExposureEpoch == state.Epoch);
        frames[0].Green.Mean.Should().BeApproximately(before.Green.Mean, 0.2, "the old exposure is still in use");
        frames[3].Green.Mean.Should().BeLessThan(before.Green.Mean - 10, "the shorter exposure has applied");
    }

    [Fact]
    public async Task The_stream_yields_frames_and_faults_are_reported()
    {
        var rig = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera());
        InstrumentFault? received = null;
        rig.Faulted += (_, fault) => received = fault;

        var frames = new List<FrameStats>();
        await foreach (var frame in rig.StreamAsync(new RoiSpec(), TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
            if (frames.Count == 5)
            {
                break;
            }
        }

        rig.InjectFault(new InstrumentFault(InstrumentFaultKind.ControlChangedExternally, "white balance changed"));

        frames.Select(f => f.Sequence).Should().Equal(0, 1, 2, 3, 4);
        received!.Kind.Should().Be(InstrumentFaultKind.ControlChangedExternally);
        (await rig.PrepareAsync(new LockRequest(), TestContext.Current.CancellationToken)).CriticalControlsLocked.Should().BeTrue();
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.2, true)]
    public async Task A_hidden_auto_white_balance_shows_up_as_white_drift_after_a_colored_patch(double drift, bool expectDrift)
    {
        var rig = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera(new SimulatedCameraOptions { AutoWhiteBalanceDrift = drift }));
        var whiteBefore = await ReadAsync(rig, new SolidPatch(DeviceRgb.White));

        await ReadAsync(rig, new SolidPatch(new DeviceRgb(255, 0, 0)));
        var whiteAfter = await ReadAsync(rig, new SolidPatch(DeviceRgb.White));

        var change = Math.Abs(whiteAfter.X - whiteBefore.X) / whiteBefore.X;
        (change > 0.01).Should().Be(expectDrift, $"red channel changed by {change:P2}");
    }

    /// <summary>
    /// The method's core claim, in simulation: matching a solid gray against a 50 % checkerboard finds the code whose
    /// light is halfway between black and white, whatever the camera's tone curve, white balance or color matrix.
    /// </summary>
    [Fact]
    public async Task Halftone_null_recovers_the_display_curve_whatever_the_camera_does()
    {
        var truth = 255 * Math.Pow(0.5, 1 / 2.2);
        SimulatedCameraOptions[] cameras =
        [
            new(),
            new() { Response = CameraResponse.Power(1.8), WhiteBalanceGains = new(1.5, 1.0, 1.9), Seed = 2 },
            new()
            {
                Response = CameraResponse.Filmic(3),
                SpectralMix = new(0.50, 0.20, 0.05, 0.15, 0.75, 0.15, 0.05, 0.20, 0.50),
                ColorCorrection = Matrix3.Identity,
                Sensitivity = 40,
                Seed = 3,
            },
        ];

        foreach (var options in cameras)
        {
            var rig = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera(options));
            var checker = new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard);
            var target = (await ReadAsync(rig, checker)).Y;

            var match = await FindMatchingCodeAsync(rig, target, c => new SolidPatch(DeviceRgb.Gray(c)), channel: 1);

            match.Should().BeApproximately(truth, 0.3);

            // The camera alone would mislead: its reading of the matched gray is nowhere near half of white's.
            var white = (await ReadAsync(rig, new SolidPatch(DeviceRgb.White))).Y;
            (target / white).Should().NotBeInRange(0.45, 0.55);
        }
    }

    [Fact]
    public async Task Halftone_null_measures_each_channel_separately()
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions { Gamma = new(2.4, 2.2, 2.0) });
        var rig = new SimulatedRig(display, new SimulatedCamera(new SimulatedCameraOptions { Response = CameraResponse.Filmic(2.5) }));
        double[] gammas = [2.4, 2.2, 2.0];

        for (var channel = 0; channel < 3; channel++)
        {
            var checker = new HalftonePatch(DeviceRgb.Primary(channel, 255), DeviceRgb.Black, HalftoneGeometry.Checkerboard);
            var target = (await ReadAsync(rig, checker))[channel];

            var match = await FindMatchingCodeAsync(rig, target, c => new SolidPatch(DeviceRgb.Primary(channel, c)), channel);

            match.Should().BeApproximately(255 * Math.Pow(0.5, 1 / gammas[channel]), 0.3, $"channel {channel}");
        }
    }

    private static async Task<Vec3> ReadAsync(SimulatedRig rig, PatchSpec patch, int frames = 20)
    {
        await rig.ShowAsync(patch, TestContext.Current.CancellationToken);
        Skip(rig, (int)Math.Ceiling(rig.Options.LatencySeconds * rig.Options.FrameRate) + 1);
        var sum = Vec3.Zero;
        for (var i = 0; i < frames; i++)
        {
            var f = rig.CaptureFrame();
            sum += new Vec3(f.Red.Mean, f.Green.Mean, f.Blue.Mean);
        }

        return sum / frames;
    }

    /// <summary>Bisection over 8-bit codes, then linear interpolation between the two bracketing codes.</summary>
    private static async Task<double> FindMatchingCodeAsync(SimulatedRig rig, double target, Func<byte, PatchSpec> patchFor, int channel)
    {
        int lo = 0, hi = 255;
        var readings = new Dictionary<int, double>();
        async Task<double> At(int code) =>
            readings.TryGetValue(code, out var v) ? v : readings[code] = (await ReadAsync(rig, patchFor((byte)code)))[channel];

        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (await At(mid) < target)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        var low = await At(lo);
        var high = await At(hi);
        return lo + ((target - low) / (high - low));
    }

    private static void Skip(SimulatedRig rig, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            rig.CaptureFrame();
        }
    }
}
