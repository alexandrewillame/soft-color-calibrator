using System.Text.Json;
using AwesomeAssertions;
using SoftCal.App.Services;
using Xunit;

namespace SoftCal.App.Tests;

/// <summary>What loading a profile does: the values for DDC/CI, what is left to the menu, and Windows' calibration
/// becoming the profile's own (or none: another profile's curves do not fit it).</summary>
public sealed class ProfileSwitchTests
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

    private static readonly ProfileCalibration DeskCalibration = new("desk.icc", "keep native tone", DateTimeOffset.Now);

    [Fact]
    public void Over_DDC_CI_the_values_are_written_and_the_rest_is_left_to_the_menu()
    {
        var plan = ProfileSwitch.Plan(new MonitorProfile("1-DESK", Desk), installed: null, ddc: true, _ => true);

        plan.Writes.Select(w => (w.Code, w.Value)).Should().Equal((0x10, 28), (0x12, 46), (0x16, 84), (0x18, 78), (0x1A, 85));
        plan.ByHand.Should().Contain(h => h.Contains("Gamma 3")).And.Contain(h => h.Contains("Black eQualizer"));
        plan.ByHand.Should().NotContain(h => h.StartsWith("Brightness", StringComparison.Ordinal));
    }

    [Fact]
    public void Red_green_and_blue_are_written_only_in_the_User_mode()
    {
        var preset = Desk with { ColorPreset = "6500 K" };

        var plan = ProfileSwitch.Plan(new MonitorProfile("Movies", preset), installed: null, ddc: true, _ => true);

        plan.Writes.Select(w => w.Code).Should().Equal(0x10, 0x12);
        plan.ByHand.Should().Contain("color temperature: 6500 K");
    }

    [Fact]
    public void Without_DDC_CI_everything_is_left_to_the_menu()
    {
        var plan = ProfileSwitch.Plan(new MonitorProfile("1-DESK", Desk), installed: null, ddc: false, _ => true);

        plan.Writes.Should().BeEmpty();
        plan.ByHand.Should().Contain("Brightness: 28").And.Contain("Contrast: 46").And.Contain("Red, Green, Blue: 84 / 78 / 85");
    }

    [Fact]
    public void What_the_monitor_has_already_is_neither_written_nor_asked_for()
    {
        var now = Desk with { Contrast = 50, BlueGain = 80 };

        var plan = ProfileSwitch.Plan(new MonitorProfile("1-DESK", Desk), installed: null, ddc: true, _ => true, now);

        plan.Writes.Select(w => (w.Code, w.Value)).Should().Equal((0x12, 46), (0x16, 84), (0x18, 78), (0x1A, 85));
        plan.ByHand.Should().BeEmpty("the User mode, the gamma and the Black eQualizer are as the profile has them");
    }

    [Fact]
    public void From_a_preset_the_User_mode_is_asked_for_and_the_gains_written()
    {
        var now = Desk with { ColorPreset = "6500 K", RedGain = 100, GreenGain = 100, BlueGain = 100 };

        var plan = ProfileSwitch.Plan(new MonitorProfile("1-DESK", Desk), installed: null, ddc: true, _ => true, now);

        plan.Writes.Select(w => w.Code).Should().Equal(0x16, 0x18, 0x1A);
        plan.ByHand.Should().ContainSingle().Which.Should().Contain("User mode");
    }

    [Theory]
    [InlineData(true, null, "Install")]
    [InlineData(true, "desk.icc", "Keep")]
    [InlineData(true, "dim.icc", "Install")]
    [InlineData(false, "dim.icc", "Remove")]
    [InlineData(false, null, "None")]
    public void Windows_calibration_becomes_the_profiles_own_or_none(bool hasOwn, string? installedFile, string expected)
    {
        var profile = new MonitorProfile("1-DESK", Desk, hasOwn ? DeskCalibration : null);

        var plan = ProfileSwitch.Plan(profile, installedFile is null ? null : Installed(installedFile), ddc: true, _ => true);

        plan.Calibration.Should().Be(Enum.Parse<CalibrationChange>(expected));
    }

    [Fact]
    public void A_profile_whose_calibration_file_is_gone_counts_as_having_none()
    {
        var profile = new MonitorProfile("1-DESK", Desk, DeskCalibration);

        var plan = ProfileSwitch.Plan(profile, Installed("dim.icc"), ddc: true, _ => false);

        plan.Calibration.Should().Be(CalibrationChange.Remove);
        plan.MissingFile.Should().BeTrue();
    }

    [Fact]
    public void An_installed_calibration_of_before_profiles_reads_its_picture_mode_as_its_profile()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        const string Older = """
            { "displayKey": "BNQ7F2B-11aedabb1c", "pictureMode": "1-DESK", "profileFile": "a.icc", "deviceName": "\\\\.\\DISPLAY1",
              "previousDefault": null, "loaderSwitchWasOn": false, "loaderTaskWasEnabled": false, "installedAt": "2026-10-18T13:09:00+00:00",
              "target": "keep native tone", "usesSoftCalLoader": true }
            """;

        var record = JsonSerializer.Deserialize<InstalledCalibration>(Older, options)!;
        var written = JsonSerializer.Serialize(record, options);

        record.Profile.Should().Be("1-DESK");
        written.Should().Contain("\"profile\":\"1-DESK\"").And.NotContain("pictureMode");
    }

    [Fact]
    public void The_panel_search_names_the_maker_the_model_and_its_EDID_id()
    {
        PanelLookup.Query("BNQ", 0x7F2B, "ZOWIE XL LCD").Should().Be("BenQ ZOWIE XL LCD BNQ7F2B monitor panel type TN IPS VA");
        PanelLookup.Query("DEL", 0xA0C1, "DELL U2720Q").Should().Be("DELL U2720Q DELA0C1 monitor panel type TN IPS VA", "a model that names its maker");
        PanelLookup.Query(null, null, "Generic PnP Monitor").Should().Be("Generic PnP Monitor monitor panel type TN IPS VA");
        PanelLookup.SearchUrl("BenQ XL2720 panel").Should().Be("https://www.google.com/search?q=BenQ%20XL2720%20panel");
    }

    private static InstalledCalibration Installed(string file) =>
        new("BNQ7F2B-11aedabb1c", "2-DIM", file, @"\\.\DISPLAY1", null, false, false, DateTimeOffset.Now, "keep native tone", UsesSoftCalLoader: true);
}
