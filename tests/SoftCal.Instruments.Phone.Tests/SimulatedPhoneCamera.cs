using SoftCal.Core.Instruments;
using SoftCal.Instruments.Simulated;
using SoftCal.Remote;

namespace SoftCal.Instruments.Phone.Tests;

/// <summary>
/// A phone's camera made of the simulated rig, as the link drives it: the rig's frames on a clock
/// <paramref name="clockOffset"/> seconds ahead of the rig's own (which plays the PC's), and pictures of the frame's
/// mean luma. Run the rig paced in real time: the link streams frames as they come, as a phone does.
/// </summary>
internal sealed class SimulatedPhoneCamera(SimulatedRig rig, double clockOffset, bool manualSensor = true) : IPhoneCamera
{
    private int _prepares;
    private int _restores;
    private int _streams;

    public event EventHandler<FaultMessage>? Faulted;

    public PhoneCapabilities Capabilities { get; private set; } = new(
        rig.Capabilities.MinExposureSeconds,
        rig.Capabilities.MaxExposureSeconds,
        50,
        3200,
        manualSensor,
        ManualPostProcessing: true,
        RealtimeTimestamps: true,
        FrameWidth: 480,
        FrameHeight: 640,
        FrameRate: rig.Options.FrameRate,
        RowTime: rig.Options.RowLineTimeSeconds,
        WhiteBalancePresets: ["Daylight"],
        MinimumFocusDiopters: 10,
        Raw: rig.Capabilities.OutputIsLinear);

    /// <summary>When set, the lock finds the raw output unusable (as a phone whose camera will not stream it beside the
    /// others): it measures from processed frames from then on.</summary>
    public bool LosesRawAtLock { get; set; }

    public double Now => rig.Now + clockOffset;

    public int Prepares => Volatile.Read(ref _prepares);

    public int Restores => Volatile.Read(ref _restores);

    /// <summary>Streams running now (each one pulls the rig's frames).</summary>
    public int Streams => Volatile.Read(ref _streams);

    /// <summary>When set, locking waits for it (a real phone waits for its lens, which can take seconds).</summary>
    public TaskCompletionSource? PrepareGate { get; set; }

    public async Task<IReadOnlyList<ControlState>> PrepareAsync(LockSettings settings, CancellationToken cancellationToken)
    {
        if (PrepareGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var report = await rig.PrepareAsync(new LockRequest { WhiteBalanceKelvin = settings.WhiteBalanceKelvin }, cancellationToken).ConfigureAwait(false);
        if (LosesRawAtLock)
        {
            Capabilities = Capabilities with { Raw = false };
        }

        Interlocked.Increment(ref _prepares);
        return [.. report.Controls.Select(c => new ControlState(c.Control.ToString(), c.Outcome.ToString(), c.Requested, c.Actual, c.Note))];
    }

    public async Task<ExposureOutcome> SetExposureAsync(double seconds, CancellationToken cancellationToken)
    {
        var state = await rig.SetExposureAsync(new ExposureRequest(seconds), cancellationToken).ConfigureAwait(false);
        return new ExposureOutcome(state.NominalSeconds, rig.Camera.ActualExposure(state.NominalSeconds), state.Epoch);
    }

    /// <summary>The focus asked for last (diopters).</summary>
    public double? Focus { get; private set; }

    public Task<FocusOutcome> SetFocusAsync(double diopters, CancellationToken cancellationToken)
    {
        Focus = diopters;
        return Task.FromResult(new FocusOutcome(diopters, false, Now));
    }

    public IDisposable StartStream(RegionSpec region, Action<FrameMessage> onFrame)
    {
        var stop = new CancellationTokenSource();
        var roi = new RoiSpec(region.CenterX, region.CenterY, region.Width, region.Height) { RowBands = region.RowBands };
        Interlocked.Increment(ref _streams);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var f in rig.StreamAsync(roi, stop.Token).ConfigureAwait(false))
                    {
                        onFrame(new FrameMessage(
                            f.Sequence,
                            f.TimestampSeconds + clockOffset,
                            f.ExposureEpoch,
                            null,
                            ToSummary(f.Red),
                            ToSummary(f.Green),
                            ToSummary(f.Blue),
                            f.PixelCount,
                            f.RowBandMeans));
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stopped.
                }
                finally
                {
                    Interlocked.Decrement(ref _streams);
                }
            },
            CancellationToken.None);
        return new Stopper(stop);
    }

    public async Task<LumaPicture> CaptureImageAsync(RegionSpec region, int frames, int skip, int maxWidth, CancellationToken cancellationToken)
    {
        const int Width = 48;
        const int Height = 64;
        var sums = new double[Width * Height];
        double first = 0, last = 0;
        for (var i = 0; i < skip + frames; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1 / rig.Options.FrameRate), cancellationToken).ConfigureAwait(false);
            var f = rig.CaptureFrame();
            if (i < skip)
            {
                continue;
            }

            var luma = (0.299 * f.Red.Mean) + (0.587 * f.Green.Mean) + (0.114 * f.Blue.Mean);
            Array.Fill(sums, sums[0] + luma);
            first = i == skip ? f.TimestampSeconds + clockOffset : first;
            last = f.TimestampSeconds + clockOffset;
        }

        var (bits, data) = Yuv420Statistics.Pack(sums, frames, 1);
        return new LumaPicture(Width, Height, bits, data, frames, first, last);
    }

    public Task RestoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _restores);
        return Task.CompletedTask;
    }

    public void RaiseFault(FaultMessage fault) => Faulted?.Invoke(this, fault);

    private static ChannelSummary ToSummary(ChannelStats c) => new(c.Mean, c.StdDev, c.ClippedFraction);

    private sealed class Stopper(CancellationTokenSource stop) : IDisposable
    {
        public void Dispose() => stop.Cancel();
    }
}
