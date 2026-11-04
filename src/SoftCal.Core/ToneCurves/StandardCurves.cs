namespace SoftCal.Core.ToneCurves;

/// <summary>The piecewise sRGB transfer function (IEC 61966-2-1).</summary>
public sealed class SrgbCurve : ITransferCurve
{
    public static SrgbCurve Instance { get; } = new();

    private SrgbCurve()
    {
    }

    public double ToLinear(double encoded)
    {
        var v = Math.Clamp(encoded, 0, 1);
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    public double ToEncoded(double linear)
    {
        var l = Math.Clamp(linear, 0, 1);
        return l <= 0.0031308 ? 12.92 * l : (1.055 * Math.Pow(l, 1 / 2.4)) - 0.055;
    }
}

/// <summary>A pure power-law curve, linear = encoded^gamma.</summary>
public sealed class PowerCurve : ITransferCurve
{
    public PowerCurve(double gamma)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gamma);
        Gamma = gamma;
    }

    public double Gamma { get; }

    public double ToLinear(double encoded) => Math.Pow(Math.Clamp(encoded, 0, 1), Gamma);

    public double ToEncoded(double linear) => Math.Pow(Math.Clamp(linear, 0, 1), 1 / Gamma);
}

/// <summary>
/// ITU-R BT.1886 reference EOTF for a display whose black is <c>blackRelative</c> = L<sub>black</sub> / L<sub>white</sub>,
/// expressed as the fraction of the black-to-white span so that 0 maps to 0 and 1 maps to 1.
/// </summary>
public sealed class Bt1886Curve : ITransferCurve
{
    private readonly double _a;
    private readonly double _b;
    private readonly double _black;

    public Bt1886Curve(double blackRelative, double gamma = 2.4)
    {
        if (blackRelative is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(blackRelative), blackRelative, "Black must be in [0, 1).");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gamma);
        Gamma = gamma;
        _black = blackRelative;
        var whiteRoot = 1.0;
        var blackRoot = Math.Pow(blackRelative, 1 / gamma);
        _a = Math.Pow(whiteRoot - blackRoot, gamma);
        _b = blackRoot / (whiteRoot - blackRoot);
    }

    public double Gamma { get; }

    public double ToLinear(double encoded)
    {
        var v = Math.Clamp(encoded, 0, 1);
        var luminance = _a * Math.Pow(Math.Max(v + _b, 0), Gamma);
        return Math.Clamp((luminance - _black) / (1 - _black), 0, 1);
    }

    public double ToEncoded(double linear)
    {
        var luminance = _black + (Math.Clamp(linear, 0, 1) * (1 - _black));
        return Math.Clamp(Math.Pow(luminance / _a, 1 / Gamma) - _b, 0, 1);
    }
}
