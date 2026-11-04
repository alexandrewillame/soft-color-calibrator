using System.Globalization;

namespace SoftCal.Core.Analysis;

public enum ComparisonVerdict
{
    Worse = -1,
    Same = 0,
    Better = 1,

    /// <summary>Not a quality (or not measured both times): shown for information.</summary>
    Info = 2,
}

/// <summary>One line of a before/after comparison.</summary>
public sealed record ComparisonRow(string Quantity, string Before, string After, ComparisonVerdict Verdict);

/// <summary>Compares two reports of one display, e.g. before and after changing its settings.</summary>
public static class ReportComparison
{
    /// <summary>Changes smaller than these are within a run's repeatability (two BenQ runs: 0.006 in gamma, 0.25
    /// code, 0.5 % in gray balance), so they are called the same.</summary>
    private const double GammaStep = 0.03;
    private const double CodeStep = 1.0;
    private const double BalanceStep = 0.01;

    public static IReadOnlyList<ComparisonRow> Compare(DisplayReport before, DisplayReport after, double targetGamma = 2.2)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
        var rows = new List<ComparisonRow>();

        if (!string.Equals(before.Profile ?? string.Empty, after.Profile ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(new("Profile", before.Profile ?? "—", after.Profile ?? "—", ComparisonVerdict.Info));
        }

        rows.Add(new(
            F($"Effective gamma (target {targetGamma:0.0})"),
            F($"{before.Tone.EffectiveGamma:0.00}"),
            F($"{after.Tone.EffectiveGamma:0.00}"),
            Closer(Math.Abs(before.Tone.EffectiveGamma - targetGamma), Math.Abs(after.Tone.EffectiveGamma - targetGamma), GammaStep)));

        var target = 255 * Math.Pow(0.5, 1 / targetGamma);
        if (Half(before) is { } b && Half(after) is { } a)
        {
            rows.Add(new(F($"Half of white at code (target {target:0})"), F($"{b:0}"), F($"{a:0}"), Closer(Math.Abs(b - target), Math.Abs(a - target), CodeStep)));
        }

        rows.Add(Balance("Mid-gray tint", Level(before, 0.5), Level(after, 0.5)));
        rows.Add(Balance("Dark-gray tint (about 3 % of white)", Level(before, 0.03), Level(after, 0.03)));

        rows.Add(new(
            "Black crush",
            Crush(before.Steps.BlackCrushUpToCode),
            Crush(after.Steps.BlackCrushUpToCode),
            Closer(before.Steps.BlackCrushUpToCode ?? 0, after.Steps.BlackCrushUpToCode ?? 0, 1)));
        rows.Add(new(
            "Highlight clipping",
            before.Steps.AnyClipping ? "yes" : "none",
            after.Steps.AnyClipping ? "yes" : "none",
            before.Steps.AnyClipping == after.Steps.AnyClipping ? ComparisonVerdict.Same : after.Steps.AnyClipping ? ComparisonVerdict.Worse : ComparisonVerdict.Better));
        rows.Add(new("Contrast", before.Contrast.ToString(), after.Contrast.ToString(), ComparisonVerdict.Info));
        return rows;
    }

    /// <summary>Smaller error is better; differences under <paramref name="step"/> are the same.</summary>
    private static ComparisonVerdict Closer(double errorBefore, double errorAfter, double step) =>
        errorAfter < errorBefore - step ? ComparisonVerdict.Better
        : errorAfter > errorBefore + step ? ComparisonVerdict.Worse
        : ComparisonVerdict.Same;

    private static double? Half(DisplayReport report) =>
        report.Tone.Points.Where(p => Math.Abs(p.Fraction - 0.5) < 0.01).Select(p => (double?)p.Code).FirstOrDefault();

    /// <summary>The gray-balance level nearest <paramref name="fraction"/> of white (within a factor of 1.5).</summary>
    private static GrayBalancePoint? Level(DisplayReport report, double fraction) =>
        report.Channels.GrayLevels?
            .Where(l => Math.Abs(Math.Log(l.Fraction / fraction)) < Math.Log(1.5))
            .OrderBy(l => Math.Abs(Math.Log(l.Fraction / fraction)))
            .FirstOrDefault();

    private static ComparisonRow Balance(string name, GrayBalancePoint? before, GrayBalancePoint? after)
    {
        static string Describe(GrayBalancePoint? p) => p is null
            ? "not measured"
            : string.Create(CultureInfo.InvariantCulture, $"{p.Tint ?? "neutral"} ({p.Imbalance:0.0%})");
        var verdict = before is null || after is null ? ComparisonVerdict.Info : Closer(before.Imbalance, after.Imbalance, BalanceStep);
        return new ComparisonRow(name, Describe(before), Describe(after), verdict);
    }

    private static string Crush(int? code) => code is { } c ? string.Create(CultureInfo.InvariantCulture, $"codes 0–{c}") : "none";
}
