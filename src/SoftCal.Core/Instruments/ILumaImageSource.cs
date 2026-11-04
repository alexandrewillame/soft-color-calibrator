namespace SoftCal.Core.Instruments;

/// <summary>The mean luma of every pixel of a region over several frames (row-major), e.g. to measure halftone ripple,
/// or one frame of it to show what the camera sees. Timestamps are on the instrument's clock, as its frames'.</summary>
public sealed record RoiImage(int Width, int Height, IReadOnlyList<double> Pixels, int Frames, double FirstTimestampSeconds, double LastTimestampSeconds)
{
    public double this[int x, int y] => Pixels[(y * Width) + x];
}

/// <summary>
/// A camera that also takes pictures (the USB webcam, the phone): what it sees, for placing it on the aiming area and
/// for checks on the picture itself.
/// </summary>
public interface ILumaImageSource
{
    /// <summary>Starts the camera with its own automatic settings, to show what it sees before a measurement locks it
    /// (nothing is changed, so there is nothing to restore).</summary>
    Task StartPreviewAsync(CancellationToken cancellationToken);

    /// <summary>Averages the luma of every pixel of the region over <paramref name="frames"/> frames, after skipping
    /// <paramref name="skipFrames"/>.</summary>
    Task<RoiImage> CaptureLumaImageAsync(RoiSpec roi, int frames, int skipFrames, CancellationToken cancellationToken);
}
