namespace SoftCal.Core.Color;

/// <summary>An additive RGB color space described by its primaries and white point chromaticities.</summary>
public sealed record RgbPrimaries(Chromaticity Red, Chromaticity Green, Chromaticity Blue, Chromaticity White)
{
    public static RgbPrimaries Srgb { get; } =
        new(new(0.64, 0.33), new(0.30, 0.60), new(0.15, 0.06), Illuminants.D65);

    public static RgbPrimaries DisplayP3 { get; } =
        new(new(0.680, 0.320), new(0.265, 0.690), new(0.150, 0.060), Illuminants.D65);

    public static RgbPrimaries AdobeRgb { get; } =
        new(new(0.64, 0.33), new(0.21, 0.71), new(0.15, 0.06), Illuminants.D65);

    /// <summary>
    /// Matrix converting linear RGB (white = 1, 1, 1) to XYZ (white luminance Y = 1).
    /// Each column is a primary's XYZ, scaled so that the three primaries add up to the white point.
    /// </summary>
    public Matrix3 RgbToXyz()
    {
        var primaries = Matrix3.FromColumns(
            Red.ToXyz().ToVec3(),
            Green.ToXyz().ToVec3(),
            Blue.ToXyz().ToVec3());
        var scale = primaries.Inverse() * White.ToXyz().ToVec3();
        return primaries * Matrix3.Diagonal(scale);
    }

    public Matrix3 XyzToRgb() => RgbToXyz().Inverse();

    /// <summary>Relative luminance (Y) contributed by each primary at full drive; the three sum to 1.</summary>
    public Vec3 PrimaryLuminances() => RgbToXyz().Row2;

    /// <summary>Area of the gamut triangle in the CIE 1976 u′v′ diagram.</summary>
    public double AreaUvPrime()
    {
        var r = Red.ToUvPrime();
        var g = Green.ToUvPrime();
        var b = Blue.ToUvPrime();
        return Math.Abs(((g.U - r.U) * (b.V - r.V)) - ((b.U - r.U) * (g.V - r.V))) / 2;
    }
}
