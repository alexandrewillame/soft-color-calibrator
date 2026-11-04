namespace SoftCal.Remote;

/// <summary>A picture of a region: luma row by row, upright, averaged over <paramref name="Frames"/> frames, packed in
/// <paramref name="Bits"/> bits per pixel (see <see cref="Yuv420Statistics.Pack"/>), with the first and last frames'
/// times on the phone's clock.</summary>
public sealed record LumaPicture(int Width, int Height, int Bits, byte[] Data, int Frames, double FirstTime, double LastTime)
{
    /// <summary>The mean luma of each pixel (0–255).</summary>
    public double[] Luma() => Yuv420Statistics.Unpack(Bits, Data, Width * Height);
}

/// <summary>An exposure as set: asked for, as the sensor reports it (null until a frame has it), and its epoch.</summary>
public readonly record struct ExposureOutcome(double Nominal, double? Actual, int Epoch);

/// <summary>A focus as set: the distance the camera reports (diopters), whether the lens still moved when the phone
/// stopped waiting for it, and the phone's time then.</summary>
public readonly record struct FocusOutcome(double? Reported, bool Moving, double Time);

/// <summary>
/// The phone's camera as the link drives it (the Android app implements it with Camera2, the iOS app with AVFoundation;
/// tests with a simulated camera). It
/// starts in automatic mode (exposure, white balance and focus follow the scene: for placing the phone), switches to
/// manual with <see cref="PrepareAsync"/> and back with <see cref="RestoreAsync"/>. Frames go to a stream's callback
/// on the camera's own thread; a picture can be taken while a stream runs.
/// </summary>
public interface IPhoneCamera
{
    PhoneCapabilities Capabilities { get; }

    /// <summary>The phone's clock (seconds), the one frame times are on.</summary>
    double Now { get; }

    Task<IReadOnlyList<ControlState>> PrepareAsync(LockSettings settings, CancellationToken cancellationToken);

    Task<ExposureOutcome> SetExposureAsync(double seconds, CancellationToken cancellationToken);

    /// <summary>Moves the locked camera's focus (diopters), keeping its exposure, and waits for the lens.</summary>
    Task<FocusOutcome> SetFocusAsync(double diopters, CancellationToken cancellationToken);

    /// <summary>Calls <paramref name="onFrame"/> for every frame from now on, until the result is disposed.</summary>
    IDisposable StartStream(RegionSpec region, Action<FrameMessage> onFrame);

    Task<LumaPicture> CaptureImageAsync(RegionSpec region, int frames, int skip, int maxWidth, CancellationToken cancellationToken);

    Task RestoreAsync(CancellationToken cancellationToken);

    /// <summary>Something went wrong outside any command (the camera was taken by another app, it overheats…).</summary>
    event EventHandler<FaultMessage>? Faulted;
}
