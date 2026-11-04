using AwesomeAssertions;
using SoftCal.Core.Analysis;
using SoftCal.Core.Color;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using Xunit;

namespace SoftCal.Instruments.Simulated.Tests;

/// <summary>
/// M3's end-to-end checks on the simulator: the Quick plan plus the analysis must recover a display whose truth is
/// known, whatever the camera does, and give the same answers through the loopback "remote" instrument.
/// </summary>
public sealed class QuickPlanTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A camera whose channels see exactly the display's: no spectral overlap, white balance or color matrix.</summary>
    private static readonly SimulatedCameraOptions IdealCamera = new()
    {
        SpectralMix = Matrix3.Identity,
        WhiteBalanceGains = Vec3.One,
        ColorCorrection = Matrix3.Identity,
    };

    private static async Task<(DisplayMeasurement Measurement, string Model)> MeasureRawAsync(
        SimulatedDisplay display,
        SimulatedCameraOptions? camera = null,
        LoopbackOptions? loopback = null)
    {
        var sim = new SimulatedRig(display, new SimulatedCamera(camera ?? new SimulatedCameraOptions()));
        IMeasurementInstrument instrument = loopback is null ? sim : new LoopbackRemoteInstrument(sim, loopback);
        await using var rig = new MeasurementRig(instrument, sim, new RoiSpec())
        {
            // What the optical sync would measure: pipeline latency plus, for the loopback, the clock offset.
            LatencySeconds = sim.Options.LatencySeconds + (loopback?.ClockOffsetSeconds ?? 0),
        };
        await instrument.PrepareAsync(new LockRequest(), Token);
        return (await new QuickPlan(rig).RunAsync(null, Token), instrument.Info.Model);
    }

    private static async Task<DisplayReport> MeasureAsync(
        SimulatedDisplayOptions display,
        SimulatedCameraOptions? camera = null,
        LoopbackOptions? loopback = null)
    {
        var (measurement, model) = await MeasureRawAsync(new SimulatedDisplay(display), camera, loopback);
        return DisplayAnalysis.Analyze(measurement, new ReportContext("simulated", null, model));
    }

    private static double TruthHalfCode(double gamma) => 255 * Math.Pow(0.5, 1 / gamma);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Ground truth: each channel's share of its own black-to-white light at a gray code, over their average.</summary>
    private static Vec3 TruthBalance(SimulatedDisplay display, double code)
    {
        Vec3 At(double c)
        {
            var lower = (byte)Math.Floor(c);
            var upper = (byte)Math.Min(255, lower + 1);
            var t = c - lower;
            return (display.Emit(DeviceRgb.Gray(lower)) * (1 - t)) + (display.Emit(DeviceRgb.Gray(upper)) * t);
        }

        var black = display.Emit(DeviceRgb.Black);
        var fraction = (At(code) - black).DivideBy(display.Emit(DeviceRgb.White) - black);
        return fraction / (fraction.Sum / 3);
    }

    [Fact]
    public async Task The_quick_plan_recovers_channel_gammas_tone_and_a_bluish_mid_gray()
    {
        var report = await MeasureAsync(new SimulatedDisplayOptions { Gamma = new Vec3(2.35, 2.2, 2.05) });

        report.Channels.HalfLightCodes.X.Should().BeApproximately(TruthHalfCode(2.35), 0.5);
        report.Channels.HalfLightCodes.Y.Should().BeApproximately(TruthHalfCode(2.2), 0.5);
        report.Channels.HalfLightCodes.Z.Should().BeApproximately(TruthHalfCode(2.05), 0.5);
        report.Channels.ChannelGammas.X.Should().BeApproximately(2.35, 0.02);
        report.Channels.ChannelGammas.Z.Should().BeApproximately(2.05, 0.02);
        report.Channels.MidGrayTint.Should().Be("bluish", "blue has the lowest gamma, so it is brightest at mid-gray");
        report.Tone.EffectiveGamma.Should().BeInRange(2.1, 2.3);
        report.Tone.DeepestMeasuredCode.Should().BeLessThan(30);
        report.Steps.BlackCrushUpToCode.Should().BeNull();
        report.Steps.WhiteClipFromCode.Should().BeNull();
        report.Findings.Should().Contain(f => f.Topic == "Gray balance");
    }

    [Fact]
    public async Task A_linear_camera_recovers_the_same_tone_and_measures_the_contrast()
    {
        // As a phone measuring from its raw data: the same nulls, and readings proportional to the light.
        var options = new SimulatedDisplayOptions { Gamma = new Vec3(2.35, 2.2, 2.05) };
        var report = await MeasureAsync(options, new SimulatedCameraOptions { Response = CameraResponse.Linear });

        report.Channels.HalfLightCodes.X.Should().BeApproximately(TruthHalfCode(2.35), 0.5);
        report.Channels.HalfLightCodes.Y.Should().BeApproximately(TruthHalfCode(2.2), 0.5);
        report.Channels.HalfLightCodes.Z.Should().BeApproximately(TruthHalfCode(2.05), 0.5);
        report.Channels.MidGrayTint.Should().Be("bluish");
        report.Tone.EffectiveGamma.Should().BeInRange(2.1, 2.3);
        report.Steps.BlackCrushUpToCode.Should().BeNull();
        report.Steps.WhiteClipFromCode.Should().BeNull();

        var display = new SimulatedDisplay(options);
        static double Luma(Vec3 v) => (0.299 * v.X) + (0.587 * v.Y) + (0.114 * v.Z);
        var truth = Luma(display.Emit(DeviceRgb.White)) / Luma(display.Emit(DeviceRgb.Black));
        report.Contrast.IsReported.Should().BeTrue("a linear camera's readings need no assumed response");
        report.Contrast.IsLowerBound.Should().BeFalse();
        report.Contrast.Ratio.Should().BeInRange(truth * 0.8, truth * 1.25);
    }

    [Fact]
    public async Task Crushed_shadows_and_clipped_highlights_are_found()
    {
        var report = await MeasureAsync(new SimulatedDisplayOptions { CrushCode = 10, ClipCode = 246 });

        report.Steps.BlackCrushUpToCode.Should().Be(8);
        report.Steps.WhiteClipFromCode.Should().Be(249);
        report.Steps.LooksLikeRangeMismatch.Should().BeFalse();
        report.Findings.Should().Contain(f => f.Topic == "Shadows").And.Contain(f => f.Topic == "Highlights");
    }

    [Fact]
    public async Task A_full_to_limited_range_mismatch_is_named()
    {
        var report = await MeasureAsync(new SimulatedDisplayOptions { RangeMismatch = RangeMismatch.FullSentLimitedExpected });

        report.Steps.LooksLikeRangeMismatch.Should().BeTrue();
        report.Findings.Should().ContainSingle(f => f.Topic == "RGB range" && f.Level == FindingLevel.Problem);
    }

    [Fact]
    public async Task The_loopback_remote_instrument_gives_the_same_answers_as_in_process()
    {
        var display = new SimulatedDisplayOptions { Gamma = new Vec3(2.3, 2.2, 2.1) };

        var local = await MeasureAsync(display);
        var remote = await MeasureAsync(display, loopback: new LoopbackOptions());

        remote.Tone.EffectiveGamma.Should().BeApproximately(local.Tone.EffectiveGamma, 0.02);
        remote.Channels.HalfLightCodes.X.Should().BeApproximately(local.Channels.HalfLightCodes.X, 0.5);
        remote.Channels.HalfLightCodes.Y.Should().BeApproximately(local.Channels.HalfLightCodes.Y, 0.5);
        remote.Channels.HalfLightCodes.Z.Should().BeApproximately(local.Channels.HalfLightCodes.Z, 0.5);
        remote.InstrumentModel.Should().Be(local.InstrumentModel);
    }

    /// <summary>The error budget: across random cameras, the 95th-percentile gamma error stays small.</summary>
    [Fact]
    public async Task Across_random_cameras_the_p95_gamma_error_is_within_budget()
    {
        var random = new Random(2026);
        var errors = new List<double>();
        for (var trial = 0; trial < 8; trial++)
        {
            var gamma = 1.9 + (random.NextDouble() * 0.7);
            CameraResponse response = (trial % 3) switch
            {
                0 => CameraResponse.Srgb,
                1 => CameraResponse.Power(1.6 + random.NextDouble()),
                _ => CameraResponse.Filmic(1 + (random.NextDouble() * 3)),
            };
            var camera = new SimulatedCameraOptions
            {
                Response = response,
                WhiteBalanceGains = new Vec3(1.4 + random.NextDouble(), 1, 1.2 + random.NextDouble()),
                Seed = trial + 1,
            };

            var report = await MeasureAsync(new SimulatedDisplayOptions { Gamma = new Vec3(gamma, gamma, gamma) }, camera);
            errors.Add(Math.Abs(report.Channels.ChannelGammas.Y - gamma));
        }

        errors.Sort();
        errors[(int)Math.Ceiling(0.95 * errors.Count) - 1].Should().BeLessThan(0.03, $"errors: {string.Join(", ", errors.Select(e => e.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)))}");
    }

    [Fact]
    public async Task Grays_are_judged_on_the_grays_when_single_colors_do_not_predict_them()
    {
        // Blue dims when red and green are lit: blue alone has a perfect γ 2.2, but in a gray it falls behind.
        var display = new SimulatedDisplay(new SimulatedDisplayOptions { ChannelInteraction = new Vec3(0, 0, 0.3) });

        var (measurement, model) = await MeasureRawAsync(display, IdealCamera);
        var report = DisplayAnalysis.Analyze(measurement, new ReportContext("simulated", null, model));
        var truth = TruthBalance(display, report.Channels.GrayLevels![0].Code);

        report.Channels.ChannelGammas.Z.Should().BeApproximately(2.2, 0.02, "blue shown alone is unaffected");
        report.Channels.MidGrayTint.Should().Be("yellowish");
        report.Channels.MidGrayBalance.X.Should().BeApproximately(truth.X, 0.006);
        report.Channels.MidGrayBalance.Z.Should().BeApproximately(truth.Z, 0.006);
        foreach (var level in report.Channels.GrayLevels.Where(l => l.Uncertainty < 0.01))
        {
            var expected = TruthBalance(display, level.Code);
            level.Balance.Z.Should().BeApproximately(expected.Z, 0.01, $"at code {level.Code:0.0}");
        }

        var seenByARealCamera = await MeasureAsync(display.Options);
        seenByARealCamera.Channels.MidGrayTint.Should().Be("yellowish", "the camera's color matrix may change the size of a tint, not its direction");
    }

    [Fact]
    public async Task Channels_that_interact_evenly_keep_grays_neutral_and_the_report_says_why_colors_disagree()
    {
        var report = await MeasureAsync(new SimulatedDisplayOptions { ChannelInteraction = new Vec3(0.3, 0.3, 0.3) });

        report.Channels.GrayConsistencyCodes.Should().BeGreaterThan(3);
        report.Channels.Confidence.Should().Be(Confidence.High);
        report.Channels.MidGrayTint.Should().BeNull();
        report.Channels.MidGrayBalance.Z.Should().BeApproximately(1, 0.01);
        report.Findings.Should().Contain(f => f.Topic == "Panel" && f.Message.Contains("interact"));
        report.Findings.Should().Contain(f => f.Topic == "Gray balance" && f.Level == FindingLevel.Info);
    }

    /// <summary>The method's core claim for gray balance: a neutral display reads neutral through any camera.</summary>
    [Fact]
    public async Task Neutral_grays_read_neutral_whatever_the_camera()
    {
        var random = new Random(17);
        for (var trial = 0; trial < 5; trial++)
        {
            var camera = new SimulatedCameraOptions
            {
                Response = trial % 2 == 0 ? CameraResponse.Filmic(1 + (random.NextDouble() * 3)) : CameraResponse.Power(1.6 + random.NextDouble()),
                WhiteBalanceGains = new Vec3(1.4 + random.NextDouble(), 1, 1.2 + random.NextDouble()),
                Seed = trial + 100,
            };

            var report = await MeasureAsync(new SimulatedDisplayOptions(), camera);

            report.Channels.MidGrayTint.Should().BeNull($"trial {trial}");
            report.Channels.GrayLevels!.Where(l => l.Uncertainty <= 0.03).Should().OnlyContain(l => l.Tint == null, $"trial {trial}");
        }
    }

    /// <summary>
    /// On a panel with uneven single-code steps (6-bit+FRC), a channel whose crossing lies outside the luma's pair must
    /// be matched inside its own pair: extrapolating from one step put a false 1.7 % cyan tint at code 39 here, and on
    /// the real BenQ inflated the code-50 blue deficit by about 3 %.
    /// </summary>
    [Fact]
    public async Task Channels_are_matched_inside_their_own_pair_of_codes_not_extrapolated()
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions { StepUnevenness = 0.4 });
        static double Outside(double m, int a) => Math.Max(0, Math.Max(a - m, m - (a + 1)));
        async Task<(DisplayMeasurement Measurement, double WorstError)> RunAsync(bool bracket)
        {
            var sim = new SimulatedRig(display, new SimulatedCamera(IdealCamera));
            await using var rig = new MeasurementRig(sim, sim, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };
            await sim.PrepareAsync(new LockRequest(), Token);
            var measurement = await new QuickPlan(rig, new QuickPlanOptions { BracketEveryChannel = bracket }).RunAsync(null, Token);
            var levels = DisplayAnalysis.GrayBalance(measurement).Where(l => l.Fraction >= 0.01);
            return (measurement, levels.Max(l => (l.Balance - TruthBalance(display, l.Code)).Map(Math.Abs).Max));
        }

        var (bracketed, bracketedError) = await RunAsync(bracket: true);
        var (_, extrapolatedError) = await RunAsync(bracket: false);

        var repeats = bracketed.GrayTree.Select(l => l.Null.Repeats[0]).ToList();
        repeats.Should().Contain(r => r.ChannelBrackets != null, "some channels cross outside the luma's pair on this panel");
        foreach (var r in repeats)
        {
            for (var c = 0; c < 3; c++)
            {
                var pairs = (r.ChannelBrackets ?? []).Where(b => b.Channel == c).Select(b => b.LowerCode).Append(r.LowerCode);
                pairs.Min(a => Outside(r.MatchedCodePerChannel[c], a)).Should().BeLessThanOrEqualTo(0.15, $"channel {c} at luma pair {r.LowerCode}");
            }
        }

        bracketedError.Should().BeLessThan(0.01);
        extrapolatedError.Should().BeGreaterThan(0.01, "the extrapolated matches are what the brackets fix");
    }

    /// <summary>A hand-held camera that wobbles: the light it sees jumps every few seconds. Palindromes that straddle a
    /// jump are read again, so the tone stays right.</summary>
    [Fact]
    public async Task A_wobbling_camera_is_caught_and_the_moved_readings_are_taken_again()
    {
        async Task<(double GammaError, int Retries)> RunAsync(double tolerance)
        {
            var sim = new SimulatedRig(
                new SimulatedDisplay(new SimulatedDisplayOptions()),
                new SimulatedCamera(new SimulatedCameraOptions()),
                new SimulatedRigOptions { ShakeEverySeconds = 2.5, ShakeAmount = 0.04 });
            await using var rig = new MeasurementRig(sim, sim, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };
            await sim.PrepareAsync(new LockRequest(), Token);
            var plan = new QuickPlanOptions { ContentCheck = false };
            plan = plan with { Nulls = plan.Nulls with { MovementTolerance = tolerance } };
            var measurement = await new QuickPlan(rig, plan).RunAsync(null, Token);
            var tone = DisplayAnalysis.Tone(measurement);
            return (Math.Abs(tone.EffectiveGamma - 2.2), measurement.GrayTree.Sum(l => l.Null.MovementRetries));
        }

        var guarded = await RunAsync(0.01);
        var unguarded = await RunAsync(double.PositiveInfinity);

        guarded.Retries.Should().BePositive("some palindromes straddle a jump");
        guarded.GammaError.Should().BeLessThan(0.03);
        guarded.GammaError.Should().BeLessThanOrEqualTo(unguarded.GammaError, $"unguarded error {unguarded.GammaError:0.000}");
    }

    [Fact]
    public async Task A_before_after_comparison_calls_a_move_toward_the_target_better_and_a_repeat_the_same()
    {
        var steep = await MeasureAsync(new SimulatedDisplayOptions { Gamma = new Vec3(2.6, 2.6, 2.6) });
        var right = await MeasureAsync(new SimulatedDisplayOptions { Gamma = new Vec3(2.2, 2.2, 2.2) });
        var again = await MeasureAsync(new SimulatedDisplayOptions { Gamma = new Vec3(2.2, 2.2, 2.2) }, new SimulatedCameraOptions { Seed = 7 });
        static ComparisonVerdict Gamma(IReadOnlyList<ComparisonRow> rows) => rows.Single(r => r.Quantity.StartsWith("Effective gamma", StringComparison.Ordinal)).Verdict;

        Gamma(ReportComparison.Compare(steep, right)).Should().Be(ComparisonVerdict.Better);
        Gamma(ReportComparison.Compare(right, steep)).Should().Be(ComparisonVerdict.Worse);
        ReportComparison.Compare(right, again).Where(r => r.Verdict != ComparisonVerdict.Info).Should().OnlyContain(r => r.Verdict == ComparisonVerdict.Same);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.3, true)]
    public async Task A_backlight_that_follows_the_picture_is_caught_by_the_content_check(double dynamicContrast, bool expected)
    {
        var sim = new SimulatedRig(
            new SimulatedDisplay(new SimulatedDisplayOptions()),
            new SimulatedCamera(new SimulatedCameraOptions()),
            new SimulatedRigOptions { DynamicContrast = dynamicContrast });
        await using var rig = new MeasurementRig(sim, sim, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };
        await sim.PrepareAsync(new LockRequest(), Token);
        var measurement = await new QuickPlan(rig).RunAsync(null, Token);
        var report = DisplayAnalysis.Analyze(measurement, new ReportContext("simulated", null, "sim"));

        measurement.Content!.ChangesWithContent.Should().Be(expected, $"first difference {measurement.Content.FirstDifference:P2}, drift {measurement.Content.Drift:P2}");
        report.Findings.Should().Contain(f => f.Topic == "Picture-dependent brightness" && f.Level == (expected ? FindingLevel.Problem : FindingLevel.Info));
    }

    [Fact]
    public async Task The_raw_measurement_round_trips_and_re_analyses_to_the_same_report()
    {
        var (measurement, model) = await MeasureRawAsync(new SimulatedDisplay(new SimulatedDisplayOptions { Gamma = new Vec3(2.3, 2.2, 2.1) }));
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        var context = new ReportContext("simulated", null, model);

        var back = ReportWriter.MeasurementFromJson(ReportWriter.MeasurementToJson(measurement))!;

        ReportWriter.ToJson(DisplayAnalysis.Analyze(back, context, clock))
            .Should().Be(ReportWriter.ToJson(DisplayAnalysis.Analyze(measurement, context, clock)));
    }

    [Fact]
    public async Task The_report_round_trips_through_json_and_renders_html()
    {
        var report = await MeasureAsync(new SimulatedDisplayOptions());

        var back = ReportWriter.FromJson(ReportWriter.ToJson(report))!;
        var html = ReportWriter.ToHtml(report);

        back.Tone.EffectiveGamma.Should().Be(report.Tone.EffectiveGamma);
        back.Findings.Should().HaveCount(report.Findings.Count);
        html.Should().Contain("<svg").And.Contain("Effective gamma").And.Contain("simulated");
    }

    [Fact]
    public async Task A_report_names_its_profile_and_older_reports_picture_mode_reads_as_it()
    {
        var report = await MeasureAsync(new SimulatedDisplayOptions()) with { Profile = "2-DIM" };

        var json = ReportWriter.ToJson(report);
        var older = ReportWriter.FromJson(json.Replace("\"profile\"", "\"pictureMode\"", StringComparison.Ordinal))!;

        json.Should().Contain("\"profile\": \"2-DIM\"").And.NotContain("pictureMode");
        ReportWriter.FromJson(json)!.Profile.Should().Be("2-DIM");
        older.Profile.Should().Be("2-DIM", "reports written before profiles named the picture mode");
        ReportWriter.ToHtml(report).Should().Contain("profile 2-DIM");
    }
}
