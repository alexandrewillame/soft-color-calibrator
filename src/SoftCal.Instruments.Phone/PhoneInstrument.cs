using System.Globalization;
using System.Runtime.CompilerServices;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.Remote;

namespace SoftCal.Instruments.Phone;

/// <summary>
/// A phone running SoftCal Camera, as an instrument: the link's commands behind <see cref="IMeasurementInstrument"/>.
/// Frame times come mapped from the phone's clock to the PC's (QPC seconds, the clock the patch window's display times
/// are on), so the rig judges which frames saw a patch settled exactly as with a USB camera; the network's delay does not
/// matter. Exposures are offered as ×2 steps, as a webcam's, though the phone can take any.
/// <para>
/// The link belongs to whoever connected it (the window keeps it between steps); disposing the instrument closes the
/// link only when it was handed over with <c>ownsClient</c>.
/// </para>
/// </summary>
public sealed class PhoneInstrument : IMeasurementInstrument, ILumaImageSource, IFocusControl
{
    /// <summary>Focus distances tried to find the one that blurs the screen (diopters, farthest first). Not exactly 0:
    /// some phones seem to take 0 as "not set".</summary>
    private static readonly double[] FocusSteps = [0.05, 0.5, 1, 2, 3.5, 5, 7.5, 10, 15, 20];

    /// <summary>The longest exposure offered: longer ones only slow every reading (a webcam's longest is the same).</summary>
    public const double LongestExposureSeconds = 0.25;

    /// <summary>The widest picture asked for: the phone sends its whole frame below this.</summary>
    private const int PictureMaxWidth = 1920;

    private readonly PhoneClient _client;
    private readonly bool _ownsClient;
    private readonly CancellationTokenSource _lost = new();
    private string? _lostReason;
    private bool _prepared;

    public PhoneInstrument(PhoneClient client, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
        var phone = client.Capabilities;
        var steps = Steps(phone.MinExposure, phone.MaxExposure);
        Capabilities = new InstrumentCapabilities
        {
            ExposureModel = ExposureModel.DiscreteSteps,
            ExposureSteps = steps,
            MinExposureSeconds = steps.Count > 0 ? steps[0] : phone.MinExposure,
            MaxExposureSeconds = steps.Count > 0 ? steps[^1] : Math.Min(phone.MaxExposure, LongestExposureSeconds),
            ReportsActualExposure = true,

            // From the sensor's raw data the statistics are proportional to the light; otherwise they went through the
            // phone's tone curve.
            OutputIsLinear = phone.Raw,
            FullScale = 255,
            LockableControls = phone.ManualSensor
                ? [CameraControl.WhiteBalance, CameraControl.Exposure, CameraControl.Gain, CameraControl.Focus, CameraControl.Sharpness]
                : [CameraControl.WhiteBalance],
            RowLineTimeSeconds = phone.RowTime,
        };
        Info = new InstrumentInfo(
            string.Create(CultureInfo.InvariantCulture, $"phone:{client.Phone.Manufacturer}:{client.Phone.Model}"),
            InstrumentKind.RemotePhone,
            Name(client.Phone),
            client.Phone.App);
        client.Faulted += OnFaulted;
        client.Disconnected += OnDisconnected;
    }

    public event EventHandler<InstrumentFault>? Faulted;

    public InstrumentInfo Info { get; }

    /// <summary>What the phone can do; a lock settles whether its readings are linear (raw data).</summary>
    public InstrumentCapabilities Capabilities { get; private set; }

    public PhoneClient Client => _client;

    /// <summary>Why the phone cannot measure (no manual exposure), or null when it can.</summary>
    public string? CannotMeasure => CannotMeasureReason(_client.Capabilities);

    /// <summary>From the farthest focus to the phone's nearest (diopters).</summary>
    public IReadOnlyList<double> FocusCandidates
    {
        get
        {
            var nearest = _client.Capabilities.MinimumFocusDiopters is > 0 and var d ? d : 10;
            return [.. FocusSteps.Where(f => f < nearest - 0.25).Append(nearest)];
        }
    }

    public string DescribeFocus(double value) => value switch
    {
        <= 0.06 => "the farthest",
        < 1 => string.Create(CultureInfo.InvariantCulture, $"{value:0.##} D ({1 / value:0.#} m)"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value:0.##} D ({100 / value:0} cm)"),
    };

    public async Task<double> SetFocusAsync(double value, CancellationToken cancellationToken)
    {
        var set = await Call(() => _client.SetFocusAsync(value, cancellationToken)).ConfigureAwait(false);
        return _client.Clock.ToPc(set.PhoneTime);
    }

    /// <summary>The phone as the user knows it ("Google Pixel 7").</summary>
    public static string Name(PhoneDescription phone)
    {
        ArgumentNullException.ThrowIfNull(phone);
        return phone.Model.StartsWith(phone.Manufacturer, StringComparison.OrdinalIgnoreCase)
            ? phone.Model
            : $"{Capitalized(phone.Manufacturer)} {phone.Model}";
    }

    /// <summary>Why a phone with these capabilities cannot measure, or null when it can.</summary>
    public static string? CannotMeasureReason(PhoneCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!capabilities.ManualSensor)
        {
            return "This phone's camera does not let apps set its exposure (manual exposure), so it cannot measure. A webcam, or another phone, can.";
        }

        return Steps(capabilities.MinExposure, capabilities.MaxExposure).Count < 3
            ? "This phone's camera offers too narrow a range of exposures to measure with."
            : null;
    }

    public async Task<LockReport> PrepareAsync(LockRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CannotMeasure is { } reason)
        {
            throw new MeasurementException(reason);
        }

        // The focus is the phone's own choice: the farthest its lens goes, which blurs a screen a few centimetres away.
        var controls = await Call(() => _client.PrepareAsync(new LockSettings(request.WhiteBalanceKelvin, null), cancellationToken)).ConfigureAwait(false);
        Capabilities = Capabilities with { OutputIsLinear = _client.Capabilities.Raw };
        _prepared = true;
        return new LockReport([.. controls.Select(ToResult)]);
    }

    public async Task<ExposureState> SetExposureAsync(ExposureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var set = await Call(() => _client.SetExposureAsync(request.Seconds, cancellationToken)).ConfigureAwait(false);
        return new ExposureState(set.Nominal, set.Actual, set.Epoch);
    }

    public async IAsyncEnumerable<FrameStats> StreamAsync(RoiSpec roi, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roi);
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lost.Token);
        var frames = _client.StreamAsync(ToRegion(roi), stream.Token).GetAsyncEnumerator(stream.Token);
        try
        {
            while (true)
            {
                FrameMessage frame;
                try
                {
                    if (!await frames.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }

                    frame = frames.Current;
                }
                catch (PhoneLinkException ex)
                {
                    throw new MeasurementException(ex.Message, ex);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && _lostReason is { } reason)
                {
                    throw new MeasurementException(reason, ex);
                }

                yield return new FrameStats(
                    frame.Seq,
                    _client.Clock.ToPc(frame.Time),
                    frame.Epoch,
                    ToStats(frame.Red),
                    ToStats(frame.Green),
                    ToStats(frame.Blue),
                    frame.Pixels,
                    frame.Bands);
            }
        }
        finally
        {
            await frames.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The phone's camera goes back to its automatic settings (it does so by itself when the link drops).</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            return;
        }

        await Call(() => _client.RestoreAsync(cancellationToken)).ConfigureAwait(false);
        _prepared = false;
    }

    /// <summary>The phone streams with its automatic settings until a measurement locks them: nothing to start, except
    /// putting it back in automatic when this instrument locked it.</summary>
    public async Task StartPreviewAsync(CancellationToken cancellationToken)
    {
        if (_prepared)
        {
            await RestoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<RoiImage> CaptureLumaImageAsync(RoiSpec roi, int frames, int skipFrames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roi);
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        var picture = await Call(() => _client.CaptureImageAsync(ToRegion(roi), frames, Math.Max(0, skipFrames), PictureMaxWidth, cancellationToken)).ConfigureAwait(false);
        return new RoiImage(
            picture.Width,
            picture.Height,
            picture.Luma(),
            picture.Frames,
            _client.Clock.ToPc(picture.FirstTime),
            _client.Clock.ToPc(picture.LastTime));
    }

    public async ValueTask DisposeAsync()
    {
        _client.Faulted -= OnFaulted;
        _client.Disconnected -= OnDisconnected;
        if (_ownsClient)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _lost.Dispose();
    }

    /// <summary>The ×2 exposure steps within the phone's range, up to <see cref="LongestExposureSeconds"/>.</summary>
    internal static IReadOnlyList<double> Steps(double minSeconds, double maxSeconds)
    {
        if (!(minSeconds > 0) || !(maxSeconds >= minSeconds))
        {
            return [];
        }

        var first = (int)Math.Ceiling(Math.Log2(minSeconds) - 1e-9);
        var last = (int)Math.Floor(Math.Log2(Math.Min(maxSeconds, LongestExposureSeconds)) + 1e-9);
        return [.. Enumerable.Range(first, Math.Max(0, last - first + 1)).Select(k => Math.Pow(2, k))];
    }

    internal static RegionSpec ToRegion(RoiSpec roi) => new(roi.CenterX, roi.CenterY, roi.Width, roi.Height, roi.RowBands);

    private static ChannelStats ToStats(ChannelSummary c) => new(c.Mean, c.StdDev, c.Clipped);

    private static ControlLockResult ToResult(ControlState state) => new(
        Enum.TryParse<CameraControl>(state.Control, ignoreCase: true, out var control) ? control : CameraControl.StudioEffects,
        Enum.TryParse<LockOutcome>(state.Outcome, ignoreCase: true, out var outcome) ? outcome : LockOutcome.Failed,
        state.Requested,
        state.Actual,
        state.Note);

    private static string Capitalized(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A link failure, as the measurement's own kind of failure (the window says it and stops the run).</summary>
    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (PhoneLinkException ex)
        {
            throw new MeasurementException(ex.Message, ex);
        }
    }

    private static async Task Call(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (PhoneLinkException ex)
        {
            throw new MeasurementException(ex.Message, ex);
        }
    }

    private void OnFaulted(object? sender, FaultMessage fault)
    {
        var kind = Enum.TryParse<InstrumentFaultKind>(fault.Kind, ignoreCase: true, out var k) ? k : InstrumentFaultKind.Other;
        if (kind == InstrumentFaultKind.Disconnected)
        {
            // The phone lost its camera: a stream waiting for frames stops now, not after the stall timeout.
            Lose("The phone's camera stopped: " + fault.Message);
        }

        Faulted?.Invoke(this, new InstrumentFault(kind, fault.Message));
    }

    private void OnDisconnected(object? sender, string reason)
    {
        Lose("The link to the phone closed: " + reason);
        Faulted?.Invoke(this, new InstrumentFault(InstrumentFaultKind.Disconnected, reason));
    }

    private void Lose(string reason)
    {
        Interlocked.CompareExchange(ref _lostReason, reason, null);
        try
        {
            _lost.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile: nothing is streaming any more.
        }
    }
}
