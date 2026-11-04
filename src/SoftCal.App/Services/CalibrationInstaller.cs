using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoftCal.Core.Calibration;
using SoftCal.Icc;
using SoftCal.Windows.ColorManagement;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Elevation;
using SoftCal.Windows.Gamma;

namespace SoftCal.App.Services;

/// <summary>What SoftCal installed for a display, and what was there before, so Revert can put it back. Kept per
/// DisplayKey (EDID): the adapter LUID changes at every boot, so the display is looked up again when reverting.</summary>
/// <param name="LoaderSwitchWasOn">'Use Windows display calibration' before SoftCal; with <paramref name="LoaderTaskWasEnabled"/>,
/// what to put back when SoftCal had turned Windows' loader on (calibrations installed before SoftCal had its own).</param>
/// <param name="TrialStamp">The calibration run's file stamp in the display's report folder (its measurements before and
/// with the curves), for comparing a later verification with them.</param>
/// <param name="PerUserWasOn">Whether 'Use my settings for this device' was on for the display before SoftCal.</param>
/// <param name="UsesSoftCalLoader">SoftCal's own loader keeps the curves loaded. False for calibrations installed before
/// it existed, which relied on Windows' loader.</param>
/// <param name="Profile">The SoftCal profile it was made for, as named when it was installed.</param>
internal sealed record InstalledCalibration(
    string DisplayKey,
    string? Profile,
    string ProfileFile,
    string DeviceName,
    string? PreviousDefault,
    bool LoaderSwitchWasOn,
    bool LoaderTaskWasEnabled,
    DateTimeOffset InstalledAt,
    string Target,
    string? TrialStamp = null,
    bool PerUserWasOn = false,
    bool UsesSoftCalLoader = false)
{
    /// <summary>Records written before profiles named the picture mode instead: read as the profile, never written.</summary>
    [JsonPropertyName("pictureMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? LegacyPictureMode
    {
        get => null;
        init => Profile ??= value;
    }
}

/// <summary>What Check found: whether the GPU holds the curves now, what SoftCal can fix (empty when all is well), and
/// a summary.</summary>
internal sealed record CalibrationCheck(bool Loaded, int MaxDifference, IReadOnlyList<string> Fixes, string Message);

/// <summary>
/// Installs a calibration for Windows (plan §7.2): the profile into the COLOR folder, as the display's default for the
/// current user with 'Use my settings for this device' on (otherwise Windows keeps giving apps the system-wide
/// profile), and SoftCal's loader in the user's startup apps, which keeps the curves in the GPU at full precision and
/// puts them back after any reset (Windows' own loader rounds them to 8 bits and only loads at sign-in and when the
/// profile changes). No administrator prompt. A record of what was there before makes <see cref="RevertAsync"/> exact.
/// </summary>
internal static class CalibrationInstaller
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>SoftCal's copies of the profiles it made (Windows keeps its own copy in the COLOR folder).</summary>
    public static string ProfilesFolder => Path.Combine(AppPaths.Root, "profiles");

    private static string RecordsFolder => Path.Combine(AppPaths.Root, "calibrations");

    public static InstalledCalibration? Current(string displayKey)
    {
        var path = RecordPath(displayKey);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<InstalledCalibration>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every installed calibration (one per display).</summary>
    public static IReadOnlyList<InstalledCalibration> All()
    {
        if (!Directory.Exists(RecordsFolder))
        {
            return [];
        }

        var records = new List<InstalledCalibration>();
        foreach (var file in Directory.GetFiles(RecordsFolder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<InstalledCalibration>(File.ReadAllText(file), Json) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A record being written or damaged is skipped; the next check reads it again.
            }
        }

        return records;
    }

    /// <summary>The installed profile's curves as a GPU ramp; null when SoftCal's copy of the profile is missing.</summary>
    public static GammaRamp? Ramp(InstalledCalibration record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Vcgt(record) is { } vcgt ? new GammaRamp([.. vcgt.Red], [.. vcgt.Green], [.. vcgt.Blue]) : null;
    }

    public static async Task<(InstalledCalibration? Record, IReadOnlyList<string> Log)> InstallAsync(
        DisplayMonitor monitor,
        string displayKey,
        string? profileName,
        IccProfile profile,
        string fileName,
        CalibrationCurves curves,
        string target,
        string? trialStamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(curves);
        var log = new List<string>();
        Directory.CreateDirectory(ProfilesFolder);
        await File.WriteAllBytesAsync(Path.Combine(ProfilesFolder, fileName), IccWriter.Write(profile), cancellationToken).ConfigureAwait(false);
        log.Add($"Wrote {fileName} (SoftCal's copy: {ProfilesFolder}).");
        var ramp = new GammaRamp(curves.Ramp(0), curves.Ramp(1), curves.Ramp(2));
        return await InstallFileAsync(monitor, displayKey, profileName, fileName, ramp, target, trialStamp, log, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs a calibration SoftCal made earlier, from its copy of the profile file (a profile being applied
    /// takes its own calibration back); null record when the copy is gone or Windows refused it.</summary>
    public static async Task<(InstalledCalibration? Record, IReadOnlyList<string> Log)> InstallSavedAsync(
        DisplayMonitor monitor,
        string displayKey,
        string? profileName,
        string fileName,
        string target,
        string? trialStamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var log = new List<string>();
        var path = Path.Combine(ProfilesFolder, fileName);
        if (!File.Exists(path))
        {
            log.Add($"SoftCal's copy of {fileName} is gone, so it could not be installed: create the calibration again.");
            return (null, log);
        }

        var vcgt = IccReader.Read(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)).Vcgt;
        var ramp = vcgt is null ? GammaRamp.Identity() : new GammaRamp([.. vcgt.Red], [.. vcgt.Green], [.. vcgt.Blue]);
        return await InstallFileAsync(monitor, displayKey, profileName, fileName, ramp, target, trialStamp, log, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(InstalledCalibration? Record, IReadOnlyList<string> Log)> InstallFileAsync(
        DisplayMonitor monitor,
        string displayKey,
        string? profileName,
        string fileName,
        GammaRamp ramp,
        string target,
        string? trialStamp,
        List<string> log,
        CancellationToken cancellationToken)
    {
        InstalledCalibration? record;
        using (CalibrationLoaderProcess.HoldCurves())
        {
            record = await InstallHeldAsync(monitor, displayKey, profileName, fileName, ramp, target, trialStamp, log, cancellationToken).ConfigureAwait(false);
        }

        if (record is not null)
        {
            log.Add(CalibrationLoaderProcess.Start() ? "SoftCal's loader is running." : "SoftCal's loader did not start now; it starts at the next sign-in.");
        }

        return (record, log);
    }

    /// <summary>Undoes an installation: association, default profile, 'Use my settings for this device', curves in the
    /// GPU, the profile file, SoftCal's loader once no display is calibrated, and (when asked, with an administrator
    /// prompt) Windows' loader for a calibration that had turned it on.</summary>
    public static async Task<IReadOnlyList<string>> RevertAsync(InstalledCalibration record, DisplayMonitor monitor, bool restoreLoader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(monitor);
        var log = new List<string>();
        if (DisplayTarget.Of(monitor) is not { } display)
        {
            log.Add("Windows does not report this display's adapter and source, so nothing was changed.");
            return log;
        }

        using (CalibrationLoaderProcess.HoldCurves())
        {
            TryRemove(display, record.ProfileFile, log, "SoftCal's profile");
            if (record.PreviousDefault is { } previous)
            {
                try
                {
                    DisplayProfileAssociation.SetDefault(display, previous);
                    log.Add($"Made {previous} the default again.");
                }
                catch (Exception ex) when (Refused(ex))
                {
                    log.Add($"Could not make {previous} the default again: {ex.Message}");
                }
            }

            if (PerUserProfiles.DeviceKey(monitor) is { } deviceKey && PerUserProfiles.IsOn(deviceKey) is { } perUser && perUser != record.PerUserWasOn)
            {
                try
                {
                    PerUserProfiles.Set(deviceKey, record.PerUserWasOn);
                    log.Add($"Turned 'Use my settings for this device' back {(record.PerUserWasOn ? "on" : "off")}, as it was.");
                }
                catch (Win32Exception ex)
                {
                    log.Add($"Could not put 'Use my settings for this device' back: {ex.Message}");
                }
            }

            File.Delete(RecordPath(record.DisplayKey));
            log.Add(new GdiGammaRampDevice(monitor.DeviceName).Write(GammaRamp.Identity())
                ? "Set the graphics card's curves back to linear."
                : "The graphics driver refused the linear curves; sign out and in to reset them.");

            if (restoreLoader && NeedsWindowsLoaderBack(record))
            {
                log.Add(await RestoreWindowsLoaderAsync(record, cancellationToken).ConfigureAwait(false));
            }
        }

        if (All().Count == 0)
        {
            CalibrationLoaderProcess.RemoveFromStartup();
            log.Add(CalibrationLoaderProcess.Stop(TimeSpan.FromSeconds(5))
                ? "Removed SoftCal's loader from your startup apps and stopped it."
                : "Removed SoftCal's loader from your startup apps; it stops by itself within seconds.");
        }

        return log;
    }

    /// <summary>Whether the GPU holds the installed profile's curves now (largest difference in 16-bit units; up to 256
    /// is rounding), whether apps get the profile, and whether SoftCal's loader keeps the curves loaded.</summary>
    public static CalibrationCheck Check(InstalledCalibration record, DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(monitor);
        if (Ramp(record) is not { } expected)
        {
            return new CalibrationCheck(false, int.MaxValue, [], "SoftCal's copy of the profile is missing: Remove this calibration, then create it again.");
        }

        var max = RampKeeper.Difference(new GdiGammaRampDevice(monitor.DeviceName).Read(), expected);
        var loaded = max <= RampKeeper.Tolerance;
        var fixes = new List<string>();
        var lines = new List<string>
        {
            loaded
                ? $"The graphics card holds the calibration curves (largest difference {max} of 65535)."
                : $"The graphics card does not hold the calibration curves (largest difference {max} of 65535).",
        };

        var (appsUseIt, apps) = AppsUse(record, monitor);
        lines.Add(apps);
        if (appsUseIt == false)
        {
            fixes.Add("Make SoftCal's profile the one Windows gives apps for this display (your user only).");
        }

        if (!record.UsesSoftCalLoader)
        {
            lines.Add("This calibration relies on Windows' own loader, which loads the curves rounded to 8 bits, and only at sign-in and when the profile changes.");
            fixes.Add("Switch to SoftCal's loader: it loads the curves at every sign-in and puts them back whenever they are reset (your startup apps, no administrator).");
        }
        else if (!CalibrationLoaderProcess.StartsThisApp)
        {
            lines.Add(CalibrationLoaderProcess.StartupCommand is null
                ? "SoftCal's loader is not in your startup apps."
                : "Your startup apps start another copy of SoftCal's loader.");
            fixes.Add("Put this copy of SoftCal's loader in your startup apps, and start it.");
        }
        else if (!CalibrationLoaderProcess.IsRunning)
        {
            lines.Add("SoftCal's loader starts at sign-in, but it is not running now.");
            fixes.Add("Start SoftCal's loader.");
        }
        else
        {
            lines.Add("SoftCal's loader is running, and starts at every sign-in.");
        }

        if (!record.UsesSoftCalLoader && NeedsWindowsLoaderBack(record))
        {
            lines.Add("'Use Windows display calibration' is still on, as SoftCal set it.");
            fixes.Add("Turn 'Use Windows display calibration' back off, as it was before SoftCal (administrator prompt).");
        }
        else if (record.UsesSoftCalLoader && CalibrationLoader.IsSwitchOn() == true)
        {
            lines.Add("'Use Windows display calibration' is on too: Windows also loads the curves, rounded to 8 bits; SoftCal's loader does the job at full precision.");
            fixes.Add("Turn 'Use Windows display calibration' off, so only SoftCal's loader sets the curves (administrator prompt).");
        }

        if (!loaded && fixes.Count == 0)
        {
            lines.Add($"SoftCal's loader may be paused or backing off: see {CalibrationLoaderProcess.LogPath}.");
            fixes.Add("Load the curves in the graphics card now.");
        }

        return new CalibrationCheck(loaded, max, fixes, string.Join(" ", lines));
    }

    /// <summary>Fixes what <see cref="Check"/> found: the profile Windows gives apps (per user, no administrator),
    /// SoftCal's loader in the startup apps and running, Windows' loader back as it was for a calibration that had
    /// turned it on (administrator prompt), and the curves loaded now. The record keeps what was there before SoftCal,
    /// so Remove stays exact.</summary>
    public static async Task<(InstalledCalibration Record, IReadOnlyList<string> Log)> RepairAsync(InstalledCalibration record, DisplayMonitor monitor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(monitor);
        var log = new List<string>();
        if (DisplayTarget.Of(monitor) is not { } display)
        {
            log.Add("Windows does not report this display's adapter and source, so nothing was changed.");
            return (record, log);
        }

        using (CalibrationLoaderProcess.HoldCurves())
        {
            // The switch first: turning it on replaces the user's list for the display with a copy of the system's.
            if (PerUserProfiles.DeviceKey(monitor) is { } deviceKey && PerUserProfiles.IsOn(deviceKey) != true)
            {
                log.Add(EnsurePerUser(deviceKey, display));
            }

            if (!string.Equals(DisplayProfileAssociation.GetDefault(display), record.ProfileFile, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    DisplayProfileAssociation.Add(display, record.ProfileFile, setAsDefault: true);
                    log.Add($"Made {record.ProfileFile} the display's default for your user again.");
                }
                catch (Exception ex) when (Refused(ex))
                {
                    log.Add($"Windows would not make {record.ProfileFile} the default again: {ex.Message}");
                }
            }

            if (!record.UsesSoftCalLoader && NeedsWindowsLoaderBack(record))
            {
                log.Add(await RestoreWindowsLoaderAsync(record, cancellationToken).ConfigureAwait(false));
            }
            else if (record.UsesSoftCalLoader && CalibrationLoader.IsSwitchOn() == true)
            {
                log.Add(await SetWindowsLoaderAsync(false, false, cancellationToken).ConfigureAwait(false));
            }

            if (!CalibrationLoaderProcess.StartsThisApp)
            {
                CalibrationLoaderProcess.AddToStartup();
                log.Add("Added SoftCal's loader to your startup apps (Task Manager › Startup apps).");
            }

            if (!record.UsesSoftCalLoader)
            {
                record = record with { UsesSoftCalLoader = true };
                Save(record);
            }

            if (Ramp(record) is { } ramp)
            {
                log.Add(new GdiGammaRampDevice(monitor.DeviceName).Write(ramp)
                    ? "Loaded the curves in the graphics card now."
                    : "The graphics driver refused the curves just now.");
            }
        }

        log.Add(CalibrationLoaderProcess.Start() ? "SoftCal's loader is running." : "SoftCal's loader did not start now; it starts at the next sign-in.");
        return (record, log);
    }

    /// <summary>Whether Windows' loader is not as it was before a calibration that had turned it on.</summary>
    public static bool NeedsWindowsLoaderBack(InstalledCalibration record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return CalibrationLoader.IsSwitchOn() != record.LoaderSwitchWasOn || CalibrationLoader.IsTaskEnabled() != record.LoaderTaskWasEnabled;
    }

    /// <summary>Whether Windows' own loader can put 'vcgt' curves in this display's GPU (its driver says); null when
    /// Windows does not say.</summary>
    public static bool? WindowsLoadsCurves(DisplayMonitor monitor) =>
        DisplayTarget.Of(monitor) is { } target ? DisplayColorCapabilities.Of(target).LoadsVcgt : null;

    /// <summary>Installs SoftCal's copy of <paramref name="fileName"/> (in the profiles folder) for the display.</summary>
    private static async Task<InstalledCalibration?> InstallHeldAsync(
        DisplayMonitor monitor,
        string displayKey,
        string? profileName,
        string fileName,
        GammaRamp ramp,
        string target,
        string? trialStamp,
        List<string> log,
        CancellationToken cancellationToken)
    {
        if (DisplayTarget.Of(monitor) is not { } display)
        {
            log.Add("Windows does not report this display's adapter and source, so the profile cannot be associated with it.");
            return null;
        }

        var path = Path.Combine(ProfilesFolder, fileName);
        try
        {
            ColorProfileStore.Install(path);
            log.Add($"Added it to Windows' color profiles ({ColorProfileStore.ColorDirectory()}).");
        }
        catch (Win32Exception ex)
        {
            log.Add($"Windows would not add the profile: {ex.Message}");
            return null;
        }

        // A recalibration replaces SoftCal's previous profile but keeps what was there before SoftCal.
        var earlier = Current(displayKey);
        var previousDefault = earlier is not null ? earlier.PreviousDefault : DisplayProfileAssociation.GetDefault(display);

        // The switch first: turning it on replaces the user's list for the display with a copy of the system's (seen on
        // the BenQ: the association made just before was gone, XL2720.icm was the default again).
        var perUserWas = earlier?.PerUserWasOn ?? false;
        if (PerUserProfiles.DeviceKey(monitor) is { } deviceKey)
        {
            perUserWas = earlier?.PerUserWasOn ?? PerUserProfiles.IsOn(deviceKey) ?? false;
            log.Add(EnsurePerUser(deviceKey, display));
        }
        else
        {
            log.Add("Windows does not name this display's device, so 'Use my settings for this device' could not be turned on: apps may keep getting the system's profile for it.");
        }

        try
        {
            DisplayProfileAssociation.Add(display, fileName, setAsDefault: true);
            var now = DisplayProfileAssociation.GetDefault(display);
            log.Add(string.Equals(now, fileName, StringComparison.OrdinalIgnoreCase)
                ? $"Made it the default profile of {monitor.FriendlyName ?? monitor.DeviceName} for your user."
                : $"Associated it, but Windows reports '{now ?? "none"}' as the default.");
        }
        catch (Exception ex) when (Refused(ex))
        {
            log.Add($"Windows would not associate the profile with the display: {ex.Message}");
            if (ColorProfileStore.Uninstall(fileName))
            {
                log.Add($"Removed {fileName} from Windows' color profiles again.");
            }

            // No record is written, so nothing could undo the switch later: put it back now.
            if (!perUserWas && PerUserProfiles.DeviceKey(monitor) is { } key && PerUserProfiles.IsOn(key) == true)
            {
                try
                {
                    PerUserProfiles.Set(key, false);
                    log.Add("Turned 'Use my settings for this device' back off.");
                }
                catch (Win32Exception off)
                {
                    log.Add($"Could not turn 'Use my settings for this device' back off: {off.Message}");
                }
            }

            return null;
        }

        // The previous SoftCal profile goes only now that the new one is in place: had the association failed, the
        // previous calibration would still be whole.
        if (earlier is not null && !earlier.ProfileFile.Equals(fileName, StringComparison.OrdinalIgnoreCase))
        {
            TryRemove(display, earlier.ProfileFile, log, "the previous SoftCal profile");
        }

        // Windows' loader stays as it is. A calibration installed before SoftCal had its own loader turned it on; it goes
        // back as it was (it cannot load curves where the driver reports no support, and resets them at sign-in there).
        var switchWas = earlier?.LoaderSwitchWasOn ?? CalibrationLoader.IsSwitchOn() ?? false;
        var taskWas = earlier?.LoaderTaskWasEnabled ?? CalibrationLoader.IsTaskEnabled() ?? false;

        // Recorded as soon as Windows uses the profile: whatever fails after this, Remove can undo it.
        var record = new InstalledCalibration(displayKey, profileName, fileName, monitor.DeviceName, previousDefault, switchWas, taskWas, DateTimeOffset.Now, target, trialStamp, perUserWas, UsesSoftCalLoader: true);
        Save(record);

        if (earlier is { UsesSoftCalLoader: false } && NeedsWindowsLoaderBack(earlier))
        {
            log.Add(await RestoreWindowsLoaderAsync(earlier, cancellationToken).ConfigureAwait(false));
        }

        CalibrationLoaderProcess.AddToStartup();
        log.Add("Added SoftCal's loader to your startup apps (Task Manager › Startup apps): it loads the curves at every sign-in and puts them back whenever Windows resets them.");

        var loaded = new GdiGammaRampDevice(monitor.DeviceName).Write(ramp);
        log.Add(loaded ? "Loaded the curves in the graphics card now." : "The graphics driver refused the curves just now; SoftCal's loader keeps trying.");
        return record;
    }

    private static async Task<string> RestoreWindowsLoaderAsync(InstalledCalibration record, CancellationToken cancellationToken) =>
        await SetWindowsLoaderAsync(record.LoaderSwitchWasOn, record.LoaderTaskWasEnabled, cancellationToken).ConfigureAwait(false);

    /// <summary>Sets 'Use Windows display calibration' and the Calibration Loader task (administrator prompt).</summary>
    private static async Task<string> SetWindowsLoaderAsync(bool switchOn, bool taskEnabled, CancellationToken cancellationToken)
    {
        var response = await ElevatedTasks.RunAsync(
            ElevatedTaskKind.RestoreCalibrationLoader,
            new Dictionary<string, string>
            {
                ["switchOn"] = switchOn ? "true" : "false",
                ["taskEnabled"] = taskEnabled ? "true" : "false",
            },
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        return response.Success
            ? "Windows' calibration loader: " + response.Message
            : $"'Use Windows display calibration' was left as it is ({response.Message}).";
    }

    /// <summary>Whether any record file is there, readable or not (the loader stops only when none is).</summary>
    public static bool AnyRecordFiles() => Directory.Exists(RecordsFolder) && Directory.EnumerateFiles(RecordsFolder, "*.json").Any();

    /// <summary>Writes a record whole: the loader, which reads the records every few seconds, never sees half of one. A
    /// sharing violation (it is reading just then) is tried again.</summary>
    private static void Save(InstalledCalibration record)
    {
        Directory.CreateDirectory(RecordsFolder);
        var path = RecordPath(record.DisplayKey);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static VideoCardGamma? Vcgt(InstalledCalibration record)
    {
        var path = Path.Combine(ProfilesFolder, record.ProfileFile);
        return File.Exists(path) ? IccReader.Read(File.ReadAllBytes(path)).Vcgt : null;
    }

    /// <summary>Which profile Windows gives apps for the display: the user's association counts only while 'Use my
    /// settings for this device' is on.</summary>
    private static (bool? Used, string Message) AppsUse(InstalledCalibration record, DisplayMonitor monitor)
    {
        if (DisplayTarget.Of(monitor) is not { } display || PerUserProfiles.DeviceKey(monitor) is not { } deviceKey || PerUserProfiles.IsOn(deviceKey) is not { } perUser)
        {
            return (null, "Windows does not say which profile it gives apps for this display.");
        }

        var mine = DisplayProfileAssociation.GetDefault(display);
        if (perUser && string.Equals(mine, record.ProfileFile, StringComparison.OrdinalIgnoreCase))
        {
            return (true, "Windows gives apps SoftCal's profile for this display.");
        }

        var system = DisplayProfileAssociation.GetDefault(display, systemWide: true);
        return (false, perUser
            ? $"Windows gives apps {mine ?? "no profile"} for this display, not SoftCal's."
            : $"Windows gives apps the system's profile for this display ({system ?? "none"}), not SoftCal's: 'Use my settings for this device' is off.");
    }

    /// <summary>Turns on 'Use my settings for this device' for the display; returns what was done.</summary>
    private static string EnsurePerUser(string deviceKey, DisplayTarget display)
    {
        if (PerUserProfiles.IsOn(deviceKey) == true)
        {
            return "'Use my settings for this device' was already on for it.";
        }

        try
        {
            PerUserProfiles.Set(deviceKey, true);
            var system = DisplayProfileAssociation.GetDefault(display, systemWide: true);
            return $"Turned on 'Use my settings for this device' for it, so Windows gives apps your profile{(system is null ? string.Empty : $" instead of the system's {system}")}.";
        }
        catch (Win32Exception ex)
        {
            return $"Windows would not turn on 'Use my settings for this device' ({ex.Message}): apps may keep getting the system's profile.";
        }
    }

    private static void TryRemove(DisplayTarget display, string fileName, List<string> log, string what)
    {
        try
        {
            DisplayProfileAssociation.Remove(display, fileName);
            log.Add($"Removed {what} ({fileName}) from the display.");
        }
        catch (Exception ex) when (Refused(ex))
        {
            log.Add($"{what} was not associated any more ({ex.Message}).");
        }

        if (ColorProfileStore.Uninstall(fileName))
        {
            log.Add($"Removed {fileName} from Windows' color profiles.");
        }
    }

    /// <summary>What the profile calls throw when Windows refuses (an HRESULT maps to several exception types).</summary>
    private static bool Refused(Exception ex) =>
        ex is ExternalException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or NotImplementedException;

    private static string RecordPath(string displayKey) =>
        Path.Combine(RecordsFolder, string.Concat(displayKey.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_')) + ".json");
}
