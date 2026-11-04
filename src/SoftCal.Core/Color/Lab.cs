namespace SoftCal.Core.Color;

/// <summary>CIE 1976 L*a*b* coordinates relative to a reference white.</summary>
public readonly record struct Lab(double L, double A, double B)
{
    private const double Delta = 6.0 / 29.0;

    public double Chroma => Math.Sqrt((A * A) + (B * B));

    public static Lab FromXyz(Xyz color, Xyz white)
    {
        var fx = F(color.X / white.X);
        var fy = F(color.Y / white.Y);
        var fz = F(color.Z / white.Z);
        return new((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    public Xyz ToXyz(Xyz white)
    {
        var fy = (L + 16) / 116;
        var fx = fy + (A / 500);
        var fz = fy - (B / 200);
        return new(white.X * FInverse(fx), white.Y * FInverse(fy), white.Z * FInverse(fz));
    }

    private static double F(double t) =>
        t > Delta * Delta * Delta ? Math.Cbrt(t) : (t / (3 * Delta * Delta)) + (4.0 / 29.0);

    private static double FInverse(double f) =>
        f > Delta ? f * f * f : 3 * Delta * Delta * (f - (4.0 / 29.0));
}
