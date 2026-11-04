using SoftCal.Instruments.Webcam.Frames;

namespace SoftCal.Instruments.Webcam.Capture;

/// <summary>One native media type of the camera's video stream.</summary>
public sealed record NativeFormat(
    int Index,
    string Subtype,
    int Width,
    int Height,
    double FrameRate,
    int? DefaultStride,
    YcbcrMatrix? Matrix,
    YcbcrRange? Range)
{
    /// <summary>The uncompressed layout, or null for formats that would need a decoder (MJPG, H.264).</summary>
    public PixelLayout? Layout => Subtype switch
    {
        "YUY2" => PixelLayout.Yuy2,
        "NV12" => PixelLayout.Nv12,
        _ => null,
    };
}

/// <summary>What the capture should prefer.</summary>
public sealed record FormatPreference(int Width = 640, int Height = 480, double FrameRate = 30);

/// <summary>Picks the native format: YUY2 first, NV12 as fallback, never a compressed one.</summary>
public static class FormatSelector
{
    public static NativeFormat? Choose(IEnumerable<NativeFormat> formats, FormatPreference preference)
    {
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(preference);
        return formats
            .Where(f => f.Layout is not null)
            .OrderBy(f => f.Layout == PixelLayout.Yuy2 ? 0 : 1)
            .ThenBy(f => Math.Abs(Math.Log((double)f.Width * f.Height / (preference.Width * preference.Height))))
            .ThenBy(f => Math.Abs(f.FrameRate - preference.FrameRate))
            .FirstOrDefault();
    }
}
