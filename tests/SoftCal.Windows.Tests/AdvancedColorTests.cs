using AwesomeAssertions;
using SoftCal.Windows.AdvancedColor;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Safety;
using Xunit;

namespace SoftCal.Windows.Tests;

public sealed class AdvancedColorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "softcal-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Switches_come_from_the_user_settings_on_24h2_and_from_wide_color_enforcement_before()
    {
        // Windows 11 24H2 and later: ADVANCED_COLOR_INFO_2 reports both user switches.
        AdvancedColorSwitches.From(new AdvancedColorState("RGB", 8, true, true, false, "WCG", false, false, true))
            .Should().Be(new AdvancedColorSwitches(false, true));
        AdvancedColorSwitches.From(new AdvancedColorState("RGB", 10, true, true, false, "HDR", true, true, false))
            .Should().Be(new AdvancedColorSwitches(true, false));

        // 22H2/23H2: "advanced color enabled" is also true under ACM; wideColorEnforced tells them apart.
        AdvancedColorSwitches.From(new AdvancedColorState("RGB", 8, true, true, true, null, null, null, null))
            .Should().Be(new AdvancedColorSwitches(false, true));
        AdvancedColorSwitches.From(new AdvancedColorState("YCbCr444", 10, true, true, false, null, null, null, null))
            .Should().Be(new AdvancedColorSwitches(true, false));
        AdvancedColorSwitches.From(new AdvancedColorState("RGB", 8, true, false, false, null, null, null, null))
            .Should().Be(new AdvancedColorSwitches(false, false));
    }

    [Fact]
    public void Hdr_and_acm_are_switched_off_for_measuring_and_put_back()
    {
        var device = new FakeAdvancedColorDevice(hdr: true, acm: true);
        var journal = new StateJournal(_directory);

        using (var guard = AdvancedColorGuard.Begin(device, journal))
        {
            journal.Pending(AdvancedColorGuard.JournalKind).Should().ContainSingle(e => e.Key == device.Key);
            guard.MakeSdr().Should().Be(new AdvancedColorSwitches(false, false));
            device.Switches.Should().Be(new AdvancedColorSwitches(false, false));
        }

        device.Switches.Should().Be(new AdvancedColorSwitches(true, true));
        journal.Pending().Should().BeEmpty();
    }

    [Fact]
    public void After_a_crash_the_restorer_puts_the_switches_back()
    {
        var device = new FakeAdvancedColorDevice(hdr: false, acm: true);
        var journal = new StateJournal(_directory);
        AdvancedColorGuard.Begin(device, journal).MakeSdr();

        // The process dies here: no Restore, no Dispose.
        var result = new AdvancedColorRestorer(_ => device).Restore(journal.Pending().Single());

        result.Outcome.Should().Be(RestoreOutcome.Restored);
        device.Switches.Should().Be(new AdvancedColorSwitches(false, true));
    }

    [Fact]
    public void A_refused_switch_fails_keeps_the_entry_and_blocks_a_new_guard()
    {
        var device = new FakeAdvancedColorDevice(hdr: true, acm: false) { Refuse = true };
        var journal = new StateJournal(_directory);
        var guard = AdvancedColorGuard.Begin(device, journal);

        FluentActions.Invoking(() => guard.MakeSdr()).Should().Throw<InvalidOperationException>().WithMessage("*refused*");

        device.Switches = new AdvancedColorSwitches(false, false);
        guard.Restore().Outcome.Should().Be(RestoreOutcome.Failed);
        journal.Pending().Should().ContainSingle("the entry stays until the switches really are back");
        new AdvancedColorRestorer(_ => null).Restore(journal.Pending().Single()).Outcome.Should().Be(RestoreOutcome.Deferred);
        FluentActions.Invoking(() => AdvancedColorGuard.Begin(device, journal)).Should().Throw<InvalidOperationException>().WithMessage("*still journaled*");
    }

    private sealed class FakeAdvancedColorDevice(bool hdr, bool acm) : IAdvancedColorDevice
    {
        public AdvancedColorSwitches Switches { get; set; } = new(hdr, acm);

        public bool Refuse { get; init; }

        public string Key => @"\\?\DISPLAY#FAKE0001#1&2&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        public AdvancedColorState? Read() => new(
            "RGB",
            Switches.Hdr ? 10 : 8,
            true,
            Switches.AnyOn,
            false,
            Switches.Hdr ? "HDR" : Switches.AutoColorManagement ? "WCG" : "SDR",
            true,
            Switches.Hdr,
            Switches.AutoColorManagement);

        public bool SetHdr(bool enabled)
        {
            if (Refuse)
            {
                return false;
            }

            Switches = Switches with { Hdr = enabled };
            return true;
        }

        public bool SetAutoColorManagement(bool enabled)
        {
            if (Refuse)
            {
                return false;
            }

            Switches = Switches with { AutoColorManagement = enabled };
            return true;
        }
    }
}
