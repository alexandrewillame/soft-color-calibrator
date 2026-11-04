using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using SoftCal.Core.Color;
using SoftCal.Core.Displays;
using SoftCal.Core.Measurement;
using SoftCal.Core.ToneCurves;

namespace SoftCal.Core.Analysis;

/// <summary>How much a reported number can be trusted (shown next to it in the report).</summary>
public enum Confidence
{
    /// <summary>A null measurement: the camera's own response cancels.</summary>
    High,

    /// <summary>Relative camera readings with known limits.</summary>
    Medium,

    /// <summary>Depends on an assumed camera response; indicative only.</summary>
    Low,

    /// <summary>Taken from the monitor's EDID, not measured.</summary>
    ManufacturerData,
}

public enum FindingLevel
{
    Info,
    Warning,
    Problem,
}

/// <summary>A plain-language finding for the report.</summary>
public sealed record ReportFinding(FindingLevel Level, string Topic, string Message);

/// <summary>What the report needs to know besides the measurement. <c>Profile</c>: the SoftCal profile measured (its
/// monitor settings and calibration), by name.</summary>
public sealed record ReportContext(
    string DisplayName,
    string? DisplayKey,
    string InstrumentModel,
    Edid? Edid = null,
    double TargetGamma = 2.2,
    bool AngleSensitivePanel = false,
    string? Profile = null);

/// <summary>The tone curve: measured points (code 0–255, fraction of the black-to-white light) and its gamma.</summary>
public sealed record ToneSummary(
    IReadOnlyList<CurvePoint> Points,
    double EffectiveGamma,
    double AverageGamma,
    double LogLogSlope,
    double DeepestMeasuredCode,
    Confidence Confidence);

public readonly record struct CurvePoint(double Code, double Fraction);

/// <summary>Gray balance at one level of the gray tree, relative to the display's own white and black.</summary>
/// <param name="Code">The gray's code (where its luma matched the line pattern).</param>
/// <param name="Fraction">Its share of the black-to-white light.</param>
/// <param name="Balance">Light of red, green and blue at that gray, each relative to its own white, divided by their
/// average: (1, 1, 1) means the gray has the same tint as white.</param>
/// <param name="Uncertainty">One standard deviation of the balance, from the readings' noise.</param>
/// <param name="Tint">A name for the tint, or null when it is within 2 % and within the noise.</param>
public sealed record GrayBalancePoint(double Code, double Fraction, Vec3 Balance, double Uncertainty, string? Tint)
{
    /// <summary>The largest channel deviation from neutral.</summary>
    public double Imbalance => Math.Max(Math.Abs(Balance.X - 1), Math.Max(Math.Abs(Balance.Y - 1), Math.Abs(Balance.Z - 1)));
}

/// <summary>Each channel's own response at one gray of the tree.</summary>
/// <param name="Code">The gray's code (where its luma matched).</param>
/// <param name="Fraction">The gray's share of the black-to-white light (luma).</param>
/// <param name="ChannelFraction">Red, green and blue's shares of their own black-to-white light at that gray.</param>
/// <param name="Uncertainty">One standard deviation of the channel shares, relative.</param>
public sealed record ChannelResponsePoint(double Code, double Fraction, Vec3 ChannelFraction, double Uncertainty);

/// <summary>Gray balance and the red, green and blue channels' own half-light codes.</summary>
/// <param name="HalfLightCodes">Code at which each channel shown alone gives half its own light.</param>
/// <param name="ChannelGammas">Effective gamma of each channel shown alone.</param>
/// <param name="MidGrayBalance">Light of each channel at mid-gray, relative to the channel average (1 = neutral).</param>
/// <param name="GrayConsistencyCodes">How far the gray half-light code is from the luma-weighted channel half-light
/// codes. An additive display keeps it small; a larger value means the panel's channels interact, so colors shown
/// alone do not predict grays.</param>
/// <param name="GrayLevels">Gray balance at each level of the gray tree, from mid-gray down to the shadows. Empty for
/// measurements that lack each camera channel's match point; the balance then comes from the single-channel nulls.</param>
public sealed record ChannelSummary(
    Vec3 HalfLightCodes,
    Vec3 ChannelGammas,
    Vec3 MidGrayBalance,
    string? MidGrayTint,
    Confidence Confidence,
    double GrayConsistencyCodes = 0,
    IReadOnlyList<GrayBalancePoint>? GrayLevels = null);

/// <summary>Shadow and highlight steps. Null codes mean every step was distinguishable.</summary>
/// <param name="ChannelClipFromCode">The same as <paramref name="WhiteClipFromCode"/> for each camera channel (R, G, B):
/// a contrast or RGB gain set too high usually clips one channel before the others.</param>
public sealed record StepSummary(
    int? BlackCrushUpToCode,
    int? WhiteClipFromCode,
    bool LooksLikeRangeMismatch,
    Confidence Confidence,
    IReadOnlyList<int?>? ChannelClipFromCode = null)
{
    /// <summary>Some channel (or the overall level) stops changing before code 255.</summary>
    public bool AnyClipping => WhiteClipFromCode is not null || (ChannelClipFromCode?.Any(c => c is not null) ?? false);
}

/// <summary>Contrast ratio; a lower bound when the display's black is below what the camera can see.</summary>
public sealed record ContrastEstimate(double Ratio, bool IsLowerBound, Confidence Confidence)
{
    /// <summary>
    /// A ratio from an assumed camera response is not shown: on the BenQ it came out at 120 000:1 for a ~1000:1 TN
    /// panel. Only a lower bound (black below the camera's floor) is meaningful without exposure bracketing. A linear
    /// camera (a phone's raw data) needs no assumed response: its ratio is shown.
    /// </summary>
    public bool IsReported => IsLowerBound || Confidence != Confidence.Low;

    /// <remarks>A lower bound shows no number either: it would turn the camera's noise into light through the same
    /// assumed response (near black a webcam is a clamp and a steep toe), and came out at millions to one.</remarks>
    public override string ToString() => (IsReported, IsLowerBound) switch
    {
        (true, true) => "black below what the camera sees",
        (true, false) => string.Create(CultureInfo.InvariantCulture, $"{Ratio:0}:1"),
        _ => "not measured (Quick check)",
    };
}

/// <summary>Colorimetry the manufacturer put in the EDID (not measured).</summary>
public sealed record EdidColorimetry(RgbPrimaries Primaries, double WhiteCct, double WhiteDuv, double SrgbAreaRatio, double? DeclaredGamma);

/// <summary>The display status report; <c>Profile</c> is the SoftCal profile measured.</summary>
public sealed record DisplayReport(
    DateTimeOffset CreatedUtc,
    string DisplayName,
    string? DisplayKey,
    string InstrumentModel,
    ToneSummary Tone,
    ChannelSummary Channels,
    StepSummary Steps,
    ContrastEstimate Contrast,
    double? PatternSpreadCodes,
    EdidColorimetry? Edid,
    double MeasurementSeconds,
    IReadOnlyList<ReportFinding> Findings,
    string? Profile = null)
{
    /// <summary>Reports written before profiles named the picture mode instead: read as the profile (which kept the
    /// name), never written.</summary>
    [JsonPropertyName("pictureMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? LegacyPictureMode
    {
        get => null;
        init => Profile ??= value;
    }
}

/// <summary>Turns a <see cref="DisplayMeasurement"/> into a <see cref="DisplayReport"/>. Pure, so it is unit-tested.</summary>
public static class DisplayAnalysis
{
    /// <summary>Camera response assumed for the low-confidence contrast estimate.</summary>
    private const double AssumedCameraGamma = 2.2;

    /// <summary>Channel deviations below this are called neutral (about the smallest gray tint people notice).</summary>
    private const double TintTolerance = 0.02;

    /// <summary>Mid-gray balance noisier than this (one standard deviation) is not trusted for a verdict.</summary>
    private const double MaxBalanceUncertainty = 0.015;

    /// <summary>Darker grays are judged only while their balance is at least this precise.</summary>
    private const double MaxShadowBalanceUncertainty = 0.03;

    /// <summary>Darker grays are judged down to this share of white (≈ code 31 at γ 2.2); a tint in the grays below
    /// is hard to see and is left to the table.</summary>
    private const double MinJudgedFraction = 0.01;

    public static DisplayReport Analyze(DisplayMeasurement measurement, ReportContext context, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(context);
        var tone = Tone(measurement);
        var channels = Channels(measurement);
        var steps = Steps(measurement);
        var contrast = Contrast(measurement);
        double? patternSpread = measurement.CheckerboardHalf is { } checker && measurement.GrayTree.Count > 0
            ? Math.Abs(checker.MatchedCode - measurement.GrayTree[0].Null.MatchedCode)
            : null;
        var edid = context.Edid is { } e ? Colorimetry(e) : null;
        var findings = Findings(tone, channels, steps, contrast, patternSpread, edid, context, measurement.Content);
        return new DisplayReport(
            (clock ?? TimeProvider.System).GetUtcNow(),
            context.DisplayName,
            context.DisplayKey,
            context.InstrumentModel,
            tone,
            channels,
            steps,
            contrast,
            patternSpread,
            edid,
            measurement.TotalSeconds,
            findings,
            context.Profile);
    }

    public static ToneSummary Tone(DisplayMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        return Tone(measurement.GrayTree);
    }

    public static ToneSummary Tone(IReadOnlyList<TreeLevel> grayTree)
    {
        ArgumentNullException.ThrowIfNull(grayTree);
        if (grayTree.Count == 0)
        {
            throw new ArgumentException("The measurement has no tone curve.", nameof(grayTree));
        }

        var points = grayTree
            .Select(l => new CurvePoint(l.Null.MatchedCode, l.Fraction))
            .OrderBy(p => p.Code)
            .ToList();
        var curve = new MeasuredCurve(
            [0, .. points.Select(p => p.Code / 255), 1],
            [0, .. points.Select(p => p.Fraction), 1]);
        var deepest = points[0].Code;
        var averageFrom = Math.Clamp(Math.Max(0.1, (deepest / 255) + 0.01), 0.1, 0.5);
        return new ToneSummary(
            points,
            GammaAnalysis.EffectiveGamma(curve),
            GammaAnalysis.AverageGamma(curve, averageFrom, 0.9),
            GammaAnalysis.LogLogSlope(points.Select(p => (p.Code / 255, p.Fraction))),
            deepest,
            Confidence.High);
    }

    public static ChannelSummary Channels(DisplayMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        if (measurement.ChannelHalves.Count != 3)
        {
            throw new ArgumentException("The measurement needs one 50 % null per channel.", nameof(measurement));
        }

        var codes = new Vec3(measurement.ChannelHalves[0].MatchedCode, measurement.ChannelHalves[1].MatchedCode, measurement.ChannelHalves[2].MatchedCode);
        var gammas = codes.Map(c => Math.Log(0.5) / Math.Log(Math.Clamp(c, 1, 254) / 255));
        var gray = measurement.GrayTree.Count > 0 ? measurement.GrayTree[0].Null.MatchedCode : codes.Sum / 3;

        // Consistency: on an additive display the gray half-light code sits near the channels' (luma-weighted) ones.
        // The BenQ XL2720 (TN) misses by 18 codes, repeatably: its channels interact, so colors shown alone do not
        // predict grays, and gray balance is measured on the grays themselves.
        var consistency = measurement.GrayTree.Count > 0
            ? Math.Abs(gray - ((0.299 * codes.X) + (0.587 * codes.Y) + (0.114 * codes.Z)))
            : 0;

        var levels = GrayBalance(measurement);
        if (levels.Count > 0)
        {
            var mid = levels[0];
            var precise = mid.Uncertainty <= MaxBalanceUncertainty;
            return new ChannelSummary(codes, gammas, mid.Balance, precise ? mid.Tint : null, precise ? Confidence.High : Confidence.Low, consistency, levels);
        }

        // Measurements without each camera channel's match point: each channel's light at the gray code that gives half
        // the gray light, 0.5 · (gray / code_c)^γ_c, which is only right on an additive display.
        var light = new Vec3(
            Math.Pow(gray / codes.X, gammas.X),
            Math.Pow(gray / codes.Y, gammas.Y),
            Math.Pow(gray / codes.Z, gammas.Z));
        var balance = light / (light.Sum / 3);
        var trusted = consistency <= 3;
        return new ChannelSummary(codes, gammas, balance, trusted ? Tint(balance) : null, trusted ? Confidence.High : Confidence.Low, consistency);
    }

    /// <summary>
    /// Gray balance at every level of the gray tree, from where each camera channel matched. A gray whose red, green and
    /// blue each give the same share of their own white light as the line pattern matches at the same code in every
    /// camera channel, whatever the camera's response, white balance or color matrix; a channel that matches at a lower
    /// code is brighter at that gray. Deeper levels are matched against the previous gray rather than white, so their
    /// ratios (and their noise) chain from level to level.
    /// </summary>
    public static IReadOnlyList<GrayBalancePoint> GrayBalance(DisplayMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        return GrayBalance(measurement.GrayTree);
    }

    /// <inheritdoc cref="GrayBalance(DisplayMeasurement)"/>
    public static IReadOnlyList<GrayBalancePoint> GrayBalance(IReadOnlyList<TreeLevel> grayTree) =>
        [.. ChannelResponse(grayTree).Select(p =>
        {
            var balance = p.ChannelFraction / (p.ChannelFraction.Sum / 3);
            return new GrayBalancePoint(p.Code, p.Fraction, balance, p.Uncertainty, Tint(balance, Math.Max(TintTolerance, 2 * p.Uncertainty)));
        })];

    /// <summary>
    /// Each channel's own response at the gray tree's codes: the share of that channel's black-to-white light at each
    /// matched gray, from where each camera channel matched (see <see cref="GrayBalance(DisplayMeasurement)"/>). This is
    /// what calibration curves are solved from.
    /// </summary>
    public static IReadOnlyList<ChannelResponsePoint> ChannelResponse(IReadOnlyList<TreeLevel> grayTree)
    {
        ArgumentNullException.ThrowIfNull(grayTree);
        var tree = grayTree.OrderBy(l => l.Level).ToList();

        // The gray curve with white on top, for each level's local log-log slope.
        var curve = tree.Select(l => (Code: l.Null.MatchedCode, l.Fraction)).Prepend((Code: 255.0, Fraction: 1.0)).ToList();
        double Slope(int i)
        {
            var (upper, lower) = (curve[i - 1], i + 1 < curve.Count ? curve[i + 1] : curve[i]);
            var slope = Math.Log(upper.Fraction / lower.Fraction) / Math.Log(upper.Code / lower.Code);
            return double.IsFinite(slope) && slope > 0 ? slope : Math.Log(curve[i].Fraction) / Math.Log(curve[i].Code / 255);
        }

        var points = new List<ChannelResponsePoint>(tree.Count);
        var ratio = Vec3.One;
        var variance = Vec3.Zero;
        for (var i = 0; i < tree.Count; i++)
        {
            var level = tree[i];
            var gray = level.Null.MatchedCode;
            if (level.Null.Repeats.Count == 0 || gray is <= 1 or >= 254 || level.Fraction is <= 0 or >= 1)
            {
                break;
            }

            var codes = level.Null.MatchedCodePerChannel;
            var errors = level.Null.MatchedCodeErrorPerChannel;
            if (!(double.IsFinite(codes.Sum) && codes.Min > 0))
            {
                break;
            }

            // Each channel reaches the reference's light at its own code m_c; at the gray's code it gives (gray / m_c)^γ
            // times that, γ being the curve's local slope.
            var gamma = Slope(i + 1);
            ratio = ratio.Scale(codes.Map(m => Math.Pow(gray / m, gamma)));
            var relative = errors.DivideBy(codes) * gamma;
            variance += relative.Scale(relative);
            points.Add(new ChannelResponsePoint(gray, level.Fraction, ratio * level.Fraction, Math.Sqrt(variance.Max)));
        }

        return points;
    }

    /// <summary>
    /// A name for a tint whose largest channel deviation exceeds <paramref name="threshold"/>, from the direction of
    /// the deviation on a hue circle (red 0°, yellow 60°, green 120°, cyan 180°, blue 240°, magenta 300°; ±30° each).
    /// Red up with blue down is therefore "yellowish" or "reddish" by which dominates, not by which is checked first.
    /// </summary>
    public static string? Tint(Vec3 balance, double threshold = TintTolerance)
    {
        var d = balance - (Vec3.One * (balance.Sum / 3));
        if (Math.Max(Math.Abs(balance.X - 1), Math.Max(Math.Abs(balance.Y - 1), Math.Abs(balance.Z - 1))) <= threshold)
        {
            return null;
        }

        var hue = Math.Atan2(Math.Sqrt(3) / 2 * (d.Y - d.Z), d.X - ((d.Y + d.Z) / 2)) * 180 / Math.PI;
        var sector = (int)Math.Floor((hue + 360 + 30) % 360 / 60);
        return sector switch
        {
            0 => "reddish",
            1 => "yellowish",
            2 => "greenish",
            3 => "cyan",
            4 => "bluish",
            _ => "magenta",
        };
    }

    public static StepSummary Steps(DisplayMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        return Steps(measurement.NearBlack, measurement.NearWhite);
    }

    /// <summary>Black crush from <paramref name="nearBlack"/> and white clipping from <paramref name="nearWhite"/>
    /// (either may be empty when only one end was measured).</summary>
    public static StepSummary Steps(IReadOnlyList<StepReading> nearBlack, IReadOnlyList<StepReading> nearWhite)
    {
        ArgumentNullException.ThrowIfNull(nearBlack);
        ArgumentNullException.ThrowIfNull(nearWhite);
        static double Luma(Vec3 v) => (0.299 * v.X) + (0.587 * v.Y) + (0.114 * v.Z);
        static double Noise(StepReading r) => Math.Sqrt(Math.Pow(0.299 * r.StdError.X, 2) + Math.Pow(0.587 * r.StdError.Y, 2) + Math.Pow(0.114 * r.StdError.Z, 2));
        static bool Distinct(StepReading a, StepReading b) =>
            Math.Abs(Luma(a.Mean) - Luma(b.Mean)) > (3 * Math.Sqrt((Noise(a) * Noise(a)) + (Noise(b) * Noise(b)))) + 0.3;
        static bool DistinctIn(int c, StepReading a, StepReading b, double floor = 0.3) =>
            Math.Abs(a.Mean[c] - b.Mean[c]) > (3 * Math.Sqrt((a.StdError[c] * a.StdError[c]) + (b.StdError[c] * b.StdError[c]))) + floor;

        // Near black the camera works at its longest exposure, where a step can rise clearly in red and green while the
        // color correction pulls blue down, so the luma alone hides it (BenQ XL2720, code 6: R +0.51, G +0.34, B −0.20,
        // noise ≈ 0.02; luma +0.33 against a 0.3 floor). A step is visible when the luma or any channel separates.
        static bool DistinctNearBlack(StepReading a, StepReading b) =>
            Distinct(a, b) || DistinctIn(0, a, b, 0.15) || DistinctIn(1, a, b, 0.15) || DistinctIn(2, a, b, 0.15);

        int? crush = null;
        var blacks = nearBlack.OrderBy(r => r.Code).ToList();
        if (blacks.Count > 1 && blacks[0].Code == 0)
        {
            foreach (var step in blacks.Skip(1))
            {
                if (DistinctNearBlack(step, blacks[0]))
                {
                    break;
                }

                crush = step.Code;
            }
        }

        int? ClipFrom(Func<StepReading, StepReading, bool> distinct)
        {
            int? from = null;
            var whites = nearWhite.OrderByDescending(r => r.Code).ToList();
            if (whites.Count > 1 && whites[0].Code == 255)
            {
                foreach (var step in whites.Skip(1))
                {
                    if (distinct(step, whites[0]))
                    {
                        break;
                    }

                    from = step.Code;
                }
            }

            return from;
        }

        var clip = ClipFrom(Distinct);
        int?[] channelClip = [ClipFrom((a, b) => DistinctIn(0, a, b)), ClipFrom((a, b) => DistinctIn(1, a, b)), ClipFrom((a, b) => DistinctIn(2, a, b))];
        var mismatch = crush >= 12 && clip <= 240;
        return new StepSummary(crush, clip, mismatch, Confidence.High, nearWhite.Count > 0 ? channelClip : null);
    }

    public static ContrastEstimate Contrast(DisplayMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        static double Luma(Vec3 v) => (0.299 * v.X) + (0.587 * v.Y) + (0.114 * v.Z);
        var floor = Luma(measurement.BlackAtShortestExposure.Mean);
        var black = measurement.BlackForContrast;
        var white = measurement.WhiteForContrast;
        var cameraGamma = measurement.LinearCamera ? 1 : AssumedCameraGamma;
        double Linear(double luma) => Math.Pow(Math.Clamp((luma - floor) / (255 - floor), 0, 1), cameraGamma);
        var noise = 3 * black.ChannelError(3) + 0.5;
        var signal = Luma(black.Mean) - floor;
        var lowerBound = signal <= noise;
        var blackLinear = Linear(floor + Math.Max(signal, noise)) / Math.Max(black.ExposureSeconds, 1e-9);
        var whiteLinear = Linear(Luma(white.Mean)) / Math.Max(white.ExposureSeconds, 1e-9);
        var ratio = blackLinear > 0 ? whiteLinear / blackLinear : double.PositiveInfinity;
        return new ContrastEstimate(ratio, lowerBound, measurement.LinearCamera ? Confidence.Medium : Confidence.Low);
    }

    public static EdidColorimetry Colorimetry(Edid edid)
    {
        ArgumentNullException.ThrowIfNull(edid);
        var p = edid.Chromaticities;
        var t = ColorTemperature.FromChromaticity(p.White);
        return new EdidColorimetry(p, t.Kelvin, t.Duv, p.AreaUvPrime() / RgbPrimaries.Srgb.AreaUvPrime(), edid.Gamma);
    }

    public static IReadOnlyList<ReportFinding> Findings(
        ToneSummary tone,
        ChannelSummary channels,
        StepSummary steps,
        ContrastEstimate contrast,
        double? patternSpread,
        EdidColorimetry? edid,
        ReportContext context,
        ContentResponse? content = null)
    {
        ArgumentNullException.ThrowIfNull(tone);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(contrast);
        ArgumentNullException.ThrowIfNull(context);
        string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
        var findings = new List<ReportFinding>();

        if (content is { ChangesWithContent: true })
        {
            findings.Add(new(
                FindingLevel.Problem,
                "Picture-dependent brightness",
                F($"The monitor changes its light with what is on screen: the same gray gave {Math.Abs(content.FirstDifference):0.0%} {(content.FirstDifference >= 0 ? "more" : "less")} light with the screen around it white than black, and drifted {content.Drift:0.0%} while it settled. Dynamic contrast, ECO, a light sensor or a similar feature is on. Turn it off in the monitor's menu, then measure again: every other number here may be off.")));
        }
        else if (content is not null)
        {
            findings.Add(new(FindingLevel.Info, "Picture-dependent brightness", "The monitor keeps a steady light whatever is on screen (no dynamic contrast detected)."));
        }

        var gammaError = tone.EffectiveGamma - context.TargetGamma;
        findings.Add(Math.Abs(gammaError) <= 0.1
            ? new(FindingLevel.Info, "Gamma", F($"Effective gamma {tone.EffectiveGamma:0.00}, close to the {context.TargetGamma:0.0} target."))
            : new(
                Math.Abs(gammaError) > 0.3 ? FindingLevel.Problem : FindingLevel.Warning,
                "Gamma",
                F($"Effective gamma {tone.EffectiveGamma:0.00} (target {context.TargetGamma:0.0}): mid-tones look {(gammaError > 0 ? "too dark and contrasty" : "too light and flat")}.")));

        if (channels.GrayLevels is { Count: > 0 } grays)
        {
            GrayFindings(findings, channels, grays);
        }
        else if (channels.Confidence == Confidence.Low)
        {
            findings.Add(new(
                FindingLevel.Warning,
                "Gray balance",
                F($"Not reliable this time: the red, green and blue measurements disagree with the gray one by {channels.GrayConsistencyCodes:0.0} codes (more than 3), so no tint verdict is given. Measure again with this version to judge it on the grays themselves.")));
        }
        else if (channels.MidGrayTint is { } tint)
        {
            var worst = new[] { channels.MidGrayBalance.X, channels.MidGrayBalance.Y, channels.MidGrayBalance.Z }.Max(v => Math.Abs(v - 1));
            findings.Add(new(
                worst > 0.06 ? FindingLevel.Problem : FindingLevel.Warning,
                "Gray balance",
                F($"Mid-grays are {tint} compared with white ({worst:P0} channel imbalance); the channel gammas are R {channels.ChannelGammas.X:0.00}, G {channels.ChannelGammas.Y:0.00}, B {channels.ChannelGammas.Z:0.00}.")));
        }

        if (steps.LooksLikeRangeMismatch)
        {
            findings.Add(new(
                FindingLevel.Problem,
                "RGB range",
                F($"Codes up to {steps.BlackCrushUpToCode} look black and codes from {steps.WhiteClipFromCode} look white: the GPU and the monitor disagree on Full/Limited range.")));
        }
        else
        {
            if (steps.BlackCrushUpToCode is { } crush)
            {
                // Code 4 gives about 0.01 % of white at γ 2.2: at the limit of what the camera separates, so merging it
                // with black is reported but not called a fault.
                findings.Add(crush >= 6
                    ? new(FindingLevel.Warning, "Shadows", F($"Codes 0–{crush} cannot be told apart: shadow detail is crushed (brightness/black level too low, or a black-boost setting)."))
                    : new(FindingLevel.Info, "Shadows", F($"Codes 0–{crush} look the same as black. Code 4 gives only about 0.01 % of white, so this is at the limit of what the camera can separate; code 6 and up are distinct.")));
            }

            if (steps.WhiteClipFromCode is { } clip)
            {
                findings.Add(new(FindingLevel.Warning, "Highlights", F($"Codes {clip}–255 cannot be told apart: highlights are clipped (contrast too high).")));
            }
            else if (steps.ChannelClipFromCode?.Select((c, i) => (Code: c, Name: new[] { "red", "green", "blue" }[i])).Where(c => c.Code is not null).ToList() is { Count: > 0 } clipped)
            {
                var names = string.Join(" and ", clipped.Select(c => F($"{c.Name} (from code {c.Code})")));
                findings.Add(new(FindingLevel.Warning, "Highlights", $"The brightest steps merge in {names}: near-whites take a tint (contrast, or that channel's RGB gain, too high)."));
            }
        }

        if (contrast.IsLowerBound)
        {
            findings.Add(new(FindingLevel.Info, "Contrast", "The display's black is below what the camera can see at its longest exposure, so the contrast ratio is not measured."));
        }

        if (patternSpread is > 1)
        {
            findings.Add(new(FindingLevel.Warning, "Panel", F($"A checkerboard and line patterns disagree by {patternSpread:0.0} codes: neighbouring pixels leak into each other, so only line patterns are used.")));
        }

        if (context.AngleSensitivePanel)
        {
            findings.Add(new(FindingLevel.Info, "Panel", "This panel changes with viewing angle (TN): results hold for the camera's position at the screen centre."));
        }

        if (edid is not null)
        {
            findings.Add(new(
                FindingLevel.Info,
                "White point",
                F($"The manufacturer declares a {edid.WhiteCct:0} K white (Δuv {edid.WhiteDuv:+0.000;-0.000}) and a gamut {edid.SrgbAreaRatio:P0} the size of sRGB (EDID data, not measured).")));
        }

        return findings;
    }

    private static void GrayFindings(List<ReportFinding> findings, ChannelSummary channels, IReadOnlyList<GrayBalancePoint> grays)
    {
        string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
        var mid = grays[0];
        if (mid.Uncertainty > MaxBalanceUncertainty)
        {
            findings.Add(new(
                FindingLevel.Warning,
                "Gray balance",
                F($"Too noisy to judge this time (±{mid.Uncertainty:P1} per channel). Dim the room or improve the hood around the camera, then measure again.")));
            return;
        }

        if (mid.Tint is { } tint)
        {
            findings.Add(new(
                mid.Imbalance > 0.06 ? FindingLevel.Problem : FindingLevel.Warning,
                "Gray balance",
                F($"Mid-grays are {tint} compared with white ({mid.Imbalance:P0} channel imbalance at code {mid.Code:0}).")));
        }

        // Darker grays that are precise enough to judge: name the strongest tint if it is new or clearly stronger.
        var judged = grays.Skip(1).Where(p => p.Uncertainty <= MaxShadowBalanceUncertainty && p.Fraction >= MinJudgedFraction).ToList();
        var shadow = judged.Where(p => p.Tint is not null).OrderByDescending(p => p.Imbalance).FirstOrDefault();
        if (shadow is not null && (shadow.Tint != mid.Tint || shadow.Imbalance > mid.Imbalance + TintTolerance))
        {
            findings.Add(new(
                shadow.Imbalance > 0.1 ? FindingLevel.Problem : FindingLevel.Warning,
                "Gray balance",
                F($"Darker grays (around code {shadow.Code:0}) are {shadow.Tint} ({shadow.Imbalance:P0} channel imbalance).")));
        }

        if (mid.Tint is null && shadow is null)
        {
            var deepest = judged.Count > 0 ? judged[^1].Code : mid.Code;
            findings.Add(new(FindingLevel.Info, "Gray balance", F($"Grays are neutral from white down to code {deepest:0}: they keep white's tint within {TintTolerance:P0}.")));
        }

        if (channels.GrayConsistencyCodes > 3)
        {
            findings.Add(new(
                FindingLevel.Info,
                "Panel",
                F($"Red, green and blue shown alone do not add up to gray (their half-light codes are {channels.GrayConsistencyCodes:0.0} codes from gray's): the panel's channels interact. Gray balance is measured on grays, so it is unaffected; the channel gammas describe single colors only.")));
        }
    }
}
