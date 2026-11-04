using SoftCal.Core.Analysis;
using SoftCal.Core.Measurement;
using SoftCal.Core.ToneCurves;
using SoftCal.FeasibilityHarness.Infrastructure;

namespace SoftCal.FeasibilityHarness.Steps;

/// <summary>Halftone nulls: repeatability, pattern shapes, the midpoint tree and the ±1 cm move test.</summary>
internal static class NullSteps
{
    private static readonly HalftonePatch Checker = new(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard);

    private static SolidPatch Gray(int code) => new(DeviceRgb.Gray((byte)code));

    // ---------------------------------------------------------------- null (matched-reading σ)

    public static async Task NullAsync(StepContext c)
    {
        Ui.Title("Halftone null at 50 % (A-B-B-A, same exposure): matched-reading σ");
        var matcher = new NullMatcher(c.Measure);
        var result = await matcher.MatchAsync("checker W/K", Checker, Gray, MidpointTree.Guess(0.5), c.NullOptions(c.Args.GetInt("repeats", 5)), c.Token);
        c.HalfNull = result;
        Print(result);
        c.Session.Result("null", new { result, sigma = result.MatchedCodeStdDev, perChannel = result.MatchedCodePerChannel });
    }

    // ---------------------------------------------------------------- shapes

    public static async Task ShapesAsync(StepContext c)
    {
        Ui.Title("Pattern shapes: checkerboard, horizontal lines, vertical lines (50 %); Bayer 4×4 vs checkerboard (25 %)");
        var matcher = new NullMatcher(c.Measure);
        var options = c.NullOptions();
        var half = c.HalfNull ?? await matcher.MatchAsync("checker W/K", Checker, Gray, MidpointTree.Guess(0.5), options, c.Token);
        var h = await matcher.MatchAsync("h-lines W/K", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.HorizontalLines), Gray, half.MatchedCode, options, c.Token);
        var v = await matcher.MatchAsync("v-lines W/K", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.VerticalLines), Gray, half.MatchedCode, options, c.Token);
        Print(half);
        Print(h);
        Print(v);
        var spread50 = FeasibilityGate.Spread([half.MatchedCode, h.MatchedCode, v.MatchedCode]);

        // 25 %: a 4/16 Bayer dither of white/black against a checkerboard of the half-light code and black.
        var bayer = await matcher.MatchAsync("bayer 4/16 W/K", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 4), Gray, MidpointTree.Guess(0.25), options, c.Token);
        var (reference, code, codeFraction, fraction) = MidpointTree.Next(half.MatchedCode, 0.5, DeviceRgb.Gray);
        var quarter = await matcher.MatchAsync($"checker {code}/K", reference, Gray, bayer.MatchedCode, options, c.Token);
        Print(bayer);
        Print(quarter);

        // The checkerboard gives light fraction(code)/2, not exactly 1/4: move its match to 1/4 along the local power law.
        var gamma = Math.Log(fraction) / Math.Log(quarter.MatchedCode / 255.0);
        var quarterAt25 = quarter.MatchedCode * Math.Pow(0.25 / fraction, 1 / gamma);
        var diff25 = Math.Abs(bayer.MatchedCode - quarterAt25);
        var spread = Math.Max(spread50, diff25);
        (spread <= 1 ? (Action<FormattableString>)Ui.Good : Ui.Warn)(
            $"50 % shapes spread {spread50:0.00} code; 25 % Bayer vs checkerboard {diff25:0.00} code → {(spread <= 1 ? "PASS" : "FAIL")} (limit 1 code)");
        c.Session.Result("shapes", new
        {
            spread,
            spread50,
            diff25,
            checker = half,
            horizontalLines = h,
            verticalLines = v,
            bayer4 = bayer,
            checkerQuarter = quarter,
            checkerQuarterCode = code,
            checkerQuarterFraction = fraction,
            checkerQuarterCodeFraction = codeFraction,
            checkerQuarterAt25 = quarterAt25,
        });
    }

    // ---------------------------------------------------------------- tree

    public static async Task TreeAsync(StepContext c)
    {
        var runs = c.Args.GetInt("runs", 3);
        var levels = c.Args.GetInt("levels", 14);
        var stopBelow = c.Args.GetDouble("stop-code", 22);
        var geometry = Geometry(c.Args.Get("geometry"));
        Ui.Title($"Midpoint tree ({geometry}) until the matched code reaches ~{stopBelow:0} (at most {levels} halvings), {runs} runs");
        var matcher = new NullMatcher(c.Measure);
        var all = new List<TreeRun>();
        for (var run = 1; run <= runs; run++)
        {
            Ui.Info($"run {run}/{runs}");
            var nodes = await MidpointTree.RunAsync(matcher, levels, c.NullOptions(), DeviceRgb.Gray, node =>
                Ui.Info($"  1/{1L << node.Level,-5} light {node.Fraction:0.000000} → code {node.Null.MatchedCode:0.00} ± {node.Null.MatchedCodeStdDev:0.00} (reference {node.HalftoneCode}/K, exposure {node.Null.ExposureSeconds * 1000:0.##} ms)"), c.Token, stopBelow, geometry);
            var points = nodes.Select(n => (Encoded: n.Null.MatchedCode / 255, Linear: n.Fraction)).ToList();
            var slope = GammaAnalysis.LogLogSlope(points);
            var curve = new MeasuredCurve(
                [0, .. points.Select(p => p.Encoded).Reverse(), 1],
                [0, .. points.Select(p => p.Linear).Reverse(), 1]);
            var effective = GammaAnalysis.PointGamma(0.5, curve.ToLinear(0.5));
            Ui.Info($"  gamma: log-log slope {slope:0.000}, effective (50 %) {effective:0.000}");
            var treeRun = new TreeRun(run, nodes, slope, effective);
            all.Add(treeRun);
            c.Session.Result(geometry == HalftoneGeometry.Checkerboard ? $"tree.run{run}" : $"tree.{geometry}.run{run}", treeRun);
        }

        // The gate's tree points are the levels nearest codes 21, 38 and 72 (1/256, 1/64 and 1/16 at γ 2.2).
        double At(TreeRun r, int level) => r.Levels.FirstOrDefault(l => l.Level == level)?.Null.MatchedCode ?? double.NaN;
        var nearest = MidpointTree.LevelsNearest([.. all.Select(r => r.Levels)], [21, 38, 72]);
        var spread72 = FeasibilityGate.Spread(all.Select(r => At(r, nearest[72])));
        var spread38 = FeasibilityGate.Spread(all.Select(r => At(r, nearest[38])));
        var spread21 = FeasibilityGate.Spread(all.Select(r => At(r, nearest[21])));
        Ui.Info($"gate points: ~72 → level {nearest[72]} (1/{1L << nearest[72]}), ~38 → level {nearest[38]}, ~21 → level {nearest[21]}");
        var gammaSpread = FeasibilityGate.Spread(all.Select(r => r.LogLogGamma));
        var effectiveSpread = FeasibilityGate.Spread(all.Select(r => r.EffectiveGamma));
        var sigmaMax = all.SelectMany(r => r.Levels).Max(l => l.Null.MatchedCodeStdDev);
        Ui.Info($"repeat (max − min over {runs} runs): ~72: {Ui.F(spread72, "0.00")}, ~38: {Ui.F(spread38, "0.00")}, ~21: {Ui.F(spread21, "0.00")} code; gamma spread {Ui.F(gammaSpread, "0.000")} (log-log), {Ui.F(effectiveSpread, "0.000")} (effective)");
        c.Session.Result(geometry == HalftoneGeometry.Checkerboard ? "tree" : "tree." + geometry, new
        {
            geometry,
            runs = all.Count,
            levels,
            gateLevels = nearest,
            gateCodes = nearest.ToDictionary(kv => kv.Key, kv => all.Select(r => At(r, kv.Value)).ToList()),
            spread72,
            spread38,
            spread21,
            gammaSpread,
            effectiveGammaSpread = effectiveSpread,
            worstLevelSigma = sigmaMax,
            codes = all.Select(r => r.Levels.Select(l => l.Null.MatchedCode)),
            gammas = all.Select(r => r.LogLogGamma),
            effectiveGammas = all.Select(r => r.EffectiveGamma),
        });
    }

    // ---------------------------------------------------------------- move

    public static async Task MoveAsync(StepContext c)
    {
        var phase = c.Args.Get("phase") ?? "base";
        Ui.Title($"Small camera move test, phase '{phase}': move the camera a little (any direction; the exact amount does not matter), then measure");
        var matcher = new NullMatcher(c.Measure);
        var options = c.NullOptions();
        var results = new List<NullResult>
        {
            await matcher.MatchAsync("checker W/K (50 %)", Checker, Gray, MidpointTree.Guess(0.5), options, c.Token),
            await matcher.MatchAsync("h-lines W/K (50 %)", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.HorizontalLines), Gray, MidpointTree.Guess(0.5), options, c.Token),
            await matcher.MatchAsync("bayer 4/16 W/K (25 %)", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 4), Gray, MidpointTree.Guess(0.25), options, c.Token),
            await matcher.MatchAsync("bayer 1/16 W/K (6.25 %)", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 1), Gray, MidpointTree.Guess(1 / 16.0), options, c.Token),
        };
        foreach (var r in results)
        {
            Print(r);
        }

        c.Session.Result("move", new { phase, results, codes = results.Select(r => r.MatchedCode) });
    }

    /// <summary>The 50 % halftone geometry named on the command line (checker, hlines, vlines).</summary>
    internal static HalftoneGeometry Geometry(string? name) => name?.ToUpperInvariant() switch
    {
        null or "CHECKER" or "CHECKERBOARD" => HalftoneGeometry.Checkerboard,
        "HLINES" or "HORIZONTAL" => HalftoneGeometry.HorizontalLines,
        "VLINES" or "VERTICAL" => HalftoneGeometry.VerticalLines,
        _ => throw new ArgumentException($"Unknown geometry '{name}' (checker, hlines, vlines)."),
    };

    internal static void Print(NullResult r) =>
        Ui.Info($"  {r.Label,-24} → code {r.MatchedCode:0.000} (σ {r.MatchedCodeStdDev:0.000}, {r.Repeats.Count} palindromes, RGB {r.MatchedCodePerChannel.X:0.00}/{r.MatchedCodePerChannel.Y:0.00}/{r.MatchedCodePerChannel.Z:0.00}, exposure {r.ExposureSeconds * 1000:0.###} ms)");
}

/// <summary>One run of the midpoint tree, with its gamma.</summary>
internal sealed record TreeRun(int Run, IReadOnlyList<TreeLevel> Levels, double LogLogGamma, double EffectiveGamma);
