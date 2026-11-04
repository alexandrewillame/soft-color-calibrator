using Wacton.Unicolour;
using UnicolourChromaticity = Wacton.Unicolour.Chromaticity;

namespace SoftCal.Core.Color;

/// <summary>Correlated color temperature and distance from the Planckian locus (CIE 1960 uv).</summary>
/// <param name="Kelvin">Correlated color temperature in kelvin.</param>
/// <param name="Duv">Signed distance from the Planckian locus; positive is above the locus (greenish).</param>
/// <param name="IsValid">False when the chromaticity is too far from the locus for CCT to be meaningful.</param>
public readonly record struct CorrelatedColorTemperature(double Kelvin, double Duv, bool IsValid);

/// <summary>CCT / Δuv conversions. Backed by Wacton.Unicolour, which uses the CIE 1931 2° observer.</summary>
public static class ColorTemperature
{
    public static CorrelatedColorTemperature FromChromaticity(Chromaticity xy)
    {
        var temperature = new Unicolour(new UnicolourChromaticity(xy.X, xy.Y)).Temperature;
        return new(temperature.Cct, temperature.Duv, temperature.IsValid);
    }

    /// <summary>Chromaticity at the given CCT and Δuv, measured from the Planckian (blackbody) locus.</summary>
    public static Chromaticity ToChromaticity(double kelvin, double duv = 0)
    {
        var c = new Unicolour(new Temperature(kelvin, duv)).Chromaticity;
        return new(c.X, c.Y);
    }

    /// <summary>Chromaticity of the CIE daylight illuminant (D series) at the given CCT; D65 ≈ 6504 K.</summary>
    public static Chromaticity Daylight(double kelvin)
    {
        var c = new Unicolour(kelvin, Locus.Daylight).Chromaticity;
        return new(c.X, c.Y);
    }
}
