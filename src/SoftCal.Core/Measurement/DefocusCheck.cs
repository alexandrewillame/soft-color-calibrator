using SoftCal.Core.Instruments;

namespace SoftCal.Core.Measurement;

/// <summary>
/// How much of a pattern of 1-pixel lines reaches the camera as lines: the spread of the region's pixels (green, the
/// channel that carries most of the light) for the lines and for a solid gray of about their light, at one exposure, and
/// the lines' clipped fraction. The solid's spread is the camera's own noise; whatever the lines add is their ripple.
/// <c>Linear</c>: the camera's values are proportional to the light (a phone's raw data).
/// </summary>
public sealed record DefocusResult(double SolidMean, double SolidSpread, double LinesMean, double LinesSpread, double LinesClipped, bool Linear = false)
{
    /// <summary>How much a γ 1/2.2 curve shrinks a small ripple of the light.</summary>
    private const double CurveSlope = 1 / 2.2;

    /// <summary>
    /// The most ripple (relative RMS) the halftone readings tolerate. Ripple r biases a halftone's reading through the
    /// camera's curve by about −0.27 r² of its light on a γ 1/2.2 camera: 0.6 % at 15 %, the same at every level and in
    /// every channel, so gamma and gray balance barely move. A camera that sees the lines shows tens of percent.
    /// </summary>
    public const double MaxRipple = 0.15;

    /// <summary>The most clipped pixels allowed in the lines: a clipped pixel's light is lost, which no blur undoes.</summary>
    public const double MaxClipped = 0.002;

    /// <summary>The lines' ripple relative to their mean, the camera's noise taken out, in the camera's own values.</summary>
    public double MeasuredRipple => LinesMean <= 0
        ? double.PositiveInfinity
        : Math.Sqrt(Math.Max(0, (LinesSpread * LinesSpread) - (SolidSpread * SolidSpread))) / LinesMean;

    /// <summary>The ripple as a γ 1/2.2 camera sees it: a linear camera's is about 2.2 times what the same blur shows
    /// through the curve, so it is scaled down, and one limit holds the same blur on both kinds. (A linear camera's
    /// readings take no bias from ripple; the limit then keeps the lines well mixed over the region, as before.)</summary>
    public double Ripple => Linear ? MeasuredRipple * CurveSlope : MeasuredRipple;

    /// <summary>The screen's pixels reach the camera mixed enough for the halftone readings.</summary>
    public bool Blurred => Ripple <= MaxRipple && LinesClipped <= MaxClipped;
}

/// <summary>
/// Checks that the camera sees the screen blurred. Every halftone reading counts on the light of its 1-pixel elements
/// reaching the camera already mixed, before the camera's nonlinear response; a camera that sees them sharply reads
/// them wrong, and its dark lines clip. A phone cannot always be trusted to report where its lens is, so the picture
/// itself is asked. About 2 s.
/// </summary>
public static class DefocusCheck
{
    /// <summary>Lines of this code over black (half the pixels lit)…</summary>
    private const byte LinesLevel = 194;

    /// <summary>…emit about the light of this solid gray on a γ 2.2 display: 255 × (½ (194/255)^2.2)^(1/2.2).</summary>
    private const byte EquivalentGray = 141;

    internal static SolidPatch Gray { get; } = new(DeviceRgb.Gray(EquivalentGray));

    internal static HalftonePatch Lines { get; } = new(DeviceRgb.Gray(LinesLevel), DeviceRgb.Black, HalftoneGeometry.HorizontalLines);

    public static async Task<DefocusResult> RunAsync(MeasurementRig rig, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rig);
        await rig.AutoExposeAsync(Gray, 140, cancellationToken).ConfigureAwait(false);
        var solid = await SpreadAsync(rig, Gray, null, cancellationToken).ConfigureAwait(false);
        var lines = await SpreadAsync(rig, Lines, null, cancellationToken).ConfigureAwait(false);
        return new DefocusResult(solid.Mean, solid.Spread, lines.Mean, lines.Spread, lines.Clipped, rig.Instrument.Capabilities.OutputIsLinear);
    }

    /// <summary>
    /// The green channel's mean and spread over the region, averaged over a third of a second of frames, and the most
    /// clipped of them. The frames are from after <paramref name="patch"/> (null: what is on screen) settled, and after
    /// <paramref name="notBefore"/> (instrument time; null: no limit).
    /// </summary>
    internal static async Task<(double Mean, double Spread, double Clipped)> SpreadAsync(MeasurementRig rig, PatchSpec? patch, double? notBefore, CancellationToken cancellationToken)
    {
        var settle = rig.LatencySeconds + rig.ExposureSeconds + ReadPolicy.Default.SettleMarginSeconds;
        if (patch is not null)
        {
            var shown = await rig.ShowAsync(patch, cancellationToken).ConfigureAwait(false);
            await rig.WaitUntilAsync(shown.PresentTimeSeconds + settle, cancellationToken).ConfigureAwait(false);
        }

        if (notBefore is { } from)
        {
            await rig.WaitUntilAsync(from + rig.ExposureSeconds + ReadPolicy.Default.SettleMarginSeconds, cancellationToken).ConfigureAwait(false);
        }

        var frames = await rig.CollectAsync(1.0 / 3, cancellationToken).ConfigureAwait(false);
        var epoch = rig.Exposure?.Epoch;
        IReadOnlyList<FrameStats> current = [.. frames.Where(f => epoch is null || f.ExposureEpoch == epoch)];
        if (current.Count == 0)
        {
            current = frames;
        }

        return (
            current.Average(f => f.Green.Mean),
            current.Average(f => f.Green.StdDev),
            current.Max(f => Math.Max(f.Green.ClippedFraction, Math.Max(f.Red.ClippedFraction, f.Blue.ClippedFraction))));
    }
}

/// <summary>One focus tried: the setting, and the lines' ripple and clipped fraction seen with it.</summary>
public sealed record FocusTrial(double Focus, double Ripple, double Clipped);

/// <summary>Every focus tried, the one kept, and the blur check with it.</summary>
public sealed record DefocusSearchResult(IReadOnlyList<FocusTrial> Trials, double Best, DefocusResult BestCheck);

/// <summary>
/// Finds the focus that blurs the screen best by looking: the lines stay on screen while the instrument's focus moves
/// through its candidates, and the setting with the least ripple (unclipped first) is kept. It stops early at a ripple
/// as low as the webcam's chosen focus gives. About a second per setting.
/// </summary>
public static class DefocusSearch
{
    /// <summary>Ripple at which the search stops: as blurred as the webcam's chosen focus.</summary>
    private const double GoodEnough = 0.025;

    public static async Task<DefocusSearchResult> RunAsync(MeasurementRig rig, IFocusControl focus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(focus);
        if (focus.FocusCandidates.Count == 0)
        {
            throw new ArgumentException("The instrument offers no focus to try.", nameof(focus));
        }

        await rig.AutoExposeAsync(DefocusCheck.Gray, 140, cancellationToken).ConfigureAwait(false);
        var solid = await DefocusCheck.SpreadAsync(rig, DefocusCheck.Gray, null, cancellationToken).ConfigureAwait(false);
        await rig.ShowAsync(DefocusCheck.Lines, cancellationToken).ConfigureAwait(false);
        var linear = rig.Instrument.Capabilities.OutputIsLinear;
        DefocusResult Check((double Mean, double Spread, double Clipped) lines) => new(solid.Mean, solid.Spread, lines.Mean, lines.Spread, lines.Clipped, linear);

        var trials = new List<FocusTrial>();
        DefocusResult? last = null;
        foreach (var candidate in focus.FocusCandidates)
        {
            var from = await focus.SetFocusAsync(candidate, cancellationToken).ConfigureAwait(false);
            last = Check(await DefocusCheck.SpreadAsync(rig, null, from, cancellationToken).ConfigureAwait(false));
            trials.Add(new FocusTrial(candidate, last.Ripple, last.LinesClipped));
            if (last.Blurred && last.Ripple <= GoodEnough)
            {
                break;
            }
        }

        var best = trials.OrderBy(t => t.Clipped > DefocusResult.MaxClipped).ThenBy(t => t.Ripple).First();
        if (best == trials[^1])
        {
            return new DefocusSearchResult(trials, best.Focus, last!);
        }

        // Back to the best setting, and looked at once more: a lens does not always return exactly.
        var back = await focus.SetFocusAsync(best.Focus, cancellationToken).ConfigureAwait(false);
        var check = Check(await DefocusCheck.SpreadAsync(rig, null, back, cancellationToken).ConfigureAwait(false));
        return new DefocusSearchResult(trials, best.Focus, check);
    }
}
