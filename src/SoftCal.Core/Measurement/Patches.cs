using System.Text.Json.Serialization;

namespace SoftCal.Core.Measurement;

/// <summary>An 8-bit device color sent to the display, before the GPU's calibration curves.</summary>
public readonly record struct DeviceRgb(byte R, byte G, byte B)
{
    public static DeviceRgb Black => new(0, 0, 0);

    public static DeviceRgb White => new(255, 255, 255);

    public static DeviceRgb Gray(byte level) => new(level, level, level);

    public byte this[int channel] => channel switch
    {
        0 => R,
        1 => G,
        2 => B,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel must be 0, 1 or 2."),
    };

    /// <summary>A color with only one channel on, e.g. <c>Primary(1, 255)</c> is full green.</summary>
    public static DeviceRgb Primary(int channel, byte level) => channel switch
    {
        0 => new(level, 0, 0),
        1 => new(0, level, 0),
        2 => new(0, 0, level),
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel must be 0, 1 or 2."),
    };
}

/// <summary>How the two colors of a halftone are arranged. Every geometry except Bayer is a 50 % mix of 1-px elements.</summary>
public enum HalftoneGeometry
{
    Checkerboard,
    HorizontalLines,
    VerticalLines,

    /// <summary>4×4 ordered dither; the "on" fraction is <see cref="HalftonePatch.OnSixteenths"/>/16.</summary>
    Bayer4x4,
}

/// <summary>Where the patch sits (normalized screen coordinates) and the color that surrounds it.</summary>
public sealed record PatchLayout(double CenterX = 0.5, double CenterY = 0.5, double Size = 0.5, DeviceRgb Surround = default);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SolidPatch), "solid")]
[JsonDerivedType(typeof(HalftonePatch), "halftone")]
[JsonDerivedType(typeof(PlacementTargetPatch), "target")]
public abstract record PatchSpec
{
    /// <summary>Null lets the presenter use its default (close-up) layout.</summary>
    public PatchLayout? Layout { get; init; }
}

public sealed record SolidPatch(DeviceRgb Color) : PatchSpec;

/// <summary>
/// A spatial halftone of two colors. A defocused camera integrates it into the average of the two lights, which is the
/// basis of the null (matching) measurements.
/// </summary>
public sealed record HalftonePatch(DeviceRgb On, DeviceRgb Off, HalftoneGeometry Geometry, int OnSixteenths = 8) : PatchSpec
{
    public int OnSixteenths { get; init; } = Geometry == HalftoneGeometry.Bayer4x4
        ? (OnSixteenths is >= 0 and <= 16 ? OnSixteenths : throw new ArgumentOutOfRangeException(nameof(OnSixteenths), OnSixteenths, "Must be 0–16."))
        : (OnSixteenths == 8 ? 8 : throw new ArgumentOutOfRangeException(nameof(OnSixteenths), OnSixteenths, "Only Bayer4x4 supports fractions other than 8/16."));

    /// <summary>Fraction of pixels showing <see cref="On"/>.</summary>
    public double OnFraction => OnSixteenths / 16.0;
}

/// <summary>
/// Not a measurement patch: the aiming area for the camera. An outlined square in the middle of the patch, filled with
/// an even grid: the camera may rest anywhere inside, and the grid shows whether it looks straight at the screen (its
/// lines then look evenly spaced). There is no center mark, which would invite tilting the camera toward it.
/// </summary>
public sealed record PlacementTargetPatch : PatchSpec;

/// <summary>A patch as actually shown, with the presenter's best estimate of when it reached the screen.</summary>
public sealed record PresentedPatch(PatchSpec Patch, long Sequence, double PresentTimeSeconds);

/// <summary>Shows measurement patches on the display under test (a pixel-exact D3D window on Windows).</summary>
public interface IPatchPresenter
{
    Task<PresentedPatch> ShowAsync(PatchSpec patch, CancellationToken cancellationToken);
}
