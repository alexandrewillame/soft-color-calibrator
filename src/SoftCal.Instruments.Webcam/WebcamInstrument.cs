using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SoftCal.Core.Color;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.Instruments.Webcam.Capture;
using SoftCal.Instruments.Webcam.Controls;
using SoftCal.Instruments.Webcam.Frames;
using SoftCal.Instruments.Webcam.Interop;
using SoftCal.Instruments.Webcam.Monitoring;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SoftCal.Instruments.Webcam;

/// <summary>The automatic controls as read at one moment of a stream (seconds from its start; null: unreadable), and the
/// whole picture's mean red, green and blue (0–255) in the latest frame (null: no frame yet).</summary>
public sealed record AutomaticSample(double Seconds, UvcControlValue? WhiteBalance, UvcControlValue? Exposure, UvcControlValue? Gain, Vec3? Picture);

/// <summary>Webcam settings that are not part of the platform-neutral lock request.</summary>
public sealed record WebcamOptions
{
    public FormatPreference Format { get; init; } = new();

    public WebcamLockSettings Lock { get; init; } = new();

    /// <summary>Frames thrown away after the stream starts, before the lock is read back.</summary>
    public int DiscardFramesAfterStart { get; init; } = 15;

    /// <summary>Overrides the Y′CbCr decoding; null uses the media type's matrix and range, or BT.601 full range.</summary>
    public YcbcrDecoder? Decoder { get; init; }

    /// <summary>Overrides the clip thresholds; null derives them from the decoder's range.</summary>
    public ClipThresholds? Clip { get; init; }

    /// <summary>Watch for other processes changing the controls (IMFCameraControlMonitor) and put them back.</summary>
    public bool WatchControls { get; init; } = true;

    /// <summary>Watch for other processes streaming from the camera (IMFSensorActivityMonitor).</summary>
    public bool WatchSensorActivity { get; init; } = true;

    /// <summary>No frame for this long (plus the exposure time) raises a <see cref="InstrumentFaultKind.StreamStalled"/> fault.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>No frame for this long (plus the exposure time) ends every frame stream with an error: the camera was
    /// unplugged, or hangs, and a reading waiting for frames must not wait for ever.</summary>
    public TimeSpan StallAbortTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>A change to a locked control made by someone else, and what was done about it.</summary>
public sealed record ExternalControlChange(UvcControlId Control, UvcControlValue Expected, UvcControlValue? Found, bool Reapplied, double TimeSeconds);

/// <summary>
/// A USB webcam behind <see cref="IMeasurementInstrument"/>: Media Foundation device source in controlling mode, a source
/// reader on the camera's native uncompressed format with converters disabled, UVC controls locked before the stream
/// starts and verified by read-back, and per-frame ROI statistics computed straight from the Y′CbCr buffer.
/// </summary>
public sealed class WebcamInstrument : IMeasurementInstrument, ILumaImageSource
{
    private readonly IDisposable _mediaFoundation;
    private readonly IMFMediaSource _source;
    private readonly MfUvcControlBackend _controls;
    private readonly IMFExtendedCameraController? _effectsController;
    private readonly Lock _gate = new();
    private readonly Dictionary<UvcControlId, UvcControlValue> _locked = [];
    private readonly List<ExternalControlChange> _externalChanges = [];
    private readonly List<StatisticsSink> _statisticsSinks = [];
    private IMFSourceReader? _reader;
    private CaptureLoop? _capture;
    private ControlChangeWatcher? _controlWatcher;
    private SensorActivityWatcher? _activityWatcher;
    private IReadOnlyList<EffectState> _effectsBefore = [];
    private Timer? _stallTimer;
    private YcbcrDecoder _decoder;
    private ClipThresholds _clip;
    private int _epoch;
    private double _exposureSeconds;
    private bool _restored = true;
    private bool _disposed;

    /// <summary>A restore's frames in manual mode before the automatic controls are switched back on.</summary>
    private static readonly TimeSpan SwitchBackAfter = TimeSpan.FromSeconds(0.5);

    /// <summary>A restore's frames in automatic before the stream stops, so the camera takes up its own adjustments.</summary>
    private static readonly TimeSpan AdjustAfterRestore = TimeSpan.FromSeconds(1.5);

    private WebcamInstrument(WebcamDevice device, WebcamOptions options, IDisposable mediaFoundation, IMFMediaSource source)
    {
        Device = device;
        Options = options;
        _mediaFoundation = mediaFoundation;
        _source = source;
        _controls = new MfUvcControlBackend(source);
        _effectsController = CameraEffects.GetController(source);
        Snapshot = ControlSnapshot.Capture(_controls, device.StableId);
        NativeFormats = ListNativeFormats(source);
        Format = FormatSelector.Choose(NativeFormats, options.Format)
            ?? throw new NotSupportedException(
                $"{device.FriendlyName} offers no uncompressed YUY2 or NV12 format; compressed formats would need a decoder.");
        _decoder = options.Decoder ?? new YcbcrDecoder(Format.Matrix ?? YcbcrMatrix.Bt601, Format.Range ?? YcbcrRange.Full);
        _clip = options.Clip ?? ClipThresholds.For(_decoder.Range);
        StudioEffectsAvailable = CameraEffects.IsStudioEffectsAvailable(device.SymbolicLink);

        var exposureRange = Snapshot.Find(UvcControlCatalog.Exposure)?.Range;
        var steps = exposureRange is null
            ? []
            : Enumerable.Range(exposureRange.Min, exposureRange.Max - exposureRange.Min + 1).Select(k => Math.Pow(2, k)).ToArray();
        Capabilities = new InstrumentCapabilities
        {
            ExposureModel = ExposureModel.DiscreteSteps,
            ExposureSteps = steps,
            MinExposureSeconds = steps.Length > 0 ? steps[0] : 0,
            MaxExposureSeconds = steps.Length > 0 ? steps[^1] : 0,
            ReportsActualExposure = false,
            OutputIsLinear = false,
            FullScale = 255,
            LockableControls = [.. Snapshot.Entries.Where(e => e.Range is not null)
                .Select(e => UvcControlCatalog.ToCameraControl(e.Id))
                .OfType<CameraControl>()],
        };
        Info = new InstrumentInfo(device.StableId, InstrumentKind.UsbWebcam, device.FriendlyName);
        _exposureSeconds = Snapshot.Find(UvcControlCatalog.Exposure)?.Value is { } e ? Math.Pow(2, e.Value) : 0;
    }

    public event EventHandler<InstrumentFault>? Faulted;

    public WebcamDevice Device { get; }

    public WebcamOptions Options { get; }

    public InstrumentInfo Info { get; }

    public InstrumentCapabilities Capabilities { get; }

    /// <summary>Every control as it was when the camera was opened (journal this before locking).</summary>
    public ControlSnapshot Snapshot { get; }

    public IReadOnlyList<NativeFormat> NativeFormats { get; }

    public NativeFormat Format { get; }

    public bool? StudioEffectsAvailable { get; }

    /// <summary>The detailed read-back of the last lock.</summary>
    public IReadOnlyList<ControlVerification>? LockVerification { get; private set; }

    public IReadOnlyList<PlannedWrite> LockPlan { get; private set; } = [];

    public IReadOnlyList<EffectState> EffectsAfterLock { get; private set; } = [];

    public string? ControlWatcherFailure { get; private set; }

    public string? ActivityWatcherFailure { get; private set; }

    public YcbcrDecoder Decoder
    {
        get
        {
            lock (_gate)
            {
                return _decoder;
            }
        }
    }

    public ClipThresholds Clip
    {
        get
        {
            lock (_gate)
            {
                return _clip;
            }
        }
    }

    public TimestampSource? TimestampSource => _capture?.TimestampSource;

    public long FramesCaptured => _capture?.FramesCaptured ?? 0;

    public double CurrentExposureSeconds
    {
        get
        {
            lock (_gate)
            {
                return _exposureSeconds;
            }
        }
    }

    public IReadOnlyList<ExternalControlChange> ExternalChanges
    {
        get
        {
            lock (_gate)
            {
                return [.. _externalChanges];
            }
        }
    }

    /// <summary>Opens the camera (no control is changed) and snapshots every control.</summary>
    public static WebcamInstrument Open(WebcamDevice device, WebcamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        var mf = MediaFoundationRuntime.Acquire();
        try
        {
            var source = CreateDeviceSource(device.SymbolicLink);
            try
            {
                return new WebcamInstrument(device, options ?? new WebcamOptions(), mf, source);
            }
            catch
            {
                ShutdownSource(source);
                throw;
            }
        }
        catch
        {
            mf.Dispose();
            throw;
        }
    }

    /// <summary>Reads every control without writing anything (unlike opening an instrument, whose disposal rewrites them).</summary>
    public static ControlSnapshot ReadControls(WebcamDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        using var mf = MediaFoundationRuntime.Acquire();
        var source = CreateDeviceSource(device.SymbolicLink);
        try
        {
            return ControlSnapshot.Capture(new MfUvcControlBackend(source), device.StableId);
        }
        finally
        {
            ShutdownSource(source);
        }
    }

    /// <summary>
    /// Puts a camera back to its everyday state: every control that supports automatic mode (white balance, exposure,
    /// focus…) set to automatic, everything else to the camera's own default. Controls without a reported range are
    /// left as they are. Done as a restore (<see cref="RestoreSnapshot"/>): every control at its default in manual mode
    /// first, then the automatic ones switched back on, while the camera streams. Returns the controls that failed.
    /// </summary>
    public static IReadOnlyList<string> ResetToAutomatic(WebcamDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var current = ReadControls(device);
        var everyday = current with
        {
            Entries = [.. current.Entries.Select(e => e.Range is { } r ? e with { Value = new UvcControlValue(r.Default, IsAuto: r.SupportsAuto) } : e)],
        };
        return RestoreSnapshot(device, everyday);
    }

    /// <summary>
    /// Streams for <paramref name="duration"/> without changing anything, reading white balance, exposure and gain every
    /// <paramref name="interval"/>, with the picture's mean colour (numbers only; no picture is kept). A camera's
    /// automatic adjustments only run while it streams, so this is how they are seen at work: an automatic value that
    /// never moves from where it was set may be stuck, unless the picture explains it (a dark one, for instance).
    /// </summary>
    public static async Task<IReadOnlyList<AutomaticSample>> WatchAutomaticAsync(WebcamDevice device, TimeSpan duration, TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var camera = Open(device, new WebcamOptions { WatchControls = false, WatchSensorActivity = false });
        await using (camera.ConfigureAwait(false))
        {
            var samples = new List<AutomaticSample>();
            var clock = Stopwatch.StartNew();
            await camera.StartPreviewAsync(cancellationToken).ConfigureAwait(false);
            var gate = new Lock();
            Vec3? picture = null;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var reading = Task.Run(
                async () =>
                {
                    await foreach (var frame in camera.StreamAsync(new RoiSpec(0.5, 0.5, 1, 1) { RowBands = 0 }, stop.Token).ConfigureAwait(false))
                    {
                        lock (gate)
                        {
                            picture = new Vec3(frame.Red.Mean, frame.Green.Mean, frame.Blue.Mean);
                        }
                    }
                },
                stop.Token);
            try
            {
                while (true)
                {
                    Vec3? seen;
                    lock (gate)
                    {
                        seen = picture;
                    }

                    samples.Add(new AutomaticSample(
                        clock.Elapsed.TotalSeconds,
                        camera.ReadControl(UvcControlCatalog.WhiteBalance),
                        camera.ReadControl(UvcControlCatalog.Exposure),
                        camera.ReadControl(UvcControlCatalog.Gain),
                        seen));
                    if (clock.Elapsed >= duration)
                    {
                        return samples;
                    }

                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                try
                {
                    await reading.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The stream ends with the watch.
                }
            }
        }
    }

    /// <summary>
    /// The picture's mean colour with the white balance at each setting in turn (a temperature in kelvins, or null for
    /// automatic), each after <paramref name="settle"/> of stream. A camera whose white balance works shows the colour
    /// follow the manual settings, and its automatic one land somewhere sensible; a stuck one shows the same colour in
    /// automatic whatever it saw. The white balance is left as the last setting: put the camera back afterwards
    /// (<see cref="ResetToAutomatic"/>).
    /// </summary>
    public static async Task<IReadOnlyList<(int? Kelvin, Vec3 Picture)>> CompareWhiteBalanceAsync(WebcamDevice device, IReadOnlyList<int?> settings, TimeSpan settle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);
        var camera = Open(device, new WebcamOptions { WatchControls = false, WatchSensorActivity = false });
        await using (camera.ConfigureAwait(false))
        {
            await camera.StartPreviewAsync(cancellationToken).ConfigureAwait(false);
            var range = camera.Snapshot.Find(UvcControlCatalog.WhiteBalance)?.Range ?? throw new NotSupportedException("The camera has no white balance control.");
            var results = new List<(int?, Vec3)>();
            foreach (var kelvin in settings)
            {
                camera._controls.Set(UvcControlCatalog.WhiteBalance, kelvin is { } k ? new UvcControlValue(range.Clamp(k), IsAuto: false) : new UvcControlValue(range.Default, IsAuto: true));
                await Task.Delay(settle, cancellationToken).ConfigureAwait(false);
                var frames = new List<FrameStats>();
                using var enough = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                enough.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    await foreach (var frame in camera.StreamAsync(new RoiSpec(0.5, 0.5, 1, 1) { RowBands = 0 }, enough.Token).ConfigureAwait(false))
                    {
                        frames.Add(frame);
                        if (frames.Count >= 5)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Fewer frames than hoped: the mean of those that came.
                }

                if (frames.Count > 0)
                {
                    results.Add((kelvin, new Vec3(frames.Average(f => f.Red.Mean), frames.Average(f => f.Green.Mean), frames.Average(f => f.Blue.Mean))));
                }
            }

            return results;
        }
    }

    /// <summary>
    /// Writes a journaled snapshot back to a camera, e.g. after a crash, while the camera streams for about two seconds
    /// (see <see cref="CameraLock.ApplyRestoreAsync"/>); returns the controls that failed. A camera that cannot stream
    /// gets the writes anyway.
    /// </summary>
    public static IReadOnlyList<string> RestoreSnapshot(WebcamDevice device, ControlSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(snapshot);

        // On a thread-pool (MTA) thread, as the camera's COM objects want: the caller may be a startup or UI thread.
        return Task.Run(() => RestoreStreamingAsync(device, snapshot)).GetAwaiter().GetResult();
    }

    private static async Task<IReadOnlyList<string>> RestoreStreamingAsync(WebcamDevice device, ControlSnapshot snapshot)
    {
        WebcamInstrument camera;
        try
        {
            camera = Open(device, new WebcamOptions { WatchControls = false, WatchSensorActivity = false });
        }
        catch (NotSupportedException)
        {
            // No format this code can stream: the writes alone.
            return await RestoreIdleAsync(device, snapshot).ConfigureAwait(false);
        }

        await using (camera.ConfigureAwait(false))
        {
            var streaming = true;
            try
            {
                await camera.StartPreviewAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
            {
                streaming = false;
            }

            var failed = await CameraLock.ApplyRestoreAsync(camera._controls, snapshot, streaming ? SwitchBackAfter : TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
            if (streaming)
            {
                await Task.Delay(AdjustAfterRestore).ConfigureAwait(false);
            }

            return failed;
        }
    }

    private static async Task<IReadOnlyList<string>> RestoreIdleAsync(WebcamDevice device, ControlSnapshot snapshot)
    {
        using var mf = MediaFoundationRuntime.Acquire();
        var source = CreateDeviceSource(device.SymbolicLink);
        try
        {
            return await CameraLock.ApplyRestoreAsync(new MfUvcControlBackend(source), snapshot, SwitchBackAfter, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            ShutdownSource(source);
        }
    }

    /// <summary>
    /// The lock, in the order that keeps Windows' per-camera defaults out: write the manual values before the stream
    /// starts, start it, discard the first frames, then read every control back and start watching for changes.
    /// </summary>
    public async Task<LockReport> PrepareAsync(LockRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_capture is not null)
        {
            throw new InvalidOperationException("The camera is already locked and streaming.");
        }

        var plan = CameraLock.Plan(Snapshot, request, Options.Lock);
        _restored = false;
        var errors = new Dictionary<UvcControlId, string>();
        foreach (var write in plan)
        {
            try
            {
                _controls.Set(write.Id, write.Value);
            }
            catch (Exception ex) when (ex is COMException or NotSupportedException or ArgumentException)
            {
                errors[write.Id] = ex.Message;
            }
        }

        _effectsBefore = request.DisableEffects ? CameraEffects.TurnOff(_effectsController) : CameraEffects.Read(_effectsController);
        LockPlan = plan;

        StartStream();
        await WaitForFramesAsync(Options.DiscardFramesAfterStart, cancellationToken).ConfigureAwait(false);

        var verification = CameraLock.Verify(Snapshot, plan, errors, _controls.Get);
        LockVerification = verification;
        EffectsAfterLock = CameraEffects.Read(_effectsController);
        lock (_gate)
        {
            _locked.Clear();
            foreach (var v in verification.Where(v => v.Outcome == LockOutcome.Locked && v.Requested is not null))
            {
                _locked[v.Id] = v.Requested!.Value;
            }

            if (_locked.TryGetValue(UvcControlCatalog.Exposure, out var exposure))
            {
                _exposureSeconds = Math.Pow(2, exposure.Value);
            }
        }

        StartWatchers();
        var effects = EffectsAfterLock.Select(s => new ControlLockResult(
            s.Control,
            !s.Supported ? LockOutcome.NotSupported : s.IsOff ? LockOutcome.Locked : LockOutcome.VerifiedMismatch,
            s.OffFlags,
            s.Flags,
            s.Note));
        var studio = new ControlLockResult(
            CameraControl.StudioEffects,
            StudioEffectsAvailable == true ? LockOutcome.VerifiedMismatch : LockOutcome.NotSupported,
            Note: StudioEffectsAvailable switch
            {
                true => "Windows Studio Effects are available on this camera: turn them off in Settings",
                false => "not available",
                _ => "unknown",
            });
        return CameraLock.ToLockReport(verification, [.. effects, studio]);
    }

    /// <summary>
    /// Streams with the camera's own (automatic) settings, for the placement preview: no control is written, so there
    /// is nothing to restore. <see cref="CaptureLumaImageAsync"/> then returns pictures. A measurement opens the camera
    /// again and locks it.
    /// </summary>
    public async Task StartPreviewAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_capture is not null)
        {
            return;
        }

        _restored = true;
        StartStream();
        await WaitForFramesAsync(3, cancellationToken).ConfigureAwait(false);
    }

    public Task<ExposureState> SetExposureAsync(ExposureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var range = Snapshot.Find(UvcControlCatalog.Exposure)?.Range
            ?? throw new NotSupportedException("The camera has no manual exposure control.");
        var log2 = range.Clamp((int)Math.Round(Math.Log2(request.Seconds)));
        var value = new UvcControlValue(log2, IsAuto: false);
        lock (_gate)
        {
            _locked[UvcControlCatalog.Exposure] = value;
            _controls.Set(UvcControlCatalog.Exposure, value);
            _exposureSeconds = Math.Pow(2, log2);
            _epoch++;
            return Task.FromResult(new ExposureState(_exposureSeconds, null, _epoch));
        }
    }

    /// <summary>Changes the manual focus (the lock records the new value, so the watcher does not undo it).</summary>
    public Task SetFocusAsync(int focus, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var range = Snapshot.Find(UvcControlCatalog.Focus)?.Range ?? throw new NotSupportedException("The camera has no focus control.");
        var value = new UvcControlValue(range.Clamp(focus), IsAuto: false);
        lock (_gate)
        {
            _locked[UvcControlCatalog.Focus] = value;
            _controls.Set(UvcControlCatalog.Focus, value);
        }

        return Task.CompletedTask;
    }

    /// <summary>Uses a new Y′CbCr decoding (e.g. after the range was detected from the clip levels).</summary>
    public void SetDecoding(YcbcrDecoder decoder, ClipThresholds? clip = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        lock (_gate)
        {
            _decoder = decoder;
            _clip = clip ?? ClipThresholds.For(decoder.Range);
        }
    }

    public async IAsyncEnumerable<FrameStats> StreamAsync(RoiSpec roi, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in StreamDetailedAsync(roi, cancellationToken).ConfigureAwait(false))
        {
            yield return frame.Stats;
        }
    }

    /// <summary><see cref="StreamAsync"/> with the Y′CbCr means, luma extremes and arrival times.</summary>
    public async IAsyncEnumerable<WebcamFrame> StreamDetailedAsync(RoiSpec roi, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roi);
        var capture = _capture ?? throw new InvalidOperationException("Call PrepareAsync before streaming.");
        var sink = new StatisticsSink(roi, () => Volatile.Read(ref _epoch), () => (Decoder, Clip));
        lock (_gate)
        {
            _statisticsSinks.Add(sink);
        }

        capture.AddSink(sink);
        try
        {
            await foreach (var frame in sink.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            capture.RemoveSink(sink);
            sink.Close();
            lock (_gate)
            {
                _statisticsSinks.Remove(sink);
            }
        }
    }

    /// <summary>Averages the luma of every pixel of the region over <paramref name="frames"/> frames.</summary>
    public async Task<RoiImage> CaptureLumaImageAsync(RoiSpec roi, int frames, int skipFrames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roi);
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        var capture = _capture ?? throw new InvalidOperationException("Call PrepareAsync before capturing.");
        var sink = new LumaImageSink(roi, frames, skipFrames, () => Decoder);
        capture.AddSink(sink);
        await using (cancellationToken.Register(sink.Cancel).ConfigureAwait(false))
        {
            try
            {
                return await sink.Completion.ConfigureAwait(false);
            }
            finally
            {
                capture.RemoveSink(sink);
            }
        }
    }

    /// <summary>Reads every locked control again and compares it with the lock (for the long lock-hold test).</summary>
    public IReadOnlyList<(UvcControlId Control, UvcControlValue Expected, UvcControlValue? Actual)> CheckLock()
    {
        KeyValuePair<UvcControlId, UvcControlValue>[] locked;
        lock (_gate)
        {
            locked = [.. _locked];
        }

        return [.. locked.Select(kv => (kv.Key, kv.Value, _controls.Get(kv.Key)))];
    }

    /// <summary>Reads one control now.</summary>
    public UvcControlValue? ReadControl(UvcControlId id) => _controls.Get(id);

    /// <summary>
    /// Puts every control back as it was when the camera was opened, while the camera still streams (the stream that
    /// measured, or a new one), then stops the stream: see <see cref="CameraLock.ApplyRestoreAsync"/>. About two
    /// seconds. Once begun it runs to the end whatever the token says: a restore left half done would leave the camera
    /// locked for every other app.
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (_restored)
        {
            await Task.Run(StopStreamAndWatchers, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Run(StopWatchers, CancellationToken.None).ConfigureAwait(false);
        var streaming = await KeepStreamingAsync().ConfigureAwait(false);
        CameraEffects.Restore(_effectsController, _effectsBefore);
        var failed = await CameraLock.ApplyRestoreAsync(_controls, Snapshot, streaming ? SwitchBackAfter : TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
        if (streaming)
        {
            await Task.Delay(AdjustAfterRestore, CancellationToken.None).ConfigureAwait(false);
        }

        await Task.Run(StopStreamAndWatchers, CancellationToken.None).ConfigureAwait(false);
        _restored = true;
        if (failed.Count > 0)
        {
            RaiseFault(InstrumentFaultKind.Other, "Some camera controls could not be restored: " + string.Join(", ", failed));
        }
    }

    /// <summary>The stream, running for a restore: the one that measured, or a new one when it stopped. False when the
    /// camera cannot stream any more: the restore is then written to it idle, the best that can be done.</summary>
    private async Task<bool> KeepStreamingAsync()
    {
        if (_capture is { IsRunning: true })
        {
            return true;
        }

        try
        {
            await Task.Run(StopStreamAndWatchers).ConfigureAwait(false);
            StartStream();
            await WaitForFramesAsync(2, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await RestoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            MfInterop.Release(_effectsController);
            ShutdownSource(_source);
            _mediaFoundation.Dispose();
        }
    }

    private static IMFMediaSource CreateDeviceSource(string symbolicLink)
    {
        var attributes = MfInterop.CreateAttributes(2);
        try
        {
            attributes.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            attributes.SetString(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, symbolicLink);
            PInvoke.MFCreateDeviceSource(attributes, out var source).ThrowOnFailure();
            return source;
        }
        finally
        {
            MfInterop.Release(attributes);
        }
    }

    private static void ShutdownSource(IMFMediaSource source)
    {
        try
        {
            source.Shutdown();
        }
        catch (COMException)
        {
        }

        MfInterop.Release(source);
    }

    private static IReadOnlyList<NativeFormat> ListNativeFormats(IMFMediaSource source)
    {
        var reader = CreateReader(source);
        try
        {
            return ReadNativeFormats(reader);
        }
        finally
        {
            MfInterop.Release(reader);
        }
    }

    private static IMFSourceReader CreateReader(IMFMediaSource source)
    {
        var attributes = MfInterop.CreateAttributes(2);
        try
        {
            attributes.SetUINT32(PInvoke.MF_READWRITE_DISABLE_CONVERTERS, 1);
            attributes.SetUINT32(PInvoke.MF_SOURCE_READER_DISCONNECT_MEDIASOURCE_ON_SHUTDOWN, 1);
            PInvoke.MFCreateSourceReaderFromMediaSource(source, attributes, out var reader).ThrowOnFailure();
            return reader;
        }
        finally
        {
            MfInterop.Release(attributes);
        }
    }

    private static List<NativeFormat> ReadNativeFormats(IMFSourceReader reader)
    {
        var stream = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
        var formats = new List<NativeFormat>();
        for (uint i = 0; ; i++)
        {
            IMFMediaType type;
            try
            {
                reader.GetNativeMediaType(stream, i, out type);
            }
            catch (COMException ex) when (ex.HResult == MfInterop.NoMoreTypes)
            {
                break;
            }

            try
            {
                formats.Add(Describe(type, (int)i));
            }
            finally
            {
                MfInterop.Release(type);
            }
        }

        return formats;
    }

    private static NativeFormat Describe(IMFMediaType type, int index)
    {
        var subtype = MfInterop.GetGuid(type, PInvoke.MF_MT_SUBTYPE) ?? Guid.Empty;
        var (width, height) = MfInterop.GetUInt64(type, PInvoke.MF_MT_FRAME_SIZE) is { } size ? MfInterop.Unpack(size) : (0u, 0u);
        var (num, den) = MfInterop.GetUInt64(type, PInvoke.MF_MT_FRAME_RATE) is { } rate ? MfInterop.Unpack(rate) : (0u, 1u);
        var stride = MfInterop.GetUInt32(type, PInvoke.MF_MT_DEFAULT_STRIDE) is { } s ? (int?)unchecked((int)s) : null;
        YcbcrMatrix? matrix = MfInterop.GetUInt32(type, PInvoke.MF_MT_YUV_MATRIX) switch
        {
            1 => YcbcrMatrix.Bt709,
            2 => YcbcrMatrix.Bt601,
            _ => null,
        };
        YcbcrRange? range = MfInterop.GetUInt32(type, PInvoke.MF_MT_VIDEO_NOMINAL_RANGE) switch
        {
            1 => YcbcrRange.Full,
            2 => YcbcrRange.Limited,
            _ => null,
        };
        return new NativeFormat(index, FourCc(subtype), (int)width, (int)height, den == 0 ? 0 : (double)num / den, stride, matrix, range);
    }

    /// <summary>Video subtypes are FOURCC-based GUIDs (Data1 holds the code).</summary>
    private static string FourCc(Guid subtype)
    {
        var data1 = BitConverter.ToUInt32(subtype.ToByteArray(), 0);
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var c = (char)((data1 >> (8 * i)) & 0xFF);
            chars[i] = c is >= ' ' and <= '~' ? c : '?';
        }

        var text = new string(chars).TrimEnd();
        return text.All(char.IsAsciiLetterOrDigit) ? text : subtype.ToString("D", CultureInfo.InvariantCulture);
    }

    private void StartStream()
    {
        var reader = CreateReader(_source);
        try
        {
            var stream = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
            reader.SetStreamSelection(unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_ALL_STREAMS), false);
            reader.SetStreamSelection(stream, true);
            reader.GetNativeMediaType(stream, (uint)Format.Index, out var type);
            try
            {
                reader.SetCurrentMediaType(stream, type);
            }
            finally
            {
                MfInterop.Release(type);
            }
        }
        catch
        {
            MfInterop.Release(reader);
            throw;
        }

        _reader = reader;
        var capture = new CaptureLoop(reader, Format);
        capture.Failed += OnCaptureFailed;
        _capture = capture;
        capture.Start();
        _stallTimer = new Timer(_ => CheckStall(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private async Task WaitForFramesAsync(int frames, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (10 + (frames * 0.5)));
        while (FramesCaptured < frames)
        {
            if (_capture is not { IsRunning: true })
            {
                throw new InvalidOperationException("The camera stream stopped while starting.");
            }

            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new TimeoutException($"The camera delivered {FramesCaptured} frames; expected {frames}.");
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    private void StartWatchers()
    {
        if (Options.WatchControls)
        {
            KeyValuePair<UvcControlId, UvcControlValue>[] locked;
            lock (_gate)
            {
                locked = [.. _locked];
            }

            _controlWatcher = ControlChangeWatcher.TryStart(
                Device.SymbolicLink,
                locked.Select(kv => kv.Key).Where(id => id.HasDirectShowInterface),
                out var failure);
            ControlWatcherFailure = failure;
            if (_controlWatcher is not null)
            {
                _controlWatcher.Changed += OnControlChanged;
                _controlWatcher.Error += hr => RaiseFault(InstrumentFaultKind.Other, string.Create(CultureInfo.InvariantCulture, $"The camera control monitor failed (0x{hr:X8})."));
            }
        }

        if (Options.WatchSensorActivity)
        {
            _activityWatcher = SensorActivityWatcher.TryStart(Device.SymbolicLink, out var failure);
            ActivityWatcherFailure = failure;
            if (_activityWatcher is not null)
            {
                _activityWatcher.Report += OnSensorActivity;
            }
        }
    }

    private void OnControlChanged(UvcControlId id)
    {
        UvcControlValue expected;
        lock (_gate)
        {
            if (!_locked.TryGetValue(id, out expected))
            {
                return;
            }
        }

        var found = _controls.Get(id);
        if (found is { } f && f.Value == expected.Value && f.IsAuto == expected.IsAuto)
        {
            return; // our own write
        }

        var reapplied = false;
        try
        {
            _controls.Set(id, expected);
            reapplied = true;
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException)
        {
        }

        var change = new ExternalControlChange(id, expected, found, reapplied, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        lock (_gate)
        {
            _externalChanges.Add(change);
        }

        RaiseFault(
            InstrumentFaultKind.ControlChangedExternally,
            string.Create(CultureInfo.InvariantCulture, $"{UvcControlCatalog.NameOf(id)} was changed by another process to {found?.Value} ({(found?.IsAuto == true ? "auto" : "manual")}); {(reapplied ? "re-applied" : "could not re-apply")} {expected.Value}."));
    }

    private void OnSensorActivity(IReadOnlyList<SensorProcess> processes)
    {
        var own = Environment.ProcessId;
        var others = processes.Where(p => p.ProcessId != own && p.Streaming).ToList();
        if (others.Count > 0)
        {
            RaiseFault(
                InstrumentFaultKind.AnotherAppStreaming,
                "Another process is streaming from the camera: PID " + string.Join(", ", others.Select(p => p.ProcessId.ToString(CultureInfo.InvariantCulture))));
        }
    }

    private void OnCaptureFailed(Exception error)
    {
        StatisticsSink[] sinks;
        lock (_gate)
        {
            sinks = [.. _statisticsSinks];
        }

        foreach (var sink in sinks)
        {
            sink.Close(new MeasurementException("The camera stream failed: " + error.Message, error));
        }

        RaiseFault(InstrumentFaultKind.Disconnected, "The camera stream failed: " + error.Message);
    }

    private void CheckStall()
    {
        var capture = _capture;
        if (capture is not { IsRunning: true } || capture.FramesCaptured == 0)
        {
            return;
        }

        var idle = (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency) - capture.LastFrameArrivalSeconds;
        if (idle > Options.StallAbortTimeout.TotalSeconds + CurrentExposureSeconds)
        {
            // Stalled for good: the frame streams end with the reason, so the run stops (and puts everything back).
            StatisticsSink[] sinks;
            lock (_gate)
            {
                sinks = [.. _statisticsSinks];
            }

            var reason = string.Create(CultureInfo.InvariantCulture, $"The camera sent no frame for {idle:F0} s: it may have been unplugged, or another app may be using it.");
            foreach (var sink in sinks)
            {
                sink.Close(new MeasurementException(reason));
            }

            RaiseFault(InstrumentFaultKind.Disconnected, reason);
        }
        else if (idle > Options.StallTimeout.TotalSeconds + CurrentExposureSeconds)
        {
            RaiseFault(InstrumentFaultKind.StreamStalled, string.Create(CultureInfo.InvariantCulture, $"No camera frame for {idle:F1} s."));
        }
    }

    /// <summary>Stops watching for other processes (a restore's own writes must not be taken for theirs).</summary>
    private void StopWatchers()
    {
        _controlWatcher?.Dispose();
        _controlWatcher = null;
        _activityWatcher?.Dispose();
        _activityWatcher = null;
    }

    private void StopStreamAndWatchers()
    {
        _stallTimer?.Dispose();
        _stallTimer = null;
        StopWatchers();
        var capture = Interlocked.Exchange(ref _capture, null);
        var stopped = true;
        if (capture is not null)
        {
            capture.Failed -= OnCaptureFailed;
            stopped = capture.Stop(TimeSpan.FromSeconds(3) + TimeSpan.FromSeconds(CurrentExposureSeconds * 2));
        }

        StatisticsSink[] sinks;
        lock (_gate)
        {
            sinks = [.. _statisticsSinks];
        }

        foreach (var sink in sinks)
        {
            sink.Close();
        }

        var reader = Interlocked.Exchange(ref _reader, null);
        if (stopped)
        {
            MfInterop.Release(reader);
        }
    }

    private void RaiseFault(InstrumentFaultKind kind, string message) => Faulted?.Invoke(this, new InstrumentFault(kind, message));
}
