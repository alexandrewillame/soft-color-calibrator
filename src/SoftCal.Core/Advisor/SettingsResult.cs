using System.Globalization;
using SoftCal.Core.Analysis;

namespace SoftCal.Core.Advisor;

/// <summary>Which kind of color temperature preset the monitor is in.</summary>
public enum ColorPresetKind
{
    /// <summary>Not known (not readable over DDC/CI and not entered).</summary>
    Unknown,

    /// <summary>The factory 6500 K white: 6500 K, or a preset named Normal or sRGB (both D65).</summary>
    Factory6500,

    /// <summary>Another factory preset: warmer or cooler than 6500 K, or the panel's native white.</summary>
    OtherFactory,

    /// <summary>A user mode, whose red, green and blue gains are set by hand.</summary>
    User,
}

/// <summary>What to do with a setting at the end of an advisor run.</summary>
public enum SettingAction
{
    /// <summary>It is right as it is.</summary>
    Keep,

    /// <summary>It was changed during the run (by the user at the monitor, or over DDC/CI).</summary>
    Changed,

    /// <summary>The user should change it in the monitor's menu (SoftCal could not test it during the run).</summary>
    ChangeInMenu,
}

/// <summary>One line of the result: a setting, its value before the run and after it, and why.</summary>
public sealed record SettingRow(string Setting, string Before, string After, SettingAction Action, string Why);

/// <summary>The color part of the monitor's settings: the preset and the red, green and blue gains, as reported.</summary>
public sealed record ColorSettings(string? Preset, int? Red, int? Green, int? Blue)
{
    public ColorPresetKind Kind => SettingsResult.KindOf(Preset);
}

/// <summary>
/// The result of an advisor run as a plain before/after table. Brightness, contrast, the gamma preset and the shadow
/// boost come from the run itself; the color temperature and the red, green and blue gains from the white match against
/// a reference screen (<see cref="WhiteMatcher"/>). Without a reference the white is not measured: a webcam's own idea
/// of white is not reliable enough, and a factory preset may not hold its factory values (a firmware update can wipe
/// them), so nothing is claimed about it.
/// </summary>
public static class SettingsResult
{
    /// <summary>The kind of a color preset from its name, as typed, chosen or read over DDC/CI (MCCS names).</summary>
    public static ColorPresetKind KindOf(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return ColorPresetKind.Unknown;
        }

        var p = preset.Trim().ToLowerInvariant();
        if (p.Contains("user", StringComparison.Ordinal) || p.Contains("custom", StringComparison.Ordinal))
        {
            return ColorPresetKind.User;
        }

        if (p.Contains("6500", StringComparison.Ordinal) || p.Contains("normal", StringComparison.Ordinal) || p.Contains("srgb", StringComparison.Ordinal) || p.Contains("d65", StringComparison.Ordinal))
        {
            return ColorPresetKind.Factory6500;
        }

        return p.Contains("not sure", StringComparison.Ordinal) ? ColorPresetKind.Unknown : ColorPresetKind.OtherFactory;
    }

    public static IReadOnlyList<SettingRow> Rows(
        MonitorControlProfile profile,
        IReadOnlyDictionary<OsdControlId, int> before,
        IReadOnlyDictionary<OsdControlId, int> after,
        bool? brightnessSetsBacklight,
        StepSummary? finalSteps,
        ColorSettings? color,
        WhiteMatchResult? white = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var rows = new List<SettingRow>();
        foreach (var control in profile.Controls)
        {
            if (!before.TryGetValue(control.Id, out var was))
            {
                continue;
            }

            var now = after.TryGetValue(control.Id, out var a) ? a : was;
            var action = now == was ? SettingAction.Keep : SettingAction.Changed;
            rows.Add(new SettingRow(control.Name, control.Describe(was), control.Describe(now), action, Why(control, was, now, brightnessSetsBacklight, finalSteps)));
        }

        if (white is not null)
        {
            rows.AddRange(WhiteRows(white, color));
        }
        else if (color is not null)
        {
            rows.AddRange(ColorRows(color));
        }

        return rows;
    }

    /// <summary>Codes up to this one merging with black is accepted (the advisor's own limit).</summary>
    private const int AcceptedCrush = 5;

    /// <summary>Why a setting ends where it does, from what the final check measured (never claiming a fix it did not see).</summary>
    private static string Why(OsdControl control, int was, int now, bool? backlight, StepSummary? steps)
    {
        var crushed = steps?.BlackCrushUpToCode is > AcceptedCrush;
        var clips = steps?.AnyClipping == true;
        return control.Id switch
        {
            OsdControlId.Brightness when crushed => F($"Codes 0–{steps!.BlackCrushUpToCode} still look the same as black, and no setting lifted them."),
            OsdControlId.Brightness when now > was => "Raised so the darkest steps stand apart from black.",
            OsdControlId.Brightness when now < was => "Lowered: black stays deep and the darkest steps still stand apart.",
            OsdControlId.Brightness when backlight == true => "It sets the backlight: any value is right, so choose what is comfortable.",
            OsdControlId.Brightness => "The darkest steps stand apart: choose what is comfortable.",
            OsdControlId.Contrast when clips => now < was
                ? F($"Lowered from {was}, but the brightest steps still merge: check the color mode and the graphics card's range.")
                : "The brightest steps still merge: check the color mode and the graphics card's range.",
            OsdControlId.Contrast when now < was => F($"At {was} the brightest steps merged; {now} keeps every step apart."),
            OsdControlId.Contrast when now > was => "Higher, and every bright step still stands apart.",
            OsdControlId.Contrast => "Every step from 235 to 255 stands apart: no highlight detail is lost.",
            OsdControlId.GammaPreset when now != was => "Clearly the closest to gamma 2.2.",
            OsdControlId.GammaPreset => "No other preset is clearly closer to gamma 2.2.",
            OsdControlId.BlackBoost when crushed => F($"Codes 0–{steps!.BlackCrushUpToCode} still look the same as black: it does not lift them."),
            OsdControlId.BlackBoost when now > was => "Lifts the darkest steps just enough to see them.",
            OsdControlId.BlackBoost when now < was => "Not needed: the darkest steps stand apart without it.",
            OsdControlId.BlackBoost => "The darkest steps stand apart as they are.",
            _ => string.Empty,
        };
    }

    /// <summary>The result's color row: on most monitors Red, Green and Blue are the color temperature setting (its
    /// User mode), so they are one setting here.</summary>
    public const string ColorRow = "Color temperature (R / G / B)";

    /// <summary>The color row after a white match against a reference screen: the gains before and after, and why. The
    /// User mode is mentioned only when the user was asked to switch to it.</summary>
    private static IEnumerable<SettingRow> WhiteRows(WhiteMatchResult white, ColorSettings? color)
    {
        var reference = white.ReferenceName;
        var before = white.GainsBefore?.ToString() ?? white.PresetBefore ?? color?.Preset ?? "not known";
        var after = white.GainsAfter?.ToString() ?? before;
        var changed = white.GainsAfter is { } gains && white.GainsBefore != gains;
        var switched = !string.Equals(white.PresetBefore ?? string.Empty, white.PresetAfter ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var mode = switched ? " Set in the User mode." : string.Empty;
        if (white.Before is not { } was)
        {
            yield return new SettingRow(ColorRow, before, before, SettingAction.Keep, white.Note ?? "The white was not measured.");
            yield break;
        }

        var why = white switch
        {
            { Finished: false } => $"Stopped before the white matched; it was {was.Brief()}.",
            { Matched: true } when !changed && !switched => $"The white already matches {reference}.",
            { Matched: true } => $"Now matches {reference}; it was {was.Brief()}.{mode}",
            { LimitReached: true } => $"{white.Note} It was {was.Brief()}.{mode}",
            _ => $"As close to {reference} as whole steps allow; it was {was.Brief()}.{mode}",
        };

        // Switching to the User mode is a change too, even with the same gains.
        yield return new SettingRow(ColorRow, before, after, changed || switched ? SettingAction.Changed : SettingAction.Keep, why);
    }

    /// <summary>Without a reference screen the white is not measured: the row shows the setting and changes nothing.</summary>
    private static IEnumerable<SettingRow> ColorRows(ColorSettings color)
    {
        var value = color is { Red: { } r, Green: { } g, Blue: { } b } ? F($"{r} / {g} / {b}") : color.Preset;
        if (!string.IsNullOrWhiteSpace(value))
        {
            yield return new SettingRow(ColorRow, value, value, SettingAction.Keep, "Not measured: choose a screen to match in step 1 to have the white checked.");
        }
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
