namespace SoftCal.Core.Analysis;

/// <summary>A level change of the coded flash sequence, at the display time reported by the presenter.</summary>
public readonly record struct SyncTransition(double TimeSeconds, bool On);

/// <summary>A frame's reading during the sync sequence (linearized light, any scale).</summary>
public readonly record struct SyncSample(double TimeSeconds, double Value);

/// <summary>Result of the optical clock/latency calibration.</summary>
public sealed record SyncResult(
    double OffsetSeconds,
    double ResidualRms,
    double Contrast,
    double DriftPpm,
    int Frames,
    double OffsetFirstHalfSeconds,
    double OffsetSecondHalfSeconds)
{
    /// <summary>Residual noise relative to the on/off difference; above ~0.15 the fit is unreliable.</summary>
    public double RelativeResidual => Contrast > 0 ? ResidualRms / Contrast : double.PositiveInfinity;
}

/// <summary>
/// Optical calibration of the delay between a patch's display time (presenter clock) and the frame timestamps that see
/// it (instrument clock): one combined offset that includes the clock offset, the display's response and the camera
/// pipeline. A pseudo-random on/off sequence (≤ 3 Hz) is shown; each frame integrates the screen over its exposure
/// window, and the offset that best explains the frames is found by least squares with sub-frame precision.
/// </summary>
public static class OpticalSync
{
    /// <summary>A maximal-length (m-) sequence from a 5-bit LFSR: 31 symbols with a sharp autocorrelation.</summary>
    public static IReadOnlyList<bool> MSequence31()
    {
        var register = 0b10101;
        var bits = new bool[31];
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] = (register & 1) == 1;
            var feedback = ((register >> 0) ^ (register >> 2)) & 1; // x^5 + x^3 + 1
            register = (register >> 1) | (feedback << 4);
        }

        return bits;
    }

    /// <summary>Highest flash rate (on-off pairs per second) a symbol duration allows.</summary>
    public static double MaxFlashRateHz(double symbolSeconds) => 1 / (2 * symbolSeconds);

    /// <summary>Fraction of [start, end] during which the sequence is on.</summary>
    public static double OnFraction(IReadOnlyList<SyncTransition> transitions, double start, double end)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        if (end <= start)
        {
            return Level(transitions, start) ? 1 : 0;
        }

        double on = 0;
        var t = start;
        var level = Level(transitions, start);
        foreach (var tr in transitions)
        {
            if (tr.TimeSeconds <= start)
            {
                continue;
            }

            if (tr.TimeSeconds >= end)
            {
                break;
            }

            if (level)
            {
                on += tr.TimeSeconds - t;
            }

            t = tr.TimeSeconds;
            level = tr.On;
        }

        if (level)
        {
            on += end - t;
        }

        return on / (end - start);
    }

    /// <summary>
    /// Fits the offset: a frame stamped at τ integrates the screen over [τ − offset − exposure, τ − offset]. For each
    /// candidate offset the frames are regressed on the on-fraction (value = a + b·fraction); the offset with the
    /// smallest residual wins, refined by golden-section search.
    /// </summary>
    public static SyncResult Fit(
        IReadOnlyList<SyncTransition> transitions,
        IReadOnlyList<SyncSample> samples,
        double exposureSeconds,
        double minOffsetSeconds = -0.05,
        double maxOffsetSeconds = 0.6)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 10 || transitions.Count < 2)
        {
            throw new ArgumentException("The sync needs at least 10 frames and 2 transitions.", nameof(samples));
        }

        var (best, residual, contrast) = FitRange(transitions, samples, exposureSeconds, minOffsetSeconds, maxOffsetSeconds);
        var half = samples.Count / 2;
        var first = FitRange(transitions, samples.Take(half).ToList(), exposureSeconds, best - 0.05, best + 0.05).Offset;
        var second = FitRange(transitions, samples.Skip(half).ToList(), exposureSeconds, best - 0.05, best + 0.05).Offset;
        var span = samples[^1].TimeSeconds - samples[0].TimeSeconds;
        var drift = span > 0 ? (second - first) / (span / 2) * 1e6 : 0;
        return new SyncResult(best, residual, contrast, drift, samples.Count, first, second);
    }

    private static (double Offset, double Residual, double Contrast) FitRange(
        IReadOnlyList<SyncTransition> transitions,
        IReadOnlyList<SyncSample> samples,
        double exposure,
        double lo,
        double hi)
    {
        // Coarse grid (1 ms), then golden section around the best grid point.
        var bestOffset = lo;
        var bestCost = double.PositiveInfinity;
        for (var offset = lo; offset <= hi; offset += 0.001)
        {
            var cost = Cost(transitions, samples, exposure, offset).Rss;
            if (cost < bestCost)
            {
                bestCost = cost;
                bestOffset = offset;
            }
        }

        double a = bestOffset - 0.002, b = bestOffset + 0.002;
        var phi = (Math.Sqrt(5) - 1) / 2;
        var c = b - (phi * (b - a));
        var d = a + (phi * (b - a));
        for (var i = 0; i < 40; i++)
        {
            if (Cost(transitions, samples, exposure, c).Rss < Cost(transitions, samples, exposure, d).Rss)
            {
                b = d;
            }
            else
            {
                a = c;
            }

            c = b - (phi * (b - a));
            d = a + (phi * (b - a));
        }

        var final = (a + b) / 2;
        var (rss, slope) = Cost(transitions, samples, exposure, final);
        return (final, Math.Sqrt(rss / samples.Count), Math.Abs(slope));
    }

    private static (double Rss, double Slope) Cost(IReadOnlyList<SyncTransition> transitions, IReadOnlyList<SyncSample> samples, double exposure, double offset)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
        var n = samples.Count;
        foreach (var s in samples)
        {
            var end = s.TimeSeconds - offset;
            var x = OnFraction(transitions, end - exposure, end);
            sx += x;
            sy += s.Value;
            sxx += x * x;
            sxy += x * s.Value;
            syy += s.Value * s.Value;
        }

        var det = (n * sxx) - (sx * sx);
        if (Math.Abs(det) < 1e-12)
        {
            return (double.PositiveInfinity, 0);
        }

        var slope = ((n * sxy) - (sx * sy)) / det;
        var intercept = (sy - (slope * sx)) / n;
        var rss = syy - (intercept * sy) - (slope * sxy);
        return (Math.Max(rss, 0), slope);
    }

    private static bool Level(IReadOnlyList<SyncTransition> transitions, double time)
    {
        var level = false;
        foreach (var tr in transitions)
        {
            if (tr.TimeSeconds > time)
            {
                break;
            }

            level = tr.On;
        }

        return level;
    }
}
