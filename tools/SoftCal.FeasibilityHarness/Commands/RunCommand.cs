using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.FeasibilityHarness.Infrastructure;
using SoftCal.FeasibilityHarness.Steps;
using SoftCal.Windows.Gamma;
using SoftCal.Windows.NightLight;

namespace SoftCal.FeasibilityHarness.Commands;

/// <summary>
/// Runs measurement steps with the camera locked, recording everything. The lock (with its read-back report) always
/// comes first and the camera is always restored at the end.
/// </summary>
internal static class RunCommand
{
    public static readonly string[] FullSequence = ["trust", "sync", "flicker", "defocus", "null", "shapes", "tree", "ramp"];

    private static readonly Dictionary<string, Func<StepContext, Task>> Steps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trust"] = CameraSteps.TrustAsync,
        ["sync"] = CameraSteps.SyncAsync,
        ["flicker"] = CameraSteps.FlickerAsync,
        ["defocus"] = CameraSteps.DefocusAsync,
        ["null"] = NullSteps.NullAsync,
        ["shapes"] = NullSteps.ShapesAsync,
        ["tree"] = NullSteps.TreeAsync,
        ["ramp"] = BracketSteps.RampAsync,
        ["move"] = NullSteps.MoveAsync,
        ["hold"] = CameraSteps.HoldAsync,
        ["dark"] = BracketSteps.DarkAsync,
        ["gamma"] = GammaStep.RunAsync,
    };

    public static async Task<int> RunAsync(Arguments args, string commandName, IReadOnlyList<string> steps, CancellationToken cancellationToken)
    {
        var simulated = args.Has("simulated");
        if (!simulated && !args.Has("allow-camera"))
        {
            Ui.Error("This run changes the camera's controls (restored at the end). Re-run with --allow-camera to confirm.");
            return 2;
        }

        if (steps.Contains("gamma", StringComparer.OrdinalIgnoreCase) && !simulated && !args.Has("allow-gamma"))
        {
            Ui.Error("The gamma step writes a test ramp to the display (journaled and restored). Re-run with --allow-gamma to confirm.");
            return 2;
        }

        var unknown = steps.Where(s => !Steps.ContainsKey(s)).ToList();
        if (unknown.Count > 0)
        {
            Ui.Error("Unknown step(s): " + string.Join(", ", unknown) + ". Known: " + string.Join(", ", Steps.Keys));
            return 2;
        }

        foreach (var line in Safety.RecoverDeadRuns())
        {
            Ui.Warn("recovered from an earlier run: " + line);
        }

        NightLightReport? nightLight = null;
        if (!simulated)
        {
            nightLight = NightLightReader.Read();
            Ui.Info(nightLight.Summary);
            if (nightLight.Active != false && !args.Has("ignore-night-light"))
            {
                Ui.Error("Turn Night light off in Settings (ms-settings:nightlight) before measuring, or pass --ignore-night-light.");
                return 3;
            }
        }

        await using var rig = HarnessRig.Open(args);
        using var session = HarnessSession.Create(
            args.Get("sessions") ?? HarnessSession.DefaultRoot,
            commandName,
            rig.MonitorLabel,
            "M1 " + string.Join(" ", Environment.GetCommandLineArgs().Skip(1)),
            rig.Clock,
            rig.DisplayKey,
            rig.DisplayName,
            rig.Instrument.Info,
            rig.Instrument.Capabilities);
        Ui.Info($"session: {session.DirectoryPath}");
        RecordContext(session, rig, nightLight);

        await using var measure = new MeasurementRig(rig.Instrument, rig.Presenter, new RoiSpec(), session);
        var context = new StepContext { Session = session, Rig = rig, Measure = measure, Args = args, Token = cancellationToken };
        var records = new List<StepRecord>();
        double lockedAt;
        try
        {
            await CameraSteps.LockAsync(context);
            lockedAt = session.Now;
        }
        catch (Exception ex) when (ex is MeasurementException or InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            Ui.Error("lock failed: " + ex.Message);
            session.Result("lock", new { failed = true, error = ex.ToString() });
            return 4;
        }

        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = session.Now;
            var faultsBefore = rig.Faults.Count;
            string? error = null;
            try
            {
                await Steps[step](context);
            }
            catch (Exception ex) when (ex is MeasurementException or InvalidOperationException or TimeoutException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                error = ex.Message;
                Ui.Error($"{step} failed: {ex.Message}");
                session.Note($"{step} failed: {ex}");
            }

            var record = new StepRecord(step, start, session.Now, rig.Faults.Count - faultsBefore, error);
            records.Add(record);
            session.Record("step." + step, record);
        }

        // The lock must have held for the whole run: watcher events plus a final read-back.
        var check = rig.Webcam?.CheckLock() ?? [];
        var mismatched = check.Where(x => x.Actual is not { } a || a.Value != x.Expected.Value || a.IsAuto != x.Expected.IsAuto).Select(x => x.Control.ToString()).ToList();
        var external = rig.Webcam?.ExternalChanges ?? [];
        var minutes = (session.Now - lockedAt) / 60;
        var held = mismatched.Count == 0 && external.Count == 0;
        session.Result("lockwatch", new
        {
            minutes,
            held,
            finalMismatches = mismatched,
            externalChanges = external,
            faults = rig.Faults,
            steps = records,
            framesRecorded = session.FramesRecorded,
        });
        (held ? (Action<FormattableString>)Ui.Good : Ui.Warn)($"lock watch: {(held ? "held" : "CHANGED")} for {minutes:0.0} min ({rig.Faults.Count} instrument faults)");
        return records.Any(r => r.Error is not null) ? 1 : 0;
    }

    private static void RecordContext(HarnessSession session, HarnessRig rig, NightLightReport? nightLight)
    {
        if (rig.Monitor is { } m)
        {
            session.Record("display", new
            {
                m.DeviceName,
                m.FriendlyName,
                m.Bounds,
                m.DpiX,
                m.OutputTechnology,
                m.RefreshRateHz,
                m.AdvancedColor,
                rig.DisplayKey,
                window = rig.Window is { } w ? new { w.Width, w.Height, w.RefreshPeriodSeconds, w.AdapterName } : null,
            });
            try
            {
                var ramp = new GdiGammaRampDevice(m.DeviceName).Read();
                session.Record("gammaRamp", new { identity = ramp.IsIdentity, maxDeviation = ramp.MaxDeviationFromIdentity() });
                if (!ramp.IsIdentity)
                {
                    Ui.Warn($"the GPU gamma ramp of {m.DeviceName} is not identity (max deviation {ramp.MaxDeviationFromIdentity()}/65535): measurements include it");
                }
            }
            catch (InvalidOperationException ex)
            {
                session.Note("gamma ramp unreadable: " + ex.Message);
            }
        }

        if (nightLight is not null)
        {
            session.Record("nightLight", new { nightLight.Active, nightLight.ColorTemperatureKelvin, nightLight.ScheduleEnabled, nightLight.Summary });
        }

        if (rig.Webcam is { } cam)
        {
            session.Record("camera", new { cam.Device.FriendlyName, cam.Device.StableId, cam.Format, cam.NativeFormats, cam.StudioEffectsAvailable, cam.Snapshot });
        }
    }
}
