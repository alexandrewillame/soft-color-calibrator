using SoftCal.Core.Color;

namespace SoftCal.Icc;

/// <summary>
/// The video card gamma table ('vcgt', Apple's private tag that Windows' calibration loader also reads): one ramp per
/// channel, 256 entries of 16 bits, loaded into the GPU so that frame-buffer code <c>i</c> leaves as <c>ramp[i]</c>.
/// </summary>
public sealed record VideoCardGamma(IReadOnlyList<ushort> Red, IReadOnlyList<ushort> Green, IReadOnlyList<ushort> Blue)
{
    public const int Entries = 256;

    public static VideoCardGamma Identity { get; } = new(IdentityRamp(), IdentityRamp(), IdentityRamp());

    public IReadOnlyList<ushort> this[int channel] => channel switch
    {
        0 => Red,
        1 => Green,
        2 => Blue,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel must be 0, 1 or 2."),
    };

    public bool IsIdentity => Enumerable.Range(0, 3).All(c => this[c].SequenceEqual(IdentityRamp()));

    private static ushort[] IdentityRamp() => [.. Enumerable.Range(0, Entries).Select(i => (ushort)(i * 257))];
}

/// <summary>
/// An RGB display profile (matrix/TRC), as SoftCal writes it: ICC v2.4, PCS XYZ D50, colorants adapted to D50 with
/// Bradford (recorded in 'chad'), the display's real white in 'wtpt', one tone curve per channel, and optionally the
/// calibration curves in 'vcgt'.
/// </summary>
/// <param name="Description">The name Windows shows for the profile.</param>
/// <param name="MediaWhite">The display's white, Y = 1 (not adapted).</param>
/// <param name="Adaptation">The Bradford matrix from <paramref name="MediaWhite"/> to the ICC D50.</param>
/// <param name="RedColorant">Each primary's XYZ at full drive, adapted to D50; the three add up to D50.</param>
/// <param name="ToneCurves">One curve per channel (R, G, B): device value → linear light, sampled evenly, 0–1.</param>
public sealed record IccProfile(
    string Description,
    string Copyright,
    Xyz MediaWhite,
    Matrix3 Adaptation,
    Xyz RedColorant,
    Xyz GreenColorant,
    Xyz BlueColorant,
    IReadOnlyList<IReadOnlyList<double>> ToneCurves,
    VideoCardGamma? Vcgt,
    DateTime CreatedUtc)
{
    /// <summary>
    /// A display profile from the display's primaries and white (e.g. its EDID), its tone curves as measured with the
    /// calibration curves applied, and those calibration curves.
    /// </summary>
    public static IccProfile ForDisplay(
        string description,
        RgbPrimaries primaries,
        IReadOnlyList<IReadOnlyList<double>> toneCurves,
        VideoCardGamma? vcgt,
        DateTime createdUtc,
        string copyright = "No copyright; created by Soft Color Calibrator.")
    {
        ArgumentNullException.ThrowIfNull(primaries);
        ArgumentNullException.ThrowIfNull(toneCurves);
        if (toneCurves.Count != 3)
        {
            throw new ArgumentException("A display profile needs one tone curve per channel.", nameof(toneCurves));
        }

        var white = primaries.White.ToXyz();
        var chad = ChromaticAdaptation.Bradford(white, Illuminants.IccD50);
        var adapted = chad * primaries.RgbToXyz();
        return new IccProfile(
            description,
            copyright,
            white,
            chad,
            Xyz.FromVec3(adapted.Column1),
            Xyz.FromVec3(adapted.Column2),
            Xyz.FromVec3(adapted.Column3),
            toneCurves,
            vcgt,
            createdUtc);
    }
}
