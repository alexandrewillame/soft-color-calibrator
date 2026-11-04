using SoftCal.Core.Analysis;
using SoftCal.Core.Color;
using SoftCal.Core.Measurement;

namespace SoftCal.Core.Advisor;

/// <summary>The short measurements the advisor repeats after each change.</summary>
[Flags]
public enum ProbeParts
{
    None = 0,

    /// <summary>Near-black steps at one exposure, for black crush (about 5 s).</summary>
    Shadows = 1,

    /// <summary>Near-white steps at one exposure, judged per channel, for clipping (about 5 s).</summary>
    Highlights = 2,

    /// <summary>The top of the gray tree (½ and ¼ of white), for gamma and mid-gray balance (about 17 s).</summary>
    Tone = 4,

    /// <summary>The white against the reference screen's, read by <see cref="WhiteMatcher"/> (about 5 s); not part of
    /// <see cref="All"/>.</summary>
    White = 8,

    All = Shadows | Highlights | Tone,
}

/// <summary>The latest advisor readings, and which of them were taken since the last change.</summary>
public sealed record AdvisorReadings
{
    public IReadOnlyList<StepReading> Shadows { get; init; } = [];

    public IReadOnlyList<StepReading> Highlights { get; init; } = [];

    public IReadOnlyList<TreeLevel> Tone { get; init; } = [];

    /// <summary>Parts measured since the last change; the others may no longer match the monitor's settings.</summary>
    public ProbeParts Fresh { get; init; }

    public StepSummary Steps => DisplayAnalysis.Steps(Shadows, Highlights);

    public bool IsFresh(ProbeParts parts) => (Fresh & parts) == parts;

    /// <summary>After a change: every part is kept for reference but none is fresh.</summary>
    public AdvisorReadings Stale() => this with { Fresh = ProbeParts.None };
}

public sealed record AdvisorProbeOptions
{
    public IReadOnlyList<int> ShadowCodes { get; init; } = [0, 4, 6, 8, 12, 16, 20];

    public IReadOnlyList<int> HighlightCodes { get; init; } = [235, 240, 245, 249, 252, 255];

    /// <summary>Gray-tree levels for the tone check: 2 = ½ and ¼ of white. Six of them (every gamma preset of a
    /// typical monitor, then the chosen one again) stay under two minutes.</summary>
    public int ToneLevels { get; init; } = 2;

    /// <summary>Reading policies and halftone geometry, shared with the Quick plan.</summary>
    public QuickPlanOptions Plan { get; init; } = new();
}

/// <summary>Takes the advisor's readings with any instrument.</summary>
public sealed class AdvisorProbe(MeasurementRig rig, AdvisorProbeOptions? options = null)
{
    private readonly MeasurementRig _rig = rig ?? throw new ArgumentNullException(nameof(rig));
    private readonly AdvisorProbeOptions _options = options ?? new AdvisorProbeOptions();

    public async Task<AdvisorReadings> MeasureAsync(AdvisorReadings previous, ProbeParts parts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var o = _options;
        var readings = previous;
        if (parts.HasFlag(ProbeParts.Shadows))
        {
            readings = readings with { Shadows = await StepTest.ReadAsync(_rig, o.ShadowCodes, o.Plan.Readings, cancellationToken).ConfigureAwait(false) };
        }

        if (parts.HasFlag(ProbeParts.Highlights))
        {
            readings = readings with { Highlights = await StepTest.ReadAsync(_rig, o.HighlightCodes, o.Plan.Readings, cancellationToken).ConfigureAwait(false) };
        }

        if (parts.HasFlag(ProbeParts.Tone))
        {
            var tone = await MidpointTree.RunAsync(
                new NullMatcher(_rig),
                o.ToneLevels,
                o.Plan.Nulls with { Channel = 3, BracketEveryChannel = true },
                DeviceRgb.Gray,
                null,
                cancellationToken,
                geometry: o.Plan.Geometry).ConfigureAwait(false);
            readings = readings with { Tone = tone };
        }

        return readings with { Fresh = readings.Fresh | parts };
    }
}
