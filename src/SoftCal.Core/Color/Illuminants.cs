namespace SoftCal.Core.Color;

/// <summary>Standard white points (CIE 1931 2° observer).</summary>
public static class Illuminants
{
    /// <summary>D65 as used by sRGB and ITU-R BT.709 (x = 0.3127, y = 0.3290).</summary>
    public static Chromaticity D65 { get; } = new(0.3127, 0.3290);

    /// <summary>D50 (x = 0.3457, y = 0.3585).</summary>
    public static Chromaticity D50 { get; } = new(0.3457, 0.3585);

    /// <summary>CIE illuminant A, a 2856 K incandescent source.</summary>
    public static Chromaticity A { get; } = new(0.44757, 0.40745);

    /// <summary>The ICC profile connection space white (D50) exactly as defined by ICC.1: X = 0.9642, Y = 1, Z = 0.8249.</summary>
    public static Xyz IccD50 { get; } = new(0.9642, 1.0, 0.8249);
}
