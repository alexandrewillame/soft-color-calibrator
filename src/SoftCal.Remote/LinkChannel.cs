using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoftCal.Remote;

/// <summary>
/// The link's messages over one WebSocket: JSON on text frames, picture pixels on binary frames. A WebSocket takes one
/// send at a time, so sends wait their turn (a picture's header and pixels go out back to back); one loop receives.
/// </summary>
internal sealed class LinkChannel(WebSocket socket) : IDisposable
{
    /// <summary>Larger messages are refused: a picture is at most a few hundred kilobytes.</summary>
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private readonly SemaphoreSlim _send = new(1, 1);
    private byte[] _buffer = new byte[64 * 1024];

    public bool IsOpen => socket.State == WebSocketState.Open;

    public async Task SendAsync(LinkMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, LinkJson.Default.LinkMessage);
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>A header and the binary payload that belongs to it, with nothing in between.</summary>
    public async Task SendAsync(LinkMessage header, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(header, LinkJson.Default.LinkMessage);
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            await socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>The next message, or a binary payload; null when the other side closed the link.</summary>
    public async Task<Received?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var length = 0;
        while (true)
        {
            if (length == _buffer.Length)
            {
                if (length >= MaxMessageBytes)
                {
                    throw new InvalidDataException($"A message over {MaxMessageBytes / (1024 * 1024)} MB was refused.");
                }

                Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, MaxMessageBytes));
            }

            var result = await socket.ReceiveAsync(_buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            length += result.Count;
            if (!result.EndOfMessage)
            {
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                return new Received(null, _buffer.AsSpan(0, length).ToArray());
            }

            var message = JsonSerializer.Deserialize(_buffer.AsSpan(0, length), LinkJson.Default.LinkMessage)
                ?? throw new InvalidDataException("An empty message was received.");
            return new Received(message, null);
        }
    }

    /// <summary>Closes the link politely when it is still open; a link already gone is no error.</summary>
    public async Task CloseAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        socket.Dispose();
        _send.Dispose();
    }

    /// <summary>A received message: JSON (<paramref name="Message"/>) or a binary payload.</summary>
    internal sealed record Received(LinkMessage? Message, byte[]? Payload);
}

/// <summary>
/// The server's half of a WebSocket's opening (RFC 6455): reads the HTTP upgrade request from a fresh connection and
/// answers it. Plain sockets and the runtime's own WebSocket do the rest, on a phone as on a PC, with no web server.
/// </summary>
internal static class WebSocketUpgrade
{
    private const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>The server WebSocket, or null (after a 400 answer) when the request is not an upgrade to <paramref name="path"/>.</summary>
    public static async Task<WebSocket?> AcceptAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        var head = await ReadHeadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return null;
        }

        var lines = head.Split("\r\n");
        var request = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        if (request.Length < 2 || request[0] != "GET" || !request[1].StartsWith(path, StringComparison.Ordinal)
            || !headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.TryGetValue("Sec-WebSocket-Key", out var key) || key.Length == 0)
        {
            await stream.WriteAsync("HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            return null;
        }

#pragma warning disable CA5350 // RFC 6455 defines the accept value with SHA-1; it is no security measure.
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + Magic)));
#pragma warning restore CA5350
        var answer = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), cancellationToken).ConfigureAwait(false);
        // A PC that vanishes (crashed, asleep, off the network) frees the phone within seconds.
        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.FromSeconds(5),
            KeepAliveTimeout = TimeSpan.FromSeconds(10),
        });
    }

    /// <summary>The request's head, byte by byte up to its blank line (nothing after it is read).</summary>
    private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512);
        var one = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                return null;
            }

            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
            {
                return Encoding.ASCII.GetString([.. bytes]);
            }
        }

        return null;
    }
}
