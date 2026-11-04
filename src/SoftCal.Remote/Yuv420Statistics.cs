namespace SoftCal.Remote;

/// <summary>A rectangle of the camera's frame, in the frame's own (sensor-oriented) pixels.</summary>
public readonly record struct FrameRect(int X, int Y, int Width, int Height);

/// <summary>A frame's statistics over a region: the camera's R′G′B′ channels (0–255), the pixels counted, and the mean
/// luma of horizontal bands of sensor rows, in the order the sensor reads them.</summary>
public sealed record RegionStatistics(ChannelSummary Red, ChannelSummary Green, ChannelSummary Blue, int Pixels, double[] Bands);

/// <summary>An upright picture of a region: its size, how many frame pixels each picture pixel averages each way, and
/// the turn from the frame to upright (clockwise degrees).</summary>
public sealed record PictureLayout(int Width, int Height, int Step, int Rotation);

/// <summary>
/// How a frame's full-range Y′CbCr encodes its R′G′B′: what Cr adds to red, what Cb and Cr take from green, and what Cb
/// adds to blue.
/// </summary>
public readonly record struct YcbcrMatrix(double CrToRed, double CbToGreen, double CrToGreen, double CbToBlue)
{
    /// <summary>BT.601, as JFIF: Android's camera frames and the webcam's.</summary>
    public static YcbcrMatrix Bt601 { get; } = new(1.402, 0.344136, 0.714136, 1.772);

    /// <summary>BT.709, HD video's: an iPhone's frames may say they use it.</summary>
    public static YcbcrMatrix Bt709 { get; } = new(1.5748, 0.187324, 0.468124, 1.8556);

    /// <summary>BT.2020 (non-constant luminance).</summary>
    public static YcbcrMatrix Bt2020 { get; } = new(1.4746, 0.164553, 0.571353, 1.8814);
}

/// <summary>
/// Statistics of a camera frame in Android's YUV_420_888 layout (a full-size Y plane; U and V at half size each way,
/// each with its own row and pixel stride, interleaved or not; an iPhone's bi-planar frames fit it too), decoded as the
/// camera encodes it: full range, BT.601 as JFIF (as the webcam's statistics do for its frames) unless the frame says
/// otherwise. Means are doubles and nothing is clipped, so the mean's R′G′B′ is the mean of the pixels' R′G′B′.
/// <para>
/// The PC gives regions on the upright picture, the phone held as its app shows it (portrait). The sensor is mounted
/// turned (by the camera's sensor orientation), so regions are turned back to frame pixels, and the row bands stay rows
/// of the frame: the rolling shutter's rows.
/// </para>
/// </summary>
public static class Yuv420Statistics
{
    /// <summary>A channel at or beyond these values is clipped (the camera clipped its R′G′B′ before encoding)…</summary>
    private const double ChannelLow = 0.5;

    private const double ChannelHigh = 254.5;

    /// <summary>The frame rectangle of a region given on the upright picture.</summary>
    /// <param name="rotation">The clockwise turn (0, 90, 180 or 270 degrees) that makes the frame upright.</param>
    public static FrameRect ToFrame(RegionSpec region, int rotation, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(region);
        var (cx, cy, w, h) = Unturn(region, rotation);

        // Even edges keep the 2×2 chroma blocks whole.
        var x0 = Math.Clamp((int)Math.Round((cx - (w / 2)) * frameWidth), 0, frameWidth) & ~1;
        var x1 = Math.Clamp((int)Math.Round((cx + (w / 2)) * frameWidth), 0, frameWidth) & ~1;
        var y0 = Math.Clamp((int)Math.Round((cy - (h / 2)) * frameHeight), 0, frameHeight) & ~1;
        var y1 = Math.Clamp((int)Math.Round((cy + (h / 2)) * frameHeight), 0, frameHeight) & ~1;
        if (x1 - x0 < 2 || y1 - y0 < 2)
        {
            throw new ArgumentException($"The region is empty on a {frameWidth}×{frameHeight} frame.", nameof(region));
        }

        return new FrameRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// A region given on a picture turned upright by <paramref name="from"/>, on the same frame turned by
    /// <paramref name="to"/> instead (clockwise degrees): e.g. the region SoftCal reads, which it gives on the picture
    /// turned the way the phone stands, drawn on the phone's own preview, which turns by the sensor's mounting only.
    /// </summary>
    public static RegionSpec Turn(RegionSpec region, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(region);
        var (x, y, w, h) = Unturn(region, from);
        var (cx, cy, width, height) = Normalize(to) switch
        {
            90 => (1 - y, x, h, w),
            180 => (1 - x, 1 - y, w, h),
            270 => (y, 1 - x, h, w),
            _ => (x, y, w, h),
        };
        return region with { CenterX = cx, CenterY = cy, Width = width, Height = height };
    }

    /// <summary>The region's statistics. Chroma is read at <c>(x/2, y/2)</c> of the U and V planes.</summary>
    /// <param name="matrix">The frame's Y′CbCr matrix (null: BT.601, as JFIF).</param>
    public static RegionStatistics Measure(
        ReadOnlySpan<byte> y,
        int yRowStride,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        int chromaRowStride,
        int chromaPixelStride,
        FrameRect rect,
        int rowBands,
        YcbcrMatrix? matrix = null)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), rect, "The rectangle is empty.");
        }

        var m = matrix ?? YcbcrMatrix.Bt601;
        var bands = Math.Clamp(rowBands, 0, rect.Height);
        var bandSums = new double[bands];
        var bandRows = new int[bands];
        Accumulator red = default, green = default, blue = default;
        for (var row = 0; row < rect.Height; row++)
        {
            var frameRow = rect.Y + row;
            var lumaRow = y.Slice((frameRow * yRowStride) + rect.X, rect.Width);
            var chromaRow = (frameRow >> 1) * chromaRowStride;
            long rowLuma = 0;
            for (var col = 0; col < lumaRow.Length; col++)
            {
                int luma = lumaRow[col];
                var chroma = chromaRow + (((rect.X + col) >> 1) * chromaPixelStride);
                double cb = u[chroma] - 128;
                double cr = v[chroma] - 128;
                var r = luma + (m.CrToRed * cr);
                var g = luma - (m.CbToGreen * cb) - (m.CrToGreen * cr);
                var b = luma + (m.CbToBlue * cb);

                // Luma at either end means the encoder clipped every channel.
                var encoderClipped = luma is 0 or 255;
                red.Add(r, encoderClipped || r <= ChannelLow || r >= ChannelHigh);
                green.Add(g, encoderClipped || g <= ChannelLow || g >= ChannelHigh);
                blue.Add(b, encoderClipped || b <= ChannelLow || b >= ChannelHigh);
                rowLuma += luma;
            }

            if (bands > 0)
            {
                var band = (int)((long)row * bands / rect.Height);
                bandSums[band] += rowLuma;
                bandRows[band]++;
            }
        }

        var n = rect.Width * rect.Height;
        var means = new double[bands];
        for (var i = 0; i < bands; i++)
        {
            means[i] = bandSums[i] / ((double)bandRows[i] * rect.Width);
        }

        return new RegionStatistics(red.ToSummary(n), green.ToSummary(n), blue.ToSummary(n), n, means);
    }

    /// <summary>The upright picture of a frame rectangle, at most <paramref name="maxWidth"/> pixels wide (each picture
    /// pixel then averages a square of frame pixels).</summary>
    public static PictureLayout PictureOf(FrameRect rect, int rotation, int maxWidth)
    {
        var turn = Normalize(rotation);
        var (width, height) = turn is 90 or 270 ? (rect.Height, rect.Width) : (rect.Width, rect.Height);
        var step = Math.Max(1, (width + Math.Max(1, maxWidth) - 1) / Math.Max(1, maxWidth));
        return new PictureLayout(Math.Max(1, width / step), Math.Max(1, height / step), step, turn);
    }

    /// <summary>Adds a frame's luma over the rectangle to <paramref name="sums"/>, upright and summed over each picture
    /// pixel's square (divide by frames × step² for the mean).</summary>
    public static void AddLuma(ReadOnlySpan<byte> y, int yRowStride, FrameRect rect, PictureLayout picture, Span<double> sums)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (sums.Length < picture.Width * picture.Height)
        {
            throw new ArgumentException("The sums are smaller than the picture.", nameof(sums));
        }

        var step = picture.Step;
        for (var row = 0; row < rect.Height; row++)
        {
            var lumaRow = y.Slice(((rect.Y + row) * yRowStride) + rect.X, rect.Width);
            for (var col = 0; col < lumaRow.Length; col++)
            {
                var (ux, uy) = picture.Rotation switch
                {
                    90 => (rect.Height - 1 - row, col),
                    180 => (rect.Width - 1 - col, rect.Height - 1 - row),
                    270 => (row, rect.Width - 1 - col),
                    _ => (col, row),
                };
                var (px, py) = (ux / step, uy / step);
                if (px < picture.Width && py < picture.Height)
                {
                    sums[(py * picture.Width) + px] += lumaRow[col];
                }
            }
        }
    }

    /// <summary>
    /// Packs a picture's mean luma for the link: 8 bits when it is one frame (nothing is lost), otherwise 16 bits (the
    /// mean × 256, little-endian), since an average of many frames resolves finer than one code.
    /// </summary>
    public static (int Bits, byte[] Data) Pack(ReadOnlySpan<double> sums, int frames, int step)
    {
        var divisor = (double)Math.Max(1, frames) * step * step;
        if (frames <= 1 && step == 1)
        {
            var bytes = new byte[sums.Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)Math.Clamp(Math.Round(sums[i] / divisor), 0, 255);
            }

            return (8, bytes);
        }

        var words = new byte[sums.Length * 2];
        for (var i = 0; i < sums.Length; i++)
        {
            var value = (ushort)Math.Clamp(Math.Round(sums[i] / divisor * 256), 0, ushort.MaxValue);
            words[2 * i] = (byte)value;
            words[(2 * i) + 1] = (byte)(value >> 8);
        }

        return (16, words);
    }

    /// <summary>The mean luma (0–255) of a picture packed by <see cref="Pack"/>.</summary>
    public static double[] Unpack(int bits, byte[] data, int pixels)
    {
        ArgumentNullException.ThrowIfNull(data);
        var luma = new double[pixels];
        if (bits == 8)
        {
            for (var i = 0; i < pixels; i++)
            {
                luma[i] = data[i];
            }
        }
        else
        {
            for (var i = 0; i < pixels; i++)
            {
                luma[i] = (data[2 * i] | (data[(2 * i) + 1] << 8)) / 256.0;
            }
        }

        return luma;
    }

    private static int Normalize(int rotation) => ((rotation % 360) + 360) % 360;

    /// <summary>A region on the picture turned upright by <paramref name="rotation"/>, in the frame's own normalized
    /// coordinates: center and size.</summary>
    private static (double X, double Y, double Width, double Height) Unturn(RegionSpec region, int rotation) => Normalize(rotation) switch
    {
        90 => (region.CenterY, 1 - region.CenterX, region.Height, region.Width),
        180 => (1 - region.CenterX, 1 - region.CenterY, region.Width, region.Height),
        270 => (1 - region.CenterY, region.CenterX, region.Height, region.Width),
        _ => (region.CenterX, region.CenterY, region.Width, region.Height),
    };

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
