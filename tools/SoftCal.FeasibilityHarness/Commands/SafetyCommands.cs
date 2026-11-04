using System.Diagnostics;
using SoftCal.FeasibilityHarness.Infrastructure;
using SoftCal.Windows.AdvancedColor;
using SoftCal.Windows.Ddc;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Elevation;
using SoftCal.Windows.Gamma;
using SoftCal.Windows.OsState;

namespace SoftCal.FeasibilityHarness.Commands;

/// <summary>M2 checks: the pre-flight state, the journal + guardian on real state, and the elevated helper.</summary>
internal static class SafetyCommands
{
    private const byte Brightness = 0x10;

    /// <summary>Everything Windows and the GPU do to displayed colors, and what to fix before measuring. Read-only.</summary>
    public static int State()
    {
        var state = WindowsColorStateProbe.Read();
        Ui.Title("Pre-flight: what Windows and the GPU do to displayed colors (read-only)");
        Ui.Info(state.NightLight.Summary);
        Ui.Info($"color filters: {(state.ColorFilters.Active ? state.ColorFilters.FilterName ?? "on" : "off")}");
        Ui.Info($"power: on battery {state.Power.OnBattery}, energy saver {state.Power.EnergySaverOn}, automatic brightness plugged in {state.Power.AdaptiveBrightnessOnAc} / on battery {state.Power.AdaptiveBrightnessOnBattery}");
        Ui.Info($"Windows calibration loader enabled: {state.CalibrationLoaderEnabled}");
        foreach (var d in state.Displays)
        {
            var switches = d.AdvancedColor is { } a ? AdvancedColorSwitches.From(a) : null;
            Ui.Info($"{d.FriendlyName ?? d.DeviceName} ({d.DeviceName}): HDR {switches?.Hdr}, ACM {switches?.AutoColorManagement}, mode {d.AdvancedColor?.ActiveColorMode}; gamma ramp identity {d.GammaRampIsIdentity}; link {d.LinkDescription ?? "unknown"}");
        }

        Ui.Title("Findings");
        foreach (var line in WindowsColorStateProbe.Describe(state.Findings))
        {
            Ui.Info(line);
        }

        if (state.ReadyToMeasure)
        {
            Ui.Good($"ready to measure");
        }
        else
        {
            Ui.Warn($"not ready to measure: fix the blockers above");
        }

        return state.ReadyToMeasure ? 0 : 1;
    }

    /// <summary>
    /// Changes each allowed kind of state under the journal: a ×0.9 gamma ramp, the Auto Color Management switch, and the
    /// DDC/CI brightness by one step. Without --crash it restores them and checks the journal is empty; with --crash it
    /// kills itself, and the guardian must restore everything (check with 'state' and 'probe' afterwards).
    /// </summary>
    public static async Task<int> SafetyCheckAsync(Arguments args, CancellationToken cancellationToken)
    {
        var monitors = DisplayEnumerator.List();
        var monitor = DisplayEnumerator.Find(monitors, args.Get("monitor") ?? throw new ArgumentException("Choose a monitor with --monitor N."));
        var crash = args.Has("crash");
        foreach (var line in Safety.RecoverDeadRuns())
        {
            Ui.Warn($"recovered from an earlier run: {line}");
        }

        Safety.EnsureGuardian();
        Ui.Title($"Safety check on {monitor}");
        var undo = new List<Func<Task<string>>>();
        var disposables = new List<IDisposable>();
        try
        {
            if (args.Has("allow-gamma"))
            {
                var guard = GammaRampGuard.Begin(new GdiGammaRampDevice(monitor.DeviceName), Safety.Journal);
                var applied = guard.Apply(GammaRamp.Scaled(0.9));
                Ui.Info($"gamma ramp ×0.9 applied: {applied}");
                undo.Add(() => Task.FromResult($"gamma ramp restored and read back: {guard.Restore()}"));
            }

            if (args.Has("allow-acm"))
            {
                var device = DisplayConfigAdvancedColorDevice.For(monitor) ?? throw new InvalidOperationException("No DisplayConfig path for this monitor.");
                var guard = AdvancedColorGuard.Begin(device, Safety.Journal);
                var target = guard.Original with { AutoColorManagement = !guard.Original.AutoColorManagement };
                Ui.Info($"switching Auto Color Management {(target.AutoColorManagement ? "on" : "off")} (was {(guard.Original.AutoColorManagement ? "on" : "off")})…");
                Ui.Info($"switches now: {guard.Apply(target)}");
                undo.Add(() => Task.FromResult($"HDR/ACM: {guard.Restore()}"));
            }

            if (args.Has("allow-ddc"))
            {
                var channel = DdcCiChannel.Open(monitor).FirstOrDefault() ?? throw new InvalidOperationException("DDC/CI is not available on this monitor.");
                disposables.Add(channel);
                var guard = new DdcValueGuard(channel, Safety.Journal);
                var current = await channel.ReadAsync(Brightness, cancellationToken) ?? throw new InvalidOperationException("The monitor does not report its brightness over DDC/CI.");
                var target = current.Current > 0 ? current.Current - 1 : current.Current + 1;
                Ui.Info($"DDC/CI brightness {current.Current} → {target}: {await guard.SetAsync(Brightness, target, cancellationToken)}");
                undo.Add(async () => "DDC/CI: " + string.Join("; ", await guard.RestoreAsync(CancellationToken.None)));
            }

            Ui.Info($"journal now holds: {string.Join(", ", Safety.Journal.Pending().Select(e => e.Kind))}");
            if (crash)
            {
                Ui.Warn($"Killing this process on purpose: the guardian must restore everything. Check with 'state' or 'probe'; its log is {Safety.GuardianLog}.");
                using var self = Process.GetCurrentProcess();
                self.Kill();
            }
        }
        finally
        {
            if (!crash)
            {
                for (var i = undo.Count - 1; i >= 0; i--)
                {
                    Ui.Info(await undo[i]());
                }

                foreach (var d in disposables)
                {
                    d.Dispose();
                }
            }
        }

        var left = Safety.Journal.Pending().Where(e => e.OwnerProcessId == Environment.ProcessId).ToList();
        if (left.Count == 0)
        {
            Ui.Good($"everything restored; the journal holds nothing from this run");
        }
        else
        {
            Ui.Warn($"{left.Count} journal entries left: {string.Join(", ", left.Select(e => e.Kind + " " + e.Key))}");
        }

        return left.Count == 0 ? 0 : 1;
    }

    /// <summary>Starts the elevated helper (one UAC prompt) and checks it really ran elevated.</summary>
    public static async Task<int> ElevatePingAsync(CancellationToken cancellationToken)
    {
        Ui.Info("Windows will ask for administrator rights for a harmless ping…");
        var response = await ElevatedTasks.RunAsync(ElevatedTaskKind.Ping, null, TimeSpan.FromMinutes(2), cancellationToken);
        if (response.Success && response.RanElevated)
        {
            Ui.Good($"{response.Message}");
            return 0;
        }

        Ui.Warn($"{response.Message} (success {response.Success}, elevated {response.RanElevated})");
        return 1;
    }
}
