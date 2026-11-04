using System.Globalization;
using SoftCal.Core.Color;

namespace SoftCal.Core.Analysis;

/// <summary>
/// The camera trust test: white read between red, blue and black (W→R→W→B→W→K→W) must not move, and white held for
/// 10 s must not drift. A camera with a hidden automatic white balance, exposure or tone mapping fails it.
/// </summary>
public static class TrustTest
{
    public const double Tolerance = 0.01;

    /// <param name="whites">The white readings in sequence order (instrument units, per channel).</param>
    /// <param name="stability">Per-frame white readings over the hold, in time order.</param>
    public static TrustResult Evaluate(IReadOnlyList<Vec3> whites, IReadOnlyList<Vec3> stability)
    {
        ArgumentNullException.ThrowIfNull(whites);
        ArgumentNullException.ThrowIfNull(stability);
        if (whites.Count < 2)
        {
            throw new ArgumentException("Need at least two white readings.", nameof(whites));
        }

        var reference = whites[0];
        var sequenceDeviation = whites.Skip(1).Max(w => MaxRelative(w, reference));
        double stabilityDrift = 0, stabilityNoise = 0;
        if (stability.Count >= 4)
        {
            // Drift = difference between the means of the first and last quarter; noise = frame-to-frame spread.
            var quarter = Math.Max(1, stability.Count / 4);
            var first = Mean(stability.Take(quarter));
            var last = Mean(stability.Skip(stability.Count - quarter));
            stabilityDrift = MaxRelative(last, first);
            var mean = Mean(stability);
            stabilityNoise = new[] { 0, 1, 2 }.Max(c =>
                Math.Sqrt(stability.Sum(s => Math.Pow(s[c] - mean[c], 2)) / (stability.Count - 1)) / mean[c]);
        }

        return new TrustResult(sequenceDeviation, stabilityDrift, stabilityNoise, sequenceDeviation <= Tolerance && stabilityDrift <= Tolerance);
    }

    private static double MaxRelative(Vec3 a, Vec3 b) =>
        Math.Max(Math.Abs(a.X - b.X) / b.X, Math.Max(Math.Abs(a.Y - b.Y) / b.Y, Math.Abs(a.Z - b.Z) / b.Z));

    private static Vec3 Mean(IEnumerable<Vec3> values)
    {
        var list = values.ToList();
        return list.Aggregate(Vec3.Zero, (s, v) => s + v) / list.Count;
    }
}

public sealed record TrustResult(double WhiteSequenceDeviation, double StabilityDrift, double StabilityNoise, bool Passed);

/// <summary>Verdict for one gate criterion.</summary>
public enum CriterionStatus
{
    Pass,
    Fail,
    NotMeasured,
}

/// <summary>One M1 exit criterion with the measured value.</summary>
public sealed record GateCriterion(string Id, string Description, double? Value, double Limit, string Unit, CriterionStatus Status, string? Note = null)
{
    public static GateCriterion AtMost(string id, string description, double? value, double limit, string unit, string? note = null) =>
        new(id, description, value, limit, unit, value is null || double.IsNaN(value.Value) ? CriterionStatus.NotMeasured : value <= limit ? CriterionStatus.Pass : CriterionStatus.Fail, note);

    public static GateCriterion Below(string id, string description, double? value, double limit, string unit, string? note = null) =>
        new(id, description, value, limit, unit, value is null || double.IsNaN(value.Value) ? CriterionStatus.NotMeasured : value < limit ? CriterionStatus.Pass : CriterionStatus.Fail, note);
}

/// <summary>The M1 decision.</summary>
public enum GateOutcome
{
    /// <summary>The halftone-null method works across the range.</summary>
    Go,

    /// <summary>The null works for mid-tones; a restricted domain (deep shadows, some patterns, mounting) needs care.</summary>
    GoWithRestrictions,

    /// <summary>The null is not trustworthy; use exposure bracketing (camera linearization) instead.</summary>
    BracketingOnly,

    /// <summary>Not even bracketing is trustworthy (locks do not hold, hidden processing, nothing repeats).</summary>
    NoGo,

    /// <summary>Nothing has failed so far, but some criteria are not measured yet.</summary>
    Incomplete,
}

/// <summary>The measured numbers behind the gate, per monitor.</summary>
public sealed record GateMeasurements
{
    public double? MatchedReadingSigma { get; init; }

    public double? PatternShapeSpread { get; init; }

    public double? TreeRepeat21 { get; init; }

    public double? TreeRepeat38 { get; init; }

    public double? TreeRepeat72 { get; init; }

    public double? MoveChange { get; init; }

    public double? RippleBias { get; init; }

    public double? GammaSpreadNull { get; init; }

    public double? GammaSpreadBracketing { get; init; }

    /// <summary>Minutes the lock was watched without any control changing (0 when a change was seen).</summary>
    public double? LockHoldMinutes { get; init; }

    public bool? LockChangedDuringRun { get; init; }

    public bool? TrustPassed { get; init; }
}

public sealed record GateResult(IReadOnlyList<GateCriterion> Criteria, GateOutcome Outcome, IReadOnlyList<string> Reasons);

/// <summary>
/// The M1 exit gate: matched-reading σ ≤ 0.3 code; pattern shapes within 1 code; tree points at codes ~21/38/72 repeat
/// within 1 code; a ±1 cm camera move changes results ≤ 1 code; ripple bias &lt; 0.1 %; gamma spread ≤ 0.03 over 3
/// runs; locks hold 30 min.
/// </summary>
public static class FeasibilityGate
{
    public static GateResult Evaluate(GateMeasurements m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var gammaSpread = m.GammaSpreadNull ?? m.GammaSpreadBracketing;
        var lockHeld = m.LockChangedDuringRun == true ? 0 : m.LockHoldMinutes;

        // A lock that changed fails; one that held but was watched for less than 30 minutes is not yet measured.
        var lockStatus = m.LockChangedDuringRun == true
            ? CriterionStatus.Fail
            : lockHeld is >= 30 ? CriterionStatus.Pass : CriterionStatus.NotMeasured;
        var lockNote = m.LockChangedDuringRun == true
            ? "a control changed during the run"
            : lockHeld is { } watched && watched < 30 ? string.Create(CultureInfo.InvariantCulture, $"held, but watched for only {watched:0} min") : null;
        GateCriterion[] criteria =
        [
            GateCriterion.AtMost("sigma", "Matched-reading σ", m.MatchedReadingSigma, 0.3, "code"),
            GateCriterion.AtMost("shapes", "Pattern shapes agree", m.PatternShapeSpread, 1.0, "code"),
            GateCriterion.AtMost("tree21", "Tree point ~21 repeats", m.TreeRepeat21, 1.0, "code"),
            GateCriterion.AtMost("tree38", "Tree point ~38 repeats", m.TreeRepeat38, 1.0, "code"),
            GateCriterion.AtMost("tree72", "Tree point ~72 repeats", m.TreeRepeat72, 1.0, "code"),
            GateCriterion.AtMost("move", "±1 cm camera move", m.MoveChange, 1.0, "code"),
            GateCriterion.Below("ripple", "Ripple bias", m.RippleBias is null ? null : Math.Abs(m.RippleBias.Value) * 100, 0.1, "%"),
            GateCriterion.AtMost("gamma", "Gamma spread over 3 runs", gammaSpread, 0.03, "", m.GammaSpreadNull is null && m.GammaSpreadBracketing is not null ? "from bracketing" : null),
            new GateCriterion("locks", "Locks hold 30 min", lockHeld, 30, "min", lockStatus, lockNote),
        ];

        var reasons = new List<string>();
        bool Failed(string id) => criteria.First(c => c.Id == id).Status == CriterionStatus.Fail;

        if (Failed("locks") || m.TrustPassed == false)
        {
            reasons.Add(Failed("locks") ? "The camera lock did not hold." : "The camera failed the trust test (hidden automatic processing).");
            var bracketOk = m.GammaSpreadBracketing is <= 0.03;
            return new GateResult(criteria, bracketOk && !Failed("locks") ? GateOutcome.BracketingOnly : GateOutcome.NoGo, reasons);
        }

        // The null's core: repeatable and unbiased at mid-tones, with a stable gamma. If that fails, only bracketing is left.
        if (Failed("sigma") || Failed("ripple") || m.GammaSpreadNull > 0.03)
        {
            reasons.Add("The halftone null is not repeatable, not unbiased enough, or its gamma does not repeat.");
            var bracketOk = m.GammaSpreadBracketing is <= 0.03;
            if (!bracketOk)
            {
                reasons.Add(m.GammaSpreadBracketing is null ? "Bracketing was not measured." : "Bracketing does not repeat either.");
            }

            return new GateResult(criteria, bracketOk ? GateOutcome.BracketingOnly : GateOutcome.NoGo, reasons);
        }

        foreach (var c in criteria.Where(c => c.Status != CriterionStatus.Pass))
        {
            reasons.Add(c.Status == CriterionStatus.NotMeasured
                ? $"{c.Description}: not measured{(c.Note is null ? "" : " (" + c.Note + ")")}."
                : string.Create(CultureInfo.InvariantCulture, $"{c.Description}: {c.Value:0.###} {c.Unit} exceeds {c.Limit:0.###} {c.Unit}."));
        }

        if (criteria.Any(c => c.Status == CriterionStatus.NotMeasured))
        {
            return new GateResult(criteria, GateOutcome.Incomplete, reasons);
        }

        if (criteria.All(c => c.Status == CriterionStatus.Pass))
        {
            reasons.Add("Every criterion passes.");
            return new GateResult(criteria, GateOutcome.Go, reasons);
        }

        // Mid-tones are fine; the failures (deep-shadow repeat, pattern shapes, mounting sensitivity) limit the domain.
        return new GateResult(criteria, GateOutcome.GoWithRestrictions, reasons);
    }

    /// <summary>Max − min of repeated values (the "repeat within" measure).</summary>
    public static double Spread(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = values.Where(v => !double.IsNaN(v)).ToList();
        return list.Count < 2 ? double.NaN : list.Max() - list.Min();
    }
}
