using System.Globalization;
using AVFoundation;
using CoreFoundation;
using CoreMedia;
using CoreVideo;
using SoftCal.Remote;

namespace SoftCal.Phone.iOS;

/// <summary>
/// The device's main back camera (the wide one) through AVFoundation, as the link drives it. One capture session feeds a
/// small full-range 4:2:0 output (about 640×480, as on Android): automatic while the phone is being placed, manual once
/// a measurement locks it — the exposure SoftCal sets, the lowest ISO, white-balance gains for the color temperature
/// asked (6500 K: D65), the farthest focus (which blurs a screen a few centimetres away), video HDR off and one tone
/// curve for the whole picture.
/// <para>
/// iOS gives apps no raw frames while it streams (only raw photos, one at a time) and no fixed tone curve, so the
/// statistics are the processed frames', whose tone curve may follow the scene: what Android does on a phone without
/// raw data. SoftCal's readings compare patches through the same curve, as with a webcam.
/// </para>
/// <para>
/// Every change of settings has its epoch. AVFoundation reports the first frame each change holds for (its sync time),
/// so every frame says exactly which exposure it was taken with. Frame times are on <see cref="Now"/> (the host clock,
/// the capture session's): when the frame's last row finished exposing, at the latest.
/// </para>
/// </summary>
internal sealed class PhoneCamera : IPhoneCamera, IDisposable
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(4);

    /// <summary>"The farthest", asked as 20 m rather than exactly 0, as on Android.</summary>
    private const double Farthest = 0.05;

    /// <summary>The host clock: frame times, sync times and <see cref="Now"/> are all on it.</summary>
    private static readonly CMClock Host = CMClock.HostTimeClock;

    private readonly AVCaptureDevice _device;
    private readonly AVCaptureDeviceFormat _format;
    private readonly AVFrameRateRange _rates;
    private readonly AVCaptureVideoDataOutput _output;
    private readonly FrameDelegate _frameDelegate;
    private readonly DispatchQueue _sessionQueue = new("SoftCal camera");
    private readonly DispatchQueue _frameQueue = new("SoftCal frames");
    private readonly object _gate = new();
    private readonly List<StreamSink> _streams = [];
    private readonly List<PictureSink> _pictures = [];
    private readonly List<(double Since, int Epoch)> _epochs = [];
    private readonly Dictionary<int, double> _exposureOfEpoch = [];
    private readonly SemaphoreSlim _opening = new(1, 1);
    private readonly Timer _idle;
    private readonly NSObject[] _observers;
    private readonly float _iso;

    /// <summary>No row of a frame reads out later than this after its first: the shortest frame time the format allows.</summary>
    private readonly double _readout;

    /// <summary>The session's clock, once it runs (the host clock on every device so far).</summary>
    private CMClock? _clock;

    /// <summary>The frames' Y′CbCr matrix, as the first one said.</summary>
    private YcbcrMatrix? _matrix;

    private volatile bool _running;

    /// <summary>Closes so far: a start that a close overtook does not count as open.</summary>
    private int _closes;
    private int _epoch;
    private bool _manual;
    private LockSettings _lock = new(6500, null);
    private double _exposure;
    private long _sequence;
    private long _framesSeen;
    private long _lastUse = Environment.TickCount64;
    private volatile int _deviceRotation;

    private PhoneCamera(AVCaptureDevice device)
    {
        _device = device;
        (_format, _rates) = ChooseFormat(device);
        _iso = _format.MinISO;
        _readout = _rates.MinFrameDuration.Seconds;
        var dimensions = Dimensions(_format);
        Size = (dimensions.Width, dimensions.Height);
        ManualSensor = device.IsExposureModeSupported(AVCaptureExposureMode.Custom);
        MinExposure = _format.MinExposureDuration.Seconds;
        MaxExposure = Math.Min(_format.MaxExposureDuration.Seconds, _rates.MaxFrameDuration.Seconds);

        // The nearest focus, in millimetres (−1 when the camera does not say).
        MinimumFocusDiopters = device.MinimumFocusDistance > 0 ? 1000.0 / device.MinimumFocusDistance : null;

        // Wide color off: the frames stay sRGB, as Android's and the webcam's.
        Session = new AVCaptureSession { AutomaticallyConfiguresCaptureDeviceForWideColor = false };
        _frameDelegate = new FrameDelegate(this);
        _output = new AVCaptureVideoDataOutput
        {
            AlwaysDiscardsLateVideoFrames = true,
            UncompressedVideoSetting = new AVVideoSettingsUncompressed { PixelFormatType = CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange },
        };
        _output.SetSampleBufferDelegate(_frameDelegate, _frameQueue);

        Session.BeginConfiguration();
        try
        {
            var input = AVCaptureDeviceInput.FromDevice(device, out var error)
                ?? throw new InvalidOperationException("The camera could not be opened: " + error?.LocalizedDescription);
            if (!Session.CanAddInput(input) || !Session.CanAddOutput(_output))
            {
                throw new InvalidOperationException("The camera cannot stream its frames to the app.");
            }

            Session.AddInput(input);
            Session.AddOutput(_output);
            Configure(d =>
            {
                d.ActiveFormat = _format;
                if (_format.SupportedColorSpaces.Contains(AVCaptureColorSpace.Srgb))
                {
                    d.ActiveColorSpace = AVCaptureColorSpace.Srgb;
                }

                // Video HDR merges several exposures into each frame: never.
                if (_format.IsVideoHdrSupported)
                {
                    d.AutomaticallyAdjustsVideoHdrEnabled = false;
                    d.VideoHdrEnabled = false;
                }

                if (d.LowLightBoostSupported)
                {
                    d.AutomaticallyEnablesLowLightBoostWhenAvailable = false;
                }

                Automatic(d);
            });

            if (_output.ConnectionFromMediaType(AVMediaTypes.Video.GetConstant()!) is { } connection && connection.SupportsVideoStabilization)
            {
                connection.PreferredVideoStabilizationMode = AVCaptureVideoStabilizationMode.Off;
            }
        }
        finally
        {
            Session.CommitConfiguration();
        }

        NoLowLightNoiseReduction();

        _observers =
        [
            AVCaptureSession.Notifications.ObserveWasInterrupted(Session, (_, e) => OnInterrupted(e.Notification)),
            AVCaptureSession.Notifications.ObserveRuntimeError(Session, (_, e) => OnFailed(e.Error)),
        ];
        _idle = new Timer(_ => CloseWhenIdle(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>Something went wrong outside a command (another app took the camera, the app left the screen).</summary>
    public event EventHandler<FaultMessage>? Faulted;

    /// <summary>True when a measurement locked the camera, false when it went back to automatic.</summary>
    public event EventHandler<bool>? ModeChanged;

    /// <summary>The capture session, for the screen's preview of it.</summary>
    public AVCaptureSession Session { get; }

    /// <summary>The frames' size, as the sensor gives them (landscape).</summary>
    public (int Width, int Height) Size { get; }

    /// <summary>The clockwise turn that makes a frame upright on a device held upright: the back camera's frames come
    /// landscape, as on Android.</summary>
    public int SensorOrientation => 90;

    public bool ManualSensor { get; }

    public double MinExposure { get; }

    public double MaxExposure { get; }

    public double? MinimumFocusDiopters { get; }

    public bool IsOpen => _running;

    /// <summary>The camera is closed once SoftCal has not needed it for this long (and opened again when it does).</summary>
    public static TimeSpan IdleAfter { get; } = TimeSpan.FromSeconds(10);

    /// <summary>The region SoftCal measures now, as given on its upright picture, and the turn that picture had (null:
    /// no measurement running).</summary>
    public (RegionSpec Region, int Rotation)? Measured
    {
        get
        {
            lock (_gate)
            {
                return _streams.LastOrDefault() is { Rotation: { } turn } stream ? (stream.Region, turn) : null;
            }
        }
    }

    /// <summary>Frames the camera delivered since it was created (the screen shows the rate).</summary>
    public long FramesSeen => Interlocked.Read(ref _framesSeen);

    /// <summary>The device's turn from upright portrait, in degrees clockwise, from its accelerometer: pictures are
    /// turned so that up is up as the device stands.</summary>
    public int DeviceRotation
    {
        get => _deviceRotation;
        set => _deviceRotation = ((value % 360) + 360) % 360;
    }

    public PhoneCapabilities Capabilities => new(
        MinExposure,
        MaxExposure,
        (int)Math.Ceiling(_format.MinISO),
        (int)Math.Floor(_format.MaxISO),
        ManualSensor,
        ManualPostProcessing: false,
        RealtimeTimestamps: true,
        Size.Height,
        Size.Width,
        Math.Min(30, _rates.MaxFrameRate),
        RowTime: null,
        ["white-balance gains for any color temperature"],
        MinimumFocusDiopters,
        Raw: false);

    public double Now => Host.CurrentTime.Seconds;

    private bool HasFocus => MinimumFocusDiopters is > 0 && _device.LockingFocusWithCustomLensPositionSupported;

    private bool CanLockWhiteBalance =>
        _device.LockingWhiteBalanceWithCustomDeviceGainsSupported && _device.IsWhiteBalanceModeSupported(AVCaptureWhiteBalanceMode.Locked);

    /// <summary>The main back camera: the wide one, never a virtual camera that switches lenses by itself.</summary>
    public static PhoneCamera Create()
    {
        var device = AVCaptureDevice.GetDefaultDevice(AVCaptureDeviceType.BuiltInWideAngleCamera, AVMediaTypes.Video, AVCaptureDevicePosition.Back)
            ?? throw new InvalidOperationException("This device has no camera on its back.");
        return new PhoneCamera(device);
    }

    /// <summary>Starts the camera in automatic (again, after iOS stopped it). One opening at a time.</summary>
    public async Task OpenAsync()
    {
        await _opening.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_running)
            {
                return;
            }

            int epoch;
            int closes;
            lock (_gate)
            {
                _manual = false;
                epoch = ++_epoch;
                closes = _closes;
            }

            await OnSessionQueue(() =>
            {
                Configure(Automatic);
                Session.StartRunning();
                if (!Session.Running)
                {
                    // Not left to start by itself when the interruption ends: SoftCal starts it again when it needs it.
                    var interrupted = Session.Interrupted;
                    Session.StopRunning();
                    throw new InvalidOperationException(interrupted
                        ? "The camera is in use by another app, or SoftCal Camera is not in front."
                        : "The camera did not start.");
                }

                _clock ??= Session.SynchronizationClock;
            }).ConfigureAwait(false);
            lock (_gate)
            {
                // Closed meanwhile (the app left the screen, the camera failed): its stop runs after this start.
                if (_closes != closes)
                {
                    throw new InvalidOperationException("The phone's camera closed while it started.");
                }

                _running = true;
            }

            Begin(epoch, Now);
        }
        finally
        {
            _opening.Release();
        }
    }

    public async Task<IReadOnlyList<ControlState>> PrepareAsync(LockSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!ManualSensor)
        {
            throw new InvalidOperationException("This device's camera does not let apps set its exposure, so it cannot measure.");
        }

        await EnsureOpenAsync().ConfigureAwait(false);
        int epoch;
        double exposure;
        lock (_gate)
        {
            _lock = settings;
            _manual = true;

            // Start from what the automatic exposure chose: SoftCal's own exposure control takes it from there.
            _exposure = Math.Clamp(_device.ExposureDuration.Seconds, MinExposure, MaxExposure);
            exposure = _exposure;
            epoch = ++_epoch;
            _exposureOfEpoch[epoch] = exposure;
            _exposureOfEpoch.Remove(epoch - 64);
        }

        var applied = await LockAsync(exposure, settings, focusDiopters: null).ConfigureAwait(false);
        var synced = await SyncedAsync(applied, LockTimeout + TimeSpan.FromSeconds(exposure * 4), cancellationToken).ConfigureAwait(false);
        Begin(epoch, synced);
        ModeChanged?.Invoke(this, true);
        return Verify(exposure);
    }

    public async Task<ExposureOutcome> SetExposureAsync(double seconds, CancellationToken cancellationToken)
    {
        await EnsureOpenAsync().ConfigureAwait(false);
        if (!ManualSensor)
        {
            throw new InvalidOperationException("This device's camera does not let apps set its exposure.");
        }

        var exposure = Math.Clamp(seconds, MinExposure, MaxExposure);
        int epoch;
        bool wasManual;
        LockSettings settings;
        lock (_gate)
        {
            wasManual = _manual;
            _manual = true;
            _exposure = exposure;
            settings = _lock;
            epoch = ++_epoch;
            _exposureOfEpoch[epoch] = exposure;
            _exposureOfEpoch.Remove(epoch - 64);
        }

        // Unlocked so far: everything else is locked with it, as Android's manual request does.
        var applied = wasManual ? await ExposeAsync(exposure).ConfigureAwait(false) : await LockAsync(exposure, settings, null).ConfigureAwait(false);
        if (!wasManual)
        {
            ModeChanged?.Invoke(this, true);
        }

        Begin(epoch, await SyncedAsync(applied, TimeSpan.FromSeconds(1.5 + (4 * exposure)), cancellationToken).ConfigureAwait(false));
        return new ExposureOutcome(exposure, _device.ExposureDuration.Seconds, epoch);
    }

    /// <summary>When the last of some changes holds from: the latest of their sync times (host seconds).</summary>
    private async Task<double> SyncedAsync(Task<CMTime[]> applied, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var synced = await applied.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return synced.Length == 0 ? Now : synced.Max(HostSeconds);
        }
        catch (TimeoutException ex)
        {
            // AVFoundation reports a change with the first frame that has it: none came.
            throw new InvalidOperationException("The phone's camera sends no frames: close the other apps that may use it, then bring SoftCal Camera back to the front.", ex);
        }
    }

    /// <summary>Moves the locked camera's focus, its exposure and epoch unchanged (the PC searches for the setting that
    /// blurs the screen best, judging the picture), and waits for the lens.</summary>
    public async Task<FocusOutcome> SetFocusAsync(double diopters, CancellationToken cancellationToken)
    {
        await EnsureOpenAsync().ConfigureAwait(false);
        if (!HasFocus)
        {
            return new FocusOutcome(null, false, Now);
        }

        lock (_gate)
        {
            if (!_manual)
            {
                throw new InvalidOperationException("The camera is not locked: SoftCal locks it before moving its focus.");
            }

            _lock = _lock with { FocusDiopters = diopters };
        }

        var moved = new TaskCompletionSource<CMTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnSessionQueue(() => Configure(d => d.SetFocusModeLocked(LensPosition(diopters), t => moved.TrySetResult(t)))).ConfigureAwait(false);
        try
        {
            await moved.Task.WaitAsync(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The lens is said to be still on its way.
        }

        // A little longer for lenses that say they have arrived before they have.
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        return new FocusOutcome(Diopters(_device.LensPosition), _device.AdjustingFocus, Now);
    }

    public IDisposable StartStream(RegionSpec region, Action<FrameMessage> onFrame)
    {
        var sink = new StreamSink(region, onFrame);
        lock (_gate)
        {
            _streams.Add(sink);
        }

        // The frames start once the camera is on (a stream right after a lock finds it on already).
        _ = StartStreamingAsync();
        return new Unregister(() =>
        {
            lock (_gate)
            {
                _streams.Remove(sink);
            }

            Touch();
        });
    }

    public async Task<LumaPicture> CaptureImageAsync(RegionSpec region, int frames, int skip, int maxWidth, CancellationToken cancellationToken)
    {
        await EnsureOpenAsync().ConfigureAwait(false);
        var sink = new PictureSink(region, frames, skip, maxWidth);
        lock (_gate)
        {
            _pictures.Add(sink);
        }

        var seen = FramesSeen;
        try
        {
            var frameTime = Math.Max(1.0 / 30, _exposure);
            return await sink.Done.Task.WaitAsync(TimeSpan.FromSeconds(5 + ((frames + skip) * frameTime)), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                FramesSeen == seen
                    ? "The phone's camera sends no frames: close the other apps that may use it, then bring SoftCal Camera back to the front."
                    : "The phone's camera sends frames, but no picture could be made from them.",
                ex);
        }
        finally
        {
            lock (_gate)
            {
                _pictures.Remove(sink);
            }
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        int epoch;
        bool wasManual;
        lock (_gate)
        {
            wasManual = _manual;
            _manual = false;
            epoch = ++_epoch;
        }

        if (_running)
        {
            await OnSessionQueue(() => Configure(Automatic)).ConfigureAwait(false);
            Begin(epoch, Now);
        }

        if (wasManual)
        {
            ModeChanged?.Invoke(this, false);
        }

        Touch();
    }

    /// <summary>The app leaves the screen: iOS stops the camera anyway; it starts again when SoftCal next needs it.</summary>
    public void Suspend()
    {
        bool wasManual;
        lock (_gate)
        {
            wasManual = _manual;
            _manual = false;
        }

        Close();
        if (wasManual)
        {
            ModeChanged?.Invoke(this, false);
        }
    }

    public void Dispose()
    {
        _idle.Dispose();
        foreach (var observer in _observers)
        {
            observer.Dispose();
        }

        Close();
    }

    /// <summary>Starts the camera when it is off (SoftCal needs it), and notes the use: it is stopped again once SoftCal
    /// has not needed it for <see cref="IdleAfter"/>.</summary>
    private async Task EnsureOpenAsync()
    {
        Touch();
        if (!_running)
        {
            await OpenAsync().ConfigureAwait(false);
        }
    }

    private async Task StartStreamingAsync()
    {
        try
        {
            await EnsureOpenAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A stream whose camera cannot start sends nothing: the PC's stall check says so.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Faulted?.Invoke(this, new FaultMessage("Disconnected", "The camera could not be started: " + ex.Message));
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastUse, Environment.TickCount64);

    /// <summary>Every two seconds: a camera SoftCal has not needed for a while is stopped (to spare the battery). Never
    /// while it is locked for a measurement, streams, or takes a picture.</summary>
    private void CloseWhenIdle()
    {
        if (!_running || Environment.TickCount64 - Interlocked.Read(ref _lastUse) < IdleAfter.TotalMilliseconds)
        {
            return;
        }

        lock (_gate)
        {
            if (_manual || _streams.Count > 0 || _pictures.Count > 0)
            {
                return;
            }
        }

        // Not while it is being started (that is a use).
        if (!_opening.Wait(0))
        {
            return;
        }

        try
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastUse) >= IdleAfter.TotalMilliseconds)
            {
                Close();
            }
        }
        finally
        {
            _opening.Release();
        }
    }

    /// <summary>A 4:3 full-range 4:2:0 format (4:3 is the whole sensor) nearest 640×480 that runs at 30 frames a second,
    /// the widest view and longest exposures first; and its frame rates.</summary>
    private static (AVCaptureDeviceFormat Format, AVFrameRateRange Rates) ChooseFormat(AVCaptureDevice device)
    {
        static double Cost(CMVideoDimensions s) =>
            Math.Abs(Math.Log((double)s.Width * s.Height / (640.0 * 480))) + (Math.Abs(((double)s.Width / s.Height) - (4.0 / 3)) < 0.01 ? 0 : 1);

        var chosen = device.Formats
            .Where(f => f.FormatDescription?.MediaSubType == (uint)CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange && Dimensions(f).Width > 0)
            .Select(f => (Format: f, Rates: f.VideoSupportedFrameRateRanges.Where(r => r.MaxFrameRate >= 29.9).MaxBy(r => r.MaxFrameDuration.Seconds)))
            .Where(f => f.Rates is not null)
            .OrderBy(f => Cost(Dimensions(f.Format)))
            .ThenByDescending(f => f.Format.VideoFieldOfView)
            .ThenByDescending(f => f.Format.MaxExposureDuration.Seconds)
            .FirstOrDefault();
        return chosen.Format is null
            ? throw new InvalidOperationException("The camera offers no video format of the kind needed.")
            : (chosen.Format, chosen.Rates!);
    }

    /// <summary>A format's frame size, through the wrapper of its own kind (the video one).</summary>
    private static CMVideoDimensions Dimensions(AVCaptureDeviceFormat format) =>
        format.FormatDescription is { } description && CMFormatDescription.Create(description.Handle) is CMVideoFormatDescription video
            ? video.Dimensions
            : default;

    /// <summary>
    /// iOS 27's extra noise reduction in low light off, where the connection takes it in the format now set: its own
    /// automatic choice first, as AVFoundation asks. Optional, so a camera that refuses it measures anyway. The
    /// properties are reached by name (key-value coding): iOS libraries older than iOS 27's, such as those GitHub's Macs
    /// build with, do not have them.
    /// </summary>
    private void NoLowLightNoiseReduction()
    {
        using var supported = new NSString("lowLightVideoNoiseReductionSupported");
        if (!OperatingSystem.IsIOSVersionAtLeast(27)
            || _output.ConnectionFromMediaType(AVMediaTypes.Video.GetConstant()!) is not { } connection
            || !connection.RespondsToSelector(new ObjCRuntime.Selector("isLowLightVideoNoiseReductionSupported"))
            || connection.ValueForKey(supported) is not NSNumber { BoolValue: true })
        {
            return;
        }

        try
        {
            // Not disposed: a boolean NSNumber is a constant that other wrappers share.
            var off = NSNumber.FromBoolean(false);
            using var automatic = new NSString("automaticallyEnablesLowLightVideoNoiseReduction");
            using var enabled = new NSString("lowLightVideoNoiseReductionEnabled");
            connection.SetValueForKey(off, automatic);
            connection.SetValueForKey(off, enabled);
        }
        catch (ObjCRuntime.ObjCException)
        {
            // Left as iOS chose it.
        }
    }

    /// <summary>Changes the camera's settings, holding it for that (AVFoundation's rule).</summary>
    private void Configure(Action<AVCaptureDevice> change)
    {
        if (!_device.LockForConfiguration(out var error))
        {
            throw new InvalidOperationException("The camera's settings could not be changed: " + error?.LocalizedDescription);
        }

        try
        {
            change(_device);
        }
        finally
        {
            _device.UnlockForConfiguration();
        }
    }

    /// <summary>Runs on the session's queue, in turn with everything else that starts, stops or sets the camera (iOS asks
    /// that the session never start on the main thread).</summary>
    private Task OnSessionQueue(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionQueue.DispatchAsync(() =>
        {
            try
            {
                action();
                done.TrySetResult();
            }
#pragma warning disable CA1031 // Handed to the caller.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>Automatic: for placing the phone, 30 frames a second (set once the exposure is automatic again: a locked
    /// long exposure would not fit in a 30th of a second).</summary>
    private void Automatic(AVCaptureDevice device)
    {
        if (device.IsExposureModeSupported(AVCaptureExposureMode.ContinuousAutoExposure))
        {
            device.ExposureMode = AVCaptureExposureMode.ContinuousAutoExposure;
        }

        SetFrameRate(device, longExposures: false);

        if (device.IsWhiteBalanceModeSupported(AVCaptureWhiteBalanceMode.ContinuousAutoWhiteBalance))
        {
            device.WhiteBalanceMode = AVCaptureWhiteBalanceMode.ContinuousAutoWhiteBalance;
        }

        if (device.IsFocusModeSupported(AVCaptureFocusMode.ContinuousAutoFocus))
        {
            device.FocusMode = AVCaptureFocusMode.ContinuousAutoFocus;
        }

        if (_format.GlobalToneMappingSupported)
        {
            device.GlobalToneMappingEnabled = false;
        }
    }

    /// <summary>
    /// Everything that would follow the scene, held: the exposure and the lowest ISO, white-balance gains for the color
    /// temperature asked, the focus, and one tone curve for the whole picture (not one per area). The task gives the
    /// sync time of each change: its first frame.
    /// </summary>
    private async Task<Task<CMTime[]>> LockAsync(double exposure, LockSettings settings, double? focusDiopters)
    {
        var changes = new List<Task<CMTime>>();
        await OnSessionQueue(() => Configure(d =>
        {
            if (_format.GlobalToneMappingSupported)
            {
                d.GlobalToneMappingEnabled = true;
            }

            changes.Add(Expose(d, exposure));
            if (CanLockWhiteBalance)
            {
                var applied = new TaskCompletionSource<CMTime>(TaskCreationOptions.RunContinuationsAsynchronously);
                d.SetWhiteBalanceModeLockedWithDeviceWhiteBalanceGains(Gains(settings.WhiteBalanceKelvin), t => applied.TrySetResult(t));
                changes.Add(applied.Task);
            }

            if (HasFocus)
            {
                var moved = new TaskCompletionSource<CMTime>(TaskCreationOptions.RunContinuationsAsynchronously);
                d.SetFocusModeLocked(LensPosition(focusDiopters ?? settings.FocusDiopters ?? Farthest), t => moved.TrySetResult(t));
                changes.Add(moved.Task);
            }
        })).ConfigureAwait(false);
        return Task.WhenAll(changes);
    }

    /// <summary>A new exposure for the locked camera, the rest held.</summary>
    private async Task<Task<CMTime[]>> ExposeAsync(double exposure)
    {
        Task<CMTime>? applied = null;
        await OnSessionQueue(() => Configure(d => applied = Expose(d, exposure))).ConfigureAwait(false);
        return Task.WhenAll(applied!);
    }

    /// <summary>The exposure and the lowest ISO, frames as long as the exposure needs.</summary>
    private Task<CMTime> Expose(AVCaptureDevice device, double exposure)
    {
        SetFrameRate(device, longExposures: true);
        var applied = new TaskCompletionSource<CMTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var duration = exposure <= _format.MinExposureDuration.Seconds ? _format.MinExposureDuration
            : exposure >= _format.MaxExposureDuration.Seconds ? _format.MaxExposureDuration
            : CMTime.FromSeconds(exposure, 1_000_000_000);
        device.LockExposure(duration, _iso, t => applied.TrySetResult(t));
        return applied.Task;
    }

    /// <summary>
    /// At most 30 frames a second. With long exposures each frame may last as long as the format allows (iOS then makes
    /// it as long as its exposure, as Android's frame duration); in automatic exactly 30.
    /// </summary>
    private void SetFrameRate(AVCaptureDevice device, bool longExposures)
    {
        var thirtieth = new CMTime(1, 30);
        var shortest = CMTime.Compare(thirtieth, _rates.MinFrameDuration) < 0 ? _rates.MinFrameDuration : thirtieth;
        var longest = longExposures ? _rates.MaxFrameDuration : shortest;

        // The longest first when it grows, so that the shortest never exceeds it.
        if (CMTime.Compare(longest, device.ActiveVideoMaxFrameDuration) >= 0)
        {
            device.ActiveVideoMaxFrameDuration = longest;
            device.ActiveVideoMinFrameDuration = shortest;
        }
        else
        {
            device.ActiveVideoMinFrameDuration = shortest;
            device.ActiveVideoMaxFrameDuration = longest;
        }
    }

    /// <summary>The device's gains that make light of <paramref name="kelvin"/> neutral (tint 0): for 6500 K, what
    /// Android's daylight preset approximates (D65), here exactly.</summary>
    private AVCaptureWhiteBalanceGains Gains(int kelvin)
    {
        var gains = _device.GetDeviceWhiteBalanceGains(new AVCaptureWhiteBalanceTemperatureAndTintValues(Math.Clamp(kelvin, 2000, 12000), 0));
        var most = _device.MaxWhiteBalanceGain;
        return new AVCaptureWhiteBalanceGains(Math.Clamp(gains.RedGain, 1, most), Math.Clamp(gains.GreenGain, 1, most), Math.Clamp(gains.BlueGain, 1, most));
    }

    /// <summary>The lens position for a focus distance: iOS's lens goes from 0 (its nearest focus) to 1 (the farthest),
    /// taken as even in diopters (the lens's travel is).</summary>
    private float LensPosition(double diopters) =>
        MinimumFocusDiopters is > 0 and var nearest ? (float)Math.Clamp(1 - (diopters / nearest), 0, 1) : 1;

    private double? Diopters(float lensPosition) =>
        MinimumFocusDiopters is > 0 and var nearest ? (1 - Math.Clamp(lensPosition, 0, 1)) * nearest : null;

    /// <summary>What the camera reports it uses, against what was asked: the lock report SoftCal logs.</summary>
    private List<ControlState> Verify(double exposure)
    {
        var controls = new List<ControlState>();

        // Sensors round exposure to whole rows and ISO to their gain steps: what matters is that both are held where they
        // were put, near what was asked.
        var actual = _device.ExposureDuration.Seconds;
        var held = _device.ExposureMode == AVCaptureExposureMode.Custom;
        controls.Add(new ControlState("Exposure", held && Math.Abs(actual - exposure) <= exposure * 0.1 ? "Locked" : "VerifiedMismatch", exposure, actual, "automatic exposure off"));
        var iso = _device.ISO;
        controls.Add(new ControlState("Gain", held && Math.Abs(iso - _iso) <= _iso * 0.1 ? "Locked" : "VerifiedMismatch", _iso, iso, "ISO, the lowest"));

        var kelvin = _lock.WhiteBalanceKelvin;
        if (CanLockWhiteBalance)
        {
            var gains = _device.DeviceWhiteBalanceGains;
            var asked = Gains(kelvin);
            var same = Math.Abs(gains.RedGain - asked.RedGain) < 0.02 && Math.Abs(gains.GreenGain - asked.GreenGain) < 0.02 && Math.Abs(gains.BlueGain - asked.BlueGain) < 0.02;
            var temperature = _device.GetTemperatureAndTintValues(gains).Temperature;
            controls.Add(new ControlState(
                "WhiteBalance",
                _device.WhiteBalanceMode == AVCaptureWhiteBalanceMode.Locked && same ? "Locked" : "VerifiedMismatch",
                kelvin,
                Math.Round(temperature),
                string.Create(CultureInfo.InvariantCulture, $"gains for {kelvin} K, tint 0 (red {gains.RedGain:0.###}, green {gains.GreenGain:0.###}, blue {gains.BlueGain:0.###})")));
        }
        else
        {
            controls.Add(new ControlState("WhiteBalance", "Failed", kelvin, Note: "this camera takes no white-balance gains"));
        }

        if (HasFocus)
        {
            var wanted = _lock.FocusDiopters ?? Farthest;
            var reported = Diopters(_device.LensPosition);
            var reached = _device.FocusMode == AVCaptureFocusMode.Locked && reported is { } d && Math.Abs(d - wanted) < 0.5 && !_device.AdjustingFocus;
            controls.Add(new ControlState(
                "Focus",
                reached ? "Locked" : "VerifiedMismatch",
                wanted,
                reported,
                string.Create(CultureInfo.InvariantCulture, $"diopters, 0 = the farthest; lens position {_device.LensPosition:0.###} (iOS: 0 the nearest, 1 the farthest); f/{_device.LensAperture:0.#} {_device.LocalizedName}")));
        }
        else
        {
            controls.Add(new ControlState("Focus", "NotSupported", Note: "fixed-focus lens"));
        }

        controls.Add(new ControlState("Sharpness", "NotSupported", Note: "iOS keeps its own sharpening and noise reduction"));
        if (_format.IsVideoHdrSupported)
        {
            controls.Add(new ControlState("VideoHdr", _device.VideoHdrEnabled ? "VerifiedMismatch" : "Locked", Note: "video HDR off"));
        }

        controls.Add(new ControlState(
            "Gamma",
            "NotSupported",
            Note: _device.GlobalToneMappingEnabled
                ? "the phone's own tone curve stays (one for the whole picture); it may follow the scene"
                : "the phone's own tone curve stays; it may follow the scene"));
        return controls;
    }

    /// <summary>Frames from <paramref name="since"/> on (host seconds, their first row's start) have the epoch's
    /// settings.</summary>
    private void Begin(int epoch, double since)
    {
        lock (_gate)
        {
            _epochs.Add((since, epoch));
            if (_epochs.Count > 32)
            {
                _epochs.RemoveRange(0, _epochs.Count - 32);
            }
        }
    }

    /// <summary>The epoch a frame that started at <paramref name="start"/> was taken with: the latest that had begun.
    /// A change whose first frame is not known yet has not begun, so a frame is never given settings it may not have
    /// (the rig skips the first frames after a change anyway). Call within the gate.</summary>
    private int EpochAt(double start)
    {
        for (var i = _epochs.Count - 1; i >= 0; i--)
        {
            if (_epochs[i].Since <= start)
            {
                return _epochs[i].Epoch;
            }
        }

        return _epochs.Count > 0 ? _epochs[0].Epoch : 0;
    }

    /// <summary>A capture time, on the host clock (the session's clock is the host clock on every device so far).</summary>
    private double HostSeconds(CMTime time) =>
        _clock is { } clock ? CMClockOrTimebase.ConvertTime(time, clock, Host).Seconds : time.Seconds;

    /// <summary>A frame: its statistics for each running stream and its luma for each picture being taken.</summary>
    private unsafe void OnFrame(CMSampleBuffer buffer)
    {
        Interlocked.Increment(ref _framesSeen);
        StreamSink[] streams;
        PictureSink[] pictures;
        lock (_gate)
        {
            streams = [.. _streams];
            pictures = [.. _pictures];
        }

        if (streams.Length == 0 && pictures.Length == 0)
        {
            return;
        }

        // AVFoundation does not say which moment a frame's time is: taken as its first row's start (were it later, the
        // frame's time is only later, never early). Its last row then finished exposing an exposure and a readout after.
        var start = HostSeconds(buffer.PresentationTimeStamp);
        int epoch;
        double? asked;
        lock (_gate)
        {
            epoch = EpochAt(start);
            asked = _exposureOfEpoch.TryGetValue(epoch, out var seconds) ? seconds : null;
        }

        var exposure = ReportedExposure(buffer) ?? asked;
        var time = start + (exposure ?? 1.0 / 30) + _readout;
        var rotation = (SensorOrientation + DeviceRotation) % 360;

        using var image = buffer.GetImageBuffer() as CVPixelBuffer
            ?? throw new InvalidOperationException("A camera frame came without its picture.");
        if (image.PixelFormatType != CVPixelFormatType.CV420YpCbCr8BiPlanarFullRange || image.PlaneCount < 2)
        {
            throw new InvalidOperationException("The camera sends its frames in another format than the one asked.");
        }

        if (image.Lock(CVPixelBufferLock.ReadOnly) != CVReturn.Success)
        {
            throw new InvalidOperationException("A camera frame could not be read.");
        }

        try
        {
            var (width, height) = ((int)image.Width, (int)image.Height);
            var yStride = (int)image.GetBytesPerRowOfPlane(0);
            var chromaStride = (int)image.GetBytesPerRowOfPlane(1);
            var y = new ReadOnlySpan<byte>((void*)image.GetBaseAddress(0), yStride * (int)image.GetHeightOfPlane(0));

            // One plane of Cb Cr pairs: Cb's values start at its first byte, Cr's one byte later.
            var chroma = new ReadOnlySpan<byte>((void*)image.GetBaseAddress(1), chromaStride * (int)image.GetHeightOfPlane(1));
            var matrix = _matrix ??= MatrixOf(image);
            foreach (var stream in streams)
            {
                var rect = stream.Rect ??= Yuv420Statistics.ToFrame(stream.Region, rotation, width, height);
                stream.Rotation ??= rotation;
                var stats = Yuv420Statistics.Measure(y, yStride, chroma, chroma[1..], chromaStride, 2, rect, stream.Region.RowBands, matrix);
                stream.OnFrame(new FrameMessage(
                    Interlocked.Increment(ref _sequence),
                    time,
                    epoch,
                    exposure,
                    stats.Red,
                    stats.Green,
                    stats.Blue,
                    stats.Pixels,
                    stats.Bands.Length > 0 ? stats.Bands : null));
            }

            foreach (var picture in pictures)
            {
                picture.Add(y, yStride, width, height, rotation, time);
            }
        }
        finally
        {
            image.Unlock(CVPixelBufferLock.ReadOnly);
        }
    }

    /// <summary>The pictures being taken fail with the reason, so SoftCal says it; a stream's PC sees the frames stop.</summary>
    private void OnFrameFailed(Exception error)
    {
        PictureSink[] pictures;
        lock (_gate)
        {
            pictures = [.. _pictures];
        }

        foreach (var picture in pictures)
        {
            picture.Done.TrySetException(new InvalidOperationException("The phone could not read a camera frame: " + error.Message, error));
        }
    }

    /// <summary>The exposure a frame was taken with, from its EXIF data (null when it has none).</summary>
    private static double? ReportedExposure(CMSampleBuffer buffer)
    {
        using var exif = CMAttachmentBearer.GetAttachment<NSDictionary>(buffer, ImageIO.CGImageProperties.ExifDictionary.ToString(), out _);
        return exif?[ImageIO.CGImageProperties.ExifExposureTime] is NSNumber seconds && seconds.DoubleValue > 0 ? seconds.DoubleValue : null;
    }

    /// <summary>The Y′CbCr matrix a frame says it was encoded with (BT.601, as JFIF, when it does not say).</summary>
    private static YcbcrMatrix MatrixOf(CVPixelBuffer image)
    {
        // Not disposed: the value may be Core Video's own constant, whose wrapper the constants below share.
        var matrix = image.GetAttachment<NSString>(CVImageBuffer.YCbCrMatrixKey, out _);
        var name = matrix?.ToString();
        return name == CVImageBuffer.YCbCrMatrix_ITU_R_709_2.ToString() ? YcbcrMatrix.Bt709
            : name == CVImageBuffer.YCbCrMatrix_ITU_R_2020.ToString() ? YcbcrMatrix.Bt2020
            : YcbcrMatrix.Bt601;
    }

    private void OnInterrupted(NSNotification notification)
    {
        if (!_running)
        {
            return;
        }

        var reason = notification.UserInfo?[AVCaptureSession.InterruptionReasonKey] is NSNumber number
            ? (AVCaptureSessionInterruptionReason)(long)number.NIntValue
            : (AVCaptureSessionInterruptionReason?)null;
        OnLost(reason switch
        {
            AVCaptureSessionInterruptionReason.VideoDeviceNotAvailableInBackground => "SoftCal Camera left the screen: iOS lends the camera only to the app in front.",
            AVCaptureSessionInterruptionReason.VideoDeviceInUseByAnotherClient => "Another app took the camera.",
            AVCaptureSessionInterruptionReason.VideoDeviceNotAvailableWithMultipleForegroundApps => "The camera stops while another app shares the screen: show SoftCal Camera alone.",
            AVCaptureSessionInterruptionReason.VideoDeviceNotAvailableDueToSystemPressure => "iOS stopped the camera: the device is too hot. Let it cool down, then measure again.",
            _ => "iOS stopped the camera.",
        });
    }

    private void OnFailed(NSError? error) =>
        OnLost("The camera failed" + (error is null ? "." : $" ({error.LocalizedDescription})."));

    /// <summary>The camera is gone: closed, and the PC told so. Always as "Disconnected", the kind for which the PC stops
    /// a running stream at once (overheating included: the camera is closed either way).</summary>
    private void OnLost(string message)
    {
        bool wasManual;
        lock (_gate)
        {
            wasManual = _manual;
            _manual = false;
        }

        Close();
        Faulted?.Invoke(this, new FaultMessage("Disconnected", message));
        if (wasManual)
        {
            ModeChanged?.Invoke(this, false);
        }
    }

    private void Close()
    {
        List<PictureSink> pictures;
        lock (_gate)
        {
            _running = false;
            _closes++;
            _matrix = null;
            pictures = [.. _pictures];
        }

        var closed = new InvalidOperationException("The phone's camera closed.");
        foreach (var picture in pictures)
        {
            picture.Done.TrySetException(closed);
        }

        // Even when it does not run: a session an interruption stopped would start again by itself when it ends.
        _sessionQueue.DispatchAsync(Session.StopRunning);
    }

    private sealed class StreamSink(RegionSpec region, Action<FrameMessage> onFrame)
    {
        public RegionSpec Region { get; } = region;

        public Action<FrameMessage> OnFrame { get; } = onFrame;

        /// <summary>The region in frame pixels, fixed by the first frame (the phone does not turn while measuring).</summary>
        public FrameRect? Rect { get; set; }

        /// <summary>The turn the region was taken with (fixed with <see cref="Rect"/>).</summary>
        public int? Rotation { get; set; }
    }

    private sealed class PictureSink(RegionSpec region, int frames, int skip, int maxWidth)
    {
        private FrameRect _rect;
        private PictureLayout? _layout;
        private double[]? _sums;
        private int _seen;
        private int _count;
        private double _first;

        public TaskCompletionSource<LumaPicture> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Add(ReadOnlySpan<byte> y, int stride, int width, int height, int rotation, double time)
        {
            if (Done.Task.IsCompleted || _seen++ < skip)
            {
                return;
            }

            if (_layout is null)
            {
                _rect = Yuv420Statistics.ToFrame(region, rotation, width, height);
                _layout = Yuv420Statistics.PictureOf(_rect, rotation, maxWidth);
                _sums = new double[_layout.Width * _layout.Height];
                _first = time;
            }

            Yuv420Statistics.AddLuma(y, stride, _rect, _layout, _sums);
            if (++_count < frames)
            {
                return;
            }

            var (bits, data) = Yuv420Statistics.Pack(_sums, _count, _layout.Step);
            Done.TrySetResult(new LumaPicture(_layout.Width, _layout.Height, bits, data, _count, _first, time));
        }
    }

    private sealed class Unregister(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>The frames, on their own queue, one at a time.</summary>
    private sealed class FrameDelegate(PhoneCamera owner) : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        public override void DidOutputSampleBuffer(AVCaptureOutput captureOutput, CMSampleBuffer sampleBuffer, AVCaptureConnection connection)
        {
            try
            {
                owner.OnFrame(sampleBuffer);
            }
#pragma warning disable CA1031 // A frame that cannot be read must not take the app down (this runs in AVFoundation's callback).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                owner.OnFrameFailed(ex);
            }
            finally
            {
                // Disposed at once, not at the next collection: the camera stops sending frames once the few buffers
                // it has are all held (as Android's image.Close()).
                sampleBuffer.Dispose();
            }
        }
    }
}
