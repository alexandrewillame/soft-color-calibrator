using SoftCal.Core.Instruments;

namespace SoftCal.Instruments.Webcam.Frames;

/// <summary>Uncompressed pixel layouts accepted from the camera (no decoder or converter in between).</summary>
public enum PixelLayout
{
    /// <summary>Packed 4:2:2, Y0 Cb Y1 Cr per pixel pair.</summary>
    Yuy2,

    /// <summary>Planar 4:2:0, a Y plane followed by an interleaved CbCr plane at half resolution.</summary>
    Nv12,
}

/// <summary>The layout of one frame buffer. <see cref="Stride"/> is the byte distance between rows of the Y plane.</summary>
public sealed record FrameGeometry(int Width, int Height, int Stride, PixelLayout Layout)
{
    public int MinimumBufferLength => Layout switch
    {
        PixelLayout.Yuy2 => Stride * Height,
        PixelLayout.Nv12 => (Stride * Height) + (Stride * ((Height + 1) / 2)),
        _ => throw new NotSupportedException($"Unsupported layout {Layout}."),
    };
}

/// <summary>A pixel rectangle inside a frame.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>Maps a normalized region to whole pixels, keeping it inside the frame and starting on an even column so
    /// that 4:2:2 pixel pairs are never split.</summary>
    public static PixelRect FromRoi(RoiSpec roi, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(roi);
        var x0 = (int)Math.Round((roi.CenterX - (roi.Width / 2)) * frameWidth);
        var x1 = (int)Math.Round((roi.CenterX + (roi.Width / 2)) * frameWidth);
        var y0 = (int)Math.Round((roi.CenterY - (roi.Height / 2)) * frameHeight);
        var y1 = (int)Math.Round((roi.CenterY + (roi.Height / 2)) * frameHeight);
        x0 = Math.Clamp(x0, 0, frameWidth) & ~1;
        x1 = Math.Clamp(x1, 0, frameWidth) & ~1;
        y0 = Math.Clamp(y0, 0, frameHeight);
        y1 = Math.Clamp(y1, 0, frameHeight);
        if (x1 - x0 < 2 || y1 - y0 < 1)
        {
            throw new ArgumentException($"The region of interest is empty on a {frameWidth}×{frameHeight} frame.", nameof(roi));
        }

        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }
}

/// <summary>
/// When a pixel counts as clipped. A channel is clipped when its decoded value reaches <see cref="ChannelLow"/> or
/// <see cref="ChannelHigh"/> (the camera clipped its R′G′B′ before encoding), and every channel of a pixel is clipped
/// when its luma sample sits at <see cref="LumaLow"/> or <see cref="LumaHigh"/> (the encoder clipped).
/// </summary>
public sealed record ClipThresholds(double ChannelLow, double ChannelHigh, int LumaLow, int LumaHigh)
{
    public static ClipThresholds For(YcbcrRange range) => range == YcbcrRange.Full
        ? new ClipThresholds(0.5, 254.5, 0, 255)
        : new ClipThresholds(0.5, 254.5, 16, 235);
}

/// <summary>Statistics of one frame's region of interest. Channel values are camera R′G′B′ codes (0–255 nominal).</summary>
public sealed record RoiStatistics(
    double MeanY,
    double MeanCb,
    double MeanCr,
    ChannelStats Red,
    ChannelStats Green,
    ChannelStats Blue,
    int PixelCount,
    IReadOnlyList<double> RowBandMeans,
    int MinLuma,
    int MaxLuma);

/// <summary>
/// Region-of-interest statistics straight from the camera's Y′CbCr buffer: means as doubles, no clipping, per-channel
/// clipped fractions, and the mean luma of horizontal bands (for rolling-shutter flicker and band-agreement checks).
/// </summary>
public static class FrameStatisticsCalculator
{
    public static RoiStatistics Compute(
        ReadOnlySpan<byte> buffer,
        FrameGeometry geometry,
        PixelRect rect,
        int rowBands,
        YcbcrDecoder decoder,
        ClipThresholds clip)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(clip);
        if (buffer.Length < geometry.MinimumBufferLength)
        {
            throw new ArgumentException(
                $"The buffer holds {buffer.Length} bytes; a {geometry.Width}×{geometry.Height} {geometry.Layout} frame needs {geometry.MinimumBufferLength}.",
                nameof(buffer));
        }

        if (rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > geometry.Width || rect.Y + rect.Height > geometry.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), rect, "The rectangle lies outside the frame.");
        }

        var bands = Math.Clamp(rowBands, 0, rect.Height);
        var bandSums = new double[bands];
        var bandCounts = new int[bands];
        double sumY = 0, sumCb = 0, sumCr = 0;
        Accumulator red = default, green = default, blue = default;
        int minY = 255, maxY = 0;

        for (var row = 0; row < rect.Height; row++)
        {
            var y = rect.Y + row;
            var band = bands == 0 ? -1 : (int)((long)row * bands / rect.Height);
            double rowLuma = 0;
            for (var col = 0; col < rect.Width; col++)
            {
                var x = rect.X + col;
                int luma, cb, cr;
                if (geometry.Layout == PixelLayout.Yuy2)
                {
                    var pair = (y * geometry.Stride) + ((x >> 1) << 2);
                    luma = buffer[pair + ((x & 1) << 1)];
                    cb = buffer[pair + 1];
                    cr = buffer[pair + 3];
                }
                else
                {
                    luma = buffer[(y * geometry.Stride) + x];
                    var chroma = (geometry.Stride * geometry.Height) + ((y >> 1) * geometry.Stride) + ((x >> 1) << 1);
                    cb = buffer[chroma];
                    cr = buffer[chroma + 1];
                }

                sumY += luma;
                sumCb += cb;
                sumCr += cr;
                rowLuma += decoder.Luma(luma);
                minY = Math.Min(minY, luma);
                maxY = Math.Max(maxY, luma);

                var encoderClipped = luma <= clip.LumaLow || luma >= clip.LumaHigh;
                var (r, g, b) = decoder.ToRgb(luma, cb, cr);
                red.Add(r, encoderClipped || r <= clip.ChannelLow || r >= clip.ChannelHigh);
                green.Add(g, encoderClipped || g <= clip.ChannelLow || g >= clip.ChannelHigh);
                blue.Add(b, encoderClipped || b <= clip.ChannelLow || b >= clip.ChannelHigh);
            }

            if (band >= 0)
            {
                bandSums[band] += rowLuma;
                bandCounts[band] += rect.Width;
            }
        }

        var n = rect.Width * rect.Height;
        var bandMeans = new double[bands];
        for (var i = 0; i < bands; i++)
        {
            bandMeans[i] = bandSums[i] / bandCounts[i];
        }

        return new RoiStatistics(
            sumY / n,
            sumCb / n,
            sumCr / n,
            red.ToStats(n),
            green.ToStats(n),
            blue.ToStats(n),
            n,
            bandMeans,
            minY,
            maxY);
    }

    /// <summary>Copies the decoded luma of a rectangle into <paramref name="destination"/> (row-major), for images that
    /// are averaged over frames, e.g. to measure halftone ripple.</summary>
    public static void AddLuma(ReadOnlySpan<byte> buffer, FrameGeometry geometry, PixelRect rect, YcbcrDecoder decoder, Span<double> destination)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(decoder);
        if (destination.Length < rect.Width * rect.Height)
        {
            throw new ArgumentException("The destination is smaller than the rectangle.", nameof(destination));
        }

        for (var row = 0; row < rect.Height; row++)
        {
            var y = rect.Y + row;
            for (var col = 0; col < rect.Width; col++)
            {
                var x = rect.X + col;
                var luma = geometry.Layout == PixelLayout.Yuy2
                    ? buffer[(y * geometry.Stride) + ((x >> 1) << 2) + ((x & 1) << 1)]
                    : buffer[(y * geometry.Stride) + x];
                destination[(row * rect.Width) + col] += decoder.Luma(luma);
            }
        }
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

        public readonly ChannelStats ToStats(int n)
        {
            var mean = _sum / n;
            var variance = n > 1 ? Math.Max(0, (_sumSquares - (n * mean * mean)) / (n - 1)) : 0;
            return new ChannelStats(mean, Math.Sqrt(variance), (double)_clipped / n);
        }
    }
}
