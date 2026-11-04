namespace SoftCal.Core.ToneCurves;

/// <summary>
/// Shape-preserving piecewise cubic Hermite interpolation (PCHIP, Fritsch–Carlson with the end conditions used by MATLAB).
/// It never overshoots the data, so a monotone set of measurements always yields a monotone curve.
/// Outside the sampled range the end values are held constant.
/// </summary>
public sealed class MonotoneCubic
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _slopes;

    public MonotoneCubic(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Count != y.Count)
        {
            throw new ArgumentException("x and y must have the same number of points.", nameof(y));
        }

        if (x.Count < 2)
        {
            throw new ArgumentException("At least two points are required.", nameof(x));
        }

        _x = [.. x];
        _y = [.. y];
        for (var i = 1; i < _x.Length; i++)
        {
            if (!(_x[i] > _x[i - 1]))
            {
                throw new ArgumentException("x values must be strictly increasing.", nameof(x));
            }
        }

        _slopes = ComputeSlopes(_x, _y);
    }

    public IReadOnlyList<double> X => _x;

    public IReadOnlyList<double> Y => _y;

    /// <summary>True when the data never decreases, which makes <see cref="Inverse"/> well defined.</summary>
    public bool IsNonDecreasing
    {
        get
        {
            for (var i = 1; i < _y.Length; i++)
            {
                if (_y[i] < _y[i - 1])
                {
                    return false;
                }
            }

            return true;
        }
    }

    public double Evaluate(double x)
    {
        if (x <= _x[0])
        {
            return _y[0];
        }

        if (x >= _x[^1])
        {
            return _y[^1];
        }

        var k = FindInterval(x);
        var h = _x[k + 1] - _x[k];
        var t = (x - _x[k]) / h;
        var t2 = t * t;
        var t3 = t2 * t;
        return (((2 * t3) - (3 * t2) + 1) * _y[k])
            + ((t3 - (2 * t2) + t) * h * _slopes[k])
            + (((-2 * t3) + (3 * t2)) * _y[k + 1])
            + ((t3 - t2) * h * _slopes[k + 1]);
    }

    /// <summary>
    /// Smallest x whose interpolated value reaches <paramref name="y"/>, for non-decreasing data.
    /// Values outside the data range are clamped to the ends.
    /// </summary>
    public double Inverse(double y)
    {
        if (!IsNonDecreasing)
        {
            throw new InvalidOperationException("The inverse is only defined for non-decreasing data.");
        }

        if (y <= _y[0])
        {
            return _x[0];
        }

        if (y >= _y[^1])
        {
            return _x[^1];
        }

        var k = 0;
        while (k < _y.Length - 2 && _y[k + 1] < y)
        {
            k++;
        }

        // The interpolant is monotone on each interval, so bisection always converges.
        double lo = _x[k], hi = _x[k + 1];
        for (var i = 0; i < 80 && hi - lo > 1e-14 * Math.Max(1, Math.Abs(hi)); i++)
        {
            var mid = 0.5 * (lo + hi);
            if (Evaluate(mid) < y)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return 0.5 * (lo + hi);
    }

    private int FindInterval(double x)
    {
        var index = Array.BinarySearch(_x, x);
        if (index >= 0)
        {
            return Math.Min(index, _x.Length - 2);
        }

        return Math.Clamp(~index - 1, 0, _x.Length - 2);
    }

    private static double[] ComputeSlopes(double[] x, double[] y)
    {
        var n = x.Length;
        var slopes = new double[n];
        var h = new double[n - 1];
        var delta = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            h[i] = x[i + 1] - x[i];
            delta[i] = (y[i + 1] - y[i]) / h[i];
        }

        if (n == 2)
        {
            slopes[0] = slopes[1] = delta[0];
            return slopes;
        }

        for (var k = 1; k < n - 1; k++)
        {
            if (delta[k - 1] * delta[k] <= 0)
            {
                slopes[k] = 0;
            }
            else
            {
                var w1 = (2 * h[k]) + h[k - 1];
                var w2 = h[k] + (2 * h[k - 1]);
                slopes[k] = (w1 + w2) / ((w1 / delta[k - 1]) + (w2 / delta[k]));
            }
        }

        slopes[0] = EndSlope(h[0], h[1], delta[0], delta[1]);
        slopes[n - 1] = EndSlope(h[n - 2], h[n - 3], delta[n - 2], delta[n - 3]);
        return slopes;
    }

    private static double EndSlope(double h0, double h1, double d0, double d1)
    {
        var slope = (((2 * h0) + h1) * d0 - (h0 * d1)) / (h0 + h1);
        if (Math.Sign(slope) != Math.Sign(d0))
        {
            return 0;
        }

        if (Math.Sign(d0) != Math.Sign(d1) && Math.Abs(slope) > Math.Abs(3 * d0))
        {
            return 3 * d0;
        }

        return slope;
    }
}
