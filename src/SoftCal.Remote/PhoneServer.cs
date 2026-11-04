using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace SoftCal.Remote;

public enum PhoneServerState
{
    /// <summary>Listening, showing the code; no PC connected.</summary>
    Waiting,

    /// <summary>A PC proved it knows the code and drives the camera.</summary>
    Connected,
}

/// <summary>What the phone shows: its state, the code to type on the PC, the PC connected, and a line on what happened.</summary>
public sealed record PhoneServerStatus(PhoneServerState State, string Code, string? Peer, string? Message);

/// <summary>
/// The phone's side of the link: a small WebSocket server on the local network (no web server: a socket and the
/// runtime's WebSocket). A PC connects, proves it knows the code the phone shows, and drives the camera with commands;
/// the phone answers each and streams one statistics message per frame. One PC at a time; when it leaves, the camera
/// goes back to its automatic settings.
/// </summary>
public sealed class PhoneServer : IDisposable
{
    /// <summary>A connection that has not finished its handshake by then is dropped.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly IPhoneCamera _camera;
    private readonly PhoneDescription _phone;
    private readonly TcpListener _listener;
    private readonly object _gate = new();
    private string _code = Pairing.NewCode();
    private int _failures;
    private int _active;
    private long _framesSent;
    private long _picturesSent;

    /// <param name="port">The port to listen on; when it is taken, the system picks one (advertise <see cref="Port"/>).</param>
    public PhoneServer(IPhoneCamera camera, PhoneDescription phone, int port = PhoneLink.DefaultPort)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _phone = phone ?? throw new ArgumentNullException(nameof(phone));
        _listener = Listen(port);
        _camera.Faulted += OnCameraFaulted;
    }

    public event EventHandler<PhoneServerStatus>? StatusChanged;

    /// <summary>The port in use.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Frames' statistics sent so far (one message per camera frame while SoftCal measures).</summary>
    public long FramesSent => Interlocked.Read(ref _framesSent);

    /// <summary>Pictures sent so far (the camera view SoftCal shows while the phone is placed).</summary>
    public long PicturesSent => Interlocked.Read(ref _picturesSent);

    /// <summary>The code the phone shows; a new one after <see cref="Pairing.MaxFailures"/> wrong proofs.</summary>
    public string Code
    {
        get
        {
            lock (_gate)
            {
                return _code;
            }
        }
    }

    private LinkChannel? CurrentLink { get; set; }

    /// <summary>Accepts PCs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Report(PhoneServerState.Waiting, null, null);
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = Task.Run(() => ServeAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    /// <summary>Stops listening, and lets go of the camera: an app that starts a new server for it (the iOS app does,
    /// each time it comes back to the front) leaves nothing of the old one behind.</summary>
    public void Dispose()
    {
        _camera.Faulted -= OnCameraFaulted;
        _listener.Stop();
    }

    private static TcpListener Listen(int port)
    {
        foreach (var candidate in new[] { port, 0 })
        {
            var listener = new TcpListener(IPAddress.IPv6Any, candidate);
            try
            {
                listener.Server.DualMode = true;
                listener.Start();
                return listener;
            }
            catch (SocketException) when (candidate != 0)
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("No port could be opened.");
    }

    private void Report(PhoneServerState state, string? peer, string? message) =>
        StatusChanged?.Invoke(this, new PhoneServerStatus(state, Code, peer, message));

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            var peer = address is null ? "?" : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshake.CancelAfter(HandshakeTimeout);
                var socket = await WebSocketUpgrade.AcceptAsync(client.GetStream(), PhoneLink.Path, handshake.Token).ConfigureAwait(false);
                if (socket is null)
                {
                    return;
                }

                using var link = new LinkChannel(socket);
                var hello = await HandshakeAsync(link, handshake.Token).ConfigureAwait(false);
                if (hello is null)
                {
                    return;
                }

                // The handshake gave the camera to this PC.
                try
                {
                    CurrentLink = link;
                    Report(PhoneServerState.Connected, $"{hello.Client} ({peer})", null);
                    await CommandsAsync(link, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CurrentLink = null;
                    await PutCameraBackAsync().ConfigureAwait(false);
                    Interlocked.Exchange(ref _active, 0);
                    Report(PhoneServerState.Waiting, null, "SoftCal disconnected.");
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or System.Net.WebSockets.WebSocketException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
            {
                // The connection broke, or was not the link's: the next one is served as usual.
            }
        }
    }

    /// <summary>
    /// The pairing handshake: the PC's hello, the phone's challenge, the PC's proof, the phone's proof in the welcome.
    /// Returns the hello of a PC that proved it knows the code and now has the camera (<c>_active</c> is released when
    /// it leaves); null otherwise, after telling it why.
    /// </summary>
    private async Task<HelloMessage?> HandshakeAsync(LinkChannel link, CancellationToken cancellationToken)
    {
        if ((await link.ReceiveAsync(cancellationToken).ConfigureAwait(false))?.Message is not HelloMessage hello)
        {
            return null;
        }

        if (hello.Protocol != PhoneLink.Protocol)
        {
            await link.SendAsync(new ErrorMessage(0, $"The phone speaks version {PhoneLink.Protocol} of the link and SoftCal version {hello.Protocol}: update the older one."), cancellationToken).ConfigureAwait(false);
            await link.CloseAsync("protocol", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var phoneNonce = Pairing.NewNonce();
        await link.SendAsync(new ChallengeMessage(PhoneLink.Protocol, phoneNonce, _phone), cancellationToken).ConfigureAwait(false);
        if ((await link.ReceiveAsync(cancellationToken).ConfigureAwait(false))?.Message is not AuthMessage auth)
        {
            return null;
        }

        var key = Pairing.Key(Code);
        if (!Pairing.Same(Pairing.PcProof(key, hello.Nonce, phoneNonce), auth.Mac))
        {
            if (Interlocked.Increment(ref _failures) >= Pairing.MaxFailures)
            {
                // Too many guesses: a new code ends them.
                lock (_gate)
                {
                    _code = Pairing.NewCode();
                }

                Interlocked.Exchange(ref _failures, 0);
                Report(PhoneServerState.Waiting, null, "Too many wrong codes were tried: this is a new code.");
            }

            await link.SendAsync(new ErrorMessage(0, "Wrong code: type the 6 digits the phone shows."), cancellationToken).ConfigureAwait(false);
            await link.CloseAsync("wrong code", cancellationToken).ConfigureAwait(false);
            return null;
        }

        Interlocked.Exchange(ref _failures, 0);

        // One PC at a time: a second one is told so, and the first keeps the camera.
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            await link.SendAsync(new ErrorMessage(0, "This phone is already connected to another PC: disconnect it there first."), cancellationToken).ConfigureAwait(false);
            await link.CloseAsync("busy", cancellationToken).ConfigureAwait(false);
            return null;
        }

        try
        {
            await link.SendAsync(new WelcomeMessage(Pairing.PhoneProof(key, hello.Nonce, phoneNonce), _camera.Capabilities), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _active, 0);
            throw;
        }

        return hello;
    }

    /// <summary>
    /// The session. Pings are answered at once, as they arrive, so the clock mapping stays exact and a command that takes
    /// seconds (locking the camera, waiting for its focus) never makes the PC think the phone is gone. Pictures are made
    /// in the background. Every other command is carried out in turn, in the order sent, and the frames of a running
    /// stream are pumped out as they come.
    /// </summary>
    private async Task CommandsAsync(LinkChannel link, CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var frames = Channel.CreateBounded<FrameMessage>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var commands = Channel.CreateUnbounded<LinkMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var pump = Task.Run(() => PumpAsync(link, frames.Reader, session.Token), CancellationToken.None);
        var worker = Task.Run(() => WorkAsync(link, commands.Reader, frames.Writer, session.Token), CancellationToken.None);
        try
        {
            while (await link.ReceiveAsync(session.Token).ConfigureAwait(false) is { } received)
            {
                switch (received.Message)
                {
                    case null:
                        continue;
                    case ByeMessage:
                        return;
                    case PingMessage ping:
                        await link.SendAsync(new PongMessage(ping.Id, ping.PcTime, _camera.Now), session.Token).ConfigureAwait(false);
                        continue;
                    case CaptureImageMessage capture:
                        _ = Task.Run(() => SendPictureAsync(link, capture, session.Token), CancellationToken.None);
                        continue;
                    case var command:
                        commands.Writer.TryWrite(command);
                        continue;
                }
            }
        }
        finally
        {
            commands.Writer.TryComplete();
            await session.CancelAsync().ConfigureAwait(false);
            frames.Writer.TryComplete();
            foreach (var task in new[] { worker, pump })
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
                {
                    // The link is going away.
                }
            }

            await link.CloseAsync("bye", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Carries out the commands in turn and answers each; a stream started here ends with the session.</summary>
    private async Task WorkAsync(LinkChannel link, ChannelReader<LinkMessage> commands, ChannelWriter<FrameMessage> frames, CancellationToken cancellationToken)
    {
        IDisposable? stream = null;
        try
        {
            await foreach (var message in commands.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                LinkMessage reply;
                try
                {
                    switch (message)
                    {
                        case PrepareMessage prepare:
                            var controls = await _camera.PrepareAsync(prepare.Lock, cancellationToken).ConfigureAwait(false);
                            reply = new PreparedMessage(prepare.Id, controls, _camera.Capabilities.Raw);
                            break;
                        case ExposureMessage exposure:
                            var set = await _camera.SetExposureAsync(exposure.Seconds, cancellationToken).ConfigureAwait(false);
                            reply = new ExposureSetMessage(exposure.Id, set.Nominal, set.Actual, set.Epoch);
                            break;
                        case StartStreamMessage start:
                            stream?.Dispose();
                            stream = _camera.StartStream(start.Region, frame => frames.TryWrite(frame));
                            reply = new OkMessage(start.Id);
                            break;
                        case StopStreamMessage stop:
                            stream?.Dispose();
                            stream = null;
                            reply = new OkMessage(stop.Id);
                            break;
                        case RestoreMessage restore:
                            stream?.Dispose();
                            stream = null;
                            await _camera.RestoreAsync(cancellationToken).ConfigureAwait(false);
                            reply = new OkMessage(restore.Id);
                            break;
                        case FocusMessage focus:
                            var moved = await _camera.SetFocusAsync(focus.Diopters, cancellationToken).ConfigureAwait(false);
                            reply = new FocusSetMessage(focus.Id, focus.Diopters, moved.Reported, moved.Moving, moved.Time);
                            break;
                        default:
                            reply = new ErrorMessage(IdOf(message), $"The phone does not expect '{message.GetType().Name}' now.");
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    reply = new ErrorMessage(IdOf(message), ex.Message);
                }

                await link.SendAsync(reply, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private async Task PumpAsync(LinkChannel link, ChannelReader<FrameMessage> frames, CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await link.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _framesSent);
        }
    }

    private async Task SendPictureAsync(LinkChannel link, CaptureImageMessage capture, CancellationToken cancellationToken)
    {
        try
        {
            var picture = await _camera.CaptureImageAsync(capture.Region, Math.Max(1, capture.Frames), Math.Max(0, capture.Skip), Math.Clamp(capture.MaxWidth, 16, 1920), cancellationToken).ConfigureAwait(false);
            var header = new ImageMessage(capture.Id, picture.Width, picture.Height, picture.Bits, picture.Frames, picture.FirstTime, picture.LastTime);
            await link.SendAsync(header, picture.Data, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _picturesSent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await link.SendAsync(new ErrorMessage(capture.Id, ex.Message), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception sendError) when (sendError is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The link is gone.
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended.
        }
    }

    private void OnCameraFaulted(object? sender, FaultMessage fault) => _ = SendFaultAsync(fault);

    /// <summary>Tells the connected PC about a camera fault; with no PC, or a link going away, there is no one to tell.</summary>
    private async Task SendFaultAsync(FaultMessage fault)
    {
        if (CurrentLink is not { IsOpen: true } link)
        {
            return;
        }

        try
        {
            await link.SendAsync(fault, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The link is gone.
        }
    }

    private async Task PutCameraBackAsync()
    {
        try
        {
            await _camera.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The camera going back to automatic must never take the server down; the next PC prepares it again.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static long IdOf(LinkMessage message) => message switch
    {
        PingMessage m => m.Id,
        PrepareMessage m => m.Id,
        ExposureMessage m => m.Id,
        StartStreamMessage m => m.Id,
        StopStreamMessage m => m.Id,
        CaptureImageMessage m => m.Id,
        RestoreMessage m => m.Id,
        FocusMessage m => m.Id,
        _ => 0,
    };
}
