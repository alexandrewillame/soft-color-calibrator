using System.Text;
using System.Text.Json;

namespace SoftCal.Core.Sessions;

/// <summary>
/// Writes a session: the header once, then one JSON event per line, flushed immediately so that a crash loses at most
/// the line being written. Files are culture-invariant UTF-8.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    public const string HeaderFileName = "session.json";
    public const string EventsFileName = "events.jsonl";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    private SessionRecorder(string directory, StreamWriter writer)
    {
        DirectoryPath = directory;
        _writer = writer;
    }

    public string DirectoryPath { get; }

    /// <summary>Creates a new session folder. Fails if the folder already holds an event log.</summary>
    public static SessionRecorder Create(string directory, SessionHeader header)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(header);
        Directory.CreateDirectory(directory);
        var eventsPath = Path.Combine(directory, EventsFileName);
        var stream = new FileStream(eventsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, HeaderFileName),
                JsonSerializer.Serialize(header, SessionJsonContext.Default.SessionHeader),
                Utf8NoBom);
            return new SessionRecorder(directory, new StreamWriter(stream, Utf8NoBom));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Append(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        var line = JsonSerializer.Serialize(sessionEvent, SessionJsonContext.Default.SessionEvent);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.Write(line);
            _writer.Write('\n');
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }
}

/// <summary>Reads sessions written by <see cref="SessionRecorder"/>, tolerating a truncated last line after a crash.</summary>
public static class SessionReader
{
    public static SessionRecording Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var header = JsonSerializer.Deserialize(
                File.ReadAllText(Path.Combine(directory, SessionRecorder.HeaderFileName)),
                SessionJsonContext.Default.SessionHeader)
            ?? throw new InvalidDataException("The session header is empty.");
        if (header.SchemaVersion > SessionHeader.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Session schema {header.SchemaVersion} is newer than this app supports ({SessionHeader.CurrentSchemaVersion}).");
        }

        var lines = new List<string>();
        using (var stream = new FileStream(
                   Path.Combine(directory, SessionRecorder.EventsFileName),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }
        }

        var events = new List<SessionEvent>(lines.Count);
        var truncated = false;
        for (var i = 0; i < lines.Count; i++)
        {
            try
            {
                events.Add(JsonSerializer.Deserialize(lines[i], SessionJsonContext.Default.SessionEvent)
                    ?? throw new InvalidDataException($"Event line {i + 1} is null."));
            }
            catch (JsonException) when (i == lines.Count - 1)
            {
                // A crash while writing can only damage the last line.
                truncated = true;
            }
        }

        return new SessionRecording(header, events, truncated);
    }
}
