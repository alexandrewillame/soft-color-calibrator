using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.Core.Sessions;

namespace SoftCal.FeasibilityHarness.Infrastructure;

/// <summary>
/// One recorded harness run: a session folder with the header, the event log (every patch, frame and exposure change)
/// and one <c>result.&lt;step&gt;</c> state snapshot per step, also written as <c>results/&lt;step&gt;.json</c>.
/// </summary>
internal sealed class HarnessSession : IRigObserver, IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SessionRecorder _recorder;
    private readonly Func<double> _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, object> _results = [];

    private HarnessSession(SessionRecorder recorder, Func<double> clock, string label)
    {
        _recorder = recorder;
        _clock = clock;
        Label = label;
    }

    public string DirectoryPath => _recorder.DirectoryPath;

    public string Label { get; }

    public long FramesRecorded { get; private set; }

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoftColorCalibrator", "sessions", "m1");

    public static HarnessSession Create(
        string root,
        string command,
        string? monitorLabel,
        string purpose,
        Func<double> clock,
        string? displayKey = null,
        string? displayName = null,
        InstrumentInfo? instrument = null,
        InstrumentCapabilities? capabilities = null)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var label = monitorLabel is null ? $"{stamp}-{command}" : $"{stamp}-{command}-{monitorLabel}";
        var directory = Path.Combine(root, label);
        for (var i = 2; Directory.Exists(directory); i++)
        {
            directory = Path.Combine(root, $"{label}-{i}");
        }

        var header = new SessionHeader
        {
            SessionId = Guid.NewGuid(),
            CreatedUtc = DateTimeOffset.UtcNow,
            AppVersion = typeof(HarnessSession).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            DisplayKey = displayKey,
            DisplayName = displayName,
            Instrument = instrument,
            InstrumentCapabilities = capabilities,
            Purpose = purpose,
        };
        var session = new HarnessSession(SessionRecorder.Create(directory, header), clock, Path.GetFileName(directory));
        session.Record("clock", new
        {
            qpcFrequency = Stopwatch.Frequency,
            qpcSecondsAtCreation = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
            utcAtCreation = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.VersionString,
        });
        return session;
    }

    public double Now => _clock();

    public IReadOnlyDictionary<string, object> Results
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, object>(_results);
            }
        }
    }

    public void Record(string kind, object value)
    {
        var element = JsonSerializer.SerializeToElement(value, Json);
        _recorder.Append(new StateSnapshotEvent(_clock(), kind, element));
    }

    /// <summary>Records a step's result in the event log and as results/&lt;step&gt;.json.</summary>
    public void Result(string step, object value)
    {
        Record("result." + step, value);
        var folder = Path.Combine(DirectoryPath, "results");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, step + ".json"), JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        lock (_gate)
        {
            _results[step] = value;
        }
    }

    public void Note(string text) => _recorder.Append(new NoteEvent(_clock(), text));

    public void OnPatchShown(PresentedPatch shown) => _recorder.Append(new PatchShownEvent(shown.PresentTimeSeconds, shown.Sequence, shown.Patch));

    public void OnFrame(FrameStats frame)
    {
        _recorder.Append(new FrameEvent(frame.TimestampSeconds, frame));
        FramesRecorded++;
    }

    public void OnExposure(ExposureState state) => _recorder.Append(new ExposureChangedEvent(_clock(), state));

    public void Dispose() => _recorder.Dispose();
}
