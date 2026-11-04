using System.Runtime.CompilerServices;
using SoftCal.Core.Instruments;

namespace SoftCal.Instruments.Simulated;

/// <summary>What the network between the PC and a phone does to the frame stream.</summary>
public sealed record LoopbackOptions
{
    /// <summary>The phone's clock minus the PC's (seconds). Optical sync absorbs it into the rig's latency.</summary>
    public double ClockOffsetSeconds { get; init; } = 3.25;

    /// <summary>Timestamp noise from the phone's capture pipeline (uniform ±, seconds).</summary>
    public double TimestampJitterSeconds { get; init; } = 0.002;

    /// <summary>Fraction of frames lost on the network.</summary>
    public double DropRate { get; init; } = 0.05;

    public int Seed { get; init; } = 99;
}

/// <summary>
/// An in-process stand-in for the future phone link: it forwards any instrument through what a LAN connection adds —
/// a different clock, timestamp jitter and lost frames — so the measurement code is proven against a remote instrument
/// before the transport exists (M6 replaces this with the WebSocket protocol).
/// </summary>
public sealed class LoopbackRemoteInstrument : IMeasurementInstrument
{
    private readonly IMeasurementInstrument _inner;
    private readonly LoopbackOptions _options;
    private readonly Random _random;

    public LoopbackRemoteInstrument(IMeasurementInstrument inner, LoopbackOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? new LoopbackOptions();
        _random = new Random(_options.Seed);
        _inner.Faulted += (_, fault) => Faulted?.Invoke(this, fault);
    }

    public event EventHandler<InstrumentFault>? Faulted;

    public InstrumentInfo Info => _inner.Info with { Id = "loopback:" + _inner.Info.Id, Kind = InstrumentKind.RemotePhone };

    public InstrumentCapabilities Capabilities => _inner.Capabilities;

    public LoopbackOptions Options => _options;

    public Task<LockReport> PrepareAsync(LockRequest request, CancellationToken cancellationToken) => _inner.PrepareAsync(request, cancellationToken);

    public Task<ExposureState> SetExposureAsync(ExposureRequest request, CancellationToken cancellationToken) => _inner.SetExposureAsync(request, cancellationToken);

    public async IAsyncEnumerable<FrameStats> StreamAsync(RoiSpec roi, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in _inner.StreamAsync(roi, cancellationToken).ConfigureAwait(false))
        {
            if (_random.NextDouble() < _options.DropRate)
            {
                continue;
            }

            var jitter = ((_random.NextDouble() * 2) - 1) * _options.TimestampJitterSeconds;
            yield return frame with { TimestampSeconds = frame.TimestampSeconds + _options.ClockOffsetSeconds + jitter };
        }
    }

    public Task RestoreAsync(CancellationToken cancellationToken) => _inner.RestoreAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
