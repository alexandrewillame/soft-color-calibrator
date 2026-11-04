using AwesomeAssertions;
using SoftCal.Core.Displays;
using Xunit;

namespace SoftCal.Core.Tests.Displays;

public sealed class EdidTests
{
    private static Edid Load(string name) =>
        Edid.ParseHex(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void Parses_the_benq_xl2720_edid()
    {
        var edid = Load("edid-benq-xl2720.hex");

        edid.ManufacturerId.Should().Be("BNQ");
        edid.ProductCode.Should().Be(0x7F2B);
        edid.SerialNumber.Should().Be(1u);
        edid.SerialText.Should().Be("ANON0000001");
        edid.MonitorName.Should().Be("ZOWIE XL LCD");
        edid.WeekOfManufacture.Should().Be(42);
        edid.YearOfManufacture.Should().Be(2013);
        (edid.VersionMajor, edid.VersionMinor).Should().Be((1, 4));
        edid.IsDigital.Should().BeTrue();
        edid.BitsPerColor.Should().Be(8);
        edid.DigitalInterface.Should().Be("DisplayPort");
        (edid.WidthCm, edid.HeightCm).Should().Be((60, 34));
        edid.Gamma.Should().BeApproximately(2.2, 1e-12);
        edid.SrgbIsDefaultColorSpace.Should().BeTrue();
        edid.PreferredTimingIsNative.Should().BeTrue();

        var c = edid.Chromaticities;
        (c.Red.X, c.Red.Y).Should().Be((660 / 1024.0, 353 / 1024.0));
        (c.Green.X, c.Green.Y).Should().Be((329 / 1024.0, 648 / 1024.0));
        (c.Blue.X, c.Blue.Y).Should().Be((155 / 1024.0, 49 / 1024.0));
        (c.White.X, c.White.Y).Should().Be((320 / 1024.0, 337 / 1024.0));

        edid.PreferredTiming!.HorizontalActive.Should().Be(1920);
        edid.PreferredTiming.VerticalActive.Should().Be(1080);
        edid.PreferredTiming.RefreshHz.Should().BeApproximately(60.0, 0.01);
        edid.RangeLimits.Should().Be(new RangeLimits(56, 144, 30, 160, 330));

        edid.ExtensionCount.Should().Be(1);
        edid.Cta!.Revision.Should().Be(3);
        edid.Cta.SupportsYCbCr444.Should().BeTrue();
        edid.Cta.SupportsYCbCr422.Should().BeTrue();
        edid.Cta.HasHdmiVendorBlock.Should().BeFalse();
        edid.Cta.DataBlockTags.Should().Equal(2, 1, 4);
    }

    [Fact]
    public void Parses_the_viewsonic_vx2770_edid()
    {
        var edid = Load("edid-viewsonic-vx2770.hex");

        edid.ManufacturerId.Should().Be("VSC");
        edid.ProductCode.Should().Be(0x3A2C);
        edid.MonitorName.Should().Be("VX2770 SERIES");
        edid.SerialText.Should().Be("ANON00000002");
        (edid.VersionMajor, edid.VersionMinor).Should().Be((1, 3));
        edid.BitsPerColor.Should().BeNull("EDID 1.3 does not declare a bit depth");
        edid.DigitalInterface.Should().BeNull();
        edid.Gamma.Should().BeApproximately(2.2, 1e-12);

        var c = edid.Chromaticities;
        (c.Red.X, c.Red.Y).Should().Be((655 / 1024.0, 346 / 1024.0));
        (c.Green.X, c.Green.Y).Should().Be((323 / 1024.0, 638 / 1024.0));
        (c.Blue.X, c.Blue.Y).Should().Be((155 / 1024.0, 65 / 1024.0));
        (c.White.X, c.White.Y).Should().Be((321 / 1024.0, 337 / 1024.0));

        edid.RangeLimits.Should().Be(new RangeLimits(50, 76, 15, 83, 180));
        edid.Cta!.HasHdmiVendorBlock.Should().BeTrue();
        edid.Cta.DataBlockTags.Should().Equal(2, 1, 4, 3);
    }

    // Windows' own decoding of the same monitors (WmiMonitorColorCharacteristics on the dev machine), which reports the
    // high 8 bits of each 10-bit chromaticity as Rx, Ry, Gx, Gy, Bx, By, Wx, Wy.
    [Theory]
    [InlineData("edid-benq-xl2720.hex", 165, 88, 82, 162, 38, 12, 80, 84)]
    [InlineData("edid-viewsonic-vx2770.hex", 163, 86, 80, 159, 38, 16, 80, 84)]
    public void Chromaticities_agree_with_the_windows_wmi_decoding(
        string name, int rx, int ry, int gx, int gy, int bx, int by, int wx, int wy)
    {
        var c = Load(name).Chromaticities;
        int High8(double value) => (int)Math.Round(value * 1024) >> 2;

        new[] { High8(c.Red.X), High8(c.Red.Y), High8(c.Green.X), High8(c.Green.Y), High8(c.Blue.X), High8(c.Blue.Y), High8(c.White.X), High8(c.White.Y) }
            .Should().Equal(rx, ry, gx, gy, bx, by, wx, wy);
    }

    [Fact]
    public void Rejects_a_bad_checksum()
    {
        var bytes = Load("edid-benq-xl2720.hex").Raw.ToArray();
        bytes[60] ^= 0x01;

        var act = () => Edid.Parse(bytes);

        act.Should().Throw<EdidFormatException>().WithMessage("*block 0*");
    }

    [Fact]
    public void Rejects_a_bad_header_and_short_input()
    {
        var bytes = Load("edid-benq-xl2720.hex").Raw.ToArray();
        bytes[1] = 0x00;

        FluentActions.Invoking(() => Edid.Parse(bytes)).Should().Throw<EdidFormatException>().WithMessage("*header*");
        FluentActions.Invoking(() => Edid.Parse(new byte[64])).Should().Throw<EdidFormatException>();
    }

    [Fact]
    public void Display_key_is_stable_distinct_and_does_not_leak_the_serial()
    {
        var benq = Load("edid-benq-xl2720.hex");
        var viewsonic = Load("edid-viewsonic-vx2770.hex");

        var key = DisplayKey.FromEdid(benq);

        key.Should().Be(DisplayKey.FromEdid(Load("edid-benq-xl2720.hex")));
        key.Value.Should().MatchRegex("^BNQ7F2B-[0-9a-f]{10}$");
        key.Value.Should().NotContain("ANON");
        DisplayKey.FromEdid(viewsonic).Value.Should().StartWith("VSC3A2C-");
        DisplayKey.FromEdid(viewsonic).Should().NotBe(key);
        DisplayKey.FromEdid(benq, "connector-2").Should().NotBe(key);
    }
}
