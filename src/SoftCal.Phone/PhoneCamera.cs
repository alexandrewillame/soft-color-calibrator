using System.Globalization;
using System.Runtime.InteropServices;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Views;
using SoftCal.Remote;

namespace SoftCal.Phone;

/// <summary>
/// The phone's main back camera through Camera2, as the link drives it. One repeating request feeds a small YUV reader
/// (about 640×480): automatic while the phone is being placed, manual once a measurement locks it — exposure, the
/// lowest sensitivity, the daylight white-balance preset (≈ D65), the farthest focus (which blurs a screen a few
/// centimetres away), and the processing that follows the scene switched off where the phone allows it (edge
/// enhancement, noise reduction, an adaptive tone curve). Every request carries its exposure epoch as its tag, so each
/// frame says exactly which exposure it was taken with.
/// <para>
/// Frame times are on <see cref="Now"/> (elapsed real time): when the frame's last row finished exposing, from the
/// sensor's own timestamp when it is on that clock, otherwise from when the frame arrived (later, so never early).
/// </para>
/// </summary>
internal sealed class PhoneCamera : IPhoneCamera, IDisposable
{
    /// <summary>White-balance presets with a CIE illuminant they approximate (Camera2's documentation).</summary>
    private static readonly (ControlAwbMode Mode, string Name, int Kelvin)[] Presets =
    [
        (ControlAwbMode.Incandescent, "incandescent (CIE A)", 2856),
        (ControlAwbMode.WarmFluorescent, "warm fluorescent (CIE F4)", 2940),
        (ControlAwbMode.Fluorescent, "fluorescent (CIE F2)", 4230),
        (ControlAwbMode.Daylight, "daylight (CIE D65)", 6504),
    ];

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(4);

    /// <summary>LENS_STATE_MOVING (the binding has no enum for it).</summary>
    private const int LensMoving = 1;

    /// <summary>"The farthest", asked as 20 m rather than exactly 0, which some phones seem to take as "not set".</summary>
    private const float Farthest = 0.05f;

    private readonly CameraManager _manager;
    private readonly string _id;
    private readonly HandlerThread _thread;
    private readonly Handler _handler;
    private readonly object _gate = new();
    private readonly List<StreamSink> _streams = [];
    private readonly List<PictureSink> _pictures = [];
    private readonly List<ResultWaiter> _waiters = [];
    private readonly Dictionary<long, int> _epochOfFrame = [];
    private readonly Dictionary<long, long> _exposureOfFrame = [];
    private readonly Dictionary<int, long> _exposureOfEpoch = [];
    private readonly Results _results;
    private readonly ImageListener _images;
    private readonly SemaphoreSlim _opening = new(1, 1);
    private readonly Timer _idle;
    private readonly int[] _afModes;
    private readonly int[] _awbModes;
    private readonly int[] _toneModes;
    private readonly int[] _noiseModes;
    private readonly int[] _edgeModes;
    private readonly int[] _stabilizationModes;
    private readonly bool _boostable;
    private readonly long _minFrameNs;
    private readonly int _iso;
    private readonly RawImageListener _rawImages;
    private readonly BayerLayout _bayer;
    private readonly double[] _black = [];
    private readonly double _white;
    private readonly long _rawMinFrameNs;
    private readonly FrameRect _rawField;

    private CameraDevice? _device;
    private CameraCaptureSession? _session;
    private ImageReader? _reader;

    /// <summary>The raw sensor frames, read while SoftCal measures (null: the camera has no raw output, or it failed).</summary>
    private ImageReader? _rawReader;

    /// <summary>The raw reader the current session can target.</summary>
    private volatile ImageReader? _rawInSession;

    /// <summary>The raw output failed to configure on this phone: measured from processed frames instead.</summary>
    private bool _rawBroken;

    /// <summary>The sensor's black level for each position of the 2×2 Bayer block, row by row, as the latest frame
    /// reported it (null: none reported; the fixed pattern counts).</summary>
    private volatile double[]? _dynamicBlack;

    /// <summary>The app's preview of what the camera sees, when it is on screen.</summary>
    private volatile Surface? _preview;

    /// <summary>The preview the current session draws on (null: none).</summary>
    private volatile Surface? _previewInSession;
    private int _epoch;
    private bool _manual;
    private LockSettings _lock = new(6500, null);
    private long _exposureNs;
    private long _lastExposureNs = 16_666_667;
    private long _lastSkewNs;
    private long _sequence;
    private long _framesSeen;
    private long _lastUse = System.Environment.TickCount64;
    private int _lastStartedEpoch;
    private volatile int _deviceRotation;

    private PhoneCamera(CameraManager manager, string id, CameraCharacteristics info)
    {
        _manager = manager;
        _id = id;
        _thread = new HandlerThread("SoftCal camera");
        _thread.Start();
        _handler = new Handler(_thread.Looper!);
        _results = new Results(this);
        _images = new ImageListener(this);
        _idle = new Timer(_ => CloseWhenIdle(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        var capabilities = Ints(info.Get(CameraCharacteristics.RequestAvailableCapabilities));
        ManualSensor = capabilities.Contains((int)RequestAvailableCapabilities.ManualSensor);
        ManualPostProcessing = capabilities.Contains((int)RequestAvailableCapabilities.ManualPostProcessing);
        _afModes = Ints(info.Get(CameraCharacteristics.ControlAfAvailableModes));
        _awbModes = Ints(info.Get(CameraCharacteristics.ControlAwbAvailableModes));
        _toneModes = Ints(info.Get(CameraCharacteristics.TonemapAvailableToneMapModes));
        _noiseModes = Ints(info.Get(CameraCharacteristics.NoiseReductionAvailableNoiseReductionModes));
        _edgeModes = Ints(info.Get(CameraCharacteristics.EdgeAvailableEdgeModes));
        _stabilizationModes = Ints(info.Get(CameraCharacteristics.LensInfoAvailableOpticalStabilization));
        _boostable = info.Get(CameraCharacteristics.ControlPostRawSensitivityBoostRange) is not null;
        SensorOrientation = Int(info.Get(CameraCharacteristics.SensorOrientation)) ?? 90;
        RealtimeTimestamps = Int(info.Get(CameraCharacteristics.SensorInfoTimestampSource)) == (int)SensorInfoTimestampSource.Realtime;
        MinimumFocusDiopters = Float(info.Get(CameraCharacteristics.LensInfoMinimumFocusDistance));

        var exposures = info.Get(CameraCharacteristics.SensorInfoExposureTimeRange)?.JavaCast<Android.Util.Range>();
        MinExposureNs = Long(exposures?.Lower) ?? 100_000;
        MaxExposureNs = Long(exposures?.Upper) ?? 100_000_000;
        var isos = info.Get(CameraCharacteristics.SensorInfoSensitivityRange)?.JavaCast<Android.Util.Range>();
        MinIso = Int(isos?.Lower) ?? 100;
        MaxIso = Int(isos?.Upper) ?? 100;

        var map = info.Get(CameraCharacteristics.ScalerStreamConfigurationMap)?.JavaCast<StreamConfigurationMap>()
            ?? throw new InvalidOperationException("The camera lists no output sizes.");
        Size = ChooseSize(map.GetOutputSizes((int)ImageFormatType.Yuv420888) ?? []);

        // Raw frames come from the sensor before any processing; usable when the camera offers them behind a Bayer
        // filter with known black and white levels.
        _rawImages = new RawImageListener(this);
        var rawSizes = map.GetOutputSizes((int)ImageFormatType.RawSensor) ?? [];
        var layout = Int(info.Get(CameraCharacteristics.SensorInfoColorFilterArrangement));
        var blackPattern = info.Get(CameraCharacteristics.SensorBlackLevelPattern)?.JavaCast<BlackLevelPattern>();
        _white = Int(info.Get(CameraCharacteristics.SensorInfoWhiteLevel)) ?? 0;
        if (capabilities.Contains((int)RequestAvailableCapabilities.Raw) && rawSizes.Length > 0 && layout is >= 0 and <= 3 && blackPattern is not null && _white > 0)
        {
            RawSize = rawSizes.MinBy(r => (long)r.Width * r.Height);
            _bayer = (BayerLayout)layout.Value;
            _black = [blackPattern.GetOffsetForIndex(0, 0), blackPattern.GetOffsetForIndex(1, 0), blackPattern.GetOffsetForIndex(0, 1), blackPattern.GetOffsetForIndex(1, 1)];

            // A stream that stalls the camera (some phones' raw one) adds its stall to every frame that targets it.
            _rawMinFrameNs = Math.Max(1, map.GetOutputMinFrameDuration((int)ImageFormatType.RawSensor, RawSize!) + map.GetOutputStallDuration((int)ImageFormatType.RawSensor, RawSize!));
            _rawField = RawField(info, RawSize!, Size);
        }

        PreviewSize = ChooseSize(map.GetOutputSizes((int)ImageFormatType.Private) ?? [], 960, 720);
        _minFrameNs = Math.Max(1, map.GetOutputMinFrameDuration((int)ImageFormatType.Yuv420888, Size));
        _iso = MinIso;
    }

    /// <summary>Something went wrong outside a command (another app took the camera, Android closed it).</summary>
    public event EventHandler<FaultMessage>? Faulted;

    /// <summary>True when a measurement locked the camera, false when it went back to automatic.</summary>
    public event EventHandler<bool>? ModeChanged;

    public Android.Util.Size Size { get; }

    /// <summary>The preview's size: 4:3 like the frames (the same view of the sensor), about 960×720.</summary>
    public Android.Util.Size PreviewSize { get; }

    /// <summary>The raw frames' size (the smallest the camera offers; null: no raw output).</summary>
    public Android.Util.Size? RawSize { get; }

    /// <summary>Measurements come from the sensor's raw data: linear, before any of the phone's processing.</summary>
    public bool MeasuresRaw => RawSize is not null && !_rawBroken;

    public int SensorOrientation { get; }

    public bool ManualSensor { get; }

    public bool ManualPostProcessing { get; }

    public bool RealtimeTimestamps { get; }

    public long MinExposureNs { get; }

    public long MaxExposureNs { get; }

    public int MinIso { get; }

    public int MaxIso { get; }

    public float? MinimumFocusDiopters { get; }

    public bool IsOpen => _session is not null;

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

    /// <summary>The phone's turn from its natural (portrait) position, in degrees clockwise, from its orientation
    /// sensor: pictures are turned so that up is up as the phone stands.</summary>
    public int DeviceRotation
    {
        get => _deviceRotation;
        set => _deviceRotation = ((value % 360) + 360) % 360;
    }

    public PhoneCapabilities Capabilities
    {
        get
        {
            var upright = SensorOrientation is 90 or 270;
            var skew = Interlocked.Read(ref _lastSkewNs);
            return new PhoneCapabilities(
                MinExposureNs / 1e9,
                MaxExposureNs / 1e9,
                MinIso,
                MaxIso,
                ManualSensor,
                ManualPostProcessing,
                RealtimeTimestamps,
                upright ? Size.Height : Size.Width,
                upright ? Size.Width : Size.Height,
                1e9 / (MeasuresRaw ? Math.Max(_minFrameNs, _rawMinFrameNs) : _minFrameNs),
                skew > 0 ? skew / 1e9 / Size.Height : null,
                [.. Presets.Where(p => _awbModes.Contains((int)p.Mode)).Select(p => p.Name)],
                MinimumFocusDiopters,
                MeasuresRaw);
        }
    }

    public double Now => SystemClock.ElapsedRealtimeNanos() / 1e9;

    /// <summary>The main back camera: the first one facing back that lets apps set its exposure, else the first facing back.</summary>
    public static PhoneCamera Create(Context context)
    {
        var manager = (CameraManager?)context.GetSystemService(Context.CameraService) ?? throw new InvalidOperationException("This phone has no camera service.");
        string? fallback = null;
        foreach (var id in manager.GetCameraIdList())
        {
            var info = manager.GetCameraCharacteristics(id);
            if (Int(info.Get(CameraCharacteristics.LensFacing)) != (int)LensFacing.Back)
            {
                continue;
            }

            fallback ??= id;
            if (Ints(info.Get(CameraCharacteristics.RequestAvailableCapabilities)).Contains((int)RequestAvailableCapabilities.ManualSensor))
            {
                return new PhoneCamera(manager, id, info);
            }
        }

        return fallback is null
            ? throw new InvalidOperationException("This phone has no camera on its back.")
            : new PhoneCamera(manager, fallback, manager.GetCameraCharacteristics(fallback));
    }

    /// <summary>Opens the camera (again, after Android took it away) and starts it in automatic. One opening at a time:
    /// the app's start and its coming back to the front may both ask.</summary>
    public async Task OpenAsync()
    {
        await _opening.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                return;
            }

            var opened = new TaskCompletionSource<CameraDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
            _manager.OpenCamera(_id, new DeviceCallback(this, opened), _handler);
            var device = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var reader = ImageReader.NewInstance(Size.Width, Size.Height, ImageFormatType.Yuv420888, 4);
            try
            {
                reader.SetOnImageAvailableListener(_images, _handler);
                if (MeasuresRaw && _rawReader is null)
                {
                    _rawReader = ImageReader.NewInstance(RawSize!.Width, RawSize.Height, ImageFormatType.RawSensor, 2);
                    _rawReader.SetOnImageAvailableListener(_rawImages, _handler);
                }

                var session = await CreateSessionAsync(device, reader).ConfigureAwait(false);
                int epoch;
                lock (_gate)
                {
                    _device = device;
                    _reader = reader;
                    _session = session;
                    _manual = false;
                    epoch = ++_epoch;
                }

                Repeat(AutoRequest(), epoch);
            }
            catch (Exception)
            {
                Close();
                device.Close();
                reader.Close();
                throw;
            }
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
            throw new InvalidOperationException("This phone's camera does not let apps set its exposure, so it cannot measure.");
        }

        await EnsureOpenAsync().ConfigureAwait(false);
        int epoch;
        int before;
        bool wasManual;
        long exposure;
        lock (_gate)
        {
            _lock = settings;
            wasManual = _manual;
            _manual = true;

            // Start from what the automatic exposure chose: SoftCal's own exposure control takes it from there.
            _exposureNs = Math.Clamp(Interlocked.Read(ref _lastExposureNs), MinExposureNs, MaxExposureNs);
            exposure = _exposureNs;
            before = _epoch;
            epoch = ++_epoch;
            _exposureOfEpoch[epoch] = exposure;
            _exposureOfEpoch.Remove(epoch - 64);
        }

        // The autofocus is told to let go while it still runs (once it is off, a trigger is ignored): some phones
        // otherwise keep the lens where it last focused, on the screen.
        if (HasFocus && !wasManual)
        {
            var letGo = AutoRequest();
            Put(letGo, CaptureRequest.ControlAfTrigger, (int)ControlAFTrigger.Cancel);
            letGo.SetTag(Java.Lang.Integer.ValueOf(before));
            (_session ?? throw new InvalidOperationException("The phone's camera is closed.")).Capture(letGo.Build(), _results, _handler);
        }

        var settled = WaitForResult(epoch, 4);
        Repeat(ManualRequest(exposure), epoch);
        var result = await settled.WaitAsync(LockTimeout + TimeSpan.FromSeconds(exposure * 4 / 1e9), cancellationToken).ConfigureAwait(false);
        var focus = HasFocus ? await SettleFocusAsync(epoch, exposure, cancellationToken).ConfigureAwait(false) : null;
        ModeChanged?.Invoke(this, true);
        return Verify(result, exposure, focus);
    }

    /// <summary>
    /// Waits for the lens to reach the focus asked for: it takes a few frames to travel. Some phones keep it where the
    /// autofocus left it (a Galaxy S23 Ultra stayed focused on the screen): then the lens is sent to its nearest focus
    /// and back, as some only move it when the distance asked for changes. A lens left on the screen would not blur its
    /// pixels, and every halftone reading would clip.
    /// </summary>
    private async Task<FocusSettling> SettleFocusAsync(int epoch, long exposureNs, CancellationToken cancellationToken)
    {
        var wanted = (float)(_lock.FocusDiopters ?? Farthest);
        var seen = new FocusSettling(false, null, null, null, false);
        bool Reached(CaptureResult result)
        {
            var af = TakeInt(result.Get(CaptureResult.ControlAfMode));
            var distance = TakeFloat(result.Get(CaptureResult.LensFocusDistance));
            var state = TakeInt(result.Get(CaptureResult.LensState));
            var reached = af == (int)ControlAFMode.Off && distance is { } d && Math.Abs(d - wanted) < 0.5 && state != LensMoving;
            seen = seen with { Reached = reached, Distance = distance, AfMode = af, Moving = state == LensMoving };
            return reached;
        }

        if (await WaitUntilAsync(epoch, Reached, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
        {
            return seen;
        }

        // To 50 cm and back, not to the nearest: a lens that stays at 50 cm still blurs a screen 5–10 cm away.
        Repeat(ManualRequest(exposureNs, Math.Min(2f, MinimumFocusDiopters ?? 2f)), epoch);
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        Repeat(ManualRequest(exposureNs), epoch);
        var reached = await WaitUntilAsync(epoch, Reached, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        return seen with { Reached = reached, Nudged = true };
    }

    /// <summary>Waits until a result of the epoch satisfies <paramref name="condition"/> (read on the camera's thread);
    /// false when none did in time.</summary>
    private async Task<bool> WaitUntilAsync(int epoch, Func<CaptureResult, bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiter = new ResultWaiter(epoch, 1, condition);
        lock (_gate)
        {
            _waiters.Add(waiter);
        }

        try
        {
            await waiter.Done.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _waiters.Remove(waiter);
            }
        }
    }

    public async Task<ExposureOutcome> SetExposureAsync(double seconds, CancellationToken cancellationToken)
    {
        await EnsureOpenAsync().ConfigureAwait(false);
        if (!ManualSensor)
        {
            throw new InvalidOperationException("This phone's camera does not let apps set its exposure.");
        }

        var ns = Math.Clamp((long)Math.Round(seconds * 1e9), MinExposureNs, MaxExposureNs);
        int epoch;
        bool wasManual;
        lock (_gate)
        {
            wasManual = _manual;
            _manual = true;
            _exposureNs = ns;
            epoch = ++_epoch;
            _exposureOfEpoch[epoch] = ns;
            _exposureOfEpoch.Remove(epoch - 64);
        }

        var first = WaitForResult(epoch, 1);
        Repeat(ManualRequest(ns), epoch);
        if (!wasManual)
        {
            ModeChanged?.Invoke(this, true);
        }

        double? actual = null;
        try
        {
            var result = await first.WaitAsync(TimeSpan.FromSeconds(1.5 + (2 * ns / 1e9)), cancellationToken).ConfigureAwait(false);
            actual = Long(result.Get(CaptureResult.SensorExposureTime)) / 1e9;
        }
        catch (TimeoutException)
        {
            // Frames of the new exposure still say which it is (their epoch); its exact value just is not known yet.
        }

        return new ExposureOutcome(ns / 1e9, actual, epoch);
    }

    /// <summary>Moves the locked camera's focus, its exposure and epoch unchanged (the PC searches for the setting
    /// that blurs the screen best, judging the picture), and waits for the lens.</summary>
    public async Task<FocusOutcome> SetFocusAsync(double diopters, CancellationToken cancellationToken)
    {
        await EnsureOpenAsync().ConfigureAwait(false);
        if (!HasFocus)
        {
            return new FocusOutcome(null, false, Now);
        }

        int epoch;
        long exposure;
        lock (_gate)
        {
            if (!_manual)
            {
                throw new InvalidOperationException("The camera is not locked: SoftCal locks it before moving its focus.");
            }

            _lock = _lock with { FocusDiopters = diopters };
            epoch = _epoch;
            exposure = _exposureNs;
        }

        float? reported = null;
        var moving = true;
        bool Settled(CaptureResult result)
        {
            reported = TakeFloat(result.Get(CaptureResult.LensFocusDistance));
            moving = TakeInt(result.Get(CaptureResult.LensState)) == LensMoving;
            return !moving && reported is { } d && Math.Abs(d - diopters) < 0.5;
        }

        Repeat(ManualRequest(exposure), epoch);
        await WaitUntilAsync(epoch, Settled, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);

        // A little longer for lenses that say they have arrived before they have.
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        return new FocusOutcome(reported, moving, Now);
    }

    public IDisposable StartStream(RegionSpec region, Action<FrameMessage> onFrame)
    {
        var sink = new StreamSink(region, onFrame);
        lock (_gate)
        {
            _streams.Add(sink);
        }

        // The frames start once the camera is on (a stream right after a lock finds it on already), from the raw output
        // when there is one.
        _ = StartStreamingAsync();
        return new Unregister(() =>
        {
            lock (_gate)
            {
                _streams.Remove(sink);
            }

            Touch();
            Reissue();
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
            var frameTime = Math.Max(_minFrameNs, Interlocked.Read(ref _exposureNs)) / 1e9;
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

    public Task RestoreAsync(CancellationToken cancellationToken)
    {
        int epoch;
        bool wasManual;
        lock (_gate)
        {
            wasManual = _manual;
            _manual = false;
            epoch = ++_epoch;
        }

        if (_session is not null)
        {
            Repeat(AutoRequest(), epoch);
        }

        if (wasManual)
        {
            ModeChanged?.Invoke(this, false);
        }

        Touch();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _idle.Dispose();
        Close();
        _rawReader?.Close();
        _thread.QuitSafely();
    }

    /// <summary>Opens the camera when it is off (SoftCal needs it), and notes the use: it is closed again once SoftCal
    /// has not needed it for <see cref="IdleAfter"/>.</summary>
    private async Task EnsureOpenAsync()
    {
        Touch();
        if (_session is null)
        {
            await OpenAsync().ConfigureAwait(false);
        }
    }

    private async Task StartStreamingAsync()
    {
        try
        {
            await EnsureOpenAsync().ConfigureAwait(false);
            Reissue();
        }
#pragma warning disable CA1031 // A stream whose camera cannot open sends nothing: the PC's stall check says so.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Faulted?.Invoke(this, new FaultMessage("Disconnected", "The camera could not be opened: " + ex.Message));
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastUse, System.Environment.TickCount64);

    /// <summary>Every two seconds: a camera SoftCal has not needed for a while is closed (to spare the battery). Never
    /// while it is locked for a measurement, streams, or takes a picture.</summary>
    private void CloseWhenIdle()
    {
        if (_session is null || System.Environment.TickCount64 - Interlocked.Read(ref _lastUse) < IdleAfter.TotalMilliseconds)
        {
            return;
        }

        lock (_gate)
        {
            if (_manual || _streams.Count > 0 || _pictures.Count > 0 || _waiters.Count > 0)
            {
                return;
            }
        }

        // Not while it is being opened (that is a use).
        if (!_opening.Wait(0))
        {
            return;
        }

        try
        {
            if (System.Environment.TickCount64 - Interlocked.Read(ref _lastUse) >= IdleAfter.TotalMilliseconds)
            {
                Close();
            }
        }
        finally
        {
            _opening.Release();
        }
    }

    private static int[] Ints(Java.Lang.Object? value) => value is null ? [] : JNIEnv.GetArray<int>(value.Handle) ?? [];

    private static int? Int(Java.Lang.Object? value) => value?.JavaCast<Java.Lang.Integer>().IntValue();

    private static long? Long(Java.Lang.Object? value) => value?.JavaCast<Java.Lang.Long>().LongValue();

    private static float? Float(Java.Lang.Object? value) => value?.JavaCast<Java.Lang.Float>().FloatValue();

    /// <summary>An int read from a frame's result, its Java box let go of at once.</summary>
    private static int? TakeInt(Java.Lang.Object? value)
    {
        if (value is null)
        {
            return null;
        }

        using (value)
        {
            return value.JavaCast<Java.Lang.Integer>().IntValue();
        }
    }

    /// <summary>A float read from a frame's result, its Java box let go of at once.</summary>
    private static float? TakeFloat(Java.Lang.Object? value)
    {
        if (value is null)
        {
            return null;
        }

        using (value)
        {
            return value.JavaCast<Java.Lang.Float>().FloatValue();
        }
    }

    /// <summary>A long read from a frame's result, its Java box let go of at once.</summary>
    private static long? TakeLong(Java.Lang.Object? value)
    {
        if (value is null)
        {
            return null;
        }

        using (value)
        {
            return value.JavaCast<Java.Lang.Long>().LongValue();
        }
    }

    /// <summary>
    /// Where the processed frames' view lies on the raw frame: the sensor's active area (a raw frame may also hold the
    /// border photosites around it), narrowed to the frames' shape as the camera crops them (the largest centred area of
    /// that shape). The region SoftCal measures, given on the processed frames, is placed on the raw frame through it.
    /// </summary>
    private static FrameRect RawField(CameraCharacteristics info, Android.Util.Size raw, Android.Util.Size frames)
    {
        var field = new FrameRect(0, 0, raw.Width, raw.Height);
        var pixels = info.Get(CameraCharacteristics.SensorInfoPixelArraySize)?.JavaCast<Android.Util.Size>();
        var active = info.Get(CameraCharacteristics.SensorInfoPreCorrectionActiveArraySize)?.JavaCast<Rect>();
        if (pixels is not null && active is not null && pixels.Width == raw.Width && pixels.Height == raw.Height
            && active.Left >= 0 && active.Top >= 0 && active.Width() > 0 && active.Height() > 0 && active.Right <= raw.Width && active.Bottom <= raw.Height)
        {
            field = new FrameRect(active.Left, active.Top, active.Width(), active.Height());
        }

        var shape = (double)frames.Width / frames.Height;
        var (width, height) = (double)field.Width / field.Height > shape
            ? ((int)Math.Round(field.Height * shape), field.Height)
            : (field.Width, (int)Math.Round(field.Width / shape));
        return new FrameRect(field.X + ((field.Width - width) / 2), field.Y + ((field.Height - height) / 2), width, height);
    }

    /// <summary>The region SoftCal measures on the raw frame, in whole 2×2 blocks (the filter pattern starts at the
    /// frame's top-left corner).</summary>
    private FrameRect RawRegion(RegionSpec region, int rotation)
    {
        var r = Yuv420Statistics.ToFrame(region, rotation, _rawField.Width, _rawField.Height);
        return new FrameRect((_rawField.X + r.X) & ~1, (_rawField.Y + r.Y) & ~1, r.Width, r.Height);
    }

    /// <summary>The 4:3 size nearest the one asked (4:3 is the whole sensor on nearly every phone), else the nearest.</summary>
    private static Android.Util.Size ChooseSize(Android.Util.Size[] sizes, int width = 640, int height = 480)
    {
        double Cost(Android.Util.Size s) =>
            Math.Abs(Math.Log((double)s.Width * s.Height / ((double)width * height))) + (Math.Abs(((double)s.Width / s.Height) - (4.0 / 3)) < 0.01 ? 0 : 1);
        return sizes.Length == 0
            ? throw new InvalidOperationException("The camera offers no output of the kind needed.")
            : sizes.MinBy(Cost)!;
    }

    private CaptureRequest.Builder NewRequest()
    {
        var device = _device ?? throw new InvalidOperationException("The phone's camera is closed.");
        var reader = _reader ?? throw new InvalidOperationException("The phone's camera is closed.");
        var builder = device.CreateCaptureRequest(CameraTemplate.Preview);
        builder.AddTarget(reader.Surface!);
        if (_previewInSession is { } preview)
        {
            builder.AddTarget(preview);
        }

        if (RawStreaming && _rawInSession is { } raw)
        {
            builder.AddTarget(raw.Surface!);
        }

        return builder;
    }

    /// <summary>A session for the frames, and for the preview when the app shows one (the preview the session was made
    /// with is the one requests may target).</summary>
    private async Task<CameraCaptureSession> CreateSessionAsync(CameraDevice device, ImageReader reader)
    {
        var preview = _preview is { IsValid: true } p ? p : null;
        var raw = MeasuresRaw ? _rawReader : null;
        try
        {
            var session = await ConfigureAsync(device, [reader.Surface!, .. raw is null ? Array.Empty<Surface>() : [raw.Surface!], .. preview is null ? Array.Empty<Surface>() : [preview]]).ConfigureAwait(false);
            (_previewInSession, _rawInSession) = (preview, raw);
            return session;
        }
        catch (Exception ex) when (raw is not null && ex is InvalidOperationException or Java.Lang.IllegalArgumentException or TimeoutException)
        {
            // This phone cannot stream its raw frames beside the others (it refused them, or never answered): it measures
            // from the processed ones (decided once the session without them works, so a camera that was closing does not
            // count against the raw output).
            var session = await ConfigureAsync(device, [reader.Surface!, .. preview is null ? Array.Empty<Surface>() : [preview]]).ConfigureAwait(false);
            _rawBroken = true;
            (_previewInSession, _rawInSession) = (preview, null);
            return session;
        }
    }

    private async Task<CameraCaptureSession> ConfigureAsync(CameraDevice device, IList<Surface> outputs)
    {
        var configured = new TaskCompletionSource<CameraCaptureSession>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CA1422 // The handler overload is deprecated from API 30 on; it works on every version from 28.
        device.CreateCaptureSession(outputs, new SessionCallback(configured), _handler);
#pragma warning restore CA1422
        return await configured.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    /// <summary>Shows what the camera sees on <paramref name="surface"/> (the app's preview), from a new session.</summary>
    public void AttachPreview(Surface surface)
    {
        _preview = surface;
        _ = ReconfigureAsync();
    }

    /// <summary>The preview is going away: the camera stops drawing on it at once, then goes on without it.</summary>
    public void DetachPreview()
    {
        _preview = null;
        if (_previewInSession is null)
        {
            return;
        }

        try
        {
            _session?.StopRepeating();
        }
        catch (Java.Lang.Exception)
        {
            // The session is already gone.
        }

        _ = ReconfigureAsync();
    }

    /// <summary>A new session with the outputs wanted now, the camera's settings (automatic or locked) carried on.</summary>
    private async Task ReconfigureAsync()
    {
        await _opening.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_device is not { } device || _reader is not { } reader || ReferenceEquals(_previewInSession, _preview is { IsValid: true } p ? p : null))
            {
                return;
            }

            var session = await CreateSessionAsync(device, reader).ConfigureAwait(false);
            int epoch;
            bool manual;
            long exposure;
            lock (_gate)
            {
                _session = session;
                epoch = _epoch;
                manual = _manual;
                exposure = _exposureNs;
            }

            Repeat(manual ? ManualRequest(exposure) : AutoRequest(), epoch);
        }
#pragma warning disable CA1031 // The preview is a convenience: the frames go on in the session they had, or the next opening.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            _opening.Release();
        }
    }

    private CaptureRequest.Builder AutoRequest()
    {
        var b = NewRequest();
        Put(b, CaptureRequest.ControlMode, (int)ControlMode.Auto);
        if (_afModes.Contains((int)ControlAFMode.ContinuousPicture))
        {
            Put(b, CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
        }

        return b;
    }

    /// <summary>Everything that would follow the scene, held: what <see cref="Verify"/> then reads back.</summary>
    private CaptureRequest.Builder ManualRequest(long exposureNs, float? focusDiopters = null)
    {
        var b = NewRequest();
        Put(b, CaptureRequest.ControlMode, (int)ControlMode.Auto);
        Put(b, CaptureRequest.ControlAeMode, (int)ControlAEMode.Off);
        Put(b, CaptureRequest.SensorExposureTime, exposureNs);
        Put(b, CaptureRequest.SensorSensitivity, _iso);
        Put(b, CaptureRequest.SensorFrameDuration, Math.Max(exposureNs, RawStreaming ? Math.Max(_minFrameNs, _rawMinFrameNs) : _minFrameNs));
        if (_boostable)
        {
            Put(b, CaptureRequest.ControlPostRawSensitivityBoost, 100);
        }

        var preset = WhiteBalancePreset(_lock.WhiteBalanceKelvin);
        if (preset is { } p)
        {
            Put(b, CaptureRequest.ControlAwbMode, (int)p.Mode);
        }
        else
        {
            Put(b, CaptureRequest.ControlAwbMode, (int)ControlAwbMode.Auto);
            Put(b, CaptureRequest.ControlAwbLock, true);
        }

        if (HasFocus)
        {
            Put(b, CaptureRequest.ControlAfMode, (int)ControlAFMode.Off);
            Put(b, CaptureRequest.LensFocusDistance, focusDiopters ?? (float)(_lock.FocusDiopters ?? Farthest));
        }

        if (_edgeModes.Contains((int)EdgeMode.Off))
        {
            Put(b, CaptureRequest.EdgeMode, (int)EdgeMode.Off);
        }

        if (_noiseModes.Contains((int)NoiseReductionMode.Off))
        {
            Put(b, CaptureRequest.NoiseReductionMode, (int)NoiseReductionMode.Off);
        }

        if (_stabilizationModes.Contains((int)LensOpticalStabilizationMode.Off))
        {
            Put(b, CaptureRequest.LensOpticalStabilizationMode, (int)LensOpticalStabilizationMode.Off);
        }

        Put(b, CaptureRequest.ControlVideoStabilizationMode, (int)ControlVideoStabilizationMode.Off);
        Put(b, CaptureRequest.ControlEffectMode, (int)ControlEffectMode.Off);
        Put(b, CaptureRequest.ControlSceneMode, (int)ControlSceneMode.Disabled);
        Put(b, CaptureRequest.BlackLevelLock, true);
        switch (ToneCurve)
        {
            case TonemapMode.PresetCurve:
                Put(b, CaptureRequest.TonemapMode, (int)TonemapMode.PresetCurve);
                Put(b, CaptureRequest.TonemapPresetCurve, (int)TonemapPresetCurveType.Srgb);
                break;
            case TonemapMode.GammaValue:
                Put(b, CaptureRequest.TonemapMode, (int)TonemapMode.GammaValue);
                Put(b, CaptureRequest.TonemapGamma, 2.2f);
                break;
        }

        return b;
    }

    private bool HasFocus => MinimumFocusDiopters is > 0 && _afModes.Contains((int)ControlAFMode.Off);

    /// <summary>The raw output is targeted: SoftCal measures, and the session has it.</summary>
    private bool RawStreaming
    {
        get
        {
            if (_rawInSession is null)
            {
                return false;
            }

            lock (_gate)
            {
                return _streams.Count > 0;
            }
        }
    }

    /// <summary>The repeating request again, as it is (after the outputs it should target changed).</summary>
    private void Reissue()
    {
        if (_session is null)
        {
            return;
        }

        int epoch;
        bool manual;
        long exposure;
        lock (_gate)
        {
            epoch = _epoch;
            manual = _manual;
            exposure = _exposureNs;
        }

        try
        {
            Repeat(manual ? ManualRequest(exposure) : AutoRequest(), epoch);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
        {
            // The camera is closing: the next opening issues its own request.
        }
    }

    /// <summary>A fixed tone curve (sRGB) when the phone takes one; otherwise its own stays (and may follow the scene).</summary>
    private TonemapMode? ToneCurve =>
        !ManualPostProcessing ? null
        : _toneModes.Contains((int)TonemapMode.PresetCurve) ? TonemapMode.PresetCurve
        : _toneModes.Contains((int)TonemapMode.GammaValue) ? TonemapMode.GammaValue
        : null;

    /// <summary>The preset nearest the color temperature asked for (in mireds, as the eye judges), when the phone has one.</summary>
    private (ControlAwbMode Mode, string Name, int Kelvin)? WhiteBalancePreset(int kelvin)
    {
        var available = Presets.Where(p => _awbModes.Contains((int)p.Mode)).ToList();
        return available.Count == 0 ? null : available.MinBy(p => Math.Abs((1e6 / p.Kelvin) - (1e6 / Math.Max(1000, kelvin))));
    }

    private void Repeat(CaptureRequest.Builder builder, int epoch)
    {
        builder.SetTag(Java.Lang.Integer.ValueOf(epoch));
        var session = _session ?? throw new InvalidOperationException("The phone's camera is closed.");
        session.SetRepeatingRequest(builder.Build(), _results, _handler);
    }

    /// <summary>What the camera reports it used, against what was asked: the lock report SoftCal logs.</summary>
    private List<ControlState> Verify(CaptureResult result, long exposureNs, FocusSettling? focusOutcome)
    {
        var controls = new List<ControlState>();
        // Sensors round exposure to whole rows and sensitivity to their gain steps: what matters is that both are held
        // where they were put, near what was asked.
        var exposure = Long(result.Get(CaptureResult.SensorExposureTime));
        var exposureOk = exposure is { } e && Math.Abs(e - exposureNs) <= exposureNs * 0.1;
        controls.Add(new ControlState("Exposure", exposureOk ? "Locked" : "VerifiedMismatch", exposureNs / 1e9, exposure / 1e9, "automatic exposure off"));

        var iso = Int(result.Get(CaptureResult.SensorSensitivity));
        var isoOk = iso is { } i && Math.Abs(i - _iso) <= _iso * 0.1;
        controls.Add(new ControlState("Gain", isoOk ? "Locked" : "VerifiedMismatch", _iso, iso, "ISO, the lowest"));

        var awb = Int(result.Get(CaptureResult.ControlAwbMode));
        var preset = WhiteBalancePreset(_lock.WhiteBalanceKelvin);
        controls.Add(preset is { } p
            ? new ControlState("WhiteBalance", awb == (int)p.Mode ? "Locked" : "VerifiedMismatch", _lock.WhiteBalanceKelvin, p.Kelvin, $"{p.Name} preset")
            : new ControlState("WhiteBalance", Bool(result.Get(CaptureResult.ControlAwbLock)) == true ? "Locked" : "Failed", _lock.WhiteBalanceKelvin, null, "no preset: held where the automatic white balance was"));

        if (focusOutcome is { } focus)
        {
            var wanted = (float)(_lock.FocusDiopters ?? Farthest);
            var how = focus.Reached
                ? focus.Nudged ? "after sending the lens to its nearest and back" : "the autofocus off"
                : string.Create(CultureInfo.InvariantCulture, $"autofocus {(focus.AfMode == (int)ControlAFMode.Off ? "off" : "still on")}, lens {(focus.Moving == true ? "still moving" : "not moving")}{(focus.Nudged ? ", even after sending it to its nearest and back" : string.Empty)}");
            controls.Add(new ControlState("Focus", focus.Reached ? "Locked" : "VerifiedMismatch", wanted, focus.Distance, $"diopters, 0 = the farthest; {how}; {Lens(result)}"));
        }
        else
        {
            controls.Add(new ControlState("Focus", "NotSupported", Note: "fixed-focus lens"));
        }

        var edge = Int(result.Get(CaptureResult.EdgeMode));
        controls.Add(_edgeModes.Contains((int)EdgeMode.Off)
            ? new ControlState("Sharpness", edge == (int)EdgeMode.Off ? "Locked" : "VerifiedMismatch", Note: "edge enhancement off")
            : new ControlState("Sharpness", "NotSupported", Note: "the phone always sharpens"));

        var tone = Int(result.Get(CaptureResult.TonemapMode));
        controls.Add(_rawInSession is not null
            ? new ControlState("Gamma", "Locked", Note: RawNote())
            : ToneCurve is { } curve
                ? new ControlState("Gamma", tone == (int)curve ? "Locked" : "VerifiedMismatch", Note: "a fixed sRGB tone curve")
                : new ControlState("Gamma", "NotSupported", Note: "the phone's own tone curve stays; it may follow the scene"));
        return controls;
    }

    /// <summary>"measured from the sensor's raw data (4000×3000 RGGB, black 64, white 1023): linear, …", for the lock
    /// report: what the readings are made of.</summary>
    private string RawNote()
    {
        static string Level(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
        var black = _black.Distinct().Count() == 1 ? Level(_black[0]) : string.Join("/", _black.Select(Level));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"measured from the sensor's raw data ({RawSize!.Width}×{RawSize.Height} {_bayer.ToString().ToUpperInvariant()}, black {black}, white {Level(_white)}): linear, before any processing (no tone curve, white balance, noise reduction or sharpening)");
    }

    private static bool? Bool(Java.Lang.Object? value) => value?.JavaCast<Java.Lang.Boolean>().BooleanValue();

    /// <summary>The lens a result was taken with: its focal length and f-number, and the physical camera behind a logical
    /// one (a phone may switch lenses by itself).</summary>
    private static string Lens(CaptureResult result)
    {
        var focal = Float(result.Get(CaptureResult.LensFocalLength));
        var aperture = Float(result.Get(CaptureResult.LensAperture));
        var physical = OperatingSystem.IsAndroidVersionAtLeast(29) ? result.Get(CaptureResult.LogicalMultiCameraActivePhysicalId)?.ToString() : null;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"lens {(focal is { } f ? $"{f:0.##} mm" : "?")} f/{(aperture is { } a ? $"{a:0.#}" : "?")}{(physical is { Length: > 0 } id ? $", physical camera {id}" : string.Empty)}");
    }

    /// <summary>Sets a request's value (the keys are declared nullable for the API levels that lack them; none used
    /// here is missing on 28).</summary>
    private static void Put(CaptureRequest.Builder builder, CaptureRequest.Key? key, Java.Lang.Object value) => builder.Set(key!, value);

    private Task<CaptureResult> WaitForResult(int epoch, int count)
    {
        var waiter = new ResultWaiter(epoch, count);
        lock (_gate)
        {
            _waiters.Add(waiter);
        }

        return waiter.Done.Task;
    }

    private void OnStarted(CaptureRequest request, long timestamp)
    {
        if (request.Tag?.JavaCast<Java.Lang.Integer>().IntValue() is not { } epoch)
        {
            return;
        }

        lock (_gate)
        {
            _epochOfFrame[timestamp] = epoch;
            _lastStartedEpoch = epoch;
            Prune(_epochOfFrame);
        }
    }

    private void OnCompleted(CaptureRequest request, TotalCaptureResult result)
    {
        var timestamp = TakeLong(result.Get(CaptureResult.SensorTimestamp));
        var exposure = TakeLong(result.Get(CaptureResult.SensorExposureTime));
        if (TakeLong(result.Get(CaptureResult.SensorRollingShutterSkew)) is { } skew)
        {
            Interlocked.Exchange(ref _lastSkewNs, skew);
        }

        if (exposure is { } e)
        {
            Interlocked.Exchange(ref _lastExposureNs, e);
        }

        if (RawStreaming && OperatingSystem.IsAndroidVersionAtLeast(28) && result.Get(CaptureResult.SensorDynamicBlackLevel) is { } dynamicBlack)
        {
            using (dynamicBlack)
            {
                // In the order of the filter pattern's positions, as the fixed one; a value off the scale is ignored.
                if (JNIEnv.GetArray<float>(dynamicBlack.Handle) is { Length: >= 4 } levels && levels.Take(4).All(l => float.IsFinite(l) && l >= 0 && l < _white / 2))
                {
                    _dynamicBlack = [levels[0], levels[1], levels[2], levels[3]];
                }
            }
        }

        var epoch = request.Tag?.JavaCast<Java.Lang.Integer>().IntValue();
        List<ResultWaiter> done = [];
        List<ResultWaiter> stale = [];
        lock (_gate)
        {
            if (timestamp is { } t && exposure is { } x)
            {
                _exposureOfFrame[t] = x;
                Prune(_exposureOfFrame);
            }

            foreach (var waiter in _waiters)
            {
                if (waiter.Epoch == epoch && (waiter.Until is { } until ? until(result) : ++waiter.Seen >= waiter.Count))
                {
                    done.Add(waiter);
                }
                else if (waiter.Epoch < epoch)
                {
                    // Results come in order: once a later request's arrive, an earlier one's never will again.
                    stale.Add(waiter);
                }
            }

            _waiters.RemoveAll(w => done.Contains(w) || stale.Contains(w));
        }

        foreach (var waiter in done)
        {
            waiter.Done.TrySetResult(result);
        }

        foreach (var waiter in stale)
        {
            waiter.Done.TrySetException(new TimeoutException("The camera moved on to newer settings first."));
        }

        // Thirty results a second: each one nobody waits for is let go of at once rather than at the next collection.
        if (done.Count == 0)
        {
            result.Dispose();
        }
    }

    /// <summary>A frame: its statistics for each running stream and its luma for each picture being taken, then the
    /// buffer goes back to the camera at once.</summary>
    private void OnImage(ImageReader reader)
    {
        Image? image;
        try
        {
            image = reader.AcquireNextImage();
        }
        catch (Java.Lang.IllegalStateException)
        {
            return;
        }

        if (image is null)
        {
            return;
        }

        try
        {
            Interlocked.Increment(ref _framesSeen);
            Process(image);
        }
#pragma warning disable CA1031 // A frame that cannot be read must not take the app down (this runs in Android's callback).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The pictures being taken fail with the reason, so SoftCal says it; a stream's PC sees the frames stop.
            PictureSink[] pictures;
            lock (_gate)
            {
                pictures = [.. _pictures];
            }

            foreach (var picture in pictures)
            {
                picture.Done.TrySetException(new InvalidOperationException("The phone could not read a camera frame: " + ex.Message, ex));
            }
        }
        finally
        {
            // Close, not only Dispose: Dispose lets go of .NET's handle, close() gives the buffer back to the camera,
            // which stops sending frames once the reader's four are all held.
            image.Close();
            image.Dispose();
        }
    }

    private unsafe void Process(Image image)
    {
        var arrival = SystemClock.ElapsedRealtimeNanos();
        var timestamp = image.Timestamp;
        StreamSink[] streams;
        PictureSink[] pictures;
        int epoch;
        long? exposureNs;
        lock (_gate)
        {
            // A frame whose start was not seen takes the newest request's epoch (the rig skips the first frames
            // after a change anyway).
            epoch = _epochOfFrame.TryGetValue(timestamp, out var e) ? e : _lastStartedEpoch;
            exposureNs = _exposureOfFrame.TryGetValue(timestamp, out var x) ? x : null;
            if (exposureNs is null && _exposureOfEpoch.TryGetValue(epoch, out var asked))
            {
                exposureNs = asked;
            }

            // With a raw output, the statistics come from the raw frames only (never mixed with processed ones).
            streams = _rawInSession is null ? [.. _streams] : [];
            pictures = [.. _pictures];
        }

        if (streams.Length == 0 && pictures.Length == 0)
        {
            return;
        }

        var exposure = exposureNs ?? Interlocked.Read(ref _lastExposureNs);
        var time = RealtimeTimestamps
            ? (timestamp + Interlocked.Read(ref _lastSkewNs) + exposure) / 1e9
            : arrival / 1e9;
        var rotation = (SensorOrientation + DeviceRotation) % 360;
        var planes = image.GetPlanes()!;
        using var yBuffer = planes[0].Buffer!;
        using var uBuffer = planes[1].Buffer!;
        using var vBuffer = planes[2].Buffer!;
        var y = new ReadOnlySpan<byte>((void*)JNIEnv.GetDirectBufferAddress(yBuffer.Handle), yBuffer.Capacity());
        var u = new ReadOnlySpan<byte>((void*)JNIEnv.GetDirectBufferAddress(uBuffer.Handle), uBuffer.Capacity());
        var v = new ReadOnlySpan<byte>((void*)JNIEnv.GetDirectBufferAddress(vBuffer.Handle), vBuffer.Capacity());
        var yStride = planes[0].RowStride;
        var cStride = planes[1].RowStride;
        var cPixel = planes[1].PixelStride;
        foreach (var plane in planes)
        {
            plane.Dispose();
        }

        foreach (var stream in streams)
        {
            var rect = stream.Rect ??= Yuv420Statistics.ToFrame(stream.Region, rotation, image.Width, image.Height);
            stream.Rotation ??= rotation;
            var stats = Yuv420Statistics.Measure(y, yStride, u, v, cStride, cPixel, rect, stream.Region.RowBands);
            stream.OnFrame(new FrameMessage(
                Interlocked.Increment(ref _sequence),
                time,
                epoch,
                exposureNs / 1e9,
                stats.Red,
                stats.Green,
                stats.Blue,
                stats.Pixels,
                stats.Bands.Length > 0 ? stats.Bands : null));
        }

        foreach (var picture in pictures)
        {
            picture.Add(y, yStride, image.Width, image.Height, rotation, time);
        }
    }

    /// <summary>A raw frame: each running stream's statistics, straight from the sensor's values.</summary>
    private void OnRawImage(ImageReader reader)
    {
        Image? image;
        try
        {
            image = reader.AcquireNextImage();
        }
        catch (Java.Lang.IllegalStateException)
        {
            return;
        }

        if (image is null)
        {
            return;
        }

        try
        {
            ProcessRaw(image);
        }
#pragma warning disable CA1031 // A frame that cannot be read must not take the app down (this runs in Android's callback).
        catch (Exception)
#pragma warning restore CA1031
        {
            // The next frame is read as usual; a stream that gets none stalls, and SoftCal says so.
        }
        finally
        {
            image.Close();
            image.Dispose();
        }
    }

    private unsafe void ProcessRaw(Image image)
    {
        var arrival = SystemClock.ElapsedRealtimeNanos();
        var timestamp = image.Timestamp;
        StreamSink[] streams;
        int epoch;
        long? exposureNs;
        lock (_gate)
        {
            epoch = _epochOfFrame.TryGetValue(timestamp, out var e) ? e : _lastStartedEpoch;
            exposureNs = _exposureOfFrame.TryGetValue(timestamp, out var x) ? x : null;
            if (exposureNs is null && _exposureOfEpoch.TryGetValue(epoch, out var asked))
            {
                exposureNs = asked;
            }

            streams = [.. _streams];
        }

        if (streams.Length == 0)
        {
            return;
        }

        var exposure = exposureNs ?? Interlocked.Read(ref _lastExposureNs);
        var time = RealtimeTimestamps
            ? (timestamp + Interlocked.Read(ref _lastSkewNs) + exposure) / 1e9
            : arrival / 1e9;
        var rotation = (SensorOrientation + DeviceRotation) % 360;
        var planes = image.GetPlanes()!;
        using var buffer = planes[0].Buffer!;
        var stride = planes[0].RowStride / 2;
        foreach (var plane in planes)
        {
            plane.Dispose();
        }

        var bytes = new ReadOnlySpan<byte>((void*)JNIEnv.GetDirectBufferAddress(buffer.Handle), buffer.Capacity());
        var raw = MemoryMarshal.Cast<byte, ushort>(bytes);
        var black = _dynamicBlack ?? _black;
        foreach (var stream in streams)
        {
            var rect = stream.RawRect ??= RawRegion(stream.Region, rotation);
            stream.Rotation ??= rotation;
            var stats = RawStatistics.Measure(raw, stride, rect, stream.Region.RowBands, _bayer, black, _white);
            stream.OnFrame(new FrameMessage(
                Interlocked.Increment(ref _sequence),
                time,
                epoch,
                exposureNs / 1e9,
                stats.Red,
                stats.Green,
                stats.Blue,
                stats.Pixels,
                stats.Bands.Length > 0 ? stats.Bands : null));
        }
    }

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
        CameraCaptureSession? session;
        CameraDevice? device;
        ImageReader? reader;
        List<ResultWaiter> waiters;
        List<PictureSink> pictures;
        lock (_gate)
        {
            (session, device, reader) = (_session, _device, _reader);
            (_session, _device, _reader) = (null, null, null);
            _previewInSession = null;
            _rawInSession = null;
            waiters = [.. _waiters];
            pictures = [.. _pictures];
            _waiters.Clear();
        }

        var closed = new InvalidOperationException("The phone's camera closed.");
        foreach (var waiter in waiters)
        {
            waiter.Done.TrySetException(closed);
        }

        foreach (var picture in pictures)
        {
            picture.Done.TrySetException(closed);
        }

        try
        {
            session?.Close();
            device?.Close();
            reader?.Close();
        }
        catch (Java.Lang.Exception)
        {
            // Already gone.
        }
    }

    private static void Prune<T>(Dictionary<long, T> byTimestamp)
    {
        if (byTimestamp.Count <= 64)
        {
            return;
        }

        foreach (var old in byTimestamp.Keys.Order().Take(byTimestamp.Count - 32).ToList())
        {
            byTimestamp.Remove(old);
        }
    }

    /// <summary>Waits for the <paramref name="count"/>th result of an epoch, or for the first one that meets
    /// <paramref name="until"/>.</summary>
    private sealed class ResultWaiter(int epoch, int count, Func<CaptureResult, bool>? until = null)
    {
        public int Epoch { get; } = epoch;

        public int Count { get; } = count;

        public Func<CaptureResult, bool>? Until { get; } = until;

        public int Seen { get; set; }

        public TaskCompletionSource<CaptureResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Where the lens went: whether it reached the focus asked for, the distance it reports (diopters), the
    /// autofocus mode it reports, whether it was still moving, and whether it had to be sent to its nearest and back.</summary>
    private sealed record FocusSettling(bool Reached, float? Distance, int? AfMode, bool? Moving, bool Nudged);

    private sealed class StreamSink(RegionSpec region, Action<FrameMessage> onFrame)
    {
        public RegionSpec Region { get; } = region;

        public Action<FrameMessage> OnFrame { get; } = onFrame;

        /// <summary>The region in frame pixels, fixed by the first frame (the phone does not turn while measuring).</summary>
        public FrameRect? Rect { get; set; }

        /// <summary>The turn the region was taken with (fixed with <see cref="Rect"/>).</summary>
        public int? Rotation { get; set; }

        /// <summary>The region in raw photosites (fixed by the first raw frame).</summary>
        public FrameRect? RawRect { get; set; }
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

    private sealed class DeviceCallback(PhoneCamera owner, TaskCompletionSource<CameraDevice> opened) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => opened.TrySetResult(camera);

        public override void OnDisconnected(CameraDevice camera)
        {
            if (!opened.TrySetException(new InvalidOperationException("The camera is in use by another app.")))
            {
                owner.OnLost("Another app took the camera, or Android closed it (SoftCal Camera must stay in front).");
            }

            camera.Close();
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            var message = string.Create(CultureInfo.InvariantCulture, $"The camera failed ({error}).");
            if (!opened.TrySetException(new InvalidOperationException(message)))
            {
                owner.OnLost(message);
            }

            camera.Close();
        }
    }

    private sealed class SessionCallback(TaskCompletionSource<CameraCaptureSession> configured) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => configured.TrySetResult(session);

        public override void OnConfigureFailed(CameraCaptureSession session) =>
            configured.TrySetException(new InvalidOperationException("The camera would not stream at the size asked."));
    }

    private sealed class Results(PhoneCamera owner) : CameraCaptureSession.CaptureCallback
    {
        public override void OnCaptureStarted(CameraCaptureSession session, CaptureRequest request, long timestamp, long frameNumber) =>
            owner.OnStarted(request, timestamp);

        public override void OnCaptureCompleted(CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result) =>
            owner.OnCompleted(request, result);
    }

    private sealed class RawImageListener(PhoneCamera owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is not null)
            {
                owner.OnRawImage(reader);
            }
        }
    }

    private sealed class ImageListener(PhoneCamera owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is not null)
            {
                owner.OnImage(reader);
            }
        }
    }
}
