namespace SoftCal.Remote;

/// <summary>A Bayer sensor's colour filters, named by its top-left 2×2 block, row by row (the values of Camera2's
/// SENSOR_INFO_COLOR_FILTER_ARRANGEMENT 0–3).</summary>
public enum BayerLayout
{
    Rggb = 0,
    Grbg = 1,
    Gbrg = 2,
    Bggr = 3,
}

/// <summary>
/// Statistics of a raw sensor frame (Camera2's RAW_SENSOR: one 16-bit value per photosite, behind a Bayer filter), taken
/// before any of the phone's processing: no white balance, no colour matrix, no tone curve (fixed or adaptive), no noise
/// reduction, no sharpening. Each 2×2 block of the region gives one red, one blue and the mean of its two greens; the
/// black level is taken off and the white level scales them to 0–255, so they are proportional to the light (linear) and
/// in the same range as the processed statistics. A block whose photosite comes within 2 % of the white level is clipped
/// in that channel (some sensors saturate a little below it). Row bands are the green's mean over bands of sensor rows
/// (the rolling shutter's).
/// </summary>
public static class RawStatistics
{
    /// <summary>The share of the range from black to white above which a photosite counts as saturated.</summary>
    public const double NearWhite = 0.98;

    /// <summary>The region's statistics.</summary>
    /// <param name="raw">The frame's values, row by row.</param>
    /// <param name="rowStride">Values from one row to the next.</param>
    /// <param name="rect">The region in photosites; its edges must be even (2×2 blocks are not split).</param>
    /// <param name="black">The black level of each position of the 2×2 block, row by row (top-left, top-right,
    /// bottom-left, bottom-right).</param>
    /// <param name="white">The value of a saturated photosite.</param>
    public static RegionStatistics Measure(ReadOnlySpan<ushort> raw, int rowStride, FrameRect rect, int rowBands, BayerLayout layout, ReadOnlySpan<double> black, double white)
    {
        if (rect.Width < 2 || rect.Height < 2 || ((rect.X | rect.Y | rect.Width | rect.Height) & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), rect, "The region must be whole 2×2 blocks.");
        }

        if (black.Length < 4)
        {
            throw new ArgumentException("A black level is needed for each of the block's four positions.", nameof(black));
        }

        // Where red, the two greens and blue sit in the block: index = row × 2 + column.
        var (r, g1, g2, b) = layout switch
        {
            BayerLayout.Rggb => (0, 1, 2, 3),
            BayerLayout.Grbg => (1, 0, 3, 2),
            BayerLayout.Gbrg => (2, 0, 3, 1),
            BayerLayout.Bggr => (3, 1, 2, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Not a Bayer layout."),
        };

        Span<double> scale = stackalloc double[4];
        Span<double> saturated = stackalloc double[4];
        for (var i = 0; i < 4; i++)
        {
            scale[i] = white > black[i] ? 255 / (white - black[i]) : 0;
            saturated[i] = black[i] + (NearWhite * (white - black[i]));
        }

        var blockRows = rect.Height / 2;
        var blockColumns = rect.Width / 2;
        var bands = Math.Clamp(rowBands, 0, blockRows);
        var bandSums = new double[bands];
        var bandBlocks = new int[bands];
        Accumulator red = default, green = default, blue = default;
        Span<double> value = stackalloc double[4];
        Span<bool> clipped = stackalloc bool[4];
        for (var by = 0; by < blockRows; by++)
        {
            var top = raw.Slice(((rect.Y + (2 * by)) * rowStride) + rect.X, rect.Width);
            var bottom = raw.Slice(((rect.Y + (2 * by) + 1) * rowStride) + rect.X, rect.Width);
            double rowGreen = 0;
            for (var bx = 0; bx < blockColumns; bx++)
            {
                var x = 2 * bx;
                ushort p0 = top[x], p1 = top[x + 1], p2 = bottom[x], p3 = bottom[x + 1];
                value[0] = (p0 - black[0]) * scale[0];
                value[1] = (p1 - black[1]) * scale[1];
                value[2] = (p2 - black[2]) * scale[2];
                value[3] = (p3 - black[3]) * scale[3];
                clipped[0] = p0 >= saturated[0];
                clipped[1] = p1 >= saturated[1];
                clipped[2] = p2 >= saturated[2];
                clipped[3] = p3 >= saturated[3];
                var g = (value[g1] + value[g2]) / 2;
                red.Add(value[r], clipped[r]);
                green.Add(g, clipped[g1] || clipped[g2]);
                blue.Add(value[b], clipped[b]);
                rowGreen += g;
            }

            if (bands > 0)
            {
                var band = (int)((long)by * bands / blockRows);
                bandSums[band] += rowGreen;
                bandBlocks[band] += blockColumns;
            }
        }

        var n = blockRows * blockColumns;
        var means = new double[bands];
        for (var i = 0; i < bands; i++)
        {
            means[i] = bandSums[i] / bandBlocks[i];
        }

        return new RegionStatistics(red.ToSummary(n), green.ToSummary(n), blue.ToSummary(n), n, means);
    }

    private struct Accumulator
    {
        private double _sum;
        private double _sumSquares;
        private int _clipped;

        public void Add(double value, bool clipped)
        {
            _sum += value;
            _sumSquares += value * value;
            if (clipped)
            {
                _clipped++;
            }
        }

        public readonly ChannelSummary ToSummary(int n)
        {
            var mean = _sum / n;
            var variance = n > 1 ? Math.Max(0, (_sumSquares - (n * mean * mean)) / (n - 1)) : 0;
            return new ChannelSummary(mean, Math.Sqrt(variance), (double)_clipped / n);
        }
    }
}
