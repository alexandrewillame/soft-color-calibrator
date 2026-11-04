using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace SoftCal.Windows.Safety;

/// <summary>
/// The guardian: a second copy of the executable, started with <c>guardian --parent PID --parent-start TICKS</c>, that
/// waits for its parent to exit and then restores whatever the parent left in the journal. It survives the parent
/// being killed (it is not in a kill-on-close job), so even a hard crash puts the camera, displays and Windows settings
/// back. Every executable that changes state must route the <see cref="Verb"/> argument to <see cref="Run"/>.
/// </summary>
public static class Guardian
{
    public const string Verb = "guardian";

    private static readonly Lock Gate = new();
    private static Process? _running;

    /// <summary>Starts one guardian for this process (no-op if it is already running).</summary>
    /// <param name="extraArguments">Appended after the parent arguments, e.g. a non-default journal directory.</param>
    public static void EnsureStarted(IEnumerable<string>? extraArguments = null)
    {
        lock (Gate)
        {
            if (_running is { HasExited: false })
            {
                return;
            }

            _running?.Dispose();
            _running = Process.Start(CreateStartInfo(ProcessIdentity.Current, extraArguments))
                ?? throw new InvalidOperationException("The guardian process could not be started.");
        }
    }

    /// <summary>The command line that starts a guardian for <paramref name="parent"/> from this executable.</summary>
    public static ProcessStartInfo CreateStartInfo(ProcessIdentity parent, IEnumerable<string>? extraArguments = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Started as "dotnet app.dll": the guardian needs the same entry assembly.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("Cannot locate the entry assembly."));
        }

        start.ArgumentList.Add(Verb);
        start.ArgumentList.Add("--parent");
        start.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
        if (parent.StartTimeUtc is { } startTime)
        {
            start.ArgumentList.Add("--parent-start");
            start.ArgumentList.Add(startTime.UtcTicks.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var argument in extraArguments ?? [])
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    /// <summary>Reads <c>--parent PID [--parent-start TICKS]</c> from a guardian command line.</summary>
    public static ProcessIdentity ParseParent(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        int? id = null;
        DateTimeOffset? start = null;
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i] == "--parent")
            {
                id = int.Parse(arguments[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
            else if (arguments[i] == "--parent-start")
            {
                start = new DateTimeOffset(long.Parse(arguments[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture), TimeSpan.Zero);
            }
        }

        return new ProcessIdentity(id ?? throw new ArgumentException("A guardian needs --parent PID.", nameof(arguments)), start);
    }

    /// <summary>Waits for the parent to exit, then restores every entry left by dead runs. Returns what was done.</summary>
    public static IReadOnlyList<RecoveryResult> Run(
        ProcessIdentity parent,
        StateJournal journal,
        IEnumerable<IStateRestorer> restorers,
        string? logPath = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(journal);
        WaitForExit(parent);

        // Let the parent's handles (camera, physical monitors) close before touching the devices.
        Thread.Sleep(300);
        var results = StateRecovery.Recover(journal, restorers);
        if (logPath is not null && results.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllLines(logPath, results.Select(r => string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:O} parent {parent.Id}: {r}")));
        }

        return results;
    }

    private static void WaitForExit(ProcessIdentity parent)
    {
        while (parent.IsAlive())
        {
            try
            {
                using var process = Process.GetProcessById(parent.Id);
                process.WaitForExit();
                return;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return;
            }
        }
    }
}
