namespace SoftCal.Core.ToneCurves;

/// <summary>
/// A monotone transfer curve between an encoded signal and relative linear light, both on [0, 1].
/// For display targets, "linear" means the fraction of the black-to-white span: output = black + linear × (white − black).
/// </summary>
public interface ITransferCurve
{
    /// <summary>Maps an encoded signal (0 = black code, 1 = white code) to relative linear light.</summary>
    double ToLinear(double encoded);

    /// <summary>Inverse of <see cref="ToLinear"/>.</summary>
    double ToEncoded(double linear);
}
