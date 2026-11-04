using System.Globalization;
using SoftCal.Core.Analysis;

namespace SoftCal.Core.Advisor;

/// <summary>The advisor's readings in a few plain lines, shown after every change.</summary>
public static class AdvisorMeter
{
    public static IReadOnlyList<string> Describe(AdvisorReadings readings, double targetGamma = 2.2)
    {
        ArgumentNullException.ThrowIfNull(readings);
        static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
        string Age(ProbeParts part) => readings.IsFresh(part) ? string.Empty : " (earlier reading)";
        var lines = new List<string>();
        var steps = readings.Steps;
        if (readings.Highlights.Count > 0)
        {
            var channels = steps.ChannelClipFromCode?
                .Select((c, i) => c is null ? null : new[] { "red", "green", "blue" }[i])
                .OfType<string>()
                .ToList() ?? [];
            lines.Add(!steps.AnyClipping
                ? "Highlights ✓ every step from 235 to 255 stands apart" + Age(ProbeParts.Highlights)
                : (steps.WhiteClipFromCode is { } from
                    ? F($"Highlights ✗ codes {from}–255 merge")
                    : "Highlights ✗ the brightest steps merge in " + string.Join(" and ", channels)) + Age(ProbeParts.Highlights));
        }

        if (readings.Shadows.Count > 0)
        {
            lines.Add(steps.BlackCrushUpToCode switch
            {
                null => "Shadows ✓ code 4 and up stand apart from black",
                < 6 => "Shadows ✓ code 6 and up stand apart from black (code 4 merging is normal)",
                { } crush => F($"Shadows ✗ codes 0–{crush} look the same as black"),
            } + Age(ProbeParts.Shadows));
        }

        if (readings.Tone.Count > 0)
        {
            // Codes against the target's, not a single "gamma": a TN panel's curve is no power law, and one number
            // read at ½ and ¼ of white (2.54 on the BenQ) disagrees with the report's gamma at 50 % (2.31).
            var levels = readings.Tone.OrderBy(l => l.Level).ToList();
            var onTarget = levels.All(l => Math.Abs(l.Null.MatchedCode - TargetCode(l.Fraction, targetGamma)) <= Tolerance(l.Fraction, targetGamma));
            var points = string.Join(", ", levels.Select(l => F($"{Share(l.Fraction)} of white at code {l.Null.MatchedCode:0} (γ {targetGamma:0.0}: {TargetCode(l.Fraction, targetGamma):0})")));
            var balance = DisplayAnalysis.GrayBalance(readings.Tone);
            var tint = balance.Count == 0 ? string.Empty : "; mid-gray " + (balance[0].Tint ?? "neutral");
            lines.Add($"Tone {(onTarget ? "✓" : "✗")} {points}{tint}" + Age(ProbeParts.Tone));
        }

        return lines;
    }

    /// <summary>The code at which a display of exactly <paramref name="gamma"/> gives <paramref name="fraction"/> of white.</summary>
    public static double TargetCode(double fraction, double gamma) => 255 * Math.Pow(fraction, 1 / gamma);

    /// <summary>How far the code may sit from the target's: the codes of gamma ± 0.1 at that light level.</summary>
    public static double Tolerance(double fraction, double gamma) =>
        Math.Abs(TargetCode(fraction, gamma + 0.1) - TargetCode(fraction, gamma - 0.1)) / 2;

    private static string Share(double fraction) => fraction switch
    {
        > 0.4 => "½",
        > 0.2 => "¼",
        > 0.1 => "⅛",
        _ => string.Create(CultureInfo.InvariantCulture, $"1/{Math.Round(1 / fraction):0}"),
    };
}
