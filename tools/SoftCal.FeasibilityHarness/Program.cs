using System.Globalization;
using SoftCal.FeasibilityHarness.Commands;
using SoftCal.FeasibilityHarness.Infrastructure;
using SoftCal.Windows.Elevation;

// The elevated helper runs before anything else: it only executes allowlisted tasks and exits.
if (ElevatedTasks.TryRunFromCommandLine(args) is { } elevatedExitCode)
{
    return elevatedExitCode;
}

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

var parsed = Arguments.Parse(args);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C: stop the measurement and restore everything; a second one kills the process (the guardian and
    // the next launch still restore the journal).
    if (!cancel.IsCancellationRequested)
    {
        e.Cancel = true;
        Ui.Warn("Stopping: restoring the camera and display…");
        cancel.Cancel();
    }
};

try
{
    return parsed.Command.ToUpperInvariant() switch
    {
        "LIST" => InfoCommands.List(),
        "PROBE" => await InfoCommands.ProbeAsync(parsed, cancel.Token),
        "NIGHTLIGHT" => await InfoCommands.NightLightAsync(parsed, cancel.Token),
        "WINDOW" => await InfoCommands.WindowAsync(parsed, cancel.Token),
        "LOCK" => await RunCommand.RunAsync(parsed, "lock", [], cancel.Token),
        "RUN" => await RunCommand.RunAsync(parsed, "run", Steps(parsed), cancel.Token),
        "FULL" => await RunCommand.RunAsync(parsed, "full", RunCommand.FullSequence, cancel.Token),
        "GAMMA" => await RunCommand.RunAsync(parsed, "gamma", ["gamma"], cancel.Token),
        "REPORT" => ReportCommand.Run(parsed),
        "REANALYZE" => SoftCal.FeasibilityHarness.Steps.BracketSteps.Reanalyze(parsed.Positional[0], parsed.Get("anchors") ?? "hlines"),
        "RECOVER" => Recover(),
        "GUARDIAN" => Safety.RunGuardian(args),
        "STATE" => SafetyCommands.State(),
        "CAMERA" => CameraCommands.Run(parsed),
        "SAFETY" => await SafetyCommands.SafetyCheckAsync(parsed, cancel.Token),
        "ELEVATE" => await SafetyCommands.ElevatePingAsync(cancel.Token),
        _ => Help(),
    };
}
catch (OperationCanceledException)
{
    Ui.Warn("Cancelled; everything that was changed has been restored.");
    return 130;
}

static IReadOnlyList<string> Steps(Arguments a) =>
    a.Get("steps") is { } s ? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [.. a.Positional];

static int Recover()
{
    var lines = Safety.RecoverDeadRuns();
    Ui.Info(lines.Count == 0 ? "Nothing to restore." : string.Join(Environment.NewLine, lines));
    return 0;
}

static int Help()
{
    Ui.Info("""
        SoftCal M1 feasibility harness. Every run is recorded under %LOCALAPPDATA%\SoftColorCalibrator\sessions\m1.

          list                                  cameras and monitors (read-only)
          probe                                 Night light, link/NVAPI, DDC/CI VCP and gamma-ramp read (read-only)
          nightlight [--wait --minutes 5]       Night light state (read-only); --wait polls until it is off
          window --monitor N                    patch window pixel-exactness test (screen read-back)
          lock   --monitor N --allow-camera     lock + read-back report, then restore
          run    --monitor N --allow-camera <steps…>
                 steps: trust sync flicker defocus null shapes tree ramp move hold dark gamma
                 options: --runs 3 --repeats 3 --levels 8 --phase base|plus|minus --minutes 30 --focus F
          full   --monitor N --allow-camera     trust sync flicker defocus null shapes tree ramp
          gamma  --monitor N --allow-camera --allow-gamma   gamma ramp write + camera check (always restored)
          report [--out file.md]                gate numbers from every recorded session (default: next to the sessions)
          reanalyze <session-folder> [--anchors hlines|checker|vlines|none]   re-fit recorded ramps
          recover                               restore anything a crashed run left journaled
          camera [--auto] [--watch]             camera controls (read-only); --auto resets them to automatic/defaults;
                                                --watch then streams 5 s (nothing changed) to show the automatic ones move;
                                                --wb-test: the picture's colour at 2800 K, 6500 K and automatic, then a reset;
                                                --lock-test: a measurement's lock for 2 s, then its restore
          state                                 pre-flight: Night light, filters, HDR/ACM, link range, power (read-only)
          safety --monitor N [--allow-gamma] [--allow-acm] [--allow-ddc] [--crash]
                                                change each allowed kind of state under the journal, then restore it;
                                                with --crash the process kills itself instead and the guardian restores
          elevate                               UAC round trip of the elevated helper (ping)
          --simulated                           use the simulated rig instead of the camera and a monitor
        """);
    return 0;
}
