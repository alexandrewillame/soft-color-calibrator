using System.Globalization;
using AwesomeAssertions;
using SoftCal.Core.Analysis;
using SoftCal.Core.Calibration;
using SoftCal.Core.Color;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.Icc;
using Xunit;

namespace SoftCal.Instruments.Simulated.Tests;

/// <summary>
/// M5's solver in closed loop: measure, solve the GPU curves, load them into the simulated GPU, measure again. The
/// second measurement is what Windows would show with the calibration file installed.
/// </summary>
public sealed class CalibrationTests(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>BenQ-like: steeper than 2.2, blue the steepest (dark grays turn yellowish), uneven steps, codes up to 3
    /// crushed, and channels that interact.</summary>
    private static SimulatedDisplay BenqLike(double stepUnevenness = 0.2, int outputBits = 8) => new(new SimulatedDisplayOptions
    {
        Gamma = new Vec3(2.4, 2.3, 2.6),
        CrushCode = 3,
        StepUnevenness = stepUnevenness,
        ChannelInteraction = new Vec3(0.05, 0.05, 0.05),
        OutputBits = outputBits,
    });

    /// <summary>Measure, solve, load; twice (the second pass corrects what the channels' interaction left).</summary>
    private async Task<(DisplayMeasurement Before, DisplayMeasurement After, CalibrationCurves Curves)> CalibrateAsync(SimulatedDisplay display, CalibrationTarget target)
    {
        var before = await MeasureAsync(display);
        Report("before", before);
        var curves = CurveSolver.Solve(before, target);
        Load(display, curves);
        var once = await MeasureAsync(display);
        Report("after one pass", once);
        curves = CurveSolver.Solve(once, target, curves);
        Load(display, curves);
        var after = await MeasureAsync(display);
        Report("after two passes", after);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"levels kept: R {curves.LevelsKept(0)}, G {curves.LevelsKept(1)}, B {curves.LevelsKept(2)}"));
        return (before, after, curves);
    }

    private static async Task<DisplayMeasurement> MeasureAsync(SimulatedDisplay display)
    {
        var sim = new SimulatedRig(display, new SimulatedCamera(new SimulatedCameraOptions()));
        await using var rig = new MeasurementRig(sim, sim, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };
        await sim.PrepareAsync(new LockRequest(), Token);
        return await new QuickPlan(rig, new QuickPlanOptions { ContentCheck = false }).RunAsync(null, Token);
    }

    private static void Load(SimulatedDisplay display, CalibrationCurves curves) => display.SetGpuLut([curves.Ramp(0), curves.Ramp(1), curves.Ramp(2)]);

    /// <summary>The worst gray imbalance among grays down to 1/32 of white.</summary>
    private static double WorstImbalance(DisplayMeasurement m) => DisplayAnalysis.GrayBalance(m).Where(l => l.Fraction >= 1.0 / 32 - 1e-9).Max(l => l.Imbalance);

    /// <summary>The worst code error against the 2.2 curve over the tree's levels down to 1/32 of white.</summary>
    private static double WorstCodeError(DisplayMeasurement m, double gamma = 2.2) =>
        m.GrayTree.Where(l => l.Fraction >= 1.0 / 32 - 1e-9).Max(l => Math.Abs(l.Null.MatchedCode - (255 * Math.Pow(l.Fraction, 1 / gamma))));

    private void Report(string name, DisplayMeasurement m)
    {
        var tone = DisplayAnalysis.Tone(m);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: effective gamma {tone.EffectiveGamma:0.000}, worst code error {WorstCodeError(m):0.00}, worst imbalance {WorstImbalance(m):0.0%}"));
        foreach (var l in DisplayAnalysis.GrayBalance(m))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   code {l.Code,6:0.00} (2.2: {255 * Math.Pow(l.Fraction, 1 / 2.2),6:0.00})  R {100 * (l.Balance.X - 1),5:+0.0;-0.0} %  G {100 * (l.Balance.Y - 1),5:+0.0;-0.0} %  B {100 * (l.Balance.Z - 1),5:+0.0;-0.0} %"));
        }
    }

    /// <summary>
    /// The BenQ-like display starts at γ 2.43 with dark grays 21 % off neutral. Two passes reach γ 2.2 within a code at
    /// every level. What tint is left (up to ~3 %) is real code-to-code structure of the uneven 6-bit+FRC-like steps,
    /// which a smooth curve through eight measured grays cannot follow: the measurement agrees with the model's truth
    /// within about 1 % (checked on 2026-10-17 with three camera seeds).
    /// </summary>
    [Fact]
    public async Task Curves_bring_a_steep_tinted_display_to_gamma_2_2_with_neutral_grays()
    {
        var (before, after, curves) = await CalibrateAsync(BenqLike(), new CalibrationTarget());

        WorstCodeError(before).Should().BeGreaterThan(5, "the display starts clearly off target");
        WorstImbalance(before).Should().BeGreaterThan(0.1, "and with tinted dark grays");
        DisplayAnalysis.Tone(after).EffectiveGamma.Should().BeApproximately(2.2, 0.04);
        WorstCodeError(after).Should().BeLessThan(1.5);
        WorstImbalance(after).Should().BeLessThan(0.035);
        Enumerable.Range(0, 3).Should().OnlyContain(c => curves.LevelsKept(c) >= 200, "a correction this size costs only a few of the 256 levels");
        curves.Red[255].Should().Be(255, "native white is kept");
        curves.Red[0].Should().Be(0, "and native black");
    }

    /// <summary>
    /// The solver's own precision, without the panel's limits: even steps and a 10-bit link (an 8-bit link without
    /// dithering rounds each channel's output to a whole code, up to ±1.2 % of light at code 99, so about 2 % between
    /// channels is the floor there).
    /// </summary>
    [Fact]
    public async Task With_even_steps_and_a_10_bit_link_the_grays_end_neutral_to_about_one_percent()
    {
        var (_, after, _) = await CalibrateAsync(BenqLike(stepUnevenness: 0, outputBits: 10), new CalibrationTarget());

        WorstImbalance(after).Should().BeLessThan(0.012);
        WorstCodeError(after).Should().BeLessThan(0.6);
    }

    /// <summary>The profile SoftCal installs: its 'vcgt' holds the curves exactly, and its tone curves (measured with the
    /// curves loaded) describe the calibrated display, so they follow the 2.2 target.</summary>
    [Fact]
    public async Task The_installed_profile_carries_the_curves_and_describes_the_calibrated_display()
    {
        var (_, after, curves) = await CalibrateAsync(BenqLike(), new CalibrationTarget());

        var bytes = IccWriter.Write(CalibrationProfile.Create("SoftCal test", RgbPrimaries.Srgb, after, curves, new DateTime(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc)));
        var profile = IccReader.Read(bytes);

        profile.Vcgt.Should().NotBeNull();
        for (var c = 0; c < 3; c++)
        {
            var vcgt = c switch { 0 => profile.Vcgt!.Red, 1 => profile.Vcgt!.Green, _ => profile.Vcgt!.Blue };
            vcgt.Should().Equal(curves.Ramp(c), "Windows' loader puts exactly these in the GPU");
            var trc = profile.ToneCurves[c];
            foreach (var code in new[] { 32, 64, 128, 192 })
            {
                var at = code / 255.0 * (trc.Count - 1);
                var i = (int)at;
                var light = trc[i] + ((at - i) * (trc[Math.Min(i + 1, trc.Count - 1)] - trc[i]));
                var target = Math.Pow(code / 255.0, 2.2);
                light.Should().BeApproximately(target, 0.06 * target + 0.002, $"channel {c} at code {code} follows the target it was calibrated to");
            }
        }
    }

    [Fact]
    public async Task Keeping_the_native_tone_only_neutralizes_the_grays()
    {
        var (before, after, _) = await CalibrateAsync(BenqLike(), new CalibrationTarget(ToneTarget.KeepNative));

        WorstImbalance(after).Should().BeLessThan(0.035);
        DisplayAnalysis.Tone(after).EffectiveGamma.Should().BeApproximately(DisplayAnalysis.Tone(before).EffectiveGamma, 0.05, "the tone stays the display's own");
    }
}
