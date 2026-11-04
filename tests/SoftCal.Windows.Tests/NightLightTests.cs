using AwesomeAssertions;
using SoftCal.Windows.NightLight;
using Xunit;

namespace SoftCal.Windows.Tests;

/// <summary>Decoding of the CloudStore Night light blobs (captured read-only on the dev machine, Night light on at 2631 K).</summary>
public sealed class NightLightTests
{
    private static byte[] Fixture(string name) =>
        Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Replace(" ", string.Empty, StringComparison.Ordinal).Trim());

    private const string GlobalState = "default$windows.data.bluelightreduction.bluelightreductionstate";
    private const string GlobalSettings = "default$windows.data.bluelightreduction.settings";
    private const string DeviceState = "{cac2befd-d9fb-4a20-9e93-4ca533af86da}$windows.data.bluelightreduction.bluelightreductionstateperdevice";
    private const string DeviceSettings = "{cac2befd-d9fb-4a20-9e93-4ca533af86da}$windows.data.bluelightreduction.settingsperdevice";

    [Fact]
    public void The_bond_reader_decodes_the_cloudstore_envelope()
    {
        var envelope = BondCompactReader.ParseMarshaled(Fixture("nightlight-global-state-on.hex"));

        var body = envelope.Struct(1)!;
        body.UInt(0).Should().Be(1791129384UL, "unix time of the last change");
        var payload = body.Struct(1)!.Bytes(1)!;
        payload.Should().HaveCount(21);
        BondCompactReader.HasHeader(payload).Should().BeTrue();
    }

    [Fact]
    public void The_global_state_reads_on_with_the_switch_time()
    {
        var blob = NightLightReader.Decode(GlobalState, Fixture("nightlight-global-state-on.hex"));

        blob.Error.Should().BeNull();
        blob.Scope.Should().Be("global");
        blob.Kind.Should().Be("state");
        blob.State!.Active.Should().BeTrue();
        blob.State.TransitionUtc.Should().Be(new DateTimeOffset(2026, 10, 4, 15, 56, 24, TimeSpan.Zero).AddTicks(9035321));
        blob.ModifiedUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1791129384));
    }

    [Fact]
    public void The_settings_read_the_colour_temperature_and_schedule()
    {
        var blob = NightLightReader.Decode(GlobalSettings, Fixture("nightlight-global-settings.hex"));

        blob.Settings!.ColorTemperatureKelvin.Should().Be(2631);
        blob.Settings.ScheduleEnabled.Should().BeFalse();
        blob.Settings.ScheduleOn.Should().Be(new TimeOnly(21, 0));
        blob.Settings.ScheduleOff.Should().Be(new TimeOnly(7, 0));
    }

    [Fact]
    public void The_newest_state_wins_when_the_global_and_per_device_keys_disagree()
    {
        // Seen on the dev machine: switching Night light off rewrote the global key only. Build that case: the global
        // "off" state stamped one second after the per-device "on" state.
        var on = Fixture("nightlight-global-state-on.hex");
        var off = on.Take(10).Concat(new byte[] { 0xF5, 0xD0, 0x90, 0xD6, 0x06 }).Concat(on.Skip(15).Take(3)).Append((byte)0x13)
            .Concat(on.Skip(19).Take(4)).Concat(on.Skip(25)).ToArray();
        var globalOff = NightLightReader.Decode(GlobalState, off);
        var deviceOn = NightLightReader.Decode(DeviceState, Fixture("nightlight-perdevice-state-on.hex"));

        globalOff.ModifiedUtc.Should().BeAfter(deviceOn.ModifiedUtc!.Value);
        var report = NightLightReader.Summarize([deviceOn, globalOff]);

        report.Active.Should().BeFalse();
        report.Summary.Should().Contain("disagree").And.Contain("global");
    }

    [Fact]
    public void Per_device_blobs_have_a_base_struct_terminator_and_the_newest_wins()
    {
        var blobs = new[]
        {
            NightLightReader.Decode(GlobalState, Fixture("nightlight-global-state-on.hex")),
            NightLightReader.Decode(GlobalSettings, Fixture("nightlight-global-settings.hex")),
            NightLightReader.Decode(DeviceState, Fixture("nightlight-perdevice-state-on.hex")),
            NightLightReader.Decode(DeviceSettings, Fixture("nightlight-perdevice-settings.hex")),
        };

        blobs.Should().OnlyContain(b => b.Error == null);
        blobs[2].Scope.Should().StartWith("per-device {cac2befd");
        var report = NightLightReader.Summarize(blobs);

        report.Active.Should().BeTrue();
        report.ColorTemperatureKelvin.Should().Be(2631);
        report.Summary.Should().Contain("ON").And.Contain("2631 K");
    }

    [Fact]
    public void A_state_without_field_0_reads_off()
    {
        // The same payload with the "10 00" field removed (and the list length fixed), as Windows writes it when off.
        var on = Fixture("nightlight-global-state-on.hex");
        var off = on.Take(18).Append((byte)0x13).Concat(on.Skip(19).Take(4)).Concat(on.Skip(25)).ToArray();

        var blob = NightLightReader.Decode(GlobalState, off);

        blob.Error.Should().BeNull();
        blob.State!.Active.Should().BeFalse();
        NightLightReader.Summarize([blob]).Summary.Should().Be("Night light is off.");
    }

    [Fact]
    public void Garbage_is_reported_not_thrown()
    {
        var blob = NightLightReader.Decode(GlobalState, [0x43, 0x42, 0x01, 0x00, 0x0A, 0x02]);

        blob.Error.Should().NotBeNull();
        NightLightReader.Summarize([blob]).Active.Should().BeNull();
    }

    [Fact]
    public void Zigzag_and_varints_decode_signed_and_large_values()
    {
        // { 0: int16 = -2631, 1: uint64 = 2^40, 2: string "ok" }
        byte[] blob = [0x43, 0x42, 0x01, 0x00, 0x0F, 0x8D, 0x29, 0x26, 0x80, 0x80, 0x80, 0x80, 0x80, 0x20, 0x49, 0x02, 0x6F, 0x6B, 0x00];

        var s = BondCompactReader.ParseMarshaled(blob);

        s.Int(0).Should().Be(-2631);
        s.UInt(1).Should().Be(1UL << 40);
        s[2]!.Value.Should().Be("ok");
    }
}
