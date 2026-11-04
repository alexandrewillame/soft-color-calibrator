using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoftCal.App.Services;

/// <summary>
/// The monitor's on-screen (OSD) settings, now or in one profile, as the user reports them (or DDC/CI reads them).
/// Nothing here is written to the monitor unless a profile is loaded.
/// </summary>
internal sealed record MonitorSettings
{
    public int? Brightness { get; init; }

    public int? Contrast { get; init; }

    public string? ColorPreset { get; init; }

    /// <summary>Older files kept the three gains in one text ("84 / 85 / 84"); read once, then split.</summary>
    public string? RgbGains { get; init; }

    public int? RedGain { get; init; }

    public int? GreenGain { get; init; }

    public int? BlueGain { get; init; }

    /// <summary>Null when the monitor has no gamma setting: gamma advice is then skipped (it is optional).</summary>
    public string? GammaSetting { get; init; }

    public bool HasGammaSetting { get; init; }

    /// <summary>The gamma presets in menu order, comma-separated (e.g. "Gamma 1, Gamma 2, …"), for the advisor.</summary>
    public string? GammaPresets { get; init; }

    /// <summary>A shadow lift such as Black eQualizer: its name, value and highest value (null = none).</summary>
    public string? BlackBoostName { get; init; }

    public int? BlackBoost { get; init; }

    public int? BlackBoostMax { get; init; }

    /// <summary>The highest Brightness and Contrast values (from DDC/CI; 100 when unknown).</summary>
    public int? BrightnessMax { get; init; }

    public int? ContrastMax { get; init; }

    /// <summary>Dynamic contrast, ECO / light sensor, low blue light, black boost: all should be off.</summary>
    public bool EnhancementsOff { get; init; }

    public string? Notes { get; init; }

    /// <summary>The gains, from the three fields or, in older files, from the combined text.</summary>
    public (int? Red, int? Green, int? Blue) Gains()
    {
        if (RedGain is not null || GreenGain is not null || BlueGain is not null || string.IsNullOrWhiteSpace(RgbGains))
        {
            return (RedGain, GreenGain, BlueGain);
        }

        var parts = RgbGains.Split(['/', ',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null)
            .ToList();
        return parts.Count == 3 ? (parts[0], parts[1], parts[2]) : (null, null, null);
    }
}

/// <summary>The calibration made for a profile: SoftCal's copy of its profile file (in the profiles folder), what it
/// aims for, when it was made and its run's file stamp. Loading the profile installs it.</summary>
internal sealed record ProfileCalibration(string File, string Target, DateTimeOffset CreatedAt, string? TrialStamp = null);

/// <summary>A SoftCal profile of one monitor: its name, the monitor's settings in it, and the calibration made for it
/// (null: none).</summary>
internal sealed record MonitorProfile(string Name, MonitorSettings Settings, ProfileCalibration? Calibration = null);

/// <summary>
/// The profiles of each monitor (by DisplayKey; "simulated" for the simulator) and which one is in use. A monitor
/// always has at least one; the first is "Default". Besides the profiles, the monitor's own state: its settings now
/// ("current": what step 1 shows beside the profile chosen, read, typed or loaded), and whether its panel is TN. File:
/// <c>monitors/&lt;key&gt;.json</c> = { "active": name, "profiles": [ { "name", "settings", "calibration" } ],
/// "tnPanel", "current" }. Files from before profiles, which kept the settings per picture mode ({ "lastMode", "modes" })
/// or as one object, are read as profiles named after their modes (an unnamed mode is "Default"), the last mode used
/// being the active one; the next save writes them in the new form.
/// </summary>
internal sealed class MonitorProfileStore(string directory)
{
    public const string DefaultName = "Default";

    public const int MaxNameLength = 40;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The store in SoftCal's data folder.</summary>
    public static MonitorProfileStore Shared { get; } = new(AppPaths.Monitors);

    /// <summary>The monitor's profiles, in the order they were made (at least one).</summary>
    public IReadOnlyList<MonitorProfile> List(string key) => Read(key).Profiles;

    /// <summary>The profile in use (the last one chosen, loaded, saved or measured).</summary>
    public MonitorProfile Active(string key)
    {
        var document = Read(key);
        return Find(document, document.Active) ?? document.Profiles[0];
    }

    public MonitorProfile? Find(string key, string name) => Find(Read(key), name);

    /// <summary>The monitor's settings now, as last read, typed or loaded. Files from before they were kept apart: the
    /// settings of the profile in use, which held them.</summary>
    public MonitorSettings Current(string key)
    {
        var document = Read(key);
        return document.Current ?? (Find(document, document.Active) ?? document.Profiles[0]).Settings;
    }

    public void SaveCurrent(string key, MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Write(key, Read(key) with { Current = settings });
    }

    /// <summary>The monitor has <paramref name="settings"/> now, and they are profile <paramref name="name"/>'s (saved
    /// from the monitor, or measured): kept as both, that profile chosen.</summary>
    public void SetFromMonitor(string key, string name, MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var document = Read(key);
        var index = IndexOf(document, name);
        if (index < 0)
        {
            document.Profiles.Add(new MonitorProfile(Clean(name), settings));
            index = document.Profiles.Count - 1;
        }
        else
        {
            document.Profiles[index] = document.Profiles[index] with { Settings = settings };
        }

        var profile = document.Profiles[index].Name;
        Write(key, document with { Active = profile, Current = settings });
    }

    /// <summary>Whether the monitor's panel is TN, as the user said (null: never said).</summary>
    public bool? IsTnPanel(string key) => Read(key).TnPanel;

    public void SetTnPanel(string key, bool tn) => Write(key, Read(key) with { TnPanel = tn });

    /// <summary>When this monitor's profiles were last saved; null when they never were.</summary>
    public DateTime? LastSaved(string? key)
    {
        if (key is null)
        {
            return null;
        }

        var path = PathFor(key);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    public void SetActive(string key, string name)
    {
        var document = Read(key);
        var profile = Find(document, name) ?? throw new ArgumentException($"There is no profile named {name}.", nameof(name));
        Write(key, document with { Active = profile.Name });
    }

    /// <summary>Saves the settings of profile <paramref name="name"/> (made when it is not there) and makes it the one in
    /// use.</summary>
    public void SaveSettings(string key, string name, MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var document = Read(key);
        var index = IndexOf(document, name);
        if (index >= 0)
        {
            document.Profiles[index] = document.Profiles[index] with { Settings = settings };
        }
        else
        {
            document.Profiles.Add(new MonitorProfile(Clean(name), settings));
        }

        Write(key, document with { Active = index >= 0 ? document.Profiles[index].Name : Clean(name) });
    }

    /// <summary>A new profile with <paramref name="settings"/>, now the one in use. Throws when the name is not
    /// valid or already taken (<see cref="CheckName"/>).</summary>
    public MonitorProfile Create(string key, string name, MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var document = Read(key);
        if (CheckName(name, document.Profiles.Select(p => p.Name)) is { } problem)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        var profile = new MonitorProfile(Clean(name), settings);
        document.Profiles.Add(profile);
        Write(key, document with { Active = profile.Name });
        return profile;
    }

    public void Rename(string key, string name, string newName)
    {
        var document = Read(key);
        var index = IndexOf(document, name);
        if (index < 0)
        {
            throw new ArgumentException($"There is no profile named {name}.", nameof(name));
        }

        if (CheckName(newName, document.Profiles.Select(p => p.Name), except: document.Profiles[index].Name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(newName));
        }

        var wasActive = string.Equals(document.Active, document.Profiles[index].Name, StringComparison.OrdinalIgnoreCase);
        document.Profiles[index] = document.Profiles[index] with { Name = Clean(newName) };
        Write(key, document with { Active = wasActive ? Clean(newName) : document.Active });
    }

    /// <summary>Removes a profile; the last one cannot be (a monitor always has one). The first remaining one is in
    /// use when the removed one was.</summary>
    public void Delete(string key, string name)
    {
        var document = Read(key);
        var index = IndexOf(document, name);
        if (index < 0)
        {
            return;
        }

        if (document.Profiles.Count == 1)
        {
            throw new InvalidOperationException("A monitor keeps at least one profile.");
        }

        var wasActive = string.Equals(document.Active, document.Profiles[index].Name, StringComparison.OrdinalIgnoreCase);

        // The monitor's settings stay what they are; they just belong to no profile any more.
        var current = document.Current ?? (Find(document, document.Active) ?? document.Profiles[0]).Settings;
        document.Profiles.RemoveAt(index);
        Write(key, document with
        {
            Active = wasActive ? document.Profiles[0].Name : document.Active,
            Current = current,
        });
    }

    /// <summary>Links (or, with null, unlinks) the calibration made for a profile.</summary>
    public void SetCalibration(string key, string name, ProfileCalibration? calibration)
    {
        var document = Read(key);
        var index = IndexOf(document, name);
        if (index < 0)
        {
            throw new ArgumentException($"There is no profile named {name}.", nameof(name));
        }

        document.Profiles[index] = document.Profiles[index] with { Calibration = calibration };
        Write(key, document);
    }

    /// <summary>
    /// Links an installed calibration that no profile holds yet (installed before profiles existed, when it was
    /// labelled with its picture mode) to the profile it names, or "Default" for an unnamed one. Returns the profile
    /// it now belongs to, or null when none is named so (it then belongs to no profile).
    /// </summary>
    public string? Adopt(string key, string? profileName, ProfileCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        var document = Read(key);
        if (document.Profiles.FirstOrDefault(p => string.Equals(p.Calibration?.File, calibration.File, StringComparison.OrdinalIgnoreCase)) is { } holder)
        {
            return holder.Name;
        }

        var index = IndexOf(document, string.IsNullOrWhiteSpace(profileName) ? DefaultName : profileName);
        if (index < 0)
        {
            return null;
        }

        document.Profiles[index] = document.Profiles[index] with { Calibration = calibration };
        Write(key, document);
        return document.Profiles[index].Name;
    }

    /// <summary>Why <paramref name="name"/> cannot name a profile, or null when it can: not empty, at most
    /// <see cref="MaxNameLength"/> characters, and no other profile of the monitor has it (case does not count).</summary>
    public static string? CheckName(string? name, IEnumerable<string> taken, string? except = null)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var clean = Clean(name);
        if (clean.Length == 0)
        {
            return "Give the profile a name.";
        }

        if (clean.Length > MaxNameLength)
        {
            return string.Create(CultureInfo.CurrentCulture, $"Keep the name under {MaxNameLength + 1} characters.");
        }

        return taken.Any(t => string.Equals(t, clean, StringComparison.OrdinalIgnoreCase) && !string.Equals(t, except, StringComparison.OrdinalIgnoreCase))
            ? $"There is already a profile named {clean}."
            : null;
    }

    private static string Clean(string? name) => name?.Trim() ?? string.Empty;

    private static MonitorProfile? Find(Document document, string? name) =>
        name is null ? null : document.Profiles.FirstOrDefault(p => string.Equals(p.Name, Clean(name), StringComparison.OrdinalIgnoreCase));

    private static int IndexOf(Document document, string name) =>
        document.Profiles.FindIndex(p => string.Equals(p.Name, Clean(name), StringComparison.OrdinalIgnoreCase));

    /// <summary>The monitor's profiles, whatever form the file is in: at least one, names unique.</summary>
    private Document Read(string key)
    {
        var path = PathFor(key);
        Document? document = null;
        try
        {
            if (File.Exists(path))
            {
                var node = JsonNode.Parse(File.ReadAllText(path));
                document = node switch
                {
                    JsonObject o when o.ContainsKey("profiles") => node.Deserialize<Document>(Json),
                    JsonObject o when o.ContainsKey("modes") => FromModes(node.Deserialize<ModeFile>(Json)),
                    JsonObject o => node.Deserialize<MonitorSettings>(Json) is { } single
                        ? new Document(null, [new MonitorProfile(NameOfMode(Text(o["pictureMode"])), single)])
                        : null,
                    _ => null,
                };
            }
        }
        catch (JsonException)
        {
            // Unreadable: kept aside (the next save writes a new file), so the profiles in it are not lost.
            try
            {
                File.Copy(path, path + ".damaged", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done for it.
            }
        }

        var profiles = new List<MonitorProfile>();
        foreach (var profile in document?.Profiles ?? [])
        {
            var name = Clean(profile.Name);
            if (name.Length > 0 && !profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                profiles.Add(profile with { Name = name, Settings = profile.Settings ?? new MonitorSettings() });
            }
        }

        if (profiles.Count == 0)
        {
            profiles.Add(new MonitorProfile(DefaultName, new MonitorSettings()));
        }

        return new Document(document?.Active, profiles, document?.TnPanel, document?.Current);
    }

    /// <summary>The settings kept per picture mode, before profiles: each mode becomes a profile of its name ("Default"
    /// for the unnamed one), "Default" first, then by name; the last mode used is the one in use.</summary>
    private static Document? FromModes(ModeFile? modes)
    {
        if (modes?.Modes is not { } byMode)
        {
            return null;
        }

        var profiles = byMode
            .Select(m => new MonitorProfile(NameOfMode(m.Key), m.Value ?? new MonitorSettings()))
            .OrderBy(p => p.Name == DefaultName ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new Document(NameOfMode(modes.LastMode), profiles);
    }

    private static string NameOfMode(string? mode) => string.IsNullOrWhiteSpace(mode) ? DefaultName : mode.Trim();

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private void Write(string key, Document document)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(key);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, Json));
        File.Move(temp, path, overwrite: true);
    }

    private string PathFor(string key) =>
        Path.Combine(directory, string.Concat(key.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_')) + ".json");

    private sealed record Document(string? Active, List<MonitorProfile> Profiles, bool? TnPanel = null, MonitorSettings? Current = null);

    /// <summary>The file before profiles: the settings per picture mode, and the last mode used.</summary>
    private sealed record ModeFile(string? LastMode, Dictionary<string, MonitorSettings?>? Modes);
}
