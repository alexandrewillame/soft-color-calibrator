using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoftCal.Windows.Safety;

/// <summary>
/// One journaled change: what was there before, so it can be put back after a crash. The owner's start time tells a
/// dead owner from a new process that reused its id; entries written before it was recorded carry no start time.
/// </summary>
public sealed record JournalEntry(
    string Kind,
    string Key,
    int OwnerProcessId,
    DateTimeOffset CreatedUtc,
    JsonElement Original,
    DateTimeOffset? OwnerStartTimeUtc = null)
{
    [JsonIgnore]
    public ProcessIdentity Owner => new(OwnerProcessId, OwnerStartTimeUtc);
}

/// <summary>
/// The state journal: every reversible change is written here before it is applied, and removed once it has been
/// undone. Entries left behind mean a run died; a guardian process (or the next launch) restores them. One file per
/// entry, written atomically, culture-invariant JSON.
/// </summary>
public sealed class StateJournal
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ProcessIdentity _owner;

    /// <param name="directory">Where the entries live.</param>
    /// <param name="owner">The process that owns new entries; the current process by default.</param>
    public StateJournal(string directory, ProcessIdentity? owner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        DirectoryPath = directory;
        _owner = owner ?? ProcessIdentity.Current;
    }

    /// <summary>%LOCALAPPDATA%\SoftColorCalibrator\journal</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoftColorCalibrator", "journal");

    public string DirectoryPath { get; }

    public void Save<T>(string kind, string key, T original)
    {
        Directory.CreateDirectory(DirectoryPath);
        var entry = new JournalEntry(
            kind,
            key,
            _owner.Id,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(original, JsonOptions),
            _owner.StartTimeUtc);
        var path = PathFor(kind, key);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entry, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public bool Contains(string kind, string key) => File.Exists(PathFor(kind, key));

    public void Remove(string kind, string key)
    {
        var path = PathFor(kind, key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public IReadOnlyList<JournalEntry> Pending(string? kind = null)
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var entries = new List<JournalEntry>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(file), JsonOptions);
                if (entry is not null && (kind is null || entry.Kind == kind))
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A torn write leaves a .tmp file, never a partial .json; skip anything unreadable.
            }
            catch (IOException)
            {
                // Deleted by its owner while we were listing.
            }
        }

        return entries;
    }

    public static T? Read<T>(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Original.Deserialize<T>(JsonOptions);
    }

    private string PathFor(string kind, string key)
    {
        var safe = new string([.. key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        return Path.Combine(DirectoryPath, $"{kind}-{safe}.json");
    }
}
