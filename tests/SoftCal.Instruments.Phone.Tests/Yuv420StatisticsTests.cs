using AwesomeAssertions;
using SoftCal.Remote;
using Xunit;

namespace SoftCal.Instruments.Phone.Tests;

/// <summary>Frames in Android's YUV_420_888 layout, decoded as the camera encodes them (full-range BT.601).</summary>
public sealed class Yuv420StatisticsTests
{
    private const int Width = 64;
    private const int Height = 48;

    [Fact]
    public void A_uniform_color_decodes_to_its_RGB()
    {
        var frame = Frame.Filled(200, 120, 60);

        var stats = frame.Measure(new FrameRect(8, 8, 32, 24), rowBands: 0);

        stats.Red.Mean.Should().BeApproximately(200, 1.5, "Y, Cb and Cr are whole codes");
        stats.Green.Mean.Should().BeApproximately(120, 1.5);
        stats.Blue.Mean.Should().BeApproximately(60, 1.5);
        stats.Red.StdDev.Should().BeLessThan(1e-3);
        stats.Pixels.Should().Be(32 * 24);
        stats.Red.Clipped.Should().Be(0);
    }

    [Fact]
    public void Planar_and_interleaved_chroma_read_the_same()
    {
        var interleaved = Frame.Filled(30, 140, 220, interleaved: true);
        var planar = Frame.Filled(30, 140, 220, interleaved: false);
        var rect = new FrameRect(0, 0, Width, Height);

        var a = interleaved.Measure(rect, 0);
        var b = planar.Measure(rect, 0);

        b.Red.Mean.Should().Be(a.Red.Mean);
        b.Green.Mean.Should().Be(a.Green.Mean);
        b.Blue.Mean.Should().Be(a.Blue.Mean);
    }

    [Fact]
    public void A_frame_in_BT709_decodes_with_its_own_matrix()
    {
        // As an iPhone's bi-planar frames may come: Cb then Cr in one plane, encoded with HD video's matrix.
        var frame = Frame.Filled(200, 120, 60, matrix: YcbcrMatrix.Bt709, cbFirst: true);
        var rect = new FrameRect(8, 8, 32, 24);

        var stats = frame.Measure(rect, rowBands: 0, YcbcrMatrix.Bt709);
        var misread = frame.Measure(rect, rowBands: 0);

        stats.Red.Mean.Should().BeApproximately(200, 1.5, "Y, Cb and Cr are whole codes");
        stats.Green.Mean.Should().BeApproximately(120, 1.5);
        stats.Blue.Mean.Should().BeApproximately(60, 1.5);
        Math.Abs(misread.Red.Mean - 200).Should().BeGreaterThan(5, "BT.601's matrix reads a BT.709 frame wrong");
    }

    [Fact]
    public void Saturated_luma_counts_as_clipped_in_every_channel()
    {
        var frame = Frame.Filled(128, 128, 128);
        for (var y = 0; y < Height / 2; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                frame.Y[(y * frame.RowStride) + x] = 255;
            }
        }

        var stats = frame.Measure(new FrameRect(0, 0, Width, Height), 0);

        stats.Red.Clipped.Should().BeApproximately(0.5, 1e-9);
        stats.Green.Clipped.Should().BeApproximately(0.5, 1e-9);
        stats.Blue.Clipped.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Row_bands_follow_the_frames_rows()
    {
        var frame = Frame.Filled(128, 128, 128);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                frame.Y[(y * frame.RowStride) + x] = (byte)(100 + y);
            }
        }

        var stats = frame.Measure(new FrameRect(0, 0, Width, Height), rowBands: 4);

        stats.Bands.Should().HaveCount(4);
        stats.Bands[0].Should().BeApproximately(100 + 5.5, 1e-9, "rows 0–11");
        stats.Bands[3].Should().BeApproximately(100 + 41.5, 1e-9, "rows 36–47");
    }

    [Theory]
    [InlineData(0, 0.25, 0.5, 0.2, 0.1)]
    [InlineData(90, 0.5, 0.75, 0.1, 0.2)]
    [InlineData(180, 0.75, 0.5, 0.2, 0.1)]
    [InlineData(270, 0.5, 0.25, 0.1, 0.2)]
    public void A_region_on_the_upright_picture_maps_to_the_sensors_pixels(int rotation, double cx, double cy, double w, double h)
    {
        // Left of the upright picture's center, wider than tall.
        var region = new RegionSpec(0.25, 0.5, 0.2, 0.1, 0);

        var rect = Yuv420Statistics.ToFrame(region, rotation, 640, 480);

        var expected = Yuv420Statistics.ToFrame(new RegionSpec(cx, cy, w, h, 0), 0, 640, 480);
        rect.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void A_region_turned_and_turned_back_is_the_same(int to)
    {
        var region = new RegionSpec(0.3, 0.6, 0.2, 0.1, 16);

        var back = Yuv420Statistics.Turn(Yuv420Statistics.Turn(region, 90, to), to, 90);

        back.CenterX.Should().BeApproximately(region.CenterX, 1e-12);
        back.CenterY.Should().BeApproximately(region.CenterY, 1e-12);
        back.Width.Should().BeApproximately(region.Width, 1e-12);
        back.Height.Should().BeApproximately(region.Height, 1e-12);
    }

    [Fact]
    public void A_region_on_a_phone_turned_on_its_side_shows_turned_on_its_preview()
    {
        // The phone stands on its left side (turned 90° more than its sensor's mounting): what is left in the upright
        // picture is at the bottom of the portrait preview.
        var region = new RegionSpec(0.2, 0.5, 0.2, 0.1, 0);

        var onPreview = Yuv420Statistics.Turn(region, 180, 90);

        onPreview.CenterX.Should().BeApproximately(0.5, 1e-12);
        onPreview.CenterY.Should().BeApproximately(0.8, 1e-12);
        (onPreview.Width, onPreview.Height).Should().Be((0.1, 0.2));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(90, Height - 1, 0)]
    [InlineData(180, Width - 1, Height - 1)]
    [InlineData(270, 0, Width - 1)]
    public void Pictures_come_upright(int rotation, int x, int y)
    {
        // A bright pixel at the frame's first pixel: where the sensor's top-left corner lands once turned upright.
        var frame = Frame.Filled(0, 0, 0);
        frame.Y[0] = 250;
        var rect = new FrameRect(0, 0, Width, Height);
        var picture = Yuv420Statistics.PictureOf(rect, rotation, 1000);
        var sums = new double[picture.Width * picture.Height];

        Yuv420Statistics.AddLuma(frame.Y, frame.RowStride, rect, picture, sums);

        (picture.Width, picture.Height).Should().Be(rotation is 90 or 270 ? (Height, Width) : (Width, Height));
        sums[(y * picture.Width) + x].Should().Be(250);
        sums.Count(v => v > 0).Should().Be(1);
    }

    [Fact]
    public void A_wide_region_is_averaged_down_to_the_width_asked()
    {
        var frame = Frame.Filled(0, 0, 0);
        for (var i = 0; i < frame.Y.Length; i++)
        {
            frame.Y[i] = 100;
        }

        var rect = new FrameRect(0, 0, Width, Height);
        var picture = Yuv420Statistics.PictureOf(rect, 0, 20);
        var sums = new double[picture.Width * picture.Height];
        Yuv420Statistics.AddLuma(frame.Y, frame.RowStride, rect, picture, sums);
        var (bits, data) = Yuv420Statistics.Pack(sums, 1, picture.Step);

        picture.Step.Should().Be(4);
        (picture.Width, picture.Height).Should().Be((16, 12));
        bits.Should().Be(16, "an average resolves finer than a code");
        Yuv420Statistics.Unpack(bits, data, 16 * 12).Should().OnlyContain(v => Math.Abs(v - 100) < 1e-9);
    }

    [Fact]
    public void One_frames_picture_travels_in_eight_bits_and_an_average_in_sixteen()
    {
        double[] one = [0, 17, 255];
        double[] four = [2, 69, 1021];

        var (bits1, data1) = Yuv420Statistics.Pack(one, 1, 1);
        var (bits4, data4) = Yuv420Statistics.Pack(four, 4, 1);

        bits1.Should().Be(8);
        data1.Should().Equal(0, 17, 255);
        bits4.Should().Be(16);
        Yuv420Statistics.Unpack(bits4, data4, 3).Should().Equal(0.5, 17.25, 255.25);
    }

    /// <summary>A YUV_420_888 frame with padded rows, chroma interleaved (V then U, as most Android phones give it; U then
    /// V, as an iPhone does) or planar.</summary>
    private sealed class Frame
    {
        public const int Padding = 16;

        public byte[] Y { get; } = new byte[(Width + Padding) * Height];

        public byte[] U { get; private init; } = [];

        public byte[] V { get; private init; } = [];

        public int RowStride => Width + Padding;

        public int ChromaRowStride { get; private init; }

        public int ChromaPixelStride { get; private init; }

        /// <param name="matrix">The encoding's matrix (null: BT.601, as JFIF).</param>
        /// <param name="cbFirst">Interleaved chroma starts with U (Cb), as an iPhone's does.</param>
        public static Frame Filled(double r, double g, double b, bool interleaved = true, YcbcrMatrix? matrix = null, bool cbFirst = false)
        {
            var luma = (0.299 * r) + (0.587 * g) + (0.114 * b);
            var (toRed, toBlue) = (1.402, 1.772);
            if (matrix is { } m)
            {
                // The luma weights the decoding coefficients come from.
                var (kr, kb) = (1 - (m.CrToRed / 2), 1 - (m.CbToBlue / 2));
                luma = (kr * r) + ((1 - kr - kb) * g) + (kb * b);
                (toRed, toBlue) = (m.CrToRed, m.CbToBlue);
            }

            var cb = Math.Clamp(Math.Round(((b - luma) / toBlue) + 128), 0, 255);
            var cr = Math.Clamp(Math.Round(((r - luma) / toRed) + 128), 0, 255);
            Frame frame;
            if (interleaved)
            {
                // One buffer V U V U… (or U V U V…): the first plane starts at its first byte, the other one byte later.
                var stride = Width + Padding;
                var pairs = new byte[stride * (Height / 2)];
                for (var i = 0; i < pairs.Length; i += 2)
                {
                    pairs[i] = (byte)(cbFirst ? cb : cr);
                    pairs[i + 1] = (byte)(cbFirst ? cr : cb);
                }

                frame = cbFirst
                    ? new Frame { U = pairs, V = pairs[1..], ChromaRowStride = stride, ChromaPixelStride = 2 }
                    : new Frame { V = pairs, U = pairs[1..], ChromaRowStride = stride, ChromaPixelStride = 2 };
            }
            else
            {
                var stride = (Width / 2) + Padding;
                var u = Enumerable.Repeat((byte)cb, stride * (Height / 2)).ToArray();
                var v = Enumerable.Repeat((byte)cr, stride * (Height / 2)).ToArray();
                frame = new Frame { U = u, V = v, ChromaRowStride = stride, ChromaPixelStride = 1 };
            }

            Array.Fill(frame.Y, (byte)Math.Round(luma));
            return frame;
        }

        public RegionStatistics Measure(FrameRect rect, int rowBands, YcbcrMatrix? matrix = null) =>
            Yuv420Statistics.Measure(Y, RowStride, U, V, ChromaRowStride, ChromaPixelStride, rect, rowBands, matrix);
    }
}
