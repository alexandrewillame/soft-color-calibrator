using System.Globalization;
using SoftCal.Core.Advisor;

namespace SoftCal.App.Services;

/// <summary>A setting a profile holds, as step 1 lists them.</summary>
internal enum ProfileSetting
{
    Brightness,
    Contrast,
    ColorTemperature,
    Gains,
    Gamma,
    ShadowBoost,
}

/// <summary>
/// The monitor's settings beside a profile's. A set of settings sets only what it has a value for (a profile saved
/// before a value was known leaves that one as it is). Red, Green and Blue count only in the User mode, the one where
/// they mean something and the only one in which loading a profile writes them. The color temperature counts by kind
/// (User, a preset), as step 1 shows it; "not sure" sets nothing.
/// </summary>
internal static class ProfileComparison
{
    /// <summary>What putting <paramref name="to"/>'s settings on <paramref name="from"/> changes: the settings
    /// <paramref name="to"/> sets that <paramref name="from"/> does not have. Loading a profile changes
    /// <c>Changes(monitor, profile)</c>; saving into it, <c>Changes(profile, monitor)</c>.</summary>
    public static IReadOnlyList<ProfileSetting> Changes(MonitorSettings from, MonitorSettings to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var changes = new List<ProfileSetting>();
        if (to.Brightness is { } brightness && from.Brightness != brightness)
        {
            changes.Add(ProfileSetting.Brightness);
        }

        if (to.Contrast is { } contrast && from.Contrast != contrast)
        {
            changes.Add(ProfileSetting.Contrast);
        }

        if (Kind(to) is { } kind && Kind(from) != kind)
        {
            changes.Add(ProfileSetting.ColorTemperature);
        }

        if (Gains(to) is { } gains && Gains(from) != gains)
        {
            changes.Add(ProfileSetting.Gains);
        }

        if (Gamma(to) is { } gamma && !string.Equals(Gamma(from), gamma, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(ProfileSetting.Gamma);
        }

        if (Boost(to) is { } boost && Boost(from) != boost)
        {
            changes.Add(ProfileSetting.ShadowBoost);
        }

        return changes;
    }

    /// <summary>The settings that differ either way: what loading or saving would change.</summary>
    public static IReadOnlySet<ProfileSetting> Differences(MonitorSettings monitor, MonitorSettings profile) =>
        new HashSet<ProfileSetting>(Changes(monitor, profile).Concat(Changes(profile, monitor)));

    /// <summary>Whether <paramref name="settings"/> set nothing (a profile made before its settings were saved).</summary>
    public static bool IsEmpty(MonitorSettings settings) => Changes(new MonitorSettings(), settings).Count == 0;

    /// <summary>The monitor's settings once <paramref name="profile"/> is loaded: the profile's where it sets them, the
    /// monitor's elsewhere.</summary>
    public static MonitorSettings Loaded(MonitorSettings monitor, MonitorSettings profile)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(profile);
        var (red, green, blue) = Gains(profile) is { } gains ? ((int?)gains.Red, (int?)gains.Green, (int?)gains.Blue) : monitor.Gains();
        var gamma = Gamma(profile);
        var boost = Boost(profile);
        return monitor with
        {
            Brightness = profile.Brightness ?? monitor.Brightness,
            Contrast = profile.Contrast ?? monitor.Contrast,
            ColorPreset = Kind(profile) is null ? monitor.ColorPreset : profile.ColorPreset,
            RgbGains = null,
            RedGain = red,
            GreenGain = green,
            BlueGain = blue,
            HasGammaSetting = gamma is not null || monitor.HasGammaSetting,
            GammaSetting = gamma ?? monitor.GammaSetting,
            GammaPresets = gamma is null ? monitor.GammaPresets : profile.GammaPresets ?? monitor.GammaPresets,
            BlackBoostName = boost is null ? monitor.BlackBoostName : profile.BlackBoostName,
            BlackBoost = boost ?? monitor.BlackBoost,
            BlackBoostMax = boost is null ? monitor.BlackBoostMax : profile.BlackBoostMax ?? monitor.BlackBoostMax,
            BrightnessMax = monitor.BrightnessMax ?? profile.BrightnessMax,
            ContrastMax = monitor.ContrastMax ?? profile.ContrastMax,
        };
    }

    /// <summary>"Brightness: 31 → 28", one line for each setting putting <paramref name="to"/> on <paramref name="from"/>
    /// changes: what Save or Load is about to do.</summary>
    public static IReadOnlyList<string> Describe(MonitorSettings from, MonitorSettings to) =>
        [.. Changes(from, to).Select(s => $"{Title(s, to.BlackBoostName ?? from.BlackBoostName)}: {Text(s, from)} → {Text(s, to)}")];

    /// <summary>A setting's value as step 1 shows it ("31", "User", "84 · 78 · 85"); "—" when it is not set (Red, Green
    /// and Blue outside the User mode).</summary>
    public static string Text(ProfileSetting setting, MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        static string Number(int? value) => value?.ToString(CultureInfo.CurrentCulture) ?? "—";
        return setting switch
        {
            ProfileSetting.Brightness => Number(settings.Brightness),
            ProfileSetting.Contrast => Number(settings.Contrast),
            ProfileSetting.ColorTemperature => string.IsNullOrWhiteSpace(settings.ColorPreset)
                ? "—"
                : string.Equals(settings.ColorPreset.Trim(), "Preset", StringComparison.OrdinalIgnoreCase) ? "A preset" : settings.ColorPreset.Trim(),
            ProfileSetting.Gains => Gains(settings) is { } g ? string.Create(CultureInfo.CurrentCulture, $"{g.Red} · {g.Green} · {g.Blue}") : "—",
            ProfileSetting.Gamma => Gamma(settings) ?? "—",
            _ => Number(Boost(settings)),
        };
    }

    /// <summary>The setting's row title in step 1; the shadow boost by its name in the menu.</summary>
    public static string Title(ProfileSetting setting, string? boostName = null) => setting switch
    {
        ProfileSetting.Brightness => "Brightness",
        ProfileSetting.Contrast => "Contrast",
        ProfileSetting.ColorTemperature => "Color temperature",
        ProfileSetting.Gains => "Red, green, blue",
        ProfileSetting.Gamma => "Gamma",
        _ => string.IsNullOrWhiteSpace(boostName) ? "Shadow boost" : boostName.Trim(),
    };

    /// <summary>The setting's name in a sentence ("contrast", "RGB"); the shadow boost by its name in the menu.</summary>
    public static string Name(ProfileSetting setting, string? boostName = null) => setting switch
    {
        ProfileSetting.Brightness => "brightness",
        ProfileSetting.Contrast => "contrast",
        ProfileSetting.ColorTemperature => "color temperature",
        ProfileSetting.Gains => "RGB",
        ProfileSetting.Gamma => "gamma",
        _ => string.IsNullOrWhiteSpace(boostName) ? "shadow boost" : boostName.Trim(),
    };

    /// <summary>The color temperature by the kinds step 1 tells apart: User, or a preset (any); null for not known.</summary>
    public static ColorPresetKind? Kind(MonitorSettings settings) => SettingsResult.KindOf(settings.ColorPreset) switch
    {
        ColorPresetKind.User => ColorPresetKind.User,
        ColorPresetKind.Factory6500 or ColorPresetKind.OtherFactory => ColorPresetKind.OtherFactory,
        _ => null,
    };

    /// <summary>Red, Green and Blue, when they count: in the User mode, all three known.</summary>
    public static (int Red, int Green, int Blue)? Gains(MonitorSettings settings) =>
        Kind(settings) == ColorPresetKind.User && settings.Gains() is ({ } red, { } green, { } blue) ? (red, green, blue) : null;

    /// <summary>The gamma setting, when the monitor has one and it is known.</summary>
    public static string? Gamma(MonitorSettings settings) =>
        settings.HasGammaSetting && !string.IsNullOrWhiteSpace(settings.GammaSetting) ? settings.GammaSetting.Trim() : null;

    /// <summary>The shadow boost's value, when the menu has one (it has a name) and it is known.</summary>
    public static int? Boost(MonitorSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.BlackBoostName) && settings.BlackBoost is { } value ? value : null;
}
