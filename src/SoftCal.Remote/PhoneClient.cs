using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SoftCal.Remote;

/// <summary>A failure of the phone link: the phone refused, answered with an error, or went away.</summary>
public sealed class PhoneLinkException : Exception
{
    public PhoneLinkException()
    {
    }

    public PhoneLinkException(string message)
        : base(message)
    {
    }

    public PhoneLinkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The PC's side of the link: connects to the phone, proves it knows the code the phone shows (and checks the phone's
/// proof), then sends commands and waits for their replies by id. Frames of a running stream come through
/// <see cref="StreamAsync"/>; pings keep the phone's clock mapped to the PC's (<see cref="Clock"/>).
/// </summary>
public sealed class PhoneClient : IAsyncDisposable
{
    /// <summary>A command not answered in this time has failed (the longest is a picture of many frames).</summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);

    /// <summary>A ping waits behind a command the phone is carrying out (locking its camera takes a second or two).</summary>
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(8);

    private readonly LinkChannel _link;
    private readonly Func<double> _clock;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<LinkMessage>> _pending = new();
    private readonly CancellationTokenSource _life = new();
    private Channel<FrameMessage>? _frames;
    private ImageMessage? _awaitingPixels;
    private Task _receiving = Task.CompletedTask;
    private Task _pinging = Task.CompletedTask;
    private long _nextId;
    private int _closed;
    private int _disposed;

    private PhoneClient(LinkChannel link, PhoneDescription phone, PhoneCapabilities capabilities, Func<double> clock)
    {
        _link = link;
        Phone = phone;
        Capabilities = capabilities;
        _clock = clock;
    }

    /// <summary>Something went wrong on the phone outside a command.</summary>
    public event EventHandler<FaultMessage>? Faulted;

    /// <summary>The link closed; the text says why.</summary>
    public event EventHandler<string>? Disconnected;

    public PhoneDescription Phone { get; }

    /// <summary>What the phone's camera can do, as it said when the link opened; a lock updates <c>Raw</c>.</summary>
    public PhoneCapabilities Capabilities { get; private set; }

    /// <summary>The phone's clock against the PC's (see <see cref="ClockSync"/>).</summary>
    public ClockSync Clock { get; } = new();

    public bool IsConnected => Volatile.Read(ref _closed) == 0;

    /// <summary>The PC's clock: QPC seconds, the clock the patch window's display times are on.</summary>
    public static double QpcSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Connects to the phone at <paramref name="address"/> (ws://host:port/softcal) and pairs with the code
    /// the phone shows. Throws <see cref="PhoneLinkException"/> with a message for the user when it cannot.</summary>
    /// <param name="clock">The PC's clock; QPC seconds by default.</param>
    public static async Task<PhoneClient> ConnectAsync(Uri address, string code, string clientName, Func<double>? clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(code);
        var socket = new ClientWebSocket();

        // A phone that vanishes (Wi-Fi lost, battery flat) is noticed within seconds, not when TCP gives up.
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connecting.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await socket.ConnectAsync(address, connecting.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            socket.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw new PhoneLinkException($"The phone at {address.Host} did not answer. Check that SoftCal Camera is open on it, and that the phone and this PC are on the same network.", ex);
        }

        var link = new LinkChannel(socket);
        PhoneClient client;
        try
        {
            var pcNonce = Pairing.NewNonce();
            await link.SendAsync(new HelloMessage(PhoneLink.Protocol, clientName, pcNonce), connecting.Token).ConfigureAwait(false);
            var challenge = await ExpectAsync<ChallengeMessage>(link, connecting.Token).ConfigureAwait(false);
            if (challenge.Protocol != PhoneLink.Protocol)
            {
                throw new PhoneLinkException($"The phone speaks version {challenge.Protocol} of the link and SoftCal version {PhoneLink.Protocol}: update the older one.");
            }

            var key = Pairing.Key(code);
            await link.SendAsync(new AuthMessage(Pairing.PcProof(key, pcNonce, challenge.Nonce)), connecting.Token).ConfigureAwait(false);
            var welcome = await ExpectAsync<WelcomeMessage>(link, connecting.Token).ConfigureAwait(false);
            if (!Pairing.Same(Pairing.PhoneProof(key, pcNonce, challenge.Nonce), welcome.Mac))
            {
                throw new PhoneLinkException("The phone could not prove it knows the code: it is not the phone you meant.");
            }

            client = new PhoneClient(link, challenge.Phone, welcome.Capabilities, clock ?? QpcSeconds);
        }
        catch (Exception ex)
        {
            link.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            if (ex is PhoneLinkException)
            {
                throw;
            }

            throw new PhoneLinkException(
                ex is OperationCanceledException ? "The phone did not finish pairing in time." : "The phone did not finish pairing: " + ex.Message,
                ex);
        }

        try
        {
            client.Start();
            await client.SyncClockAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (Exception)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<ControlState>> PrepareAsync(LockSettings settings, CancellationToken cancellationToken)
    {
        var prepared = await RequestAsync<PreparedMessage>(id => new PrepareMessage(id, settings), cancellationToken).ConfigureAwait(false);
        Capabilities = Capabilities with { Raw = prepared.Raw };
        return prepared.Controls;
    }

    public Task<ExposureSetMessage> SetExposureAsync(double seconds, CancellationToken cancellationToken) =>
        RequestAsync<ExposureSetMessage>(id => new ExposureMessage(id, seconds), cancellationToken);

    public Task RestoreAsync(CancellationToken cancellationToken) =>
        RequestAsync<OkMessage>(id => new RestoreMessage(id), cancellationToken);

    /// <summary>Moves the locked camera's focus (diopters, 0 the farthest); the reply says when its frames show it.</summary>
    public Task<FocusSetMessage> SetFocusAsync(double diopters, CancellationToken cancellationToken) =>
        RequestAsync<FocusSetMessage>(id => new FocusMessage(id, diopters), cancellationToken);

    /// <summary>A picture of a region (8-bit luma), averaged over <paramref name="frames"/> frames after skipping
    /// <paramref name="skip"/>.</summary>
    public async Task<LumaPicture> CaptureImageAsync(RegionSpec region, int frames, int skip, int maxWidth, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync<PictureReceived>(id => new CaptureImageMessage(id, region, frames, skip, maxWidth), cancellationToken).ConfigureAwait(false);
        var h = reply.Header;
        if (h.Bits is not (8 or 16) || h.Width <= 0 || h.Height <= 0 || reply.Pixels.Length < h.Width * h.Height * (h.Bits / 8))
        {
            throw new PhoneLinkException("The phone sent a picture that does not match its size.");
        }

        return new LumaPicture(h.Width, h.Height, h.Bits, reply.Pixels, h.Frames, h.FirstTime, h.LastTime);
    }

    /// <summary>Starts the phone's stream for a region and returns its frames as they come, until cancelled (the stream
    /// is stopped then). Frames that pile up beyond a few seconds are dropped, oldest first. A stream that sends nothing
    /// for <paramref name="stallTimeout"/> (10 s by default) fails: the phone's camera stopped.</summary>
    public async IAsyncEnumerable<FrameMessage> StreamAsync(RegionSpec region, [EnumeratorCancellation] CancellationToken cancellationToken, TimeSpan? stallTimeout = null)
    {
        var stall = stallTimeout ?? TimeSpan.FromSeconds(10);
        var frames = Channel.CreateBounded<FrameMessage>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _frames = frames;
        try
        {
            await RequestAsync<OkMessage>(id => new StartStreamMessage(id, region), cancellationToken).ConfigureAwait(false);
            while (await WaitForFramesAsync(frames.Reader, stall, cancellationToken).ConfigureAwait(false))
            {
                while (frames.Reader.TryRead(out var frame))
                {
                    yield return frame;
                }
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _frames, null, frames);
            if (IsConnected)
            {
                try
                {
                    await RequestAsync<OkMessage>(id => new StopStreamMessage(id), CancellationToken.None).ConfigureAwait(false);
                }
                catch (PhoneLinkException)
                {
                    // The link went away meanwhile.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (IsConnected)
        {
            try
            {
                await _link.SendAsync(new ByeMessage("SoftCal closed the link."), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or WebSocketException or ObjectDisposedException)
            {
                // Already gone.
            }

            // A phone that vanished does not answer the close: it is not waited for long.
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _link.CloseAsync("bye", closing.Token).ConfigureAwait(false);
        }

        await _life.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_receiving, _pinging).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException)
        {
            // Stopping.
        }

        _link.Dispose();
        _life.Dispose();
    }

    /// <summary>Waits for frames; false when the stream ended. Throws when none came for <paramref name="stall"/>.</summary>
    private static async Task<bool> WaitForFramesAsync(ChannelReader<FrameMessage> reader, TimeSpan stall, CancellationToken cancellationToken)
    {
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        quiet.CancelAfter(stall);
        try
        {
            return await reader.WaitToReadAsync(quiet.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PhoneLinkException($"The phone sent no frame for {stall.TotalSeconds:0} s: its camera stopped (is SoftCal Camera still open, in front?).");
        }
    }

    private static async Task<T> ExpectAsync<T>(LinkChannel link, CancellationToken cancellationToken)
        where T : LinkMessage
    {
        var received = await link.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return received?.Message switch
        {
            T message => message,
            ErrorMessage error => throw new PhoneLinkException(error.Message),
            null => throw new PhoneLinkException("The phone closed the link during pairing."),
            var other => throw new PhoneLinkException($"The phone answered '{other.GetType().Name}' during pairing."),
        };
    }

    private void Start()
    {
        _receiving = Task.Run(ReceiveLoopAsync, CancellationToken.None);
        _pinging = Task.Run(PingLoopAsync, CancellationToken.None);
    }

    /// <summary>A few quick pings right after connecting, so frame times map well from the first reading.</summary>
    private async Task SyncClockAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 6; i++)
        {
            var sent = _clock();
            await RequestAsync<PongMessage>(id => new PingMessage(id, sent), cancellationToken).ConfigureAwait(false);
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A ping every two seconds keeps the clock mapping fresh; a phone that stops answering them is gone.</summary>
    private async Task PingLoopAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, _life.Token).ConfigureAwait(false);
                var sent = _clock();
                await RequestAsync<PongMessage>(id => new PingMessage(id, sent), _life.Token, PingTimeout).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (PhoneLinkException) when (IsConnected)
        {
            Close($"The phone stopped answering for {PingTimeout.TotalSeconds:0} s. Is SoftCal Camera still open on it, and the phone on the network?");
            await _life.CancelAsync().ConfigureAwait(false);
        }
        catch (PhoneLinkException)
        {
            // The link went away; the receive loop says why.
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var reason = "The phone closed the link.";
        try
        {
            while (await _link.ReceiveAsync(_life.Token).ConfigureAwait(false) is { } received)
            {
                if (received.Message is null)
                {
                    // A picture's pixels follow its header.
                    if (_awaitingPixels is { } header && received.Payload is { } pixels && _pending.TryRemove(header.Id, out var waiting))
                    {
                        waiting.TrySetResult(new PictureReceived(header, pixels));
                    }

                    _awaitingPixels = null;
                    continue;
                }

                switch (received.Message)
                {
                    case FrameMessage frame:
                        _frames?.Writer.TryWrite(frame);
                        break;
                    case ImageMessage header:
                        _awaitingPixels = header;
                        break;
                    case PongMessage pong:
                        // Timed here, on arrival, not when the waiting command gets round to it.
                        Clock.Add(pong.PcTime, pong.PhoneTime, _clock());
                        Complete(pong.Id, pong);
                        break;
                    case FaultMessage fault:
                        Faulted?.Invoke(this, fault);
                        break;
                    case ErrorMessage { Id: 0 } error:
                        Faulted?.Invoke(this, new FaultMessage("Other", error.Message));
                        break;
                    case ErrorMessage error:
                        Complete(error.Id, error);
                        break;
                    case PreparedMessage m:
                        Complete(m.Id, m);
                        break;
                    case ExposureSetMessage m:
                        Complete(m.Id, m);
                        break;
                    case OkMessage m:
                        Complete(m.Id, m);
                        break;
                    case FocusSetMessage m:
                        Complete(m.Id, m);
                        break;
                    case ByeMessage bye:
                        reason = bye.Reason ?? reason;
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "SoftCal closed the link.";
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or InvalidDataException or System.Text.Json.JsonException)
        {
            reason = "The link to the phone broke: " + ex.Message;
        }
        finally
        {
            Close(reason);
        }
    }

    private void Complete(long id, LinkMessage message)
    {
        if (_pending.TryRemove(id, out var waiting))
        {
            waiting.TrySetResult(message);
        }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        var error = new PhoneLinkException(reason);
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiting))
            {
                waiting.TrySetException(error);
            }
        }

        _frames?.Writer.TryComplete(error);
        Disconnected?.Invoke(this, reason);
    }

    private async Task<T> RequestAsync<T>(Func<long, LinkMessage> build, CancellationToken cancellationToken, TimeSpan? timeout = null)
        where T : LinkMessage
    {
        var wait = timeout ?? ReplyTimeout;
        if (!IsConnected)
        {
            throw new PhoneLinkException("The phone is not connected.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<LinkMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            try
            {
                await _link.SendAsync(build(id), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or WebSocketException or ObjectDisposedException)
            {
                throw new PhoneLinkException("The link to the phone broke: " + ex.Message, ex);
            }

            LinkMessage message;
            try
            {
                message = await reply.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new PhoneLinkException($"The phone did not answer within {wait.TotalSeconds:0} s.", ex);
            }

            return message switch
            {
                T expected => expected,
                ErrorMessage error => throw new PhoneLinkException(error.Message),
                _ => throw new PhoneLinkException($"The phone answered '{message.GetType().Name}' to '{typeof(T).Name}'."),
            };
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>A picture's header and pixels, as one reply (never sent: put together on arrival).</summary>
    private sealed record PictureReceived(ImageMessage Header, byte[] Pixels) : LinkMessage;
}
