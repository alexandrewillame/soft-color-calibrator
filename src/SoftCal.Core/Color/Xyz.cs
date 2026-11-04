namespace SoftCal.Core.Color;

/// <summary>CIE 1931 tristimulus values. Relative values use Y = 1 for the reference white.</summary>
public readonly record struct Xyz(double X, double Y, double Z)
{
    public Vec3 ToVec3() => new(X, Y, Z);

    public static Xyz FromVec3(Vec3 v) => new(v.X, v.Y, v.Z);

    public static Xyz operator +(Xyz a, Xyz b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Xyz operator -(Xyz a, Xyz b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Xyz operator *(Xyz a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    /// <summary>Returns the same chromaticity scaled to the given luminance.</summary>
    public Xyz WithLuminance(double y)
    {
        if (Y <= 0)
        {
            throw new InvalidOperationException("Cannot rescale a color with zero or negative luminance.");
        }

        return this * (y / Y);
    }

    /// <summary>CIE 1931 xy chromaticity.</summary>
    public Chromaticity ToChromaticity()
    {
        var sum = X + Y + Z;
        if (sum <= 0)
        {
            throw new InvalidOperationException("Chromaticity is undefined for a color with no stimulus.");
        }

        return new(X / sum, Y / sum);
    }

    /// <summary>CIE 1976 u′v′ chromaticity.</summary>
    public UvPrime ToUvPrime()
    {
        var d = X + (15 * Y) + (3 * Z);
        if (d <= 0)
        {
            throw new InvalidOperationException("u′v′ is undefined for a color with no stimulus.");
        }

        return new(4 * X / d, 9 * Y / d);
    }
}
