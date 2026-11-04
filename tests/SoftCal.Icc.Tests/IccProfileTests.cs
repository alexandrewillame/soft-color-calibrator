using System.Buffers.Binary;
using System.Text;
using AwesomeAssertions;
using SoftCal.Core.Color;
using Xunit;

namespace SoftCal.Icc.Tests;

public sealed class IccProfileTests
{
    private static readonly DateTime Created = new(2026, 10, 17, 20, 15, 0, DateTimeKind.Utc);

    /// <summary>An sRGB-like display at γ 2.2 with a small blue-boosting calibration curve.</summary>
    private static IccProfile Sample()
    {
        double[] Curve(double gamma) => [.. Enumerable.Range(0, 256).Select(i => Math.Pow(i / 255.0, gamma))];
        ushort[] Ramp(double scale) => [.. Enumerable.Range(0, 256).Select(i => (ushort)Math.Round(Math.Min(65535, i * 257 * scale)))];
        return IccProfile.ForDisplay(
            "BenQ XL2720 (1-DESK) — SoftCal 2026-10-17",
            RgbPrimaries.Srgb,
            [Curve(2.2), Curve(2.2), Curve(2.2)],
            new VideoCardGamma(Ramp(0.98), Ramp(0.99), Ramp(1.0)),
            Created);
    }

    [Fact]
    public void A_profile_reads_back_as_written()
    {
        var profile = Sample();

        var back = IccReader.Read(IccWriter.Write(profile));

        back.Description.Should().Be("BenQ XL2720 (1-DESK) ? SoftCal 2026-10-17", "v2 text is ASCII; the dash becomes '?'");
        back.CreatedUtc.Should().Be(Created);
        back.MediaWhite.X.Should().BeApproximately(profile.MediaWhite.X, 1e-4);
        back.MediaWhite.Z.Should().BeApproximately(profile.MediaWhite.Z, 1e-4);
        back.RedColorant.X.Should().BeApproximately(profile.RedColorant.X, 1e-4);
        back.BlueColorant.Z.Should().BeApproximately(profile.BlueColorant.Z, 1e-4);
        back.Adaptation.M11.Should().BeApproximately(profile.Adaptation.M11, 1e-4);
        back.ToneCurves.Should().HaveCount(3);
        back.ToneCurves[1].Should().HaveCount(IccWriter.CurveEntries);
        back.ToneCurves[1][IccWriter.CurveEntries / 2].Should().BeApproximately(Math.Pow(0.5, 2.2), 2e-3);
        back.Vcgt!.Red.Should().Equal(profile.Vcgt!.Red);
        back.Vcgt.Blue.Should().Equal(profile.Vcgt.Blue);
    }

    [Fact]
    public void The_file_follows_the_icc_layout()
    {
        var bytes = IccWriter.Write(Sample());

        BinaryPrimitives.ReadUInt32BigEndian(bytes).Should().Be((uint)bytes.Length, "the header's size field is the file size");
        (bytes.Length % 4).Should().Be(0);
        Encoding.ASCII.GetString(bytes, 36, 4).Should().Be("acsp");
        Encoding.ASCII.GetString(bytes, 12, 12).Should().Be("mntrRGB XYZ ");
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)).Should().Be(0x02400000u, "version 2.4.0");
        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(68)).Should().Be(0x0000F6D6, "the PCS illuminant is ICC's D50");
        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(128));
        for (var i = 0; i < count; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(132 + (12 * i) + 4));
            (offset % 4).Should().Be(0, "every tag starts on a 4-byte boundary");
        }
    }

    [Fact]
    public void The_colorants_add_up_to_d50_and_the_white_stays_the_displays()
    {
        var profile = Sample();
        var sum = profile.RedColorant + profile.GreenColorant + profile.BlueColorant;

        sum.X.Should().BeApproximately(Illuminants.IccD50.X, 1e-4);
        sum.Y.Should().BeApproximately(1, 1e-4);
        sum.Z.Should().BeApproximately(Illuminants.IccD50.Z, 1e-4);
        profile.MediaWhite.ToChromaticity().X.Should().BeApproximately(0.3127, 1e-4);
    }

    [Fact]
    public void Bytes_that_are_not_a_profile_are_refused()
    {
        var act = () => IccReader.Read(new byte[200]);

        act.Should().Throw<IccFormatException>();
    }
}
