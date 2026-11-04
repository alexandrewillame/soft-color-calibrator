namespace SoftCal.Core.ToneCurves;

/// <summary>Summary statistics describing how a tone curve compares with a pure power law.</summary>
public static class GammaAnalysis
{
    /// <summary>The exponent of the power law that passes through this single point: ln(linear) / ln(encoded).</summary>
    public static double PointGamma(double encoded, double linear)
    {
        if (encoded is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(encoded), encoded, "Point gamma needs 0 < encoded < 1.");
        }

        if (linear <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(linear), linear, "Point gamma needs a positive linear value.");
        }

        return Math.Log(linear) / Math.Log(encoded);
    }

    /// <summary>
    /// Effective gamma as reported by common calibration tools: the gamma of the pure power law that gives the same output
    /// at 50 % input. sRGB scores ≈ 2.22 on this measure.
    /// </summary>
    public static double EffectiveGamma(ITransferCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        return PointGamma(0.5, curve.ToLinear(0.5));
    }

    /// <summary>Unweighted mean of the point gammas between <paramref name="from"/> and <paramref name="to"/>; shows how the
    /// gamma drifts across the range when compared with <see cref="EffectiveGamma"/>.</summary>
    public static double AverageGamma(ITransferCurve curve, double from = 0.1, double to = 0.9, int samples = 81)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var points = Sample(curve, from, to, samples);
        return points.Average(p => PointGamma(p.Encoded, p.Linear));
    }

    /// <summary>
    /// Least-squares slope through the origin of ln(linear) against ln(encoded). Dark points weigh the most, so this is the
    /// best single-exponent fit near black (sRGB, with its linear toe, scores ≈ 2.12).
    /// </summary>
    public static double LogLogSlope(ITransferCurve curve, double from = 0.1, double to = 0.9, int samples = 81)
    {
        ArgumentNullException.ThrowIfNull(curve);
        return LogLogSlope(Sample(curve, from, to, samples), from, to);
    }

    /// <summary><see cref="LogLogSlope(ITransferCurve, double, double, int)"/> for measured points; points outside
    /// [from, to] or with non-positive values are ignored.</summary>
    public static double LogLogSlope(IEnumerable<(double Encoded, double Linear)> points, double from = 0.1, double to = 0.9)
    {
        ArgumentNullException.ThrowIfNull(points);
        double sxy = 0, sxx = 0;
        var used = 0;
        foreach (var (encoded, linear) in points)
        {
            if (encoded < from || encoded > to || encoded <= 0 || encoded >= 1 || linear <= 0)
            {
                continue;
            }

            var lx = Math.Log(encoded);
            sxy += lx * Math.Log(linear);
            sxx += lx * lx;
            used++;
        }

        if (used == 0)
        {
            throw new ArgumentException("No usable points in the requested range.", nameof(points));
        }

        return sxy / sxx;
    }

    private static List<(double Encoded, double Linear)> Sample(ITransferCurve curve, double from, double to, int samples)
    {
        if (samples < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), samples, "Need at least two samples.");
        }

        if (!(from > 0 && to < 1 && to > from))
        {
            throw new ArgumentOutOfRangeException(nameof(from), from, "Need 0 < from < to < 1.");
        }

        var points = new List<(double, double)>(samples);
        for (var i = 0; i < samples; i++)
        {
            var e = from + ((to - from) * i / (samples - 1));
            points.Add((e, curve.ToLinear(e)));
        }

        return points;
    }
}
