using SoftCal.Core.Color;

namespace SoftCal.Core.Instruments;

public enum InstrumentKind
{
    UsbWebcam,
    RemotePhone,
    Simulated,
    Colorimeter,
}

public sealed record InstrumentInfo(string Id, InstrumentKind Kind, string Model, string? Firmware = null);

public enum ExposureModel
{
    /// <summary>Any exposure time in [min, max] (phones with manual sensor control).</summary>
    Continuous,

    /// <summary>Only listed exposures (Windows UVC webcams: powers of two seconds).</summary>
    DiscreteSteps,
}

/// <summary>Camera controls that can alter a measurement and therefore must be locked or switched off.</summary>
public enum CameraControl
{
    WhiteBalance,
    Exposure,
    Gain,
    Focus,
    BacklightCompensation,
    Sharpness,
    Saturation,
    Contrast,
    Brightness,
    Gamma,
    Hue,
    PowerLineFrequency,
    VideoHdr,
    BackgroundEffects,
    EyeContact,
    AutoFraming,
    StudioEffects,
}

/// <summary>What an instrument can do; negotiated once when it connects (the phone sends this in its hello message).</summary>
public sealed record InstrumentCapabilities
{
    public required ExposureModel ExposureModel { get; init; }

    /// <summary>Nominal exposure times in seconds, ascending (for <see cref="ExposureModel.DiscreteSteps"/>).</summary>
    public IReadOnlyList<double> ExposureSteps { get; init; } = [];

    public required double MinExposureSeconds { get; init; }

    public required double MaxExposureSeconds { get; init; }

    /// <summary>True when frames report the exposure actually used. Windows webcams do not; their true ratios are fitted.</summary>
    public bool ReportsActualExposure { get; init; }

    /// <summary>True when channel values are proportional to light (RAW pipelines); false for gamma-encoded output.</summary>
    public bool OutputIsLinear { get; init; }

    /// <summary>Channel value of a saturated pixel (255 for 8-bit output).</summary>
    public required double FullScale { get; init; }

    public IReadOnlyList<CameraControl> LockableControls { get; init; } = [];

    /// <summary>Rolling-shutter line time, used for flicker analysis, when known.</summary>
    public double? RowLineTimeSeconds { get; init; }

    /// <summary>Instrument-supplied camera RGB → XYZ matrix (e.g. a DNG forward matrix) when available.</summary>
    public Matrix3? CameraToXyz { get; init; }
}

public sealed record LockRequest
{
    public int WhiteBalanceKelvin { get; init; } = 6500;

    public bool MinimizeGain { get; init; } = true;

    public bool MinimizeSharpness { get; init; } = true;

    /// <summary>Manual focus value; null lets the instrument choose its most defocused setting.</summary>
    public int? FocusValue { get; init; }

    public bool DisableEffects { get; init; } = true;
}

public enum LockOutcome
{
    Locked,
    NotSupported,
    Failed,

    /// <summary>The write succeeded but reading the value back returned something else.</summary>
    VerifiedMismatch,
}

public sealed record ControlLockResult(
    CameraControl Control,
    LockOutcome Outcome,
    double? Requested = null,
    double? Actual = null,
    string? Note = null);

public sealed record LockReport(IReadOnlyList<ControlLockResult> Controls)
{
    /// <summary>Controls that must be locked for any trustworthy measurement.</summary>
    public static IReadOnlyList<CameraControl> Critical { get; } =
        [CameraControl.WhiteBalance, CameraControl.Exposure, CameraControl.Gain];

    public bool CriticalControlsLocked =>
        Critical.All(c => Controls.Any(r => r.Control == c && r.Outcome == LockOutcome.Locked));
}

public sealed record ExposureRequest(double Seconds);

/// <summary>Exposure after a request. <paramref name="ActualSeconds"/> is null when the instrument cannot report it.</summary>
public sealed record ExposureState(double NominalSeconds, double? ActualSeconds, int Epoch);

/// <summary>Region of interest in normalized frame coordinates (0–1).</summary>
public sealed record RoiSpec(double CenterX = 0.5, double CenterY = 0.5, double Width = 0.3, double Height = 0.3)
{
    /// <summary>Number of horizontal bands in <see cref="FrameStats.RowBandMeans"/> (0 = none). Use one band per row of
    /// the region for flicker analysis.</summary>
    public int RowBands { get; init; } = 16;
}

/// <summary>Statistics of one color channel over the region of interest, in instrument units (see <see cref="InstrumentCapabilities.FullScale"/>).</summary>
public readonly record struct ChannelStats(double Mean, double StdDev, double ClippedFraction);

/// <summary>
/// One frame's statistics, timestamped on the instrument's own monotonic clock. <see cref="RowBandMeans"/> holds the mean
/// luma of each horizontal band of the region, top to bottom, in instrument units.
/// </summary>
public sealed record FrameStats(
    long Sequence,
    double TimestampSeconds,
    int ExposureEpoch,
    ChannelStats Red,
    ChannelStats Green,
    ChannelStats Blue,
    int PixelCount,
    IReadOnlyList<double>? RowBandMeans = null);

public enum InstrumentFaultKind
{
    Disconnected,
    ControlChangedExternally,
    AnotherAppStreaming,
    StreamStalled,
    Thermal,
    Other,
}

public sealed record InstrumentFault(InstrumentFaultKind Kind, string Message);
