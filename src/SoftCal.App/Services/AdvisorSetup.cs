using System.Globalization;
using SoftCal.Core.Advisor;

namespace SoftCal.App.Services;

/// <summary>The advisor's view of a monitor, built from the settings questionnaire.</summary>
/// <param name="EnhancementsOff">The user confirmed dynamic contrast, ECO, low blue light and the like are off; when
/// not, the advisor asks for that first.</param>
/// <param name="Color">The color preset and gains as entered (or read), for the white match; null when none was given.</param>
internal sealed record AdvisorSetup(MonitorControlProfile Profile, Dictionary<OsdControlId, int> Settings, IReadOnlyList<string> Skipped, bool EnhancementsOff = true, ColorSettings? Color = null)
{
    public static AdvisorSetup From(MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var controls = new List<OsdControl>();
        var values = new Dictionary<OsdControlId, int>();
        var skipped = new List<string>();

        void Numeric(OsdControlId id, string name, int? value, int? max)
        {
            if (value is { } v)
            {
                controls.Add(new OsdControl(id, name, 0, max ?? 100));
                values[id] = v;
            }
            else
            {
                skipped.Add($"{name}: no current value was entered, so it is left alone.");
            }
        }

        Numeric(OsdControlId.Brightness, "Brightness", settings.Brightness, settings.BrightnessMax);
        Numeric(OsdControlId.Contrast, "Contrast", settings.Contrast, settings.ContrastMax);

        if (settings.HasGammaSetting)
        {
            var presets = (settings.GammaPresets ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = PresetIndex(presets, settings.GammaSetting);
            if (presets.Length >= 2 && current is { } index)
            {
                controls.Add(new OsdControl(OsdControlId.GammaPreset, "Gamma", 0, presets.Length - 1, presets));
                values[OsdControlId.GammaPreset] = index;
            }
            else
            {
                skipped.Add("Gamma: list the presets in menu order and the current one, so SoftCal can compare them.");
            }
        }
        else
        {
            skipped.Add("Gamma: no gamma setting entered, so the presets are not compared. If the menu has one (e.g. Gamma 1–5), tick 'Gamma setting' under 'More settings' in step 1 and list them.");
        }

        if (!string.IsNullOrWhiteSpace(settings.BlackBoostName) && settings.BlackBoost is { } boost)
        {
            controls.Add(new OsdControl(OsdControlId.BlackBoost, settings.BlackBoostName.Trim(), 0, settings.BlackBoostMax ?? 20));
            values[OsdControlId.BlackBoost] = boost;
        }
        else
        {
            skipped.Add("Shadow boost: none entered. If the menu has one (e.g. Black eQualizer), give its name, value and highest value under 'More settings' in step 1.");
        }

        var (red, green, blue) = settings.Gains();
        var color = string.IsNullOrWhiteSpace(settings.ColorPreset) && red is null ? null : new ColorSettings(settings.ColorPreset, red, green, blue);
        return new AdvisorSetup(new MonitorControlProfile(controls), values, skipped, settings.EnhancementsOff, color);
    }

    /// <summary>The current preset by name ("Gamma 3") or by its number in the list ("3").</summary>
    private static int? PresetIndex(string[] presets, string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return null;
        }

        var byName = Array.FindIndex(presets, p => string.Equals(p, current.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byName >= 0)
        {
            return byName;
        }

        return int.TryParse(current.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= presets.Length ? n - 1 : null;
    }

    /// <summary>The questionnaire with the advisor's final values written back, and the white match's preset and gains
    /// when it used the user mode.</summary>
    public static MonitorSettings Apply(MonitorSettings settings, MonitorControlProfile profile, IReadOnlyDictionary<OsdControlId, int> final, WhiteMatchResult? white = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(final);
        int? Get(OsdControlId id, int? old) => final.TryGetValue(id, out var v) ? v : old;
        var applied = settings with
        {
            Brightness = Get(OsdControlId.Brightness, settings.Brightness),
            Contrast = Get(OsdControlId.Contrast, settings.Contrast),
            GammaSetting = final.TryGetValue(OsdControlId.GammaPreset, out var g) && profile.Find(OsdControlId.GammaPreset) is { } gamma ? gamma.Describe(g) : settings.GammaSetting,
            BlackBoost = Get(OsdControlId.BlackBoost, settings.BlackBoost),
        };
        return white is { GainsAfter: { } gains }
            ? applied with { ColorPreset = white.PresetAfter ?? applied.ColorPreset, RgbGains = null, RedGain = gains.Red, GreenGain = gains.Green, BlueGain = gains.Blue }
            : applied;
    }
}
