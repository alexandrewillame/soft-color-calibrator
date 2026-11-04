namespace SoftCal.Core.Color;

/// <summary>CIE 1931 xy chromaticity coordinates (<see cref="X"/> = x, <see cref="Y"/> = y).</summary>
public readonly record struct Chromaticity(double X, double Y)
{
    /// <summary>Tristimulus values with the given luminance Y.</summary>
    public Xyz ToXyz(double luminance = 1.0)
    {
        if (Y <= 0)
        {
            throw new InvalidOperationException("Chromaticity y must be positive to compute XYZ.");
        }

        return new(X * luminance / Y, luminance, (1 - X - Y) * luminance / Y);
    }

    public UvPrime ToUvPrime()
    {
        var d = (-2 * X) + (12 * Y) + 3;
        return new(4 * X / d, 9 * Y / d);
    }

    public static Chromaticity FromUvPrime(UvPrime uv)
    {
        var d = (6 * uv.U) - (16 * uv.V) + 12;
        return new(9 * uv.U / d, 4 * uv.V / d);
    }
}

/// <summary>CIE 1976 UCS chromaticity coordinates u′v′.</summary>
public readonly record struct UvPrime(double U, double V)
{
    /// <summary>Euclidean distance Δu′v′, the usual measure of white-point error.</summary>
    public double DistanceTo(UvPrime other)
    {
        var du = U - other.U;
        var dv = V - other.V;
        return Math.Sqrt((du * du) + (dv * dv));
    }
}
