using System.Diagnostics;
using System.Globalization;
using SoftCal.Core.Color;

namespace SoftCal.Core.Measurement;

/// <summary>How the Quick plan runs. The defaults follow the M1 report: horizontal-line halftones (TN panels show
/// horizontal crosstalk with checkerboards), one palindrome per null, frames capped by integrated exposure.</summary>
public sealed record QuickPlanOptions
{
    public HalftoneGeometry Geometry { get; init; } = HalftoneGeometry.HorizontalLines;

    /// <summary>The gray tree stops once a matched code falls to this (the deep shadows).</summary>
    public double StopBelowCode { get; init; } = 22;

    public int MaxTreeLevels { get; init; } = 10;

    /// <summary>Also match a checkerboard against the lines at 50 %: a disagreement means the panel is not additive.</summary>
    public bool PatternCheck { get; init; } = true;

    /// <summary>First check whether the monitor changes its light with what is on screen (dynamic contrast and the
    /// like): the same gray read after a white screen and after a black one. About 10 seconds.</summary>
    public bool ContentCheck { get; init; } = true;

    /// <summary>Match each gray in every camera channel inside that channel's own pair of codes (see
    /// <see cref="NullOptions.BracketEveryChannel"/>). Costs an extra palindrome on levels where channels diverge.</summary>
    public bool BracketEveryChannel { get; init; } = true;

    public NullOptions Nulls { get; init; } = new()
    {
        Repeats = 1,
        Coarse = ReadPolicy.Default with { Frames = 4, MinFrames = 2, TargetIntegrationSeconds = 0.25, MaxClippedFraction = 1, SettleMarginSeconds = 0.06 },
        Fine = ReadPolicy.Default with { Frames = 8, MinFrames = 3, TargetIntegrationSeconds = 0.5, SettleMarginSeconds = 0.06 },
    };

    public ReadPolicy Readings { get; init; } = ReadPolicy.Default with { Frames = 8, MinFrames = 3, TargetIntegrationSeconds = 0.4, SettleMarginSeconds = 0.06 };

    /// <summary>Codes read at one exposure to find black crush. It starts at 4: on a correct γ 2.2 display code 2 emits
    /// only 0.002 % of white, so it is legitimately indistinguishable from black (PLUGE-style tests start near 4–5).</summary>
    public IReadOnlyList<int> NearBlackCodes { get; init; } = [0, 4, 6, 8, 12, 16, 20];

    /// <summary>Codes read at one exposure to find white clipping.</summary>
    public IReadOnlyList<int> NearWhiteCodes { get; init; } = [235, 240, 245, 249, 252, 255];
}

/// <summary>A solid patch read at a fixed exposure, for the step tests.</summary>
public sealed record StepReading(int Code, Vec3 Mean, Vec3 StdError, double ExposureSeconds);

/// <summary>How long a phase took, so the plan can be held to the user's time budget.</summary>
public sealed record PhaseTiming(string Name, double Seconds);

/// <summary>
/// The same gray patch read repeatedly with the rest of the screen white, then black, at one exposure (luma, instrument
/// units). A steady monitor reads the same both times; one whose backlight follows the picture (dynamic contrast,
/// "smart" brightness) reads brighter with the white surround, dimmer with the black one, and drifts as it adapts.
/// </summary>
/// <param name="Linear">The readings are proportional to the light (a phone's raw data); otherwise they are encoded
/// (γ ≈ 1/2.2), where a change of the light shows about 2.2 times smaller.</param>
public sealed record ContentResponse(IReadOnlyList<double> AfterWhite, IReadOnlyList<double> AfterBlack, double ExposureSeconds, bool Linear = false)
{
    /// <summary>A change of the light of 4 % or more: far above the readings' noise (about 0.1 %), far below what
    /// dynamic contrast and the like do (tens of percent).</summary>
    public const double Limit = 0.04;

    /// <summary>How much less an encoded reading moves than the light.</summary>
    private const double EncodedSlope = 1 / 2.2;

    /// <summary>The first readings' difference (white surround minus black), as a share of the gray's light.</summary>
    public double FirstDifference => Light(AfterWhite[0] - AfterBlack[0]);

    /// <summary>The larger drift from the first to the last reading of either sequence, as a share of the light.</summary>
    public double Drift => Math.Max(Math.Abs(Light(AfterWhite[^1] - AfterWhite[0])), Math.Abs(Light(AfterBlack[^1] - AfterBlack[0])));

    public bool ChangesWithContent => Math.Abs(FirstDifference) >= Limit || Drift >= Limit;

    private double Light(double difference)
    {
        var mean = AfterWhite.Concat(AfterBlack).Average();
        return mean > 0 ? difference / mean / (Linear ? 1 : EncodedSlope) : 0;
    }
}

/// <summary>Everything the Quick plan measured, in instrument units; analysis turns it into a report.
/// <c>LinearCamera</c>: the readings are proportional to the light (a phone's raw data), not encoded.</summary>
public sealed record DisplayMeasurement(
    HalftoneGeometry Geometry,
    IReadOnlyList<TreeLevel> GrayTree,
    IReadOnlyList<NullResult> ChannelHalves,
    NullResult? CheckerboardHalf,
    IReadOnlyList<StepReading> NearBlack,
    IReadOnlyList<StepReading> NearWhite,
    Reading WhiteForContrast,
    Reading BlackForContrast,
    Reading BlackAtShortestExposure,
    IReadOnlyList<PhaseTiming> Phases,
    ContentResponse? Content = null,
    bool LinearCamera = false)
{
    public double TotalSeconds => Phases.Sum(p => p.Seconds);
}

/// <summary>
/// The Quick measurement (≈ 5 minutes on the C920e): a gray midpoint tree down to the shadows, a 50 % null per
/// channel for gray balance, an optional pattern-shape check, near-black and near-white steps for crush and clipping,
/// and white/black readings for a contrast estimate. Works with any <see cref="Instruments.IMeasurementInstrument"/>.
/// </summary>
public sealed class QuickPlan(MeasurementRig rig, QuickPlanOptions? options = null)
{
    private readonly MeasurementRig _rig = rig ?? throw new ArgumentNullException(nameof(rig));
    private readonly QuickPlanOptions _options = options ?? new QuickPlanOptions();

    public async Task<DisplayMeasurement> RunAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var o = _options;
        var phases = new List<PhaseTiming>();
        var matcher = new NullMatcher(_rig);
        var watch = Stopwatch.StartNew();
        void Done(string phase)
        {
            phases.Add(new PhaseTiming(phase, watch.Elapsed.TotalSeconds));
            watch.Restart();
        }

        ContentResponse? content = null;
        if (o.ContentCheck)
        {
            progress?.Report("Content check: does the monitor change its brightness with what is on screen?");
            content = await ContentCheckAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(content.ChangesWithContent
                ? string.Create(CultureInfo.InvariantCulture, $"  Yes: the same gray gives {Math.Abs(content.FirstDifference):0.0%} {(content.FirstDifference >= 0 ? "more" : "less")} light with the screen around it white than black, and drifts {content.Drift:0.0%} as it settles (dynamic contrast or similar is on).")
                : "  No: the same gray gives the same light with the screen around it white or black.");
            Done("content check");
        }

        progress?.Report("Tone curve: matching grays against line patterns, from white down to the shadows");
        var tree = await MidpointTree.RunAsync(
            matcher,
            o.MaxTreeLevels,
            o.Nulls with { Channel = 3, BracketEveryChannel = o.BracketEveryChannel },
            DeviceRgb.Gray,
            node => progress?.Report(string.Create(
                CultureInfo.InvariantCulture,
                $"  1/{1L << node.Level} of white → code {node.Null.MatchedCode:0.0}{(node.Null.MovementRetries > 0 ? $" (read again {node.Null.MovementRetries}× because the camera or the screen moved: keep the tube against the screen)" : string.Empty)}")),
            cancellationToken,
            o.StopBelowCode,
            o.Geometry).ConfigureAwait(false);
        Done("tone curve");

        progress?.Report("Gray balance: the half-light level of red, green and blue");
        var guess = tree.Count > 0 ? tree[0].Null.MatchedCode : MidpointTree.Guess(0.5);
        var channels = new List<NullResult>(3);
        foreach (var channel in new[] { 0, 1, 2 })
        {
            var reference = new HalftonePatch(DeviceRgb.Primary(channel, 255), DeviceRgb.Black, o.Geometry);
            channels.Add(await matcher.MatchAsync(
                "RGB"[channel] + " 50 %",
                reference,
                code => new SolidPatch(DeviceRgb.Primary(channel, (byte)code)),
                guess,
                o.Nulls with { Channel = channel },
                cancellationToken).ConfigureAwait(false));
        }

        Done("gray balance");

        NullResult? checker = null;
        if (o.PatternCheck && o.Geometry != HalftoneGeometry.Checkerboard)
        {
            progress?.Report("Pattern check: does the panel mix a checkerboard the same way as lines?");
            checker = await matcher.MatchAsync(
                "checkerboard 50 %",
                new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard),
                code => new SolidPatch(DeviceRgb.Gray((byte)code)),
                guess,
                o.Nulls with { Channel = 3 },
                cancellationToken).ConfigureAwait(false);
            Done("pattern check");
        }

        progress?.Report("Shadows and highlights: are the darkest and brightest steps distinguishable?");
        var nearBlack = await StepTest.ReadAsync(_rig, o.NearBlackCodes, o.Readings, cancellationToken).ConfigureAwait(false);
        var nearWhite = await StepTest.ReadAsync(_rig, o.NearWhiteCodes, o.Readings, cancellationToken).ConfigureAwait(false);
        Done("steps");

        progress?.Report("Contrast: white, then black at the longest exposure");
        await _rig.AutoExposeAsync(new SolidPatch(DeviceRgb.White), 180, cancellationToken).ConfigureAwait(false);
        var contrastPolicy = o.Readings with { MaxClippedFraction = 1 };
        var white = await _rig.ReadAsync(new SolidPatch(DeviceRgb.White), contrastPolicy, cancellationToken).ConfigureAwait(false);
        var exposures = _rig.Instrument.Capabilities.ExposureSteps;
        await _rig.SetExposureAsync(exposures.Count > 0 ? exposures[0] : _rig.Instrument.Capabilities.MinExposureSeconds, cancellationToken).ConfigureAwait(false);
        var floor = await _rig.ReadAsync(new SolidPatch(DeviceRgb.Black), contrastPolicy, cancellationToken).ConfigureAwait(false);
        await _rig.SetExposureAsync(exposures.Count > 0 ? exposures[^1] : _rig.Instrument.Capabilities.MaxExposureSeconds, cancellationToken).ConfigureAwait(false);
        var black = await _rig.ReadAsync(new SolidPatch(DeviceRgb.Black), contrastPolicy, cancellationToken).ConfigureAwait(false);
        Done("contrast");

        return new DisplayMeasurement(o.Geometry, tree, channels, checker, nearBlack, nearWhite, white, black, floor, phases, content, _rig.Instrument.Capabilities.OutputIsLinear);
    }

    private Task<ContentResponse> ContentCheckAsync(CancellationToken cancellationToken) =>
        ContentCheck.RunAsync(_rig, _options.Readings, cancellationToken);
}

/// <summary>Whether the monitor changes its brightness with what is on screen (dynamic contrast, ECO, light sensor).</summary>
public static class ContentCheck
{
    /// <summary>
    /// A large gray patch with the rest of the screen white for 2 s, then the gray read six times (about 2 s); the same
    /// with the rest black. The camera only ever sees the gray, so a camera that adapts to what it sees (a phone's own
    /// tone curve) changes nothing here; the monitor sees its picture go from nearly white to nearly black, which is
    /// what dynamic contrast and the like follow. One exposure throughout. About 10 seconds.
    /// </summary>
    public static async Task<ContentResponse> RunAsync(MeasurementRig rig, ReadPolicy readings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(readings);
        var gray = DeviceRgb.Gray(160);
        await rig.AutoExposeAsync(new SolidPatch(gray), 140, cancellationToken).ConfigureAwait(false);
        var hold = readings with { Frames = 60, MinFrames = 30, TargetIntegrationSeconds = 2, MaxClippedFraction = 1 };
        var read = readings with { Frames = 8, MinFrames = 4, TargetIntegrationSeconds = 0.25 };
        async Task<IReadOnlyList<double>> Within(DeviceRgb surround)
        {
            // Three quarters of the screen's height: the camera's view stays well inside it wherever it rests on the
            // aiming area, while the surround still makes most of the screen.
            var patch = new SolidPatch(gray) { Layout = new PatchLayout(0.5, 0.5, 0.75, surround) };
            await rig.ReadAsync(patch, hold, cancellationToken).ConfigureAwait(false);
            var values = new List<double>(6);
            for (var i = 0; i < 6; i++)
            {
                values.Add((await rig.ReadAsync(patch, read, cancellationToken).ConfigureAwait(false)).Channel(3));
            }

            return values;
        }

        var withWhite = await Within(DeviceRgb.White).ConfigureAwait(false);
        var withBlack = await Within(DeviceRgb.Black).ConfigureAwait(false);
        return new ContentResponse(withWhite, withBlack, rig.ExposureSeconds, rig.Instrument.Capabilities.OutputIsLinear);
    }
}

/// <summary>Solid grays read at one exposure, to see which neighbouring steps the display keeps apart.</summary>
public static class StepTest
{
    /// <summary>Exposes on the brightest code, then reads every code at that exposure.</summary>
    public static async Task<IReadOnlyList<StepReading>> ReadAsync(MeasurementRig rig, IReadOnlyList<int> codes, ReadPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(policy);
        await rig.AutoExposeAsync(new SolidPatch(DeviceRgb.Gray((byte)codes.Max())), 200, cancellationToken).ConfigureAwait(false);
        var accept = policy with { MaxClippedFraction = 1 };
        var readings = new List<StepReading>(codes.Count);
        foreach (var code in codes)
        {
            var r = await rig.ReadAsync(new SolidPatch(DeviceRgb.Gray((byte)code)), accept, cancellationToken).ConfigureAwait(false);
            readings.Add(new StepReading(code, r.Mean, r.StdError, r.ExposureSeconds));
        }

        return readings;
    }
}
