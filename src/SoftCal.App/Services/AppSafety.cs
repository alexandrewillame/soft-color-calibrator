using System.IO;
using SoftCal.Instruments.Webcam.Controls;
using SoftCal.Windows.AdvancedColor;
using SoftCal.Windows.Ddc;
using SoftCal.Windows.Gamma;
using SoftCal.Windows.Safety;

namespace SoftCal.App.Services;

/// <summary>The app's journal and the restorers for every kind of state it changes.</summary>
internal static class AppSafety
{
    public static StateJournal Journal { get; } = new(StateJournal.DefaultDirectory);

    public static IReadOnlyList<IStateRestorer> Restorers { get; } =
    [
        new GammaRampRestorer(),
        new CameraControlsRestorer(),
        new AdvancedColorRestorer(),
        new DdcValueRestorer(),
    ];

    public static string GuardianLog => Path.Combine(Journal.DirectoryPath, "guardian.log");

    public static IReadOnlyList<RecoveryResult> RecoverDeadRuns() => StateRecovery.Recover(Journal, Restorers);

    /// <summary>Before a run: whatever this process could not put back earlier (a restore that did not read back) and
    /// whatever a dead run left is put back now, so the run starts from the true originals. Another SoftCal that is
    /// still running keeps its own entries.</summary>
    public static IReadOnlyList<RecoveryResult> RecoverBeforeRun() => StateRecovery.Recover(
        Journal,
        Restorers,
        includeLiveOwners: true,
        filter: e => e.Owner == ProcessIdentity.Current || !e.Owner.IsAlive());
}

/// <summary>Where the app keeps its files: %LOCALAPPDATA%\SoftColorCalibrator (never the OneDrive-synced profile folders).</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoftColorCalibrator");

    public static string Reports => Path.Combine(Root, "reports");

    public static string Monitors => Path.Combine(Root, "monitors");

    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>
    /// The packaged app whose storage this process's AppData writes are redirected to, or null when they reach the
    /// real folder. Windows does this, silently, to processes started from inside an MSIX app such as Claude's desktop
    /// app (even without its package identity): their registry and AppData changes stay inside that app, so a
    /// calibration installed from there is invisible to Windows. Found by writing a probe file and looking for it in
    /// each package's LocalCache.
    /// </summary>
    public static string? RedirectedTo()
    {
        var name = $"redirect-probe-{Guid.NewGuid():N}.tmp";
        var probe = Path.Combine(Root, name);
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(probe, string.Empty);
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            return Directory.Exists(packages)
                ? Directory.EnumerateDirectories(packages)
                    .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "LocalCache", "Local", "SoftColorCalibrator", name))) is { } package
                    ? Path.GetFileName(package)
                    : null
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover empty probe file does no harm.
            }
        }
    }

    /// <summary>Writes an error with its full stack trace to logs\error-…txt and returns the file path.</summary>
    public static string WriteErrorLog(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Directory.CreateDirectory(Logs);
        var path = Path.Combine(Logs, $"error-{DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.txt");
        File.WriteAllText(path, exception.ToString());
        return path;
    }
}
