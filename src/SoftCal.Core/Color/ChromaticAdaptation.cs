namespace SoftCal.Core.Color;

/// <summary>Chromatic adaptation transforms, used for example to adapt display colorants to the ICC D50 PCS.</summary>
public static class ChromaticAdaptation
{
    /// <summary>The Bradford cone-response matrix (as used by ICC.1 for the 'chad' tag).</summary>
    public static Matrix3 BradfordConeResponse { get; } = new(
        0.8951, 0.2664, -0.1614,
        -0.7502, 1.7135, 0.0367,
        0.0389, -0.0685, 1.0296);

    /// <summary>Linear Bradford matrix adapting XYZ seen under <paramref name="sourceWhite"/> to <paramref name="destinationWhite"/>.</summary>
    public static Matrix3 Bradford(Xyz sourceWhite, Xyz destinationWhite)
    {
        var cone = BradfordConeResponse;
        var source = cone * sourceWhite.ToVec3();
        var destination = cone * destinationWhite.ToVec3();
        var gain = Matrix3.Diagonal(destination.DivideBy(source));
        return cone.Inverse() * gain * cone;
    }

    public static Xyz Adapt(Xyz color, Xyz sourceWhite, Xyz destinationWhite) =>
        Xyz.FromVec3(Bradford(sourceWhite, destinationWhite) * color.ToVec3());
}
