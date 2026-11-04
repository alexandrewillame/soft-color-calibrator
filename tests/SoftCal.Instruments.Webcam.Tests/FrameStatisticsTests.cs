using AwesomeAssertions;
using SoftCal.Core.Instruments;
using SoftCal.Instruments.Webcam.Capture;
using SoftCal.Instruments.Webcam.Frames;
using Xunit;

namespace SoftCal.Instruments.Webcam.Tests;

public sealed class FrameStatisticsTests
{
    [Theory]
    [InlineData(YcbcrMatrix.Bt601, YcbcrRange.Full)]
    [InlineData(YcbcrMatrix.Bt601, YcbcrRange.Limited)]
    [InlineData(YcbcrMatrix.Bt709, YcbcrRange.Full)]
    [InlineData(YcbcrMatrix.Bt709, YcbcrRange.Limited)]
    public void Decoding_inverts_encoding_without_clipping(YcbcrMatrix matrix, YcbcrRange range)
    {
        var decoder = new YcbcrDecoder(matrix, range);
        foreach (var (r, g, b) in new[] { (255.0, 255.0, 255.0), (0.0, 0.0, 0.0), (200.0, 30.0, 90.0), (-12.0, 270.0, 128.0) })
        {
            var (y, cb, cr) = decoder.FromRgb(r, g, b);
            var back = decoder.ToRgb(y, cb, cr);
            back.R.Should().BeApproximately(r, 1e-9);
            back.G.Should().BeApproximately(g, 1e-9);
            back.B.Should().BeApproximately(b, 1e-9);
        }
    }

    [Fact]
    public void Limited_range_white_and_black_map_to_the_full_code_range()
    {
        var decoder = new YcbcrDecoder(YcbcrMatrix.Bt601, YcbcrRange.Limited);

        var white = decoder.ToRgb(235, 128, 128);
        var black = decoder.ToRgb(16, 128, 128);
        new[] { white.R, white.G, white.B }.Should().AllSatisfy(v => v.Should().BeApproximately(255, 1e-9));
        new[] { black.R, black.G, black.B }.Should().AllSatisfy(v => v.Should().BeApproximately(0, 1e-9));
        decoder.Luma(235).Should().BeApproximately(255, 1e-9);
    }

    [Fact]
    public void Yuy2_statistics_average_the_region_and_convert_the_mean()
    {
        var decoder = YcbcrDecoder.Bt601Full;
        var geometry = new FrameGeometry(8, 4, 16, PixelLayout.Yuy2);
        var buffer = Yuy2(geometry, (x, y) => ((byte)(100 + x), (byte)120, (byte)140));

        var s = FrameStatisticsCalculator.Compute(buffer, geometry, new PixelRect(2, 1, 4, 2), 2, decoder, ClipThresholds.For(YcbcrRange.Full));

        s.PixelCount.Should().Be(8);
        s.MeanY.Should().BeApproximately(103.5, 1e-12, "columns 2..5 hold Y 102..105");
        s.MeanCb.Should().Be(120);
        s.MeanCr.Should().Be(140);
        var (r, g, b) = decoder.ToRgb(103.5, 120, 140);
        s.Red.Mean.Should().BeApproximately(r, 1e-9);
        s.Green.Mean.Should().BeApproximately(g, 1e-9);
        s.Blue.Mean.Should().BeApproximately(b, 1e-9);
        s.Red.StdDev.Should().BeGreaterThan(0);
        s.RowBandMeans.Should().HaveCount(2).And.OnlyContain(v => Math.Abs(v - 103.5) < 1e-9);
        s.Red.ClippedFraction.Should().Be(0);
    }

    [Fact]
    public void Nv12_statistics_read_the_shared_chroma_of_each_2x2_block()
    {
        var geometry = new FrameGeometry(4, 4, 4, PixelLayout.Nv12);
        var buffer = new byte[geometry.MinimumBufferLength];
        for (var i = 0; i < 16; i++)
        {
            buffer[i] = (byte)(50 + i);
        }

        // Chroma plane: 2 rows × (2 pairs of Cb, Cr).
        byte[] chroma = [100, 150, 110, 160, 120, 170, 130, 180];
        chroma.CopyTo(buffer, 16);

        var s = FrameStatisticsCalculator.Compute(buffer, geometry, new PixelRect(0, 0, 4, 4), 4, YcbcrDecoder.Bt601Full, ClipThresholds.For(YcbcrRange.Full));

        s.MeanY.Should().BeApproximately(57.5, 1e-12);
        s.MeanCb.Should().BeApproximately(115, 1e-12);
        s.MeanCr.Should().BeApproximately(165, 1e-12);
        s.RowBandMeans.Should().Equal(51.5, 55.5, 59.5, 63.5);
    }

    [Fact]
    public void Clipped_pixels_are_counted_per_channel_and_at_the_encoder_limits()
    {
        var geometry = new FrameGeometry(4, 2, 8, PixelLayout.Yuy2);
        var buffer = Yuy2(geometry, (x, y) => x switch
        {
            0 => ((byte)255, (byte)128, (byte)128), // encoder clip (Y = 255): every channel
            1 => ((byte)150, (byte)128, (byte)128),
            _ => ((byte)120, (byte)128, (byte)255), // pixels 2 and 3 share Cr = 255: red decodes far above 255
        });

        var s = FrameStatisticsCalculator.Compute(buffer, geometry, new PixelRect(0, 0, 4, 2), 0, YcbcrDecoder.Bt601Full, ClipThresholds.For(YcbcrRange.Full));

        s.Red.ClippedFraction.Should().Be(0.75);
        s.Green.ClippedFraction.Should().Be(0.25, "only the Y = 255 pixel clips green");
        s.Blue.ClippedFraction.Should().Be(0.25);
        s.MaxLuma.Should().Be(255);
        s.MinLuma.Should().Be(120);
        s.RowBandMeans.Should().BeEmpty();
    }

    [Fact]
    public void The_region_is_mapped_to_whole_pixels_starting_on_an_even_column()
    {
        var rect = PixelRect.FromRoi(new RoiSpec(0.5, 0.5, 0.3, 0.3), 640, 480);

        rect.Should().Be(new PixelRect(224, 168, 192, 144));
        (rect.X % 2).Should().Be(0);
        var corner = PixelRect.FromRoi(new RoiSpec(0.01, 0.99, 0.5, 0.5), 640, 480);
        corner.X.Should().Be(0);
        (corner.Y + corner.Height).Should().Be(480);
        FluentActions.Invoking(() => PixelRect.FromRoi(new RoiSpec(0.5, 0.5, 0.001, 0.3), 640, 480)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_luma_image_accumulates_over_frames()
    {
        var geometry = new FrameGeometry(4, 2, 8, PixelLayout.Yuy2);
        var buffer = Yuy2(geometry, (x, y) => ((byte)(10 * (x + 1)), (byte)128, (byte)128));
        var sum = new double[4];

        FrameStatisticsCalculator.AddLuma(buffer, geometry, new PixelRect(0, 1, 4, 1), YcbcrDecoder.Bt601Full, sum);
        FrameStatisticsCalculator.AddLuma(buffer, geometry, new PixelRect(0, 1, 4, 1), YcbcrDecoder.Bt601Full, sum);

        sum.Should().Equal(20, 40, 60, 80);
    }

    [Fact]
    public void The_format_selector_prefers_native_yuy2_and_never_a_compressed_format()
    {
        NativeFormat[] formats =
        [
            new(0, "MJPG", 640, 480, 30, null, null, null),
            new(1, "NV12", 640, 480, 30, 640, null, null),
            new(2, "YUY2", 1920, 1080, 5, 3840, null, null),
            new(3, "YUY2", 640, 480, 15, 1280, null, null),
            new(4, "YUY2", 640, 480, 30, 1280, YcbcrMatrix.Bt601, YcbcrRange.Limited),
        ];

        FormatSelector.Choose(formats, new FormatPreference())!.Index.Should().Be(4);
        FormatSelector.Choose(formats.Where(f => f.Subtype != "YUY2"), new FormatPreference())!.Subtype.Should().Be("NV12");
        FormatSelector.Choose(formats.Where(f => f.Subtype == "MJPG"), new FormatPreference()).Should().BeNull();
    }

    private static byte[] Yuy2(FrameGeometry geometry, Func<int, int, (byte Y, byte Cb, byte Cr)> pixel)
    {
        var buffer = new byte[geometry.MinimumBufferLength];
        for (var y = 0; y < geometry.Height; y++)
        {
            for (var x = 0; x < geometry.Width; x += 2)
            {
                var p0 = pixel(x, y);
                var p1 = pixel(x + 1, y);
                var o = (y * geometry.Stride) + (x * 2);
                buffer[o] = p0.Y;
                buffer[o + 1] = p0.Cb;
                buffer[o + 2] = p1.Y;
                buffer[o + 3] = p0.Cr;
            }
        }

        return buffer;
    }
}
