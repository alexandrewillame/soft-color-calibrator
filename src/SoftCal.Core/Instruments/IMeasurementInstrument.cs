namespace SoftCal.Core.Instruments;

/// <summary>
/// A light-measuring instrument (USB webcam, phone on the LAN, simulator, colorimeter).
/// It is a timestamped stream of region-of-interest statistics plus control commands; settling, exposure control,
/// averaging and null matching all happen once, on the PC, in the reading service — so every instrument behaves the same.
/// </summary>
public interface IMeasurementInstrument : IAsyncDisposable
{
    InstrumentInfo Info { get; }

    InstrumentCapabilities Capabilities { get; }

    /// <summary>Locks every automatic feature (white balance, exposure, gain, focus, effects) and reports, per control,
    /// whether the lock was applied and verified by reading the value back.</summary>
    Task<LockReport> PrepareAsync(LockRequest request, CancellationToken cancellationToken);

    /// <summary>Requests an exposure. Discrete-step instruments snap to the nearest step. The returned epoch tags the
    /// frames captured after the request; frames may still use the old exposure for a few frames.</summary>
    Task<ExposureState> SetExposureAsync(ExposureRequest request, CancellationToken cancellationToken);

    /// <summary>Streams per-frame statistics for the region of interest until cancelled.</summary>
    IAsyncEnumerable<FrameStats> StreamAsync(RoiSpec roi, CancellationToken cancellationToken);

    /// <summary>Restores the instrument settings captured before <see cref="PrepareAsync"/>.</summary>
    Task RestoreAsync(CancellationToken cancellationToken);

    /// <summary>Raised when the instrument detects a problem (disconnect, a control changed by another app, …).</summary>
    event EventHandler<InstrumentFault>? Faulted;
}
