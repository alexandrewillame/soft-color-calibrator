using SoftCal.Core.Color;
using SoftCal.Core.Instruments;

namespace SoftCal.Core.Measurement;

/// <summary>Which frames a reading accepts and how many it averages.</summary>
public sealed record ReadPolicy
{
    public static ReadPolicy Default { get; } = new();

    /// <summary>Accepted frames averaged into one reading.</summary>
    public int Frames { get; init; } = 15;

    /// <summary>Extra settling time after the patch's display time + latency + exposure, for the panel's response.</summary>
    public double SettleMarginSeconds { get; init; } = 0.10;

    /// <summary>Frames of a new exposure epoch that are skipped (the camera may still use the old exposure).</summary>
    public int SkipFramesAfterExposureChange { get; init; } = 3;

    /// <summary>Frames with more clipped ROI pixels than this (in any channel) are rejected.</summary>
    public double MaxClippedFraction { get; init; } = 0.0005;

    /// <summary>A frame whose row bands spread more than this (relative to their mean) is rejected; 0 disables the check.</summary>
    public double MaxBandSpread { get; init; }

    /// <summary>Give up when no reading completes within this many seconds of instrument time.</summary>
    public double TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// With <see cref="TargetIntegrationSeconds"/>: a reading may stop after this many frames once their exposure adds
    /// up to the target. Long exposures average little noise per frame anyway, and they are slow (4 fps at ¼ s).
    /// </summary>
    public int MinFrames { get; init; } = 3;

    /// <summary>Total exposure after which a reading may stop early (0 = always average <see cref="Frames"/>).</summary>
    public double TargetIntegrationSeconds { get; init; }

    /// <summary>Frames this policy needs at a given exposure.</summary>
    public int FramesFor(double exposureSeconds) =>
        TargetIntegrationSeconds > 0 && exposureSeconds > 0
            ? Math.Clamp((int)Math.Ceiling(TargetIntegrationSeconds / exposureSeconds), Math.Min(MinFrames, Frames), Frames)
            : Frames;
}

/// <summary>One averaged reading of a patch, in instrument units.</summary>
public sealed record Reading(
    PatchSpec Patch,
    long PatchSequence,
    double DisplayTimeSeconds,
    int ExposureEpoch,
    double ExposureSeconds,
    Vec3 Mean,
    Vec3 StdError,
    Vec3 FrameStdDev,
    int Frames,
    int Rejected,
    double FirstFrameSeconds,
    double LastFrameSeconds,
    double MaxClippedFraction)
{
    /// <summary>Channel 0–2, or 3 for luma (BT.601 weights of the instrument's R′G′B′).</summary>
    public double Channel(int channel) => channel == 3 ? (0.299 * Mean.X) + (0.587 * Mean.Y) + (0.114 * Mean.Z) : Mean[channel];

    public double ChannelError(int channel) => channel == 3
        ? Math.Sqrt(Math.Pow(0.299 * StdError.X, 2) + Math.Pow(0.587 * StdError.Y, 2) + Math.Pow(0.114 * StdError.Z, 2))
        : StdError[channel];
}

/// <summary>Thrown when a reading cannot be completed (timeout, clipping, a stalled stream).</summary>
public sealed class MeasurementException : Exception
{
    public MeasurementException()
    {
    }

    public MeasurementException(string message)
        : base(message)
    {
    }

    public MeasurementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Callbacks that let a recorder see everything the rig does.</summary>
public interface IRigObserver
{
    void OnPatchShown(PresentedPatch shown);

    void OnFrame(FrameStats frame);

    void OnExposure(ExposureState state);
}

/// <summary>
/// The instrument-agnostic core of every measurement: show a patch, wait until it has settled on screen and in the
/// camera (display time + optical latency + exposure), then average accepted frames. Frames are pulled from one
/// long-lived stream, so a simulated instrument advances its virtual clock only while a reading needs frames. When the
/// presenter watches its patch (<see cref="IPatchWatch"/>), a reading taken while something covered the patch is taken
/// again once the patch is clear.
/// </summary>
public sealed class MeasurementRig : IAsyncDisposable
{
    /// <summary>Margin around a reading's frames when asking the presenter what reached the screen meanwhile.</summary>
    private const double CoverMarginSeconds = 0.05;

    /// <summary>How long the patch must be clear again before a disturbed reading is taken again.</summary>
    private const double ClearSeconds = 0.5;

    /// <summary>How long the rig waits for the patch to be clear before giving up.</summary>
    private const double ClearTimeoutSeconds = 60;

    /// <summary>Disturbed readings of one patch taken again before giving up.</summary>
    private const int MaxRetakes = 5;

    private readonly IAsyncEnumerator<FrameStats> _frames;
    private readonly IRigObserver? _observer;
    private IPatchPresenter _presenter;
    private int _epochFramesSeen;
    private int _lastEpoch = int.MinValue;
    private List<FrameStats>? _capture;

    /// <param name="streamCancellation">Ends the frame stream: the run's token, so a reading waiting for a frame that
    /// never comes (a stalled camera) ends with the run instead of waiting for ever.</param>
    public MeasurementRig(IMeasurementInstrument instrument, IPatchPresenter presenter, RoiSpec roi, IRigObserver? observer = null, CancellationToken streamCancellation = default)
    {
        Instrument = instrument ?? throw new ArgumentNullException(nameof(instrument));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        Roi = roi ?? throw new ArgumentNullException(nameof(roi));
        _observer = observer;
        _frames = instrument.StreamAsync(roi, streamCancellation).GetAsyncEnumerator(streamCancellation);
    }

    public IMeasurementInstrument Instrument { get; }

    /// <summary>Where patches are shown. A white match switches it to the reference display and back while the camera
    /// keeps streaming.</summary>
    public IPatchPresenter Presenter
    {
        get => _presenter;
        set => _presenter = value ?? throw new ArgumentNullException(nameof(value));
    }

    public RoiSpec Roi { get; }

    /// <summary>Display-to-frame-timestamp delay. Conservative until the optical sync measures it.</summary>
    public double LatencySeconds { get; set; } = 0.25;

    /// <summary>The current exposure, after the last <see cref="SetExposureAsync"/>.</summary>
    public ExposureState? Exposure { get; private set; }

    /// <summary>The most recent frame pulled from the stream.</summary>
    public FrameStats? LastFrame { get; private set; }

    public PresentedPatch? LastShown { get; private set; }

    /// <summary>Raised (on the measuring thread) when something reached the screen over the patch during a reading; the
    /// text says what. The reading is then taken again.</summary>
    public event EventHandler<string>? PatchDisturbed;

    public double ExposureSeconds => Exposure?.ActualSeconds ?? Exposure?.NominalSeconds ?? 0;

    public async Task<PresentedPatch> ShowAsync(PatchSpec patch, CancellationToken cancellationToken)
    {
        var shown = await Presenter.ShowAsync(patch, cancellationToken).ConfigureAwait(false);
        LastShown = shown;
        _observer?.OnPatchShown(shown);
        return shown;
    }

    public async Task<ExposureState> SetExposureAsync(double seconds, CancellationToken cancellationToken)
    {
        var state = await Instrument.SetExposureAsync(new ExposureRequest(seconds), cancellationToken).ConfigureAwait(false);
        Exposure = state;
        _observer?.OnExposure(state);
        return state;
    }

    /// <summary>The nominal exposure steps the instrument supports, ascending.</summary>
    public IReadOnlyList<double> ExposureSteps => Instrument.Capabilities.ExposureSteps;

    /// <summary>Pulls the next frame from the stream (and reports it to the observer).</summary>
    public async Task<FrameStats> NextFrameAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _frames.MoveNextAsync().ConfigureAwait(false))
        {
            throw new MeasurementException("The instrument stream ended.");
        }

        var frame = _frames.Current;
        if (frame.ExposureEpoch != _lastEpoch)
        {
            _lastEpoch = frame.ExposureEpoch;
            _epochFramesSeen = 0;
        }

        _epochFramesSeen++;
        LastFrame = frame;
        _capture?.Add(frame);
        _observer?.OnFrame(frame);
        return frame;
    }

    /// <summary>Starts keeping every frame pulled from now on (for sequences such as the optical sync).</summary>
    public void BeginCapture() => _capture = [];

    /// <summary>Stops keeping frames and returns those pulled since <see cref="BeginCapture"/>.</summary>
    public IReadOnlyList<FrameStats> EndCapture()
    {
        var frames = _capture ?? [];
        _capture = null;
        return frames;
    }

    /// <summary>Shows a patch and reads it.</summary>
    public async Task<Reading> ReadAsync(PatchSpec patch, ReadPolicy? policy, CancellationToken cancellationToken)
    {
        var shown = await ShowAsync(patch, cancellationToken).ConfigureAwait(false);
        return await ReadShownAsync(shown, policy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The clipped fraction that matters: the worst of the channels that carry signal. A channel sitting near black
    /// (the empty channels of a primary such as pure green) always has pixels at the camera's black clamp, which says
    /// nothing about over-exposure; on the C920e that alone flagged 58 % of a green patch's pixels.
    /// </summary>
    private double RelevantClipping(FrameStats frame)
    {
        var floor = Instrument.Capabilities.FullScale / 8;
        var clipped = 0.0;
        foreach (var channel in new[] { frame.Red, frame.Green, frame.Blue })
        {
            if (channel.Mean >= floor)
            {
                clipped = Math.Max(clipped, channel.ClippedFraction);
            }
        }

        return clipped;
    }

    /// <summary>Reads a patch that is already on screen. A reading the presenter saw disturbed (a window, a
    /// notification or the pointer over the patch, or the patch gone from the screen) is taken again once the patch is
    /// clear.</summary>
    public async Task<Reading> ReadShownAsync(PresentedPatch shown, ReadPolicy? policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shown);
        policy ??= ReadPolicy.Default;
        var notBefore = double.NegativeInfinity;
        for (var retakes = 0; ; retakes++)
        {
            var reading = await ReadOnceAsync(shown, policy, notBefore, cancellationToken).ConfigureAwait(false);
            if (Presenter is not IPatchWatch watch)
            {
                return reading;
            }

            // The screen as the frames saw it: from before the first frame's exposure (through the latency) to the last
            // frame's time, which is never earlier than what it shows.
            var from = reading.FirstFrameSeconds - LatencySeconds - reading.ExposureSeconds - CoverMarginSeconds;
            var until = reading.LastFrameSeconds + CoverMarginSeconds;
            if (await watch.DisturbanceAsync(from, until, cancellationToken).ConfigureAwait(false) is not { } what)
            {
                return reading;
            }

            if (retakes >= MaxRetakes)
            {
                throw new MeasurementException($"The reading of {Describe(shown.Patch)} was disturbed {retakes + 1} times in a row ({what}). Keep other windows and the pointer off the measured screen, then start again.");
            }

            PatchDisturbed?.Invoke(this, what);
            notBefore = await watch.WaitUntilClearAsync(ClearSeconds, ClearTimeoutSeconds, cancellationToken).ConfigureAwait(false)
                ?? throw new MeasurementException($"Something kept covering the measured patch for a minute ({what}). Close it or move it off that screen, then start again.");
        }
    }

    /// <summary>One reading of a patch on screen, from frames that saw it settled: after its display time, or after
    /// <paramref name="notBefore"/> when the patch was disturbed and is clear again from then.</summary>
    private async Task<Reading> ReadOnceAsync(PresentedPatch shown, ReadPolicy policy, double notBefore, CancellationToken cancellationToken)
    {
        var settledAt = Math.Max(shown.PresentTimeSeconds, notBefore) + LatencySeconds + ExposureSeconds + policy.SettleMarginSeconds;
        var epoch = Exposure?.Epoch;
        var needed = policy.FramesFor(ExposureSeconds);
        var accepted = new List<FrameStats>(needed);
        var rejected = 0;
        double? start = null;
        var maxClipped = 0.0;
        while (accepted.Count < needed)
        {
            var frame = await NextFrameAsync(cancellationToken).ConfigureAwait(false);
            start ??= frame.TimestampSeconds;
            if (frame.TimestampSeconds - Math.Max(start.Value, settledAt) > policy.TimeoutSeconds)
            {
                throw new MeasurementException(
                    $"No reading of {Describe(shown.Patch)} after {policy.TimeoutSeconds:F0} s ({accepted.Count} frames accepted, {rejected} rejected).");
            }

            if (frame.TimestampSeconds < settledAt)
            {
                continue;
            }

            if (epoch is not null && (frame.ExposureEpoch != epoch || _epochFramesSeen <= policy.SkipFramesAfterExposureChange))
            {
                continue;
            }

            var clipped = RelevantClipping(frame);
            if (clipped > policy.MaxClippedFraction || (policy.MaxBandSpread > 0 && BandSpread(frame) > policy.MaxBandSpread))
            {
                rejected++;
                if (rejected > policy.Frames * 4 && accepted.Count == 0)
                {
                    throw new MeasurementException(
                        $"Every frame of {Describe(shown.Patch)} is rejected (clipped fraction {clipped:P2}); the exposure is probably too long.");
                }

                continue;
            }

            maxClipped = Math.Max(maxClipped, clipped);
            accepted.Add(frame);
        }

        return Summarize(shown, accepted, rejected, maxClipped);
    }

    /// <summary>Collects every frame for a duration after the current time (for flicker and sync analysis).</summary>
    public async Task<IReadOnlyList<FrameStats>> CollectAsync(double seconds, CancellationToken cancellationToken)
    {
        var frames = new List<FrameStats>();
        var first = await NextFrameAsync(cancellationToken).ConfigureAwait(false);
        frames.Add(first);
        while (frames[^1].TimestampSeconds - first.TimestampSeconds < seconds)
        {
            frames.Add(await NextFrameAsync(cancellationToken).ConfigureAwait(false));
        }

        return frames;
    }

    /// <summary>Waits until the stream reaches the given instrument time.</summary>
    public async Task WaitUntilAsync(double timeSeconds, CancellationToken cancellationToken)
    {
        while ((LastFrame?.TimestampSeconds ?? double.NegativeInfinity) < timeSeconds)
        {
            await NextFrameAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Own auto-exposure: picks the exposure step that puts the patch's brightest channel nearest
    /// <paramref name="target"/> without clipping. Steps are ×2 (log2 seconds); the camera's curve is unknown, so each
    /// step is checked by a short reading rather than predicted.
    /// </summary>
    public async Task<ExposureState> AutoExposeAsync(PatchSpec patch, double target, CancellationToken cancellationToken, double maxClippedFraction = 0.0005)
    {
        var steps = ExposureSteps;
        if (steps.Count == 0)
        {
            throw new MeasurementException("The instrument reports no exposure steps.");
        }

        var index = Exposure is null ? steps.Count / 2 : Nearest(steps, Exposure.NominalSeconds);
        var quick = ReadPolicy.Default with { Frames = 4, MaxClippedFraction = 1.0 };
        await SetExposureAsync(steps[index], cancellationToken).ConfigureAwait(false);
        var shown = await ShowAsync(patch, cancellationToken).ConfigureAwait(false);
        var tried = new Dictionary<int, double>();
        for (var attempt = 0; attempt < steps.Count + 2; attempt++)
        {
            var reading = await ReadShownAsync(shown, quick, cancellationToken).ConfigureAwait(false);
            var level = Math.Max(reading.Mean.X, Math.Max(reading.Mean.Y, reading.Mean.Z));

            // Clipped fractions count both ends; pixels at the black clamp (a primary's empty channels, an unblurred
            // halftone's dark pixels) are no reason to shorten the exposure, so only bright readings count as clipping.
            // "Bright" starts at half the target, not at the target: a saturated primary (a red line pattern on the
            // C920e) clips ~17 % of its pixels while its mean is still on target, and every fine reading would then be
            // rejected.
            var clipped = level > 245 || (reading.MaxClippedFraction > maxClippedFraction && level > target / 2);
            tried[index] = clipped ? double.PositiveInfinity : level;

            // One ×2 step moves an encoded (≈ γ 1/2.2) reading by about ×1.37, a linear one (raw data) by ×2: step
            // while that lands closer to the target and safely below clipping; each step is then verified by a reading.
            var stepFactor = Instrument.Capabilities.OutputIsLinear ? 2.0 : 1.37;
            int next;
            if (clipped)
            {
                next = index - 1;
            }
            else if (level * stepFactor < 215 && Math.Abs((level * stepFactor) - target) < Math.Abs(level - target))
            {
                next = index + 1;
            }
            else if (Math.Abs((level / stepFactor) - target) < Math.Abs(level - target) && level > target)
            {
                next = index - 1;
            }
            else
            {
                break;
            }

            if (next < 0 || next >= steps.Count || tried.ContainsKey(next))
            {
                break;
            }

            index = next;
            await SetExposureAsync(steps[index], cancellationToken).ConfigureAwait(false);
        }

        // Prefer the unclipped step closest to the target.
        var best = tried.Where(t => !double.IsPositiveInfinity(t.Value)).OrderBy(t => Math.Abs(t.Value - target)).Select(t => (int?)t.Key).FirstOrDefault();
        if (best is null)
        {
            throw new MeasurementException($"{Describe(patch)} clips even at the shortest exposure.");
        }

        if (Exposure is null || Nearest(steps, Exposure.NominalSeconds) != best.Value)
        {
            await SetExposureAsync(steps[best.Value], cancellationToken).ConfigureAwait(false);
        }

        return Exposure!;
    }

    public ValueTask DisposeAsync() => _frames.DisposeAsync();

    public static string Describe(PatchSpec patch) => patch switch
    {
        SolidPatch s => $"solid {s.Color.R}/{s.Color.G}/{s.Color.B}",
        HalftonePatch h => $"{h.Geometry} {h.On.R}/{h.On.G}/{h.On.B} over {h.Off.R}/{h.Off.G}/{h.Off.B} ({h.OnSixteenths}/16)",
        PlacementTargetPatch => "placement target",
        _ => patch.GetType().Name,
    };

    private static int Nearest(IReadOnlyList<double> steps, double seconds)
    {
        var best = 0;
        for (var i = 1; i < steps.Count; i++)
        {
            if (Math.Abs(Math.Log(steps[i] / seconds)) < Math.Abs(Math.Log(steps[best] / seconds)))
            {
                best = i;
            }
        }

        return best;
    }

    private static double BandSpread(FrameStats frame)
    {
        if (frame.RowBandMeans is not { Count: > 1 } bands)
        {
            return 0;
        }

        var mean = bands.Average();
        return mean <= 0 ? 0 : (bands.Max() - bands.Min()) / mean;
    }

    private Reading Summarize(PresentedPatch shown, List<FrameStats> frames, int rejected, double maxClipped)
    {
        var n = frames.Count;
        Vec3 Get(FrameStats f) => new(f.Red.Mean, f.Green.Mean, f.Blue.Mean);
        var mean = frames.Aggregate(Vec3.Zero, (s, f) => s + Get(f)) / n;
        var variance = frames.Aggregate(Vec3.Zero, (s, f) =>
        {
            var d = Get(f) - mean;
            return s + d.Scale(d);
        }) / Math.Max(1, n - 1);
        var sd = variance.Map(Math.Sqrt);
        return new Reading(
            shown.Patch,
            shown.Sequence,
            shown.PresentTimeSeconds,
            frames[0].ExposureEpoch,
            ExposureSeconds,
            mean,
            sd / Math.Sqrt(n),
            sd,
            n,
            rejected,
            frames[0].TimestampSeconds,
            frames[^1].TimestampSeconds,
            maxClipped);
    }
}
