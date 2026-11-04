using System.Collections.Concurrent;
using AwesomeAssertions;
using SoftCal.Windows.Displays;
using SoftCal.Windows.NightLight;
using SoftCal.Windows.OsState;
using SoftCal.Windows.Power;
using Xunit;

namespace SoftCal.Windows.Tests;

public sealed class OsStateTests
{
    private static readonly ColorFilterState NoFilters = new(false, null);
    private static readonly PowerState Mains = new(false, false, null, null);
    private static readonly AdvancedColorState Sdr = new("RGB", 8, true, false, false, "SDR", false, false, false);

    private static NightLightReport NightLight(bool? active, bool? schedule = false) => new([], active, 2631, schedule, "test");

    private static DisplayColorState Display(AdvancedColorState? advanced = null, bool? rampIdentity = true, bool? fullRange = true, bool? loadsCurves = null) => new(
        @"\\.\DISPLAY1",
        "ZOWIE XL LCD",
        null,
        advanced ?? Sdr,
        rampIdentity,
        fullRange,
        fullRange == false ? "RGB, limited (CEA-861) range, 8 bpc" : "RGB, full (VESA) range, 8 bpc",
        loadsCurves);

    [Fact]
    public void A_clean_system_has_nothing_to_report()
    {
        WindowsColorStateProbe.Evaluate(NightLight(false), NoFilters, Mains, false, [Display()]).Should().BeEmpty();
    }

    [Fact]
    public void A_running_nvidia_overlay_warns_with_a_button_to_its_settings()
    {
        WindowsColorStateProbe.Evaluate(NightLight(false), NoFilters, Mains, false, [Display()], ["The NVIDIA overlay"])
            .Should().ContainSingle(f => f.Severity == FindingSeverity.Warning && f.Area == "Overlays" && f.Action!.Target == PreflightAction.NvidiaApp);
    }

    [Fact]
    public void Night_light_on_blocks_while_unknown_or_scheduled_warns()
    {
        WindowsColorStateProbe.Evaluate(NightLight(true), NoFilters, Mains, false, [Display()])
            .Should().ContainSingle(f => f.Severity == FindingSeverity.Blocker && f.Area == "Night light" && f.Action!.Target == "ms-settings:nightlight");
        WindowsColorStateProbe.Evaluate(NightLight(null), NoFilters, Mains, false, [Display()])
            .Should().ContainSingle(f => f.Severity == FindingSeverity.Warning && f.Area == "Night light");
        WindowsColorStateProbe.Evaluate(NightLight(false, schedule: true), NoFilters, Mains, false, [Display()])
            .Should().ContainSingle(f => f.Severity == FindingSeverity.Warning && f.Message.Contains("scheduled"));
    }

    [Fact]
    public void Hdr_and_auto_color_management_block_and_say_the_app_can_switch_them()
    {
        var hdr = new AdvancedColorState("RGB", 10, true, true, false, "HDR", true, true, false);
        var acm = new AdvancedColorState("RGB", 8, true, true, false, "WCG", false, false, true);

        var findings = WindowsColorStateProbe.Evaluate(NightLight(false), NoFilters, Mains, false, [Display(hdr), Display(acm)]);

        findings.Should().HaveCount(2).And.OnlyContain(f => f.Severity == FindingSeverity.Blocker && f.Fix!.Contains("SoftCal can switch"));
        findings.Should().ContainSingle(f => f.Message.Contains("ignores 'vcgt'"));
    }

    [Fact]
    public void Filters_block_and_link_range_power_loader_and_curves_are_reported()
    {
        var findings = WindowsColorStateProbe.Evaluate(
            NightLight(false),
            new ColorFilterState(true, 0),
            new PowerState(true, true, false, true),
            true,
            [Display(rampIdentity: false, fullRange: false, loadsCurves: false)]);

        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Blocker && f.Message.Contains("grayscale"));
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Warning && f.Message.Contains("full-range"));
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Warning && f.Message.Contains("Energy saver"));
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Warning && f.Message.Contains("Automatic brightness"));
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Info && f.Message.Contains("calibration curve is loaded"));
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Info && f.Message.Contains("no support for loading calibration curves"));
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Info && f.Area == "Calibration loader" && f.Message.Contains("rounded to 8 bits"));
        WindowsColorStateProbe.Describe(findings).First().Should().StartWith("[Blocker]");
    }

    [Fact]
    public void Night_light_and_color_filters_stop_a_measurement_hdr_and_acm_do_not()
    {
        var hdr = new AdvancedColorState("RGB", 10, true, true, false, "HDR", true, true, false);

        var findings = WindowsColorStateProbe.Evaluate(NightLight(true), new ColorFilterState(true, 0), Mains, false, [Display(hdr)]);

        findings.Where(WindowsColorStateProbe.BlocksMeasuring).Select(f => f.Area).Should().BeEquivalentTo("Night light", "Color filters");
        findings.Should().ContainSingle(f => f.HandledBySoftCal && f.Message.StartsWith("HDR is on", StringComparison.Ordinal));
    }

    [Fact]
    public void Readiness_follows_the_blockers()
    {
        var blocked = new WindowsColorState(DateTimeOffset.UtcNow, NightLight(true), NoFilters, Mains, true, [], [new(FindingSeverity.Blocker, "x", "y")]);
        var warned = blocked with { Findings = [new(FindingSeverity.Warning, "x", "y")] };

        blocked.ReadyToMeasure.Should().BeFalse();
        warned.ReadyToMeasure.Should().BeTrue();
    }

    [Fact]
    public void Color_filter_names_follow_the_registry_values()
    {
        new ColorFilterState(true, 3).FilterName.Should().Be("red-green (deuteranopia)");
        new ColorFilterState(true, 99).FilterName.Should().BeNull();
    }

    [Fact]
    public void Night_light_changes_count_only_when_they_matter()
    {
        NightLightWatcher.HasChanged(NightLight(false), NightLight(false)).Should().BeFalse();
        NightLightWatcher.HasChanged(NightLight(false), NightLight(true)).Should().BeTrue();
        NightLightWatcher.HasChanged(NightLight(false), NightLight(false, schedule: true)).Should().BeTrue();
        NightLightWatcher.HasChanged(NightLight(null), NightLight(false)).Should().BeTrue();
    }

    [Fact]
    public void The_watcher_raises_changed_once_when_night_light_switches_on()
    {
        var sequence = new[] { NightLight(false), NightLight(false), NightLight(true) };
        var calls = 0;
        NightLightReport Next() => sequence[Math.Min(Interlocked.Increment(ref calls) - 1, sequence.Length - 1)];
        var raised = new ConcurrentQueue<NightLightReport>();

        using (var watcher = NightLightWatcher.Start(TimeSpan.FromHours(1), Next))
        {
            watcher.Changed += (_, report) => raised.Enqueue(report);
            watcher.Check();
            watcher.Check();
            watcher.Current.Active.Should().BeTrue();
        }

        raised.Should().ContainSingle(r => r.Active == true);
    }

    [Fact]
    public void The_display_keep_awake_request_is_taken_and_released()
    {
        var awake = DisplayKeepAwake.Acquire("SoftCal unit test");

        awake.IsActive.Should().BeTrue();
        awake.Dispose();
        awake.IsActive.Should().BeFalse();
    }
}
