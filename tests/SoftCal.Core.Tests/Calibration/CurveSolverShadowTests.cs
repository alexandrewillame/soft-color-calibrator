using System.Globalization;
using AwesomeAssertions;
using SoftCal.Core.Analysis;
using SoftCal.Core.Calibration;
using SoftCal.Core.Color;
using SoftCal.Core.Measurement;
using Xunit;

namespace SoftCal.Core.Tests.Calibration;

/// <summary>
/// The solver's shadows, on the BenQ's real measurements. On 2026-10-18 the refine pass took its black crush from the
/// trial measurement's near-black steps (taken through pass 1's curves), which reported codes 0–20 as black: the
/// installed curve then sent inputs 1–12 to codes 10.8–11.7, two levels on the 8-bit link, so the darkest twelve
/// steps merged and black jumped straight to code 11.
/// </summary>
public sealed class CurveSolverShadowTests(ITestOutputHelper output)
{
    private static DisplayMeasurement Load(string name) =>
        ReportWriter.MeasurementFromJson(File.ReadAllText(Fixture(name))) ?? throw new InvalidOperationException(name);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Calibration", name);

    /// <summary>A measurement whose near-black steps all read like its black: the step test then puts the crush at the
    /// top of its range (code 20), as the 2026-10-18 trial did.</summary>
    private static DisplayMeasurement WithBlackSteps(DisplayMeasurement m)
    {
        var black = m.NearBlack.Single(r => r.Code == 0);
        return m with { NearBlack = [.. m.NearBlack.Select(r => black with { Code = r.Code })] };
    }

    /// <summary>What a measurement through <paramref name="curves"/> would record: each gray matched at the input the
    /// curves send to the code matched natively (each channel through its own curve; the gray's code through green's,
    /// the largest share of luma).</summary>
    private static DisplayMeasurement ThroughCurves(DisplayMeasurement native, CalibrationCurves curves)
    {
        double Back(int channel, double code)
        {
            var curve = curves[channel];
            for (var i = 1; i < curve.Count; i++)
            {
                if (curve[i] >= code)
                {
                    return i - 1 + ((code - curve[i - 1]) / Math.Max(1e-9, curve[i] - curve[i - 1]));
                }
            }

            return 255;
        }

        Vec3 BackEach(Vec3 codes) => new(Back(0, codes.X), Back(1, codes.Y), Back(2, codes.Z));
        TreeLevel Map(TreeLevel level) => level with
        {
            Null = level.Null with
            {
                MatchedCode = Back(1, level.Null.MatchedCode),
                Repeats = [.. level.Null.Repeats.Select(r => r with { MatchedCode = Back(1, r.MatchedCode), MatchedCodePerChannel = BackEach(r.MatchedCodePerChannel) })],
            },
        };
        return native with { GrayTree = [.. native.GrayTree.Select(Map)] };
    }

    /// <summary>The installed curve's 'vcgt' (a big-endian ICC tag), as output codes 0–255 per channel.</summary>
    private static double[][] InstalledCurves(string name)
    {
        var bytes = File.ReadAllBytes(Fixture(name));
        static uint U32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);
        static int U16(byte[] b, int at) => (b[at] << 8) | b[at + 1];
        for (var i = 0; i < U32(bytes, 128); i++)
        {
            var entry = 132 + (12 * i);
            if (bytes[entry] == 'v' && bytes[entry + 1] == 'c' && bytes[entry + 2] == 'g' && bytes[entry + 3] == 't')
            {
                var at = (int)U32(bytes, entry + 4) + 18;
                return [.. Enumerable.Range(0, 3).Select(c => Enumerable.Range(0, 256).Select(v => U16(bytes, at + (2 * ((256 * c) + v))) / 65535.0 * 255).ToArray())];
            }
        }

        throw new InvalidOperationException("No vcgt.");
    }

    private static string First(IReadOnlyList<double> curve, int count = 17) =>
        string.Join(" ", curve.Take(count).Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)));

    /// <summary>Distinct visible 8-bit output codes among inputs 1–16: the darkest steps the panel can tell apart
    /// (codes up to the crush all look black).</summary>
    private static int DarkLevels(IReadOnlyList<double> curve, int crush) =>
        curve.Skip(1).Take(16).Select(v => (int)Math.Round(v)).Where(v => v > crush).Distinct().Count();

    [Fact]
    public void A_refine_pass_keeps_the_shadows_rising_from_just_above_black()
    {
        var target = new CalibrationTarget();
        var before = Load("benq-2026-10-18-before.measurement.json");
        var pass1 = CurveSolver.Solve(before, target);

        // The trial measured through pass 1's curves (here: exactly what pass 1 predicts), and its near-black step
        // test reported codes 0–20 as black, as on 2026-10-18.
        var trial = WithBlackSteps(ThroughCurves(before, pass1));
        DisplayAnalysis.Steps(trial).BlackCrushUpToCode.Should().Be(20);
        var pass2 = CurveSolver.Solve(trial, target, pass1);

        var installed = InstalledCurves("benq-2026-10-18-installed.icc");
        for (var c = 0; c < 3; c++)
        {
            output.WriteLine($"ch{c} installed (2026-10-18): {First(installed[c])}");
            output.WriteLine($"ch{c} pass 1:                 {First(pass1[c])}");
            output.WriteLine($"ch{c} pass 2 now:             {First(pass2[c])}");
        }

        const int Crush = 4;
        DisplayAnalysis.Steps(before).BlackCrushUpToCode.Should().Be(Crush, "the BenQ's own crush that morning");
        pass2.AnchorCrush.Should().Be(Crush, "the display's own crush travels with the curves");
        for (var c = 0; c < 3; c++)
        {
            pass2[c][1].Should().BeInRange(Crush, Crush + 1, "input 1 leaves just above the display's own crush, not at code 11");
            DarkLevels(pass2[c], Crush).Should().BeGreaterThanOrEqualTo(DarkLevels(pass1[c], Crush) - 1, "the darkest sixteen steps keep about as many visible levels as pass 1 gave them");
            pass2[c].Skip(1).Take(16).Should().BeInAscendingOrder();
            Enumerable.Range(1, 40).Max(i => Math.Abs(pass2[c][i] - pass1[c][i])).Should().BeLessThan(1.5, "the trial is what pass 1 predicted, so the refine changes little");
        }
    }

    /// <summary>The gray tree measured light at its deepest point, so codes there cannot be black: a step test that says
    /// they are (one noisy black reading) is not trusted that far.</summary>
    [Fact]
    public void A_crush_at_or_above_the_deepest_measured_gray_is_capped_below_it()
    {
        var measurement = WithBlackSteps(Load("benq-2026-10-18-before.measurement.json"));
        var deepest = measurement.GrayTree.Min(l => l.Null.MatchedCode);

        DisplayAnalysis.Steps(measurement).BlackCrushUpToCode.Should().Be(20);
        ChannelCurve.Crush(measurement).Should().BeLessThan((int)Math.Floor(deepest));
        ChannelCurve.Measured(measurement).Should().OnlyContain(curve => curve.Evaluate(deepest) > 0);
    }
}
