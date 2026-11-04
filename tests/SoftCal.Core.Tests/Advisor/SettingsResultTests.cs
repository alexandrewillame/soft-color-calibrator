using AwesomeAssertions;
using SoftCal.Core.Advisor;
using Xunit;

namespace SoftCal.Core.Tests.Advisor;

/// <summary>The before/after table at the end of a settings run, with the color settings from the white match.</summary>
public sealed class SettingsResultTests
{
    private static readonly MonitorControlProfile BrightnessAndContrast = new(
    [
        new OsdControl(OsdControlId.Brightness, "Brightness", 0, 100),
        new OsdControl(OsdControlId.Contrast, "Contrast", 0, 100),
    ]);

    [Theory]
    [InlineData("6500 K", ColorPresetKind.Factory6500)]
    [InlineData("sRGB", ColorPresetKind.Factory6500)]
    [InlineData("Normal", ColorPresetKind.Factory6500)]
    [InlineData("user 1", ColorPresetKind.User)]
    [InlineData("User (custom red, green, blue)", ColorPresetKind.User)]
    [InlineData("9300 K", ColorPresetKind.OtherFactory)]
    [InlineData("Warm", ColorPresetKind.OtherFactory)]
    [InlineData("Not sure", ColorPresetKind.Unknown)]
    [InlineData(null, ColorPresetKind.Unknown)]
    public void Color_presets_are_recognized_by_name(string? preset, ColorPresetKind kind) =>
        SettingsResult.KindOf(preset).Should().Be(kind);

    /// <summary>The BenQ on 2026-10-18, without a screen to match: nothing to change, and the color settings are shown
    /// as they are, not judged.</summary>
    [Fact]
    public void A_monitor_already_right_keeps_every_setting()
    {
        var settings = new Dictionary<OsdControlId, int> { [OsdControlId.Brightness] = 18, [OsdControlId.Contrast] = 50 };

        var rows = SettingsResult.Rows(BrightnessAndContrast, settings, settings, brightnessSetsBacklight: null, finalSteps: null, new ColorSettings("6500 K", 84, 85, 84));

        rows.Select(r => r.Setting).Should().Equal("Brightness", "Contrast", SettingsResult.ColorRow);
        rows.Should().OnlyContain(r => r.Action == SettingAction.Keep && r.Before == r.After);
        rows[2].After.Should().Be("84 / 85 / 84");
    }

    [Fact]
    public void A_change_made_during_the_run_shows_before_and_after_with_its_reason()
    {
        var before = new Dictionary<OsdControlId, int> { [OsdControlId.Brightness] = 40, [OsdControlId.Contrast] = 70 };
        var after = new Dictionary<OsdControlId, int> { [OsdControlId.Brightness] = 40, [OsdControlId.Contrast] = 60 };

        var rows = SettingsResult.Rows(BrightnessAndContrast, before, after, brightnessSetsBacklight: true, finalSteps: null, color: null);

        rows.Should().HaveCount(2);
        rows[0].Action.Should().Be(SettingAction.Keep);
        rows[0].Why.Should().Contain("backlight");
        rows[1].Should().Be(rows[1] with { Before = "70", After = "60", Action = SettingAction.Changed });
        rows[1].Why.Should().Contain("At 70 the brightest steps merged");
    }

    [Fact]
    public void Without_a_reference_screen_the_white_is_not_judged()
    {
        var none = new Dictionary<OsdControlId, int>();

        var rows = SettingsResult.Rows(BrightnessAndContrast, none, none, null, null, new ColorSettings("user 1", 90, 80, 85));

        rows.Should().ContainSingle();
        rows[0].Should().Be(rows[0] with { Setting = SettingsResult.ColorRow, Before = "90 / 80 / 85", After = "90 / 80 / 85", Action = SettingAction.Keep });
        rows[0].Why.Should().StartWith("Not measured");
    }

    [Fact]
    public void A_white_that_already_matches_keeps_its_preset_and_gains()
    {
        var none = new Dictionary<OsdControlId, int>();
        var white = new WhiteMatchResult("VX2770 SERIES", new WhiteDifference(1.004, 0.996), new WhiteDifference(1.004, 0.996), Matched: true, Finished: true, "6500 K", "6500 K", new RgbGains(84, 85, 84), new RgbGains(84, 85, 84));

        var rows = SettingsResult.Rows(BrightnessAndContrast, none, none, null, null, new ColorSettings("6500 K", 84, 85, 84), white);

        rows.Should().ContainSingle().Which.Should().Be(new SettingRow(SettingsResult.ColorRow, "84 / 85 / 84", "84 / 85 / 84", SettingAction.Keep, "The white already matches VX2770 SERIES."));
    }

    /// <summary>A monitor on a preset, green against the ViewSonic, matched in its User mode (the user was asked to switch).</summary>
    [Fact]
    public void A_matched_white_shows_the_new_preset_and_gains_with_what_was_wrong()
    {
        var none = new Dictionary<OsdControlId, int>();
        var white = new WhiteMatchResult("VX2770 SERIES", new WhiteDifference(0.86, 0.82), new WhiteDifference(1.003, 0.995), Matched: true, Finished: true, "6500 K", "User", new RgbGains(84, 85, 84), new RgbGains(97, 90, 100));

        var rows = SettingsResult.Rows(BrightnessAndContrast, none, none, null, null, new ColorSettings("6500 K", 84, 85, 84), white);

        rows.Should().ContainSingle();
        rows[0].Should().Be(rows[0] with { Setting = SettingsResult.ColorRow, Before = "84 / 85 / 84", After = "97 / 90 / 100", Action = SettingAction.Changed });
        rows[0].Why.Should().Be("Now matches VX2770 SERIES; it was greener (14 % less red, 18 % less blue). Set in the User mode.");
    }

    [Fact]
    public void A_stopped_match_says_so()
    {
        var none = new Dictionary<OsdControlId, int>();
        var white = new WhiteMatchResult("VX2770 SERIES", new WhiteDifference(0.86, 0.82), null, Matched: false, Finished: false, "6500 K", "User", new RgbGains(84, 85, 84), new RgbGains(100, 100, 100));

        var rows = SettingsResult.Rows(BrightnessAndContrast, none, none, null, null, null, white);

        rows.Should().ContainSingle().Which.Why.Should().StartWith("Stopped before the white matched;");
    }

    [Fact]
    public void A_white_never_read_changes_nothing()
    {
        var none = new Dictionary<OsdControlId, int>();
        var white = new WhiteMatchResult("VX2770 SERIES", null, null, Matched: false, Finished: false, "6500 K", "6500 K", new RgbGains(84, 85, 84), null, "The camera did not read the white of VX2770 SERIES.");

        var rows = SettingsResult.Rows(BrightnessAndContrast, none, none, null, null, null, white);

        rows.Should().ContainSingle().Which.Should().Be(new SettingRow(SettingsResult.ColorRow, "84 / 85 / 84", "84 / 85 / 84", SettingAction.Keep, "The camera did not read the white of VX2770 SERIES."));
    }
}
