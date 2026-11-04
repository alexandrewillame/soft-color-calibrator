using AwesomeAssertions;
using SoftCal.Remote;
using Xunit;

namespace SoftCal.Instruments.Phone.Tests;

/// <summary>Raw sensor frames read before any processing: per 2×2 Bayer block, black level off, scaled to 0–255.</summary>
public sealed class RawStatisticsTests
{
    private const int Width = 32;
    private const int Height = 24;
    private const int Stride = Width + 8;
    private const double White = 1023;
    private static readonly double[] Black = [64, 64, 64, 64];

    [Theory]
    [InlineData(BayerLayout.Rggb)]
    [InlineData(BayerLayout.Grbg)]
    [InlineData(BayerLayout.Gbrg)]
    [InlineData(BayerLayout.Bggr)]
    public void Each_layout_reads_its_red_green_and_blue(BayerLayout layout)
    {
        var raw = Frame(layout, red: 500, green: 300, blue: 200);

        var stats = RawStatistics.Measure(raw, Stride, new FrameRect(4, 4, 16, 12), 0, layout, Black, White);

        stats.Red.Mean.Should().BeApproximately((500 - 64) * 255 / (White - 64), 1e-9);
        stats.Green.Mean.Should().BeApproximately((300 - 64) * 255 / (White - 64), 1e-9);
        stats.Blue.Mean.Should().BeApproximately((200 - 64) * 255 / (White - 64), 1e-9);
        stats.Pixels.Should().Be(8 * 6, "one value per 2×2 block");
        stats.Red.Clipped.Should().Be(0);
    }

    [Fact]
    public void Values_are_proportional_to_the_light()
    {
        var once = RawStatistics.Measure(Frame(BayerLayout.Rggb, 64 + 100, 64 + 200, 64 + 50), Stride, new FrameRect(0, 0, Width, Height), 0, BayerLayout.Rggb, Black, White);
        var twice = RawStatistics.Measure(Frame(BayerLayout.Rggb, 64 + 200, 64 + 400, 64 + 100), Stride, new FrameRect(0, 0, Width, Height), 0, BayerLayout.Rggb, Black, White);

        (twice.Red.Mean / once.Red.Mean).Should().BeApproximately(2, 1e-12);
        (twice.Green.Mean / once.Green.Mean).Should().BeApproximately(2, 1e-12);
        (twice.Blue.Mean / once.Blue.Mean).Should().BeApproximately(2, 1e-12);
    }

    [Fact]
    public void Each_position_has_its_own_black_level()
    {
        double[] black = [60, 64, 66, 70];
        var raw = new ushort[Stride * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                raw[(y * Stride) + x] = (ushort)(black[((y & 1) * 2) + (x & 1)] + 100);
            }
        }

        var stats = RawStatistics.Measure(raw, Stride, new FrameRect(0, 0, Width, Height), 0, BayerLayout.Rggb, black, White);

        stats.Red.Mean.Should().BeApproximately(100 * 255 / (White - 60), 1e-9);
        stats.Green.Mean.Should().BeApproximately(((100 * 255 / (White - 64)) + (100 * 255 / (White - 66))) / 2, 1e-9);
        stats.Blue.Mean.Should().BeApproximately(100 * 255 / (White - 70), 1e-9);
    }

    [Fact]
    public void A_saturated_photosite_clips_its_channel()
    {
        var raw = Frame(BayerLayout.Rggb, 500, 300, 200);

        // The top half's reds saturate, and one green of each block in the bottom half.
        for (var y = 0; y < Height / 2; y += 2)
        {
            for (var x = 0; x < Width; x += 2)
            {
                raw[(y * Stride) + x] = 1023;
            }
        }

        for (var y = Height / 2; y < Height; y += 2)
        {
            for (var x = 0; x < Width; x += 2)
            {
                raw[(y * Stride) + x + 1] = 1023;
            }
        }

        var stats = RawStatistics.Measure(raw, Stride, new FrameRect(0, 0, Width, Height), 0, BayerLayout.Rggb, Black, White);

        stats.Red.Clipped.Should().BeApproximately(0.5, 1e-12);
        stats.Green.Clipped.Should().BeApproximately(0.5, 1e-12);
        stats.Blue.Clipped.Should().Be(0);
    }

    [Theory]
    [InlineData(0.99, true)]
    [InlineData(0.95, false)]
    public void A_photosite_near_the_white_level_counts_as_saturated(double share, bool clipped)
    {
        var level = (int)Math.Round(64 + (share * (White - 64)));
        var raw = Frame(BayerLayout.Rggb, level, 300, 200);

        var stats = RawStatistics.Measure(raw, Stride, new FrameRect(0, 0, Width, Height), 0, BayerLayout.Rggb, Black, White);

        stats.Red.Clipped.Should().Be(clipped ? 1 : 0);
        stats.Green.Clipped.Should().Be(0);
    }

    [Fact]
    public void Row_bands_follow_the_sensor_rows()
    {
        var raw = new ushort[Stride * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                raw[(y * Stride) + x] = (ushort)(64 + (10 * (y / 2)));
            }
        }

        var stats = RawStatistics.Measure(raw, Stride, new FrameRect(0, 0, Width, Height), 3, BayerLayout.Rggb, Black, White);

        stats.Bands.Should().HaveCount(3);
        stats.Bands[0].Should().BeApproximately(10 * 1.5 * 255 / (White - 64), 1e-9, "block rows 0–3");
        stats.Bands[2].Should().BeApproximately(10 * 9.5 * 255 / (White - 64), 1e-9, "block rows 8–11");
    }

    [Fact]
    public void A_region_that_splits_blocks_is_refused()
    {
        var measure = () => RawStatistics.Measure(Frame(BayerLayout.Rggb, 1, 1, 1), Stride, new FrameRect(1, 0, 4, 4), 0, BayerLayout.Rggb, Black, White);

        measure.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>A uniform frame: each photosite holds its filter's value, with padding at the end of each row.</summary>
    private static ushort[] Frame(BayerLayout layout, int red, int green, int blue)
    {
        // The filter of each block position (row × 2 + column) for each layout.
        char[] filters = layout switch
        {
            BayerLayout.Rggb => ['R', 'G', 'G', 'B'],
            BayerLayout.Grbg => ['G', 'R', 'B', 'G'],
            BayerLayout.Gbrg => ['G', 'B', 'R', 'G'],
            _ => ['B', 'G', 'G', 'R'],
        };
        var raw = new ushort[Stride * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                raw[(y * Stride) + x] = (ushort)(filters[((y & 1) * 2) + (x & 1)] switch { 'R' => red, 'G' => green, _ => blue });
            }
        }

        return raw;
    }
}
