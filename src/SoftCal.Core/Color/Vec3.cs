namespace SoftCal.Core.Color;

/// <summary>A plain three-component vector used for the linear algebra behind color conversions.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 Zero => new(0, 0, 0);

    public static Vec3 One => new(1, 1, 1);

    public double this[int index] => index switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be 0, 1 or 2."),
    };

    public double Sum => X + Y + Z;

    public double Max => Math.Max(X, Math.Max(Y, Z));

    public double Min => Math.Min(X, Math.Min(Y, Z));

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator *(double s, Vec3 a) => a * s;

    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    /// <summary>Element-wise product.</summary>
    public Vec3 Scale(Vec3 factors) => new(X * factors.X, Y * factors.Y, Z * factors.Z);

    /// <summary>Element-wise quotient.</summary>
    public Vec3 DivideBy(Vec3 divisors) => new(X / divisors.X, Y / divisors.Y, Z / divisors.Z);

    public double Dot(Vec3 other) => (X * other.X) + (Y * other.Y) + (Z * other.Z);

    public Vec3 Map(Func<double, double> f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return new(f(X), f(Y), f(Z));
    }
}
