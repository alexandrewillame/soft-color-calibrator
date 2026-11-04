using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using SoftCal.Core.Displays;
using SoftCal.Icc;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Gamma;

namespace SoftCal.App.Services;

/// <summary>
/// SoftCal's own calibration loader: a background copy of the app (<see cref="Verb"/>, no window) that keeps every
/// installed calibration's curves in the graphics card. It starts at sign-in from the current user's startup list
/// (Task Manager › Startup apps; no administrator), checks every two seconds and puts the curves back whenever Windows
/// or another program reset them, pauses while SoftCal measures or installs, and stops by itself once no calibration
/// is installed. Windows' own loader rounds the curves to 8 bits and only loads them at sign-in and when a profile
/// changes. It polls rather than listening for display events: listening needs a window, and a window on a
/// thread that is not pumping messages stalls Windows' broadcasts.
/// </summary>
internal static class CalibrationLoaderProcess
{
    public const string Verb = "--load-calibrations";

    public const string StopVerb = "--stop-loader";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "SoftColorCalibrator.Loader";
    private const string RunningName = @"Local\SoftColorCalibrator.Loader.Running";
    private const string StopName = @"Local\SoftColorCalibrator.Loader.Stop";
    private const string BusyName = @"Local\SoftColorCalibrator.Curves.Busy";
    private const long MaxLogBytes = 512 * 1024;

    private static readonly TimeSpan Period = TimeSpan.FromSeconds(2);

    /// <summary>How often the displays are looked up again (they can be renumbered when one is plugged in).</summary>
    private static readonly TimeSpan Remap = TimeSpan.FromSeconds(30);

    public static string LogPath => Path.Combine(AppPaths.Logs, "loader.log");

    public static bool IsRunning => Exists(RunningName);

    /// <summary>The startup list's command for the loader; null when it is not there.</summary>
    public static string? StartupCommand
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) as string;
        }
    }

    /// <summary>The startup list starts this copy of the app's loader.</summary>
    public static bool StartsThisApp => string.Equals(StartupCommand, Command, StringComparison.OrdinalIgnoreCase);

    private static string Command => $"\"{Environment.ProcessPath}\" {Verb}";

    public static void AddToStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(RunValue, Command);
    }

    public static void RemoveFromStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>Starts the loader unless it runs already; true once it runs.</summary>
    public static bool Start()
    {
        if (IsRunning)
        {
            return true;
        }

        using (Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running executable."), Verb) { UseShellExecute = false }))
        {
        }

        return WaitFor(() => IsRunning, TimeSpan.FromSeconds(5));
    }

    /// <summary>Asks a running loader to stop; true once none runs.</summary>
    public static bool Stop(TimeSpan timeout)
    {
        if (EventWaitHandle.TryOpenExisting(StopName, out var stop))
        {
            using (stop)
            {
                stop.Set();
            }
        }

        return WaitFor(() => !IsRunning, timeout);
    }

    /// <summary>At app start: starts the loader when a calibration is installed and the startup list names this copy
    /// of the app (a rebuild stops it; a development build never starts one).</summary>
    public static void EnsureRunning()
    {
        try
        {
            if (!IsRunning && StartsThisApp && CalibrationInstaller.All().Count > 0)
            {
                Start();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppPaths.WriteErrorLog(ex);
        }
    }

    /// <summary>Held while SoftCal measures or installs: the loader leaves the curves alone meanwhile (a measurement
    /// sets them to linear or loads trial curves on purpose).</summary>
    public static IDisposable HoldCurves() => new EventWaitHandle(false, EventResetMode.ManualReset, BusyName);

    /// <summary>The loader itself (the <see cref="Verb"/> process).</summary>
    public static int Run()
    {
        using var running = new EventWaitHandle(false, EventResetMode.ManualReset, RunningName, out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName);
        var keeper = new RampKeeper(name => new GdiGammaRampDevice(name));
        var targets = new Targets();
        var paused = false;
        Log($"Started ({Environment.ProcessPath}).");
        while (true)
        {
            try
            {
                if (targets.Current() is not { } current)
                {
                    if (CalibrationInstaller.AnyRecordFiles())
                    {
                        // A record being replaced, or unreadable for a moment: looked at again shortly.
                        if (stop.WaitOne(TimeSpan.FromSeconds(2)))
                        {
                            Log("Stopped.");
                            return 0;
                        }

                        continue;
                    }

                    Log("No SoftCal calibration is installed any more: stopping.");
                    return 0;
                }

                if (Exists(BusyName))
                {
                    if (!paused)
                    {
                        Log("SoftCal is measuring or installing: paused.");
                        paused = true;
                    }
                }
                else
                {
                    if (paused)
                    {
                        Log("Resumed.");
                        paused = false;
                    }

                    foreach (var e in keeper.Check(current))
                    {
                        Log(e.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or IccFormatException or ArgumentException)
            {
                Log("A check failed: " + ex.Message);
            }

            if (stop.WaitOne(Period))
            {
                Log("Stopped.");
                return 0;
            }
        }
    }

    private static bool Exists(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var handle))
        {
            return false;
        }

        handle.Dispose();
        return true;
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return true;
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
            {
                File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.log"), overwrite: true);
            }

            File.AppendAllText(LogPath, string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is for diagnosis only; the curves matter more.
        }
    }

    /// <summary>The installed calibrations as ramps for the displays connected now.</summary>
    private sealed class Targets
    {
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private IReadOnlyList<KeptRamp> _list = [];
        private string _signature = string.Empty;
        private DateTime _mapped;

        /// <summary>Null once no calibration is installed.</summary>
        public IReadOnlyList<KeptRamp>? Current()
        {
            var records = CalibrationInstaller.All();
            if (records.Count == 0)
            {
                return null;
            }

            var signature = string.Join("|", records.Select(r => r.DisplayKey + "=" + r.ProfileFile));
            if (signature == _signature && DateTime.UtcNow - _mapped < Remap)
            {
                return _list;
            }

            var monitors = DisplayEnumerator.List();
            var list = new List<KeptRamp>();
            foreach (var record in records)
            {
                var monitor = monitors.FirstOrDefault(m => string.Equals(KeyOf(m), record.DisplayKey, StringComparison.Ordinal))
                    ?? monitors.FirstOrDefault(m => string.Equals(m.DeviceName, record.DisplayKey, StringComparison.OrdinalIgnoreCase));
                if (monitor is null)
                {
                    Once($"{record.ProfileFile}: its display is not connected; the curves load when it is.");
                }
                else if (CalibrationInstaller.Ramp(record) is not { } ramp)
                {
                    Once($"{record.ProfileFile}: SoftCal's copy of the profile is missing, so its curves cannot be loaded.");
                }
                else
                {
                    list.Add(new KeptRamp(monitor.DeviceName, ramp, record.ProfileFile));
                }
            }

            (_list, _signature, _mapped) = (list, signature, DateTime.UtcNow);
            return list;
        }

        private static string? KeyOf(DisplayMonitor monitor)
        {
            try
            {
                return monitor.Edid is { } bytes ? DisplayKey.FromEdid(Edid.Parse(bytes)).Value : null;
            }
            catch (EdidFormatException)
            {
                return null;
            }
        }

        private void Once(string message)
        {
            if (_reported.Add(message))
            {
                Log(message);
            }
        }
    }
}
