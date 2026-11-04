using SoftCal.Core.Analysis;
using SoftCal.Core.Measurement;
using SoftCal.Core.ToneCurves;

namespace SoftCal.Core.Calibration;

/// <summary>
/// One channel's measured response: the share of its own black-to-white light at each frame-buffer code, through the
/// gray tree's points. Between them it is interpolated in log-log space, where a display curve is nearly straight (the
/// gray tree halves the light at each level, so the points sit evenly there; in linear space a monotone cubic between
/// them errs by up to 2 %). Below the deepest point the light falls along that point's local power law to nothing at
/// the crush code.
/// </summary>
public sealed class ChannelCurve
{
    private readonly MonotoneCubic _logLog;
    private readonly double _deepestCode;
    private readonly double _deepestFraction;
    private readonly double _crush;
    private readonly double _slope;

    /// <param name="crush">The last code that gives no light; fractional for a refine pass, where it lies between
    /// two input codes.</param>
    public ChannelCurve(IEnumerable<(double Code, double Fraction)> points, double crush)
    {
        ArgumentNullException.ThrowIfNull(points);
        var kept = new List<(double Code, double Fraction)>();
        foreach (var p in points.Where(p => p.Code > crush + 0.5 && p.Code < 255 && p.Fraction is > 0 and < 1).OrderByDescending(p => p.Code))
        {
            // Strictly decreasing light toward black (noise at the bottom can tie two levels).
            if (kept.Count == 0 || (p.Code < kept[^1].Code - 1e-6 && p.Fraction < kept[^1].Fraction))
            {
                kept.Add(p);
            }
        }

        kept.Reverse();
        if (kept.Count == 0)
        {
            throw new ArgumentException("No usable response points.", nameof(points));
        }

        _logLog = new MonotoneCubic(
            [.. kept.Select(p => Math.Log(p.Code)), Math.Log(255)],
            [.. kept.Select(p => Math.Log(p.Fraction)), 0]);
        (_deepestCode, _deepestFraction, _crush) = (kept[0].Code, kept[0].Fraction, crush);
        var next = kept.Count > 1 ? kept[1] : (Code: 255.0, Fraction: 1.0);
        _slope = Math.Max(1, Math.Log(next.Fraction / _deepestFraction) / Math.Log((next.Code - crush) / (_deepestCode - crush)));
    }

    /// <summary>The last code that gives no light, from the near-black step test, and always below the deepest gray
    /// the tree measured light at: one noisy black reading can make every step up to code 20 look black.</summary>
    public static int Crush(DisplayMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        var steps = DisplayAnalysis.Steps(measurement).BlackCrushUpToCode ?? 0;
        return measurement.GrayTree.Count == 0
            ? steps
            : Math.Min(steps, Math.Max(0, (int)Math.Floor(measurement.GrayTree.Min(l => l.Null.MatchedCode)) - 1));
    }

    /// <summary>Red, green and blue's measured responses (each relative to its own black and white).</summary>
    public static IReadOnlyList<ChannelCurve> Measured(DisplayMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        var response = DisplayAnalysis.ChannelResponse(measurement.GrayTree);
        var crush = Crush(measurement);
        return [.. Enumerable.Range(0, 3).Select(c => new ChannelCurve(response.Select(p => (p.Code, p.ChannelFraction[c])), crush))];
    }

    /// <summary>The share of light at a code (0–255).</summary>
    public double Evaluate(double code)
    {
        if (code <= _crush)
        {
            return 0;
        }

        return code >= _deepestCode
            ? Math.Exp(_logLog.Evaluate(Math.Log(Math.Min(code, 255))))
            : _deepestFraction * Math.Pow((code - _crush) / (_deepestCode - _crush), _slope);
    }

    /// <summary>The code that gives a share of light.</summary>
    public double Inverse(double fraction)
    {
        if (fraction <= 0)
        {
            return _crush;
        }

        return fraction >= _deepestFraction
            ? Math.Exp(_logLog.Inverse(Math.Log(Math.Min(fraction, 1))))
            : _crush + ((_deepestCode - _crush) * Math.Pow(fraction / _deepestFraction, 1 / _slope));
    }

    /// <summary>The curve at the 256 codes, as a profile's tone curve.</summary>
    public double[] Sample() => [.. Enumerable.Range(0, 256).Select(code => Evaluate(code))];
}
