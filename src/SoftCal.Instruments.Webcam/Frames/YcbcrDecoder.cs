namespace SoftCal.Instruments.Webcam.Frames;

/// <summary>The Y′CbCr → R′G′B′ matrix a camera encodes with.</summary>
public enum YcbcrMatrix
{
    Bt601,
    Bt709,
}

/// <summary>Code range of the Y′CbCr samples: full (0–255, as in JPEG) or limited ("studio", Y′ 16–235, C 16–240).</summary>
public enum YcbcrRange
{
    Full,
    Limited,
}

/// <summary>
/// Converts the camera's 8-bit Y′CbCr to its R′G′B′ code values (0–255 nominal) as doubles, without clipping, so that
/// averaging and conversion commute: the RGB of the mean equals the mean of the per-pixel RGB.
/// </summary>
public sealed record YcbcrDecoder(YcbcrMatrix Matrix, YcbcrRange Range)
{
    public static YcbcrDecoder Bt601Full { get; } = new(YcbcrMatrix.Bt601, YcbcrRange.Full);

    private double Kr => Matrix == YcbcrMatrix.Bt601 ? 0.299 : 0.2126;

    private double Kb => Matrix == YcbcrMatrix.Bt601 ? 0.114 : 0.0722;

    /// <summary>Luma from R′G′B′ codes, so a row-band mean is in the same units as the channel means.</summary>
    public double Luma(double y) => Range == YcbcrRange.Full ? y : (y - 16) * 255.0 / 219.0;

    public (double R, double G, double B) ToRgb(double y, double cb, double cr)
    {
        double yn, cbn, crn;
        if (Range == YcbcrRange.Full)
        {
            yn = y;
            cbn = cb - 128;
            crn = cr - 128;
        }
        else
        {
            yn = (y - 16) * 255.0 / 219.0;
            cbn = (cb - 128) * 255.0 / 224.0;
            crn = (cr - 128) * 255.0 / 224.0;
        }

        var kr = Kr;
        var kb = Kb;
        var kg = 1 - kr - kb;
        var r = yn + (2 * (1 - kr) * crn);
        var b = yn + (2 * (1 - kb) * cbn);
        var g = (yn - (kr * r) - (kb * b)) / kg;
        return (r, g, b);
    }

    /// <summary>The inverse of <see cref="ToRgb"/>, for tests and synthetic frames.</summary>
    public (double Y, double Cb, double Cr) FromRgb(double r, double g, double b)
    {
        var kr = Kr;
        var kb = Kb;
        var yn = (kr * r) + ((1 - kr - kb) * g) + (kb * b);
        var cbn = (b - yn) / (2 * (1 - kb));
        var crn = (r - yn) / (2 * (1 - kr));
        return Range == YcbcrRange.Full
            ? (yn, cbn + 128, crn + 128)
            : ((yn * 219.0 / 255.0) + 16, (cbn * 224.0 / 255.0) + 128, (crn * 224.0 / 255.0) + 128);
    }
}
