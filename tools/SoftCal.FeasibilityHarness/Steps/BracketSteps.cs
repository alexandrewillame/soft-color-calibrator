using SoftCal.Core.Analysis;
using SoftCal.Core.Measurement;
using SoftCal.Core.ToneCurves;
using SoftCal.FeasibilityHarness.Infrastructure;

namespace SoftCal.FeasibilityHarness.Steps;

/// <summary>Exposure bracketing (Robertson-style, exposure ratios as unknowns) on a 17-step gray ramp, and dark frames.</summary>
internal static class BracketSteps
{
    /// <summary>The 17-step ramp: 0, 16, 32, …, 240, 255.</summary>
    public static IReadOnlyList<int> RampCodes { get; } = [.. Enumerable.Range(0, 16).Select(i => i * 16), 255];

    public static async Task RampAsync(StepContext c)
    {
        var runs = c.Args.GetInt("ramp-runs", c.Quick ? 1 : c.Args.GetInt("runs", 3));
        Ui.Title($"17-step gray ramp, bracketed over every exposure step, {runs} run(s)");
        var gammas = new List<double>();
        var effective = new List<double>();
        var ratios = new List<IReadOnlyList<double>>();
        for (var run = 1; run <= runs; run++)
        {
            var result = await RunOnceAsync(c, run);
            gammas.Add(result.LogLogGamma);
            effective.Add(result.EffectiveGamma);
            ratios.Add(result.Fit.StepRatios);
            c.CameraGamma = result.Fit.LocalGamma(140) is var g && double.IsFinite(g) && g > 1 ? g : c.CameraGamma;
            c.Session.Result($"ramp.run{run}", result);
        }

        var spread = FeasibilityGate.Spread(gammas);
        var effectiveSpread = FeasibilityGate.Spread(effective);
        (spread <= 0.03 ? (Action<FormattableString>)Ui.Good : Ui.Warn)(
            $"bracketing gamma over {runs} runs: {string.Join(", ", gammas.Select(g => Ui.F(g, "0.000")))} → spread {Ui.F(spread, "0.000")} (limit 0.03); effective {Ui.F(effectiveSpread, "0.000")}");
        c.Session.Result("ramp", new { runs, gammas, effectiveGammas = effective, gammaSpread = spread, effectiveGammaSpread = effectiveSpread, stepRatios = ratios, cameraGamma = c.CameraGamma });
    }

    private static async Task<RampRun> RunOnceAsync(StepContext c, int run)
    {
        // Patches: the ramp plus halftones of known light. Halftones anchor the exposure scale; which ones can be
        // trusted depends on the panel (TN panels show horizontal subpixel crosstalk with 1-px checkerboards and
        // vertical lines), so the fit is repeated with each anchor set and the halftones' additivity is reported.
        var (patches, _) = BuildPatches();
        var steps = c.Measure.ExposureSteps;
        var observations = new List<BracketObservation>();
        var readings = new List<object>();
        var lastCode = new double[patches.Count];
        var policy = c.Readings(8) with { MaxClippedFraction = 1 };
        for (var k = 0; k < steps.Count; k++)
        {
            await c.Measure.SetExposureAsync(steps[k], c.Token);
            for (var j = 0; j < patches.Count; j++)
            {
                // Skip patches that clipped (or nearly) at the previous, shorter exposure: they can only clip now.
                if (k > 0 && lastCode[j] > 200)
                {
                    observations.Add(new BracketObservation(j, k, 255, Clipped: true));
                    continue;
                }

                var r = await c.Measure.ReadAsync(patches[j], policy, c.Token);
                var luma = r.Channel(3);
                var clipped = r.MaxClippedFraction > 0.002;

                // Clipped fractions count both ends. A dark reading clipped at the black clamp is unusable now but
                // will rise into range at a longer exposure; only bright clipping means "skip from here on".
                lastCode[j] = clipped && luma > 100 ? 255 : luma;
                observations.Add(new BracketObservation(j, k, luma, clipped));
                readings.Add(new { patch = j, exposure = k, seconds = steps[k], mean = r.Mean, luma, clipped = r.MaxClippedFraction });
            }

            Ui.Info($"  run {run}: exposure {steps[k] * 1000:0.###} ms done ({observations.Count(o => o.Exposure == k && !o.Clipped && o.Code is > 6 and < 248)} usable)");
        }

        return Evaluate(run, observations, steps, c.Args.Get("anchors") ?? "hlines", readings);
    }

    /// <summary>The ramp's patches: the 17 grays (black first, white last) and the halftones with their anchor sets.</summary>
    private static (List<PatchSpec> Patches, List<(string Set, LightAnchor Anchor)> Anchors) BuildPatches()
    {
        var patches = new List<PatchSpec>(RampCodes.Select(v => (PatchSpec)new SolidPatch(DeviceRgb.Gray((byte)v))));
        var white = RampCodes.Count - 1;
        var black = 0;
        var halftones = new (string Set, HalftonePatch Patch, double OnFraction)[]
        {
            ("checker", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard), 0.5),
            ("checker", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 4), 0.25),
            ("checker", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 12), 0.75),
            ("checker", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 1), 1 / 16.0),
            ("hlines", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.HorizontalLines), 0.5),
            ("vlines", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.VerticalLines), 0.5),
        };
        var anchors = new List<(string Set, LightAnchor Anchor)>();
        foreach (var (set, patch, f) in halftones)
        {
            anchors.Add((set, new LightAnchor(patches.Count, [(white, f), (black, 1 - f)])));
            patches.Add(patch);
        }

        return (patches, anchors);
    }

    /// <summary>Fits the bracketed readings with every anchor set and returns the run with the primary set's numbers.</summary>
    private static RampRun Evaluate(int run, IReadOnlyList<BracketObservation> observations, IReadOnlyList<double> steps, string primaryName, object readings)
    {
        var (patches, anchors) = BuildPatches();
        var white = RampCodes.Count - 1;
        var black = 0;
        var options = new ResponseFitOptions();
        foreach (var (name, j) in new[] { ("black", black), ("white", white) })
        {
            if (!observations.Any(o => o.Patch == j && !o.Clipped && o.Code > options.MinCode && o.Code < options.MaxCode))
            {
                Ui.Warn($"  run {run}: display {name} was never inside the camera's usable range; the ramp cannot be normalized");
            }
        }

        var variants = new List<RampFitVariant>();
        foreach (var set in new[] { "hlines", "checker", "vlines", "none" })
        {
            var chosen = anchors.Where(a => a.Set == set).Select(a => a.Anchor).ToList();
            variants.Add(Analyze(set, observations, steps, patches, [.. anchors.Select(a => a.Anchor)], chosen, white, black, options));
        }

        var primary = variants.FirstOrDefault(v => v.Anchors == primaryName) ?? variants[0];
        var blackClamp = observations.Where(o => o.Patch == black && !o.Clipped).Select(o => o.Code).DefaultIfEmpty(double.NaN).Min();
        foreach (var v in variants)
        {
            Ui.Info($"  run {run}, anchors {v.Anchors,-7}: gamma {v.LogLogGamma:0.000} (log-log), {v.EffectiveGamma:0.000} (50 %); exponent {v.Exponent:0.000}; residual {v.ResidualRms:0.0000} ln; halftone light vs expected: {string.Join(", ", v.Additivity.Select(a => $"{a.Patch} {a.Deviation * 100:+0.0;-0.0} %"))}");
        }

        return new RampRun(run, primary.LogLogGamma, primary.EffectiveGamma, primary.Points, primary.Fit, blackClamp, primary.Anchors, variants, readings);
    }

    /// <summary>
    /// Re-fits the ramps recorded in a session from their saved readings (analysis is a pure function of the
    /// recording) and rewrites the ramp results, keeping the originals as ramp.runN.orig.json.
    /// </summary>
    public static int Reanalyze(string sessionDirectory, string primaryAnchors)
    {
        var results = Path.Combine(sessionDirectory, "results");
        var gammas = new List<double>();
        var effective = new List<double>();
        foreach (var file in Directory.GetFiles(results, "ramp.run*.json").Where(f => !f.EndsWith(".orig.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var run = root.GetProperty("run").GetInt32();
            var readings = root.GetProperty("readings").EnumerateArray().ToList();
            var steps = readings
                .Select(r => (Index: r.GetProperty("exposure").GetInt32(), Seconds: r.GetProperty("seconds").GetDouble()))
                .Distinct()
                .OrderBy(x => x.Index)
                .Select(x => x.Seconds)
                .ToList();
            var observations = readings.Select(r => new BracketObservation(
                r.GetProperty("patch").GetInt32(),
                r.GetProperty("exposure").GetInt32(),
                r.GetProperty("luma").GetDouble(),
                r.GetProperty("clipped").GetDouble() > 0.002)).ToList();
            var rerun = Evaluate(run, observations, steps, primaryAnchors, root.GetProperty("readings").Clone());
            var original = Path.ChangeExtension(file, ".orig.json");
            if (!File.Exists(original))
            {
                File.Copy(file, original);
            }

            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(rerun, HarnessSession.Json), new System.Text.UTF8Encoding(false));
            gammas.Add(rerun.LogLogGamma);
            effective.Add(rerun.EffectiveGamma);
        }

        if (gammas.Count == 0)
        {
            Ui.Warn($"no ramp results in {sessionDirectory}");
            return 1;
        }

        var aggregate = new { runs = gammas.Count, gammas, effectiveGammas = effective, gammaSpread = FeasibilityGate.Spread(gammas), effectiveGammaSpread = FeasibilityGate.Spread(effective), reanalyzed = true, anchors = primaryAnchors };
        File.WriteAllText(Path.Combine(results, "ramp.json"), System.Text.Json.JsonSerializer.Serialize(aggregate, HarnessSession.Json), new System.Text.UTF8Encoding(false));
        Ui.Good($"re-analysed {gammas.Count} ramp run(s): gamma {string.Join(", ", gammas.Select(g => Ui.F(g, "0.000")))}");
        return 0;
    }

    /// <summary>One fit of the bracketed data with a given set of halftone anchors.</summary>
    private static RampFitVariant Analyze(
        string name,
        IReadOnlyList<BracketObservation> observations,
        IReadOnlyList<double> steps,
        IReadOnlyList<PatchSpec> patches,
        IReadOnlyList<LightAnchor> allHalftones,
        IReadOnlyList<LightAnchor> anchors,
        int white,
        int black,
        ResponseFitOptions options)
    {
        var fit = ResponseFit.Fit(observations, steps, patches.Count, anchors.Count == 0 ? null : anchors, options);

        // A display black below the camera's floor at the longest exposure counts as zero light.
        var e0 = fit.PatchLight(black);
        var e255 = fit.PatchLight(white);
        var points = RampCodes.Select((v, j) => new RampPoint(v, ResponseFit.RelativeFraction(fit.PatchLight(j), e0, e255))).ToList();
        var slope = GammaAnalysis.LogLogSlope(points.Where(p => p.Code is > 0 and < 255).Select(p => (p.Code / 255.0, p.Fraction)));
        var curve = new MeasuredCurve([.. points.Select(p => p.Code / 255.0)], [.. points.Select(p => p.Fraction)]);
        var effective = GammaAnalysis.PointGamma(0.5, curve.ToLinear(0.5));
        var additivity = allHalftones.Select(a =>
        {
            var expected = a.Parts.Sum(p => p.Weight * fit.PatchLight(p.Patch));
            return new HalftoneAdditivity(MeasurementRig.Describe(patches[a.Patch]), (fit.PatchLight(a.Patch) / expected) - 1);
        }).ToList();
        return new RampFitVariant(name, slope, effective, fit.Exponent, fit.ResidualRms, fit.AnchorResidualRms, fit.StepRatios, points, additivity, fit.IsObserved(black), fit);
    }

    // ---------------------------------------------------------------- dark frames

    public static async Task DarkAsync(StepContext c)
    {
        Ui.Title("Dark frames (lens covered) at every exposure: black clamp");
        var readings = new List<object>();
        foreach (var exposure in c.Measure.ExposureSteps)
        {
            await c.Measure.SetExposureAsync(exposure, c.Token);
            var r = await c.Measure.ReadAsync(new SolidPatch(DeviceRgb.Black), ReadPolicy.Default with { Frames = 10, MaxClippedFraction = 1 }, c.Token);
            readings.Add(new { seconds = exposure, mean = r.Mean, noise = r.FrameStdDev, clipped = r.MaxClippedFraction });
            Ui.Info($"  {exposure * 1000,8:0.###} ms: R/G/B {r.Mean.X:0.00}/{r.Mean.Y:0.00}/{r.Mean.Z:0.00}, clipped-at-black fraction {r.MaxClippedFraction:P1}");
        }

        c.Session.Result("dark", new { readings });
    }
}

internal sealed record RampPoint(int Code, double Fraction);

/// <summary>How far a halftone's measured light is from the mix of its two colors' light (+0.01 = 1 % brighter).</summary>
internal sealed record HalftoneAdditivity(string Patch, double Deviation);

internal sealed record RampFitVariant(
    string Anchors,
    double LogLogGamma,
    double EffectiveGamma,
    double Exponent,
    double ResidualRms,
    double AnchorResidualRms,
    IReadOnlyList<double> StepRatios,
    IReadOnlyList<RampPoint> Points,
    IReadOnlyList<HalftoneAdditivity> Additivity,
    bool BlackObserved,
    [property: System.Text.Json.Serialization.JsonIgnore] ResponseFitResult Fit);

internal sealed record RampRun(
    int Run,
    double LogLogGamma,
    double EffectiveGamma,
    IReadOnlyList<RampPoint> Points,
    ResponseFitResult Fit,
    double BlackClampCode,
    string PrimaryAnchors,
    IReadOnlyList<RampFitVariant> Variants,
    object Readings);
