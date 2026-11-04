using System.Runtime.CompilerServices;
using SoftCal.Core.Color;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;

namespace SoftCal.Instruments.Simulated;

public sealed record SimulatedRigOptions
{
    public double FrameRate { get; init; } = 30;

    /// <summary>Delay between a patch being presented and the camera seeing it (display + capture pipeline).</summary>
    public double LatencySeconds { get; init; } = 0.1;

    /// <summary>Frames that still use the old exposure after a change, as with real webcams.</summary>
    public int ExposureApplyFrames { get; init; } = 2;

    /// <summary>False runs on a virtual clock (fast and deterministic); true paces frames in real time.</summary>
    public bool PaceInRealTime { get; init; }

    /// <summary>Backlight PWM frequency; 0 = flicker-free backlight.</summary>
    public double PwmFrequencyHz { get; init; }

    /// <summary>Relative depth of the PWM modulation of the emitted light (0–1).</summary>
    public double PwmDepth { get; init; }

    /// <summary>Rolling-shutter line time: row r of a frame is exposed r line times after row 0.</summary>
    public double RowLineTimeSeconds { get; init; } = 60e-6;

    /// <summary>Rows of the simulated sensor's region of interest (row bands are spread over them).</summary>
    public int RoiRows { get; init; } = 432;

    /// <summary>
    /// 0 = a steady backlight. Otherwise the backlight follows the picture like "dynamic contrast": its light is scaled
    /// by 1 + k × (average screen level − 0.25), the level being smoothed over <see cref="DynamicContrastSeconds"/>.
    /// </summary>
    public double DynamicContrast { get; init; }

    public double DynamicContrastSeconds { get; init; } = 1.0;

    /// <summary>0 = a still camera. Otherwise the camera "moves" every this many seconds: the light it sees jumps by up
    /// to ± <see cref="ShakeAmount"/> (relative), as a hand-held camera tilting on a TN panel would see.</summary>
    public double ShakeEverySeconds { get; init; }

    public double ShakeAmount { get; init; } = 0.03;
}

/// <summary>
/// A simulated display and camera behind the same interfaces as the real hardware, with known ground truth.
/// Time is virtual: each captured frame advances the clock by one frame period.
/// </summary>
public sealed class SimulatedRig : IMeasurementInstrument, IPatchPresenter
{
    private readonly Lock _gate = new();
    private readonly List<(double Time, PatchSpec Patch)> _timeline = [];
    private readonly List<(double Time, PatchSpec Patch)> _referenceTimeline = [];
    private readonly TimeProvider _timeProvider;
    private long _frameIndex;
    private long _patchSequence;
    private int _epoch;
    private double _activeExposure;
    private double _pendingExposure;
    private long _pendingApplyFrame = -1;
    private double _averageLevel = 0.25;

    public SimulatedRig(
        SimulatedDisplay display,
        SimulatedCamera camera,
        SimulatedRigOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        Display = display ?? throw new ArgumentNullException(nameof(display));
        Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        Options = options ?? new SimulatedRigOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _activeExposure = Camera.SnapExposure(1.0 / 32);
        var steps = Camera.NominalExposures;
        Capabilities = new InstrumentCapabilities
        {
            ExposureModel = ExposureModel.DiscreteSteps,
            ExposureSteps = steps,
            MinExposureSeconds = steps[0],
            MaxExposureSeconds = steps[^1],
            OutputIsLinear = Camera.Options.Response.IsLinear,
            FullScale = 255,
            LockableControls = [CameraControl.WhiteBalance, CameraControl.Exposure, CameraControl.Gain, CameraControl.Focus],
        };
    }

    public event EventHandler<InstrumentFault>? Faulted;

    public SimulatedDisplay Display { get; }

    /// <summary>A second screen to compare whites with (a white match); null = none.</summary>
    public SimulatedDisplay? ReferenceDisplay { get; init; }

    /// <summary>Shows patches on <see cref="ReferenceDisplay"/>; the rig itself shows them on <see cref="Display"/>.</summary>
    public IPatchPresenter ReferencePresenter => new ReferenceScreen(this);

    /// <summary>True while the camera rests on the reference screen (the user moved it there).</summary>
    public bool CameraOnReference { get; set; }

    public SimulatedCamera Camera { get; }

    public SimulatedRigOptions Options { get; }

    public InstrumentInfo Info { get; } = new("simulated-rig", InstrumentKind.Simulated, "SimulatedRig");

    public InstrumentCapabilities Capabilities { get; }

    /// <summary>Current virtual time in seconds.</summary>
    public double Now
    {
        get
        {
            lock (_gate)
            {
                return _frameIndex / Options.FrameRate;
            }
        }
    }

    public Task<PresentedPatch> ShowAsync(PatchSpec patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var time = _frameIndex / Options.FrameRate;
            _timeline.Add((time, patch));
            return Task.FromResult(new PresentedPatch(patch, ++_patchSequence, time));
        }
    }

    public Task<LockReport> PrepareAsync(LockRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // A camera with a hidden auto white balance still *claims* the lock; only the trust test can tell.
        LockReport report = new(
        [
            new(CameraControl.WhiteBalance, LockOutcome.Locked, request.WhiteBalanceKelvin, request.WhiteBalanceKelvin),
            new(CameraControl.Exposure, LockOutcome.Locked),
            new(CameraControl.Gain, LockOutcome.Locked, 0, 0),
            new(CameraControl.Focus, LockOutcome.Locked),
        ]);
        return Task.FromResult(report);
    }

    public Task<ExposureState> SetExposureAsync(ExposureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var snapped = Camera.SnapExposure(request.Seconds);
        lock (_gate)
        {
            _epoch++;
            _pendingExposure = snapped;
            _pendingApplyFrame = _frameIndex + Options.ExposureApplyFrames;
            return Task.FromResult(new ExposureState(snapped, null, _epoch));
        }
    }

    public async IAsyncEnumerable<FrameStats> StreamAsync(
        RoiSpec roi,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roi);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return CaptureFrame(roi.RowBands);
            if (Options.PaceInRealTime)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 / Options.FrameRate), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    /// <summary>Captures the next frame synchronously and advances the virtual clock by one frame.</summary>
    /// <param name="rowBands">Horizontal bands to report (0 = none); PWM flicker shows up in them as rolling-shutter bands.</param>
    public FrameStats CaptureFrame(int rowBands = 0)
    {
        lock (_gate)
        {
            var time = _frameIndex / Options.FrameRate;
            if (_pendingApplyFrame >= 0 && _frameIndex >= _pendingApplyFrame)
            {
                _activeExposure = _pendingExposure;
                _pendingApplyFrame = -1;
            }

            var onReference = CameraOnReference && ReferenceDisplay is not null;
            var display = onReference ? ReferenceDisplay! : Display;
            var visible = VisiblePatch(onReference ? _referenceTimeline : _timeline, time - Options.LatencySeconds);
            var light = visible is null ? display.Emit(DeviceRgb.Black) : display.Emit(visible);
            if (Options.DynamicContrast != 0)
            {
                var follow = 1 - Math.Exp(-1 / (Options.FrameRate * Math.Max(1e-3, Options.DynamicContrastSeconds)));
                _averageLevel += (AverageLevel(visible) - _averageLevel) * follow;
                light *= 1 + (Options.DynamicContrast * (_averageLevel - 0.25));
            }

            if (Options.ShakeEverySeconds > 0)
            {
                // A hashed pose number, not new Random(pose): nearby seeds give nearly equal first values.
                var pose = (uint)Math.Floor(time / Options.ShakeEverySeconds);
                var hash = unchecked(pose * 2654435761u) ^ unchecked((pose + 1) * 40503u);
                light *= 1 + (Options.ShakeAmount * ((2 * ((hash % 10007) / 10006.0)) - 1));
            }

            var exposure = Camera.ActualExposure(_activeExposure);
            var (red, green, blue) = Camera.Capture(light, exposure);
            var bands = rowBands > 0 ? RowBands(light, exposure, time, Math.Min(rowBands, Options.RoiRows)) : null;
            var frame = new FrameStats(_frameIndex, time, _epoch, red, green, blue, Camera.Options.RoiPixelCount, bands);
            _frameIndex++;
            return frame;
        }
    }

    /// <summary>
    /// Mean luma per band, noise-free, with the PWM modulation each band's rows integrate over their exposure windows
    /// (row r ends its exposure r line times after row 0, at the frame time).
    /// </summary>
    private double[] RowBands(Vec3 light, double exposure, double frameTime, int bands)
    {
        var rows = Options.RoiRows;
        var result = new double[bands];
        var counts = new int[bands];
        for (var r = 0; r < rows; r++)
        {
            var end = frameTime - (Options.LatencySeconds / 2) + (r * Options.RowLineTimeSeconds);
            var modulation = 1.0;
            if (Options.PwmFrequencyHz > 0 && Options.PwmDepth > 0)
            {
                // Mean of 1 + d·sin(2πft) over [end − exposure, end].
                var w = 2 * Math.PI * Options.PwmFrequencyHz;
                var mean = (Math.Cos(w * (end - exposure)) - Math.Cos(w * end)) / (w * exposure);
                modulation = 1 + (Options.PwmDepth * mean);
            }

            var codes = Camera.ExpectedCodes(light * modulation, exposure);
            var band = (int)((long)r * bands / rows);
            result[band] += (0.299 * codes.X) + (0.587 * codes.Y) + (0.114 * codes.Z);
            counts[band]++;
        }

        for (var b = 0; b < bands; b++)
        {
            result[b] /= Math.Max(1, counts[b]);
        }

        return result;
    }

    /// <summary>Raises <see cref="Faulted"/>, for fault-injection tests.</summary>
    public void InjectFault(InstrumentFault fault) => Faulted?.Invoke(this, fault);

    public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The whole screen's average drive level (0–1): the patch over its share of the screen (a square of 60 %
    /// of the height on 16:9, unless its layout fills the screen) and the surround over the rest.</summary>
    private static double AverageLevel(PatchSpec? patch)
    {
        static double Level(DeviceRgb c) => (c.R + c.G + c.B) / (3 * 255.0);
        var patchLevel = patch switch
        {
            SolidPatch s => Level(s.Color),
            HalftonePatch h => (Level(h.On) * h.OnFraction) + (Level(h.Off) * (1 - h.OnFraction)),
            _ => 0,
        };
        var layout = patch?.Layout;
        var share = layout is null ? 0.6 * 0.6 * 9 / 16 : Math.Min(1, layout.Size * layout.Size * 9 / 16);
        var surround = layout is null ? 0 : Level(layout.Surround);
        return (patchLevel * share) + (surround * (1 - share));
    }

    private static PatchSpec? VisiblePatch(List<(double Time, PatchSpec Patch)> timeline, double time)
    {
        PatchSpec? visible = null;
        foreach (var (shownAt, patch) in timeline)
        {
            if (shownAt <= time + 1e-9)
            {
                visible = patch;
            }
            else
            {
                break;
            }
        }

        return visible;
    }

    private Task<PresentedPatch> ShowOnReference(PatchSpec patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var time = _frameIndex / Options.FrameRate;
            _referenceTimeline.Add((time, patch));
            return Task.FromResult(new PresentedPatch(patch, ++_patchSequence, time));
        }
    }

    private sealed class ReferenceScreen(SimulatedRig rig) : IPatchPresenter
    {
        public Task<PresentedPatch> ShowAsync(PatchSpec patch, CancellationToken cancellationToken) => rig.ShowOnReference(patch, cancellationToken);
    }
}
