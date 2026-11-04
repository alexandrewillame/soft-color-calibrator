using AwesomeAssertions;
using SoftCal.App.Services;
using Xunit;

namespace SoftCal.App.Tests;

/// <summary>The monitor's settings beside a profile's: what loading or saving would change, a profile setting only
/// what it has a value for, Red, Green and Blue counting in the User mode only.</summary>
public sealed class ProfileComparisonTests
{
    private static readonly MonitorSettings Desk = new()
    {
        Brightness = 28,
        Contrast = 46,
        ColorPreset = "User",
        RedGain = 84,
        GreenGain = 78,
        BlueGain = 85,
        HasGammaSetting = true,
        GammaSetting = "Gamma 3",
        BlackBoostName = "Black eQualizer",
        BlackBoost = 0,
    };

    [Fact]
    public void The_same_settings_differ_in_nothing()
    {
        var typed = Desk with { ColorPreset = "user", GammaSetting = " gamma 3 ", RedGain = null, GreenGain = null, BlueGain = null, RgbGains = "84 / 78 / 85" };

        ProfileComparison.Differences(Desk, typed).Should().BeEmpty("the case, spaces and the older gains text do not count");
    }

    [Fact]
    public void Loading_changes_what_the_profile_sets_and_saving_what_the_monitor_has()
    {
        var monitor = Desk with { Contrast = 50, HasGammaSetting = false, GammaSetting = null };
        var profile = Desk with { Brightness = null };

        ProfileComparison.Changes(monitor, profile).Should().Equal(ProfileSetting.Contrast, ProfileSetting.Gamma);
        ProfileComparison.Changes(profile, monitor).Should().Equal(ProfileSetting.Brightness, ProfileSetting.Contrast);
        ProfileComparison.Differences(monitor, profile).Should().BeEquivalentTo([ProfileSetting.Brightness, ProfileSetting.Contrast, ProfileSetting.Gamma]);
    }

    [Fact]
    public void Red_green_and_blue_count_only_in_the_User_mode()
    {
        var preset = Desk with { ColorPreset = "6500 K", RedGain = 100, GreenGain = 100, BlueGain = 100 };
        var otherPreset = preset with { ColorPreset = "Warm", RedGain = 50 };

        ProfileComparison.Changes(preset, Desk).Should().Equal(ProfileSetting.ColorTemperature, ProfileSetting.Gains);
        ProfileComparison.Differences(preset, otherPreset).Should().BeEmpty("two presets look the same in step 1, and their gains mean nothing");
    }

    [Fact]
    public void A_profile_made_before_its_settings_were_saved_sets_nothing()
    {
        ProfileComparison.IsEmpty(new MonitorSettings()).Should().BeTrue();
        ProfileComparison.IsEmpty(new MonitorSettings { ColorPreset = "Not sure", HasGammaSetting = true }).Should().BeTrue();
        ProfileComparison.IsEmpty(new MonitorSettings { Contrast = 50 }).Should().BeFalse();
        ProfileComparison.Changes(Desk, new MonitorSettings()).Should().BeEmpty("loading it would change nothing");
    }

    [Fact]
    public void Save_and_Load_list_what_they_change_value_by_value()
    {
        var monitor = Desk with { Brightness = 31, ColorPreset = "6500 K", HasGammaSetting = false, GammaSetting = null };

        ProfileComparison.Describe(monitor, Desk).Should().Equal(
            "Brightness: 31 → 28",
            "Color temperature: 6500 K → User",
            "Red, green, blue: — → 84 · 78 · 85",
            "Gamma: — → Gamma 3");
        ProfileComparison.Describe(Desk with { BlackBoost = 5 }, Desk).Should().Equal("Black eQualizer: 5 → 0");
        ProfileComparison.Describe(Desk, Desk).Should().BeEmpty();
    }

    [Fact]
    public void Once_loaded_the_monitor_has_the_profiles_settings_and_keeps_its_own_elsewhere()
    {
        var monitor = new MonitorSettings { Brightness = 40, Contrast = 50, ColorPreset = "6500 K", RedGain = 100, GreenGain = 100, BlueGain = 100, BrightnessMax = 100 };
        var profile = Desk with { Brightness = null, RedGain = null, GreenGain = null, BlueGain = null, RgbGains = "84 / 78 / 85" };

        var loaded = ProfileComparison.Loaded(monitor, profile);

        (loaded.Brightness, loaded.Contrast, loaded.ColorPreset).Should().Be((40, 46, "User"));
        (loaded.RedGain, loaded.GreenGain, loaded.BlueGain).Should().Be((84, 78, 85));
        loaded.RgbGains.Should().BeNull("the gains are written out");
        (loaded.HasGammaSetting, loaded.GammaSetting, loaded.BlackBoost).Should().Be((true, "Gamma 3", 0));
        loaded.BrightnessMax.Should().Be(100);
        ProfileComparison.Changes(loaded, profile).Should().BeEmpty();
    }
}
