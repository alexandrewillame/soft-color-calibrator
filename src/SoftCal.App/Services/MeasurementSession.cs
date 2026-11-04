using System.Globalization;
using System.IO;
using SoftCal.Core.Advisor;
using SoftCal.Core.Analysis;
using SoftCal.Core.Calibration;
using SoftCal.Core.Color;
using SoftCal.Core.Displays;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.Instruments.Phone;
using SoftCal.Instruments.Simulated;
using SoftCal.Instruments.Webcam;
using SoftCal.Instruments.Webcam.Controls;
using SoftCal.Remote;
using SoftCal.Windows.AdvancedColor;
using SoftCal.Windows.Ddc;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Gamma;
using SoftCal.Windows.OsState;
using SoftCal.Windows.Patches;
using SoftCal.Windows.Power;
using SoftCal.Windows.Safety;

namespace SoftCal.App.Services;

/// <summary>What to measure and with what.</summary>
/// <param name="Profile">The SoftCal profile being measured (its monitor settings and calibration), by name: reports and
/// calibrations are kept per profile.</param>
/// <param name="KeepCurves">Measure with the calibration curves in the graphics card left as they are (verifying an
/// installed calibration); otherwise they are set to linear for the measurement and put back afterwards.</param>
/// <param name="Reference">The screen whose white the monitor's is matched to (null on the simulator, or none).</param>
/// <param name="ReferenceName">That screen's name as the user knows it; null = the white is not matched.</param>
/// <param name="Screens">The window's side of a white match on real screens (moving SoftCal's window, the camera's
/// view); null runs it without them.</param>
/// <param name="Alerts">Where to say, at once and visibly, that a reading is taken again because something covered the
/// measured patch (it also goes to the log).</param>
/// <param name="Phone">The phone to measure with, connected in step 2 (its link stays open after the run); null measures
/// with <paramref name="Camera"/>.</param>
internal sealed record MeasurementRequest(
    DisplayMonitor? Monitor,
    WebcamDevice? Camera,
    bool Simulated,
    bool AngleSensitivePanel,
    string? Profile = null,
    bool KeepCurves = false,
    DisplayMonitor? Reference = null,
    string? ReferenceName = null,
    IScreenHost? Screens = null,
    IProgress<string>? Alerts = null,
    PhoneClient? Phone = null)
{
    /// <summary>The camera measured with, the same from run to run: a webcam's stable id, or the phone's make and model.</summary>
    public string? CameraId => Phone is { } phone
        ? string.Create(CultureInfo.InvariantCulture, $"phone:{phone.Phone.Manufacturer}:{phone.Phone.Model}")
        : Camera?.StableId;

    /// <summary>The camera as the user knows it.</summary>
    public string? CameraName => Phone is { } phone ? PhoneInstrument.Name(phone.Phone) : Camera?.FriendlyName;

    /// <summary>The kind of camera measured with: "simulator", "phone camera" or "USB webcam".</summary>
    public string CameraKind => Simulated ? "simulator" : Phone is not null ? "phone camera" : "USB webcam";
}

/// <summary>The raw readings and the report made from them.</summary>
internal sealed record MeasurementOutcome(DisplayMeasurement Measurement, DisplayReport Report);

/// <summary>How an advisor run ended: its steps, the settings it ended on, whether it finished, what it could not fix,
/// what it learned about Brightness, its last readings, and whether the brightness followed the picture.</summary>
internal sealed record AdvisorOutcome(
    IReadOnlyList<AdvisorStep> Steps,
    IReadOnlyDictionary<OsdControlId, int> Settings,
    bool Finished,
    string? DdcNote,
    IReadOnlyList<string>? Notes = null,
    bool? BrightnessSetsBacklight = null,
    AdvisorReadings? FinalReadings = null,
    bool EnhancementsDetected = false,
    WhiteMatchResult? White = null);

/// <summary>A stretch of a measurement, for the progress bar: what is being measured and about how long it takes.</summary>
internal sealed record MeasurePhase(string Name, double Seconds);

/// <summary>A calibration run: the display before (no curves) and after (with the final curves), and the curves.</summary>
internal sealed record CalibrationRun(DisplayMeasurement Before, DisplayMeasurement After, CalibrationCurves Curves, DisplayReport BeforeReport, DisplayReport AfterReport);

/// <summary>A measurement that stopped for a reason the user can act on (Night light switched on, the lock failed…).</summary>
internal sealed class MeasurementStoppedException(string message) : Exception(message);

/// <summary>
/// Runs measurements with every safety guard: the guardian is armed, the display is kept awake, HDR/ACM are switched
/// off and a loaded calibration curve set to linear for the measurement (both journaled and restored), the camera's
/// controls are journaled before the lock and restored after, and Night light switching on stops the run. The Quick
/// plan and the OSD advisor both run inside it; the advisor keeps it open while the user changes the monitor's menu.
/// </summary>
internal static class MeasurementSession
{
    /// <summary>Everything a measurement needs once the guards are up. <see cref="LoadCurves"/> puts calibration curves
    /// in the GPU for the rest of the session (journaled; the original ramp is put back when it ends); false when the
    /// driver rejected them.</summary>
    private sealed record Scope(MeasurementRig Rig, string InstrumentModel, SimulatedDisplay? Simulator, IProgress<string> Progress, Func<CalibrationCurves, bool> LoadCurves, IWhiteMatchStage? White = null, IProgress<string>? Alerts = null)
    {
        /// <summary>In the log, and where the user looks (the measure step's alert line).</summary>
        public void Say(string line)
        {
            Progress.Report(line);
            Alerts?.Report(line);
        }
    }

    /// <summary>
    /// Creates the calibration curves (plan §7): solves them from <paramref name="before"/> (measured with no curves
    /// loaded; measured here first when null), loads them in the GPU for a trial, measures with them, and with
    /// <paramref name="refine"/> solves, loads and measures once more. The original GPU ramp is back when it returns;
    /// installing is a separate, explicit step.
    /// </summary>
    public static Task<CalibrationRun> CalibrateAsync(
        MeasurementRequest request,
        DisplayMeasurement? before,
        CalibrationTarget target,
        bool refine,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);
        return RunGuardedAsync(
            request,
            progress,
            new SimulatedDisplayOptions { Gamma = new Vec3(2.4, 2.3, 2.6), CrushCode = 3, StepUnevenness = 0.2, ChannelInteraction = new Vec3(0.05, 0.05, 0.05) },
            async (scope, token) =>
            {
                // The simulator starts a new display each session, so it is always measured here.
                if (before is null || scope.Simulator is not null)
                {
                    scope.Progress.Report("Measuring the display as it is now (no calibration curves)…");
                    before = await new QuickPlan(scope.Rig).RunAsync(scope.Progress, token).ConfigureAwait(false);
                }

                var check = new QuickPlanOptions { ContentCheck = false, PatternCheck = false };
                var curves = CurveSolver.Solve(before, target);
                var after = await TryAsync(curves, 1).ConfigureAwait(false);
                if (refine)
                {
                    curves = CurveSolver.Solve(after, target, curves);
                    after = await TryAsync(curves, 2).ConfigureAwait(false);
                }

                var context = Context(request, scope);
                return new CalibrationRun(before, after, curves, DisplayAnalysis.Analyze(before, context), DisplayAnalysis.Analyze(after, context));

                async Task<DisplayMeasurement> TryAsync(CalibrationCurves trial, int pass)
                {
                    scope.Progress.Report(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Pass {pass}: loading trial calibration curves in the graphics card (kept {Math.Min(trial.LevelsKept(0), Math.Min(trial.LevelsKept(1), trial.LevelsKept(2)))} of 256 levels; put back afterwards)…"));
                    if (!scope.LoadCurves(trial))
                    {
                        throw new MeasurementStoppedException("The graphics driver refused the calibration curves, so a Windows calibration file cannot work on this display. A profile-only file is still possible.");
                    }

                    scope.Progress.Report($"Pass {pass}: measuring with them…");
                    return await new QuickPlan(scope.Rig, check).RunAsync(scope.Progress, token).ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    /// <summary>Measures the display as Windows shows it now, with the installed calibration curves left in the
    /// graphics card (the faster check plan: no content or pattern check).</summary>
    public static Task<MeasurementOutcome> VerifyAsync(MeasurementRequest request, IProgress<string> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var keep = request with { KeepCurves = true };
        return RunGuardedAsync(
            keep,
            progress,
            new SimulatedDisplayOptions { Gamma = new Vec3(2.2, 2.2, 2.2), CrushCode = 3 },
            async (scope, token) =>
            {
                scope.Progress.Report("Measuring with the installed calibration curves left on…");
                var measurement = await new QuickPlan(scope.Rig, new QuickPlanOptions { ContentCheck = false, PatternCheck = false }).RunAsync(scope.Progress, token).ConfigureAwait(false);
                return new MeasurementOutcome(measurement, DisplayAnalysis.Analyze(measurement, Context(keep, scope)));
            },
            cancellationToken);
    }

    public static Task<MeasurementOutcome> RunAsync(MeasurementRequest request, IProgress<string> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunGuardedAsync(
            request,
            progress,
            new SimulatedDisplayOptions { Gamma = new Vec3(2.35, 2.2, 2.1), CrushCode = 5 },
            async (scope, token) =>
            {
                var measurement = await new QuickPlan(scope.Rig).RunAsync(scope.Progress, token).ConfigureAwait(false);
                return new MeasurementOutcome(measurement, DisplayAnalysis.Analyze(measurement, Context(request, scope)));
            },
            cancellationToken);
    }

    /// <summary>Runs the OSD advisor. It first checks whether the brightness follows the picture (dynamic contrast, ECO,
    /// a light sensor) and asks to turn that off only when it does. Brightness and Contrast can be set over DDC/CI when
    /// the monitor allows: always with <paramref name="useDdc"/>, otherwise when the user clicks "Set it for me" on that
    /// change (journaled; kept when the run finishes, put back when it stops). Everything else is asked of the user.</summary>
    public static Task<AdvisorOutcome> AdviseAsync(
        MeasurementRequest request,
        AdvisorSetup setup,
        IAdvicePrompt user,
        bool useDdc,
        IProgress<string> progress,
        IProgress<Advice> advised,
        IProgress<AdvisorStep> measured,
        IProgress<MeasurePhase>? phases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(setup);
        phases?.Report(new MeasurePhase("Preparing the camera", 8));
        return RunGuardedAsync(
            request,
            progress,
            SimulatedMonitor(setup, matchWhite: request.ReferenceName is not null),
            async (scope, token) =>
            {
                var settings = new Dictionary<OsdControlId, int>(setup.Settings);
                IOsdActuator actuator = new PromptActuator(user);
                DdcCiChannel? channel = null;
                DdcValueGuard? guard = null;
                if (scope.Simulator is { } sim)
                {
                    // The demo monitor answers over DDC/CI like most monitors: SoftCal sets its values itself, as it
                    // would a real one's, when the user lets it.
                    SimulatedMonitorActuator.Set(sim, settings);
                    SimulatedMonitorActuator.SetColor(sim, setup.Color);
                    var demoDdc = new DdcActuator((_, _, _) => Task.FromResult(DdcWriteOutcome.Applied), user, scope.Progress, automatic: useDdc);
                    actuator = new SimulatedMonitorActuator(demoDdc, sim);
                }
                else if (request.Monitor is { } monitor)
                {
                    channel = DdcCiChannel.Open(monitor).FirstOrDefault();
                    if (channel is not null)
                    {
                        guard = new DdcValueGuard(channel, AppSafety.Journal);
                        actuator = new DdcActuator(guard.SetAsync, user, scope.Progress, automatic: useDdc);
                    }
                    else if (useDdc)
                    {
                        scope.Progress.Report("DDC/CI is not available on this monitor: every change is asked of you.");
                    }
                }

                var finished = false;
                try
                {
                    // Dynamic contrast, ECO or a light sensor would change every reading; ask about them only when seen.
                    phases?.Report(new MeasurePhase("Checking whether the brightness follows the picture", 13));
                    var content = await ContentCheck.RunAsync(scope.Rig, new QuickPlanOptions().Readings, token).ConfigureAwait(false);
                    var detected = content.ChangesWithContent;
                    scope.Progress.Report(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Brightness check: {(detected ? "the brightness changed with the picture" : "steady whatever is on screen")} (the same gray's light {content.FirstDifference:+0.0%;-0.0%} with a white surround than a black one, drifting {content.Drift:0.0%} as it settled; {ContentResponse.Limit:0%} or more is a change)."));

                    var advisor = new OsdAdvisor(setup.Profile, enhancementsOff: !detected, enhancementsDetected: detected);
                    var loop = new AdvisorLoop(new AdvisorProbe(scope.Rig), advisor, actuator, setup.Profile);
                    phases?.Report(new MeasurePhase("Measuring the darkest and brightest steps and the mid-grays", 27));
                    var steps = await loop.RunAsync(settings, advised, measured, token).ConfigureAwait(false);
                    var loopFinished = steps.Count > 0 && steps[^1].Advice.Kind == AdviceKind.Done;
                    var white = loopFinished && scope.White is { } stage && request.ReferenceName is { } referenceName
                        ? await MatchWhiteAsync(request, setup, user, scope, stage, referenceName, channel, useDdc, advised, token).ConfigureAwait(false)
                        : null;

                    // A cancel during the white match ends the whole run as cancelled: everything set is put back.
                    token.ThrowIfCancellationRequested();
                    finished = loopFinished;
                    string? note = null;
                    if (guard is { Originals.Count: > 0 })
                    {
                        if (finished)
                        {
                            guard.Keep();
                            note = "The values SoftCal set over DDC/CI are kept.";
                        }
                        else
                        {
                            note = await PutBackAsync(guard, "Brightness and Contrast", scope).ConfigureAwait(false)
                                ? "The run stopped, so the values SoftCal set over DDC/CI are put back."
                                : "The run stopped, but some values SoftCal set over DDC/CI could not be put back (see above): set them in the monitor's menu if they stay.";
                        }
                    }

                    return new AdvisorOutcome(steps, settings, finished, note, advisor.Notes, advisor.BrightnessSetsBacklight, steps.Count > 0 ? steps[^1].Readings : null, detected, white);
                }
                finally
                {
                    // Cancelled or failed: what is still changed is put back, and said when it cannot be.
                    if (!finished && guard is { Originals.Count: > 0 })
                    {
                        await PutBackAsync(guard, "Brightness and Contrast", scope).ConfigureAwait(false);
                    }

                    channel?.Dispose();
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Matches the monitor's white to the reference screen's with its red, green and blue gains, after the other
    /// settings. Gains written over DDC/CI go through their own journaled guard: kept when the match finishes, put back
    /// when it stops or fails.
    /// </summary>
    private static async Task<WhiteMatchResult> MatchWhiteAsync(
        MeasurementRequest request,
        AdvisorSetup setup,
        IAdvicePrompt user,
        Scope scope,
        IWhiteMatchStage stage,
        string referenceName,
        DdcCiChannel? channel,
        bool useDdc,
        IProgress<Advice> advised,
        CancellationToken cancellationToken)
    {
        var gains = channel is null ? null : new DdcValueGuard(channel, AppSafety.Journal);
        var whiteUser = new WhiteMatchUser(user, scope.Progress) { Channel = channel, Guard = gains, SetOverDdc = useDdc, Simulated = scope.Simulator };
        var name = scope.Simulator is not null ? "the demo monitor" : request.Monitor?.FriendlyName ?? request.Monitor?.DeviceName ?? "this monitor";

        // Set over DDC/CI, a change takes a few seconds and no trip to the menu: allow more of them.
        var options = new WhiteMatchOptions { MaxGainRounds = whiteUser.SetsGainsDirectly ? 6 : 4 };

        // The gains' highest value: the monitor's own (0–100 on most, 0–255 on some); a match never raises the highest
        // gain anyway, so without DDC/CI the widest range is safe.
        var gainMax = channel is not null && await channel.ReadAsync(0x16, cancellationToken).ConfigureAwait(false) is { Maximum: > 0 } red
            ? (int)Math.Min(red.Maximum, 255)
            : 255;
        WhiteMatchResult? result = null;
        try
        {
            result = await new WhiteMatcher(scope.Rig, stage, whiteUser, options)
                .RunAsync(name, referenceName, setup.Color ?? new ColorSettings(null, null, null, null), gainMax, advised, scope.Progress, cancellationToken)
                .ConfigureAwait(false);

            // A monitor that still reports its factory preset after the switch keeps the gains there: say so.
            if (whiteUser.ReportedPreset is { } reported && result.PresetAfter != result.PresetBefore)
            {
                result = result with { PresetAfter = reported };
            }
        }
        finally
        {
            if (gains is { Originals.Count: > 0 })
            {
                if (result is { Finished: true })
                {
                    gains.Keep();
                }
                else if (await PutBackAsync(gains, "Red, Green and Blue", scope).ConfigureAwait(false) && result is not null)
                {
                    // Put back: the monitor is on its first gains again, whatever the stopped match had reached.
                    result = result with { GainsAfter = result.GainsBefore, PresetAfter = result.PresetBefore };
                }
            }
        }

        return result!;
    }

    /// <summary>
    /// Puts back the values a guard set over DDC/CI and says what could not be (in the log, and in the measure step's
    /// alert line): those stay journaled, so SoftCal tries them again before the next measurement, and its guardian when
    /// SoftCal closes. Never throws, so the rest of the clean-up still runs.
    /// </summary>
    private static async Task<bool> PutBackAsync(DdcValueGuard guard, string what, Scope scope)
    {
        try
        {
            var failed = (await guard.RestoreAsync(CancellationToken.None).ConfigureAwait(false))
                .Where(r => r.Outcome != RestoreOutcome.Restored)
                .ToList();
            if (failed.Count == 0)
            {
                return true;
            }

            scope.Say($"{what}: {string.Join("; ", failed.Select(f => f.Message))}. SoftCal tries again before the next measurement, and when it closes.");
        }
#pragma warning disable CA1031 // A failed put-back must never skip the rest of the clean-up; it is said.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            scope.Say($"{what} could not be put back over DDC/CI ({ex.Message}). SoftCal tries again before the next measurement, and when it closes.");
        }

        return false;
    }

    /// <summary>Puts back one thing in a clean-up and says when it could not be; never throws, so the next one runs.</summary>
    private static void PutBack(IProgress<string> progress, IProgress<string>? alerts, string what, Func<bool> restore)
    {
        string? problem = null;
        try
        {
            if (!restore())
            {
                problem = "it did not read back";
            }
        }
#pragma warning disable CA1031 // One clean-up step failing must never skip the others; it is said.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            problem = ex.Message;
        }

        if (problem is not null)
        {
            var line = $"{what} could not be put back now ({problem}). SoftCal tries again before the next measurement, and when it closes.";
            progress.Report(line);
            alerts?.Report(line);
        }
    }

    /// <summary>The demo monitor for the advisor: BenQ-like controls starting from the questionnaire's values. For a
    /// white match its own white is green, as the BenQ's was after its firmware reset.</summary>
    private static SimulatedDisplayOptions SimulatedMonitor(AdvisorSetup setup, bool matchWhite) => new()
    {
        ChannelGain = matchWhite ? new Vec3(0.86, 1.0, 0.80) : Vec3.One,
        Gamma = new Vec3(2.3, 2.2, 2.15),
        CrushCode = 5,
        Controls = new SimulatedOsdOptions
        {
            BrightnessKind = BrightnessKind.Backlight,
            GammaPresets = setup.Profile.Find(OsdControlId.GammaPreset) is { Presets: { } presets }
                ? [.. Enumerable.Range(0, presets.Count).Select(i => 1.8 + (0.8 * i / Math.Max(1, presets.Count - 1)))]
                : [],
            BoostPerStep = setup.Profile.Find(OsdControlId.BlackBoost) is null ? 0 : 1,
        },
    };

    private static ReportContext Context(MeasurementRequest request, Scope scope) =>
        Context(scope.Simulator is null ? request : request with { Simulated = true }, scope.InstrumentModel);

    /// <summary>What a report of this request says about the display (also for re-analysing saved measurements).</summary>
    public static ReportContext Context(MeasurementRequest request, string instrumentModel)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Simulated || request.Monitor is not { } monitor)
        {
            return new ReportContext("Simulated display", null, instrumentModel, AngleSensitivePanel: request.AngleSensitivePanel, Profile: request.Profile);
        }

        var edid = TryParse(monitor.Edid);
        return new ReportContext(
            monitor.FriendlyName ?? monitor.DeviceName,
            edid is null ? null : DisplayKey.FromEdid(edid).Value,
            instrumentModel,
            edid,
            AngleSensitivePanel: request.AngleSensitivePanel,
            Profile: request.Profile);
    }

    private static async Task<T> RunGuardedAsync<T>(
        MeasurementRequest request,
        IProgress<string> progress,
        SimulatedDisplayOptions simulated,
        Func<Scope, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        // The camera's COM objects must live in the multithreaded apartment: created on WPF's STA thread they cannot be
        // used from the thread-pool threads the measurement continues on (InvalidCastException), so run it all there.
        return await Task.Run(
            () => request.Simulated
                ? RunSimulatedAsync(request, progress, simulated, work, cancellationToken)
                : RunRealAsync(request, progress, work, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> RunSimulatedAsync<T>(
        MeasurementRequest request,
        IProgress<string> progress,
        SimulatedDisplayOptions options,
        Func<Scope, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        progress.Report("Simulated display and camera (no hardware is touched).");
        var sim = new SimulatedRig(new SimulatedDisplay(options), new SimulatedCamera(new SimulatedCameraOptions { Response = CameraResponse.Filmic(2) }))
        {
            ReferenceDisplay = request.ReferenceName is null ? null : new SimulatedDisplay(new SimulatedDisplayOptions()),
        };
        await using var rig = new MeasurementRig(sim, sim, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };
        await sim.PrepareAsync(new LockRequest(), cancellationToken).ConfigureAwait(false);
        bool LoadCurves(CalibrationCurves curves)
        {
            sim.Display.SetGpuLut([curves.Ramp(0), curves.Ramp(1), curves.Ramp(2)]);
            return true;
        }

        var white = sim.ReferenceDisplay is null ? null : new SimulatedWhiteStage(rig, sim);
        return await work(new Scope(rig, sim.Info.Model, sim.Display, progress, LoadCurves, white), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> RunRealAsync<T>(
        MeasurementRequest request,
        IProgress<string> progress,
        Func<Scope, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        var monitor = request.Monitor ?? throw new MeasurementStoppedException("Choose a display first.");
        if (request.Phone is null && request.Camera is null)
        {
            throw new MeasurementStoppedException("No camera is selected: choose one in step 2.");
        }

        var journal = AppSafety.Journal;
        Guardian.EnsureStarted();

        // What this process could not put back earlier, or a run that died left, is put back first: the run starts from
        // the true originals (and its guards would refuse to start over an entry still there).
        foreach (var leftover in AppSafety.RecoverBeforeRun())
        {
            progress.Report(leftover.Outcome == RestoreOutcome.Restored
                ? $"Put back from an earlier run: {leftover.Message}."
                : $"Still not put back from an earlier run ({leftover.Kind}): {leftover.Message}.");
        }

        // SoftCal's loader leaves the curves alone while measuring: a measurement sets them to linear or loads trial
        // curves on purpose (the gamma guard puts the original back afterwards).
        using var curvesBusy = CalibrationLoaderProcess.HoldCurves();
        using var awake = DisplayKeepAwake.Acquire("Soft Color Calibrator is measuring the display");

        // Declared before the watchers that cancel it, so it is disposed after them.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string? stopReason = null;
        void Stop(string reason)
        {
            stopReason ??= reason;
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The measurement has just ended.
            }
        }

        using var nightLight = NightLightWatcher.Start();
        if (nightLight.Current.Active == true)
        {
            throw new MeasurementStoppedException("Night light is on. Turn it off in Settings › System › Display › Night light, then start again.");
        }

        // Whatever else must be off (a color filter) is checked once more as the measurement starts.
        if (WindowsColorStateProbe.ReadMeasuringBlockers().FirstOrDefault() is { } blocker)
        {
            throw new MeasurementStoppedException($"{blocker.Message} {blocker.Fix}");
        }

        nightLight.Changed += (_, report) =>
        {
            if (report.Active != false)
            {
                Stop("Night light switched on during the measurement, which would tint every reading. Turn it off and start again.");
            }
        };

        // A color filter (Win+Ctrl+C) tints every reading too: looked at every two seconds.
        using var filters = new Timer(
            _ =>
            {
                if (OsProbes.ReadColorFilters().Active)
                {
                    Stop("A color filter switched on during the measurement, which would tint every reading. Turn it off (Win+Ctrl+C) and start again.");
                }
            },
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));

        AdvancedColorGuard? advancedColor = null;
        GammaRampGuard? gamma = null;
        try
        {
            if (DisplayConfigAdvancedColorDevice.For(monitor) is { } colorDevice
                && colorDevice.Read() is { } colorState
                && AdvancedColorSwitches.From(colorState).AnyOn)
            {
                progress.Report("Switching HDR / Auto Color Management off for the measurement (restored afterwards)…");
                advancedColor = AdvancedColorGuard.Begin(colorDevice, journal);
                advancedColor.MakeSdr();
            }

            var rampDevice = new GdiGammaRampDevice(monitor.DeviceName);
            if (request.KeepCurves)
            {
                progress.Report(rampDevice.Read().IsIdentity
                    ? "The graphics card holds no calibration curves for this display: this measures it uncalibrated."
                    : "The graphics card's calibration curves stay as they are for this measurement.");
            }
            else if (!rampDevice.Read().IsIdentity)
            {
                progress.Report("Setting the loaded calibration curve to linear for the measurement (restored afterwards)…");
                gamma = GammaRampGuard.Begin(rampDevice, journal);
                gamma.Apply(GammaRamp.Identity());
            }

            // Calibration curves for a trial go through the same journaled guard: the original ramp comes back when the
            // session ends, or from the guardian after a crash.
            bool LoadCurves(CalibrationCurves curves)
            {
                gamma ??= GammaRampGuard.Begin(rampDevice, journal);
                return gamma.Apply(new GammaRamp(curves.Ramp(0), curves.Ramp(1), curves.Ramp(2)));
            }

            var reference = request.ReferenceName is null ? null : request.Reference;
            return request.Phone is { } phone
                ? await MeasureWithPhoneAsync(monitor, phone, reference, request.Screens, progress, request.Alerts, LoadCurves, work, stop.Token).ConfigureAwait(false)
                : await MeasureWithCameraAsync(monitor, request.Camera!, reference, request.Screens, journal, progress, request.Alerts, LoadCurves, work, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopReason is not null)
        {
            throw new MeasurementStoppedException(stopReason);
        }
        finally
        {
            // Each on its own: one that fails must not keep the other from being put back; what could not be is said.
            PutBack(progress, request.Alerts, "The graphics card's calibration curve", () => gamma?.Restore() ?? true);
            PutBack(progress, request.Alerts, "HDR / Auto Color Management", () => advancedColor is null || advancedColor.Restore().Outcome == RestoreOutcome.Restored);
        }
    }

    private static async Task<T> MeasureWithCameraAsync<T>(
        DisplayMonitor monitor,
        WebcamDevice camera,
        DisplayMonitor? reference,
        IScreenHost? screens,
        StateJournal journal,
        IProgress<string> progress,
        IProgress<string>? alerts,
        Func<CalibrationCurves, bool> loadCurves,
        Func<Scope, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        // The camera's settings are journaled from their true originals: an entry still there (a restore that failed, and
        // could not be redone before this run) is the only record of them, so it is never overwritten.
        if (journal.Contains(CameraControlsRestorer.JournalKind, camera.StableId))
        {
            throw new MeasurementStoppedException($"{camera.FriendlyName}'s own settings from an earlier run could not be put back yet. Unplug it and plug it back in, then start again.");
        }

        progress.Report($"Opening {camera.FriendlyName}…");
        var webcam = WebcamInstrument.Open(camera);
        var faults = new List<InstrumentFault>();
        var lastSaid = new Dictionary<InstrumentFaultKind, DateTime>();
        webcam.Faulted += (_, fault) =>
        {
            bool say;
            lock (faults)
            {
                faults.Add(fault);

                // A stalled stream is reported every second: said once in a while is enough.
                say = !lastSaid.TryGetValue(fault.Kind, out var said) || DateTime.UtcNow - said > TimeSpan.FromSeconds(10);
                if (say)
                {
                    lastSaid[fault.Kind] = DateTime.UtcNow;
                }
            }

            if (say)
            {
                progress.Report($"Camera: {fault.Message}");
            }
        };

        try
        {
            CameraControlsRestorer.Journal(journal, camera, webcam.Snapshot);

            // Watched for anything covering the patch: a reading taken meanwhile is taken again.
            using var window = D3DPatchWindow.Open(monitor.DeviceName, new PatchWindowOptions { WatchForCover = true });
            screens?.RunCoversScreen();

            // Focus 50 gave the least residual pattern ripple at 8–10 cm on both monitors (M1 report).
            progress.Report("Locking white balance, exposure, gain and focus…");
            var locked = await webcam.PrepareAsync(new LockRequest { WhiteBalanceKelvin = 6500, FocusValue = 50 }, cancellationToken).ConfigureAwait(false);
            progress.Report(LockLine(locked));
            if (!locked.CriticalControlsLocked)
            {
                throw new MeasurementStoppedException("The camera would not lock its white balance, exposure and gain, so readings could not be trusted.");
            }

            // The frame stream ends with the run: a camera that stops sending frames cannot hold the run for ever.
            await using var rig = new MeasurementRig(webcam, window, new RoiSpec(), streamCancellation: cancellationToken);
            rig.PatchDisturbed += (_, what) =>
            {
                var line = $"Took a reading again: {what}. Keep other windows and the mouse off the measured screen until the end.";
                progress.Report(line);
                alerts?.Report(line);
            };

            // The watch starts with the window; by now it has looked at the screen, or said why it cannot.
            if (window.WatchProblem is { } problem)
            {
                progress.Report($"SoftCal cannot watch {monitor.FriendlyName ?? monitor.DeviceName} for windows covering the patch ({problem}): keep other windows off it while measuring.");
            }

            using var white = reference is null ? null : new ScreenWhiteStage(rig, window, monitor, reference, webcam, screens, progress);
            var result = await work(new Scope(rig, camera.FriendlyName, null, progress, loadCurves, white, alerts), cancellationToken).ConfigureAwait(false);
            ReportLockHeld(webcam, progress);
            return result;
        }
        finally
        {
            progress.Report("Restoring the camera's settings…");
            try
            {
                await webcam.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The clean-up goes on; the journal keeps the camera's originals for the next try.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                lock (faults)
                {
                    faults.Add(new InstrumentFault(InstrumentFaultKind.Disconnected, "Some camera controls could not be restored: " + ex.Message));
                }

                progress.Report("The camera did not close cleanly: " + ex.Message);
            }

            bool restoreFailed;
            lock (faults)
            {
                restoreFailed = faults.Any(f => f.Message.StartsWith("Some camera controls could not be restored", StringComparison.Ordinal));
            }

            if (!restoreFailed)
            {
                try
                {
                    journal.Remove(CameraControlsRestorer.JournalKind, camera.StableId);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    progress.Report("The camera's journal entry could not be removed: " + ex.Message);
                }
            }
            else
            {
                var line = $"{camera.FriendlyName}'s own settings could not all be put back. SoftCal tries again before the next measurement, and when it closes.";
                progress.Report(line);
                alerts?.Report(line);
            }
        }
    }

    /// <summary>
    /// A measurement with a phone running SoftCal Camera, over the link connected in step 2. Its camera's settings live
    /// on the phone, which puts them back in automatic when told to at the end, and by itself whenever the link drops:
    /// there is nothing to journal on this PC. The link stays open for the next run.
    /// </summary>
    private static async Task<T> MeasureWithPhoneAsync<T>(
        DisplayMonitor monitor,
        PhoneClient link,
        DisplayMonitor? reference,
        IScreenHost? screens,
        IProgress<string> progress,
        IProgress<string>? alerts,
        Func<CalibrationCurves, bool> loadCurves,
        Func<Scope, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        if (!link.IsConnected)
        {
            throw new MeasurementStoppedException("The phone is no longer connected: connect it again in step 2.");
        }

        var phone = new PhoneInstrument(link);
        await using (phone.ConfigureAwait(false))
        {
            var lastSaid = new Dictionary<InstrumentFaultKind, DateTime>();
            phone.Faulted += (_, fault) =>
            {
                lock (lastSaid)
                {
                    if (lastSaid.TryGetValue(fault.Kind, out var said) && DateTime.UtcNow - said < TimeSpan.FromSeconds(10))
                    {
                        return;
                    }

                    lastSaid[fault.Kind] = DateTime.UtcNow;
                }

                progress.Report($"Phone: {fault.Message}");
                alerts?.Report($"Phone: {fault.Message}");
            };

            progress.Report(string.Create(
                CultureInfo.InvariantCulture,
                $"Measuring with {phone.Info.Model} ({phone.Info.Firmware}) over the network; its clock maps to this PC's within {(link.Clock.Uncertainty ?? 0) * 1000:0.#} ms."));

            // Watched for anything covering the patch: a reading taken meanwhile is taken again.
            using var window = D3DPatchWindow.Open(monitor.DeviceName, new PatchWindowOptions { WatchForCover = true });
            screens?.RunCoversScreen();
            progress.Report("Locking the phone's white balance, exposure, sensitivity and focus…");
            try
            {
                var locked = await phone.PrepareAsync(new LockRequest { WhiteBalanceKelvin = 6500 }, cancellationToken).ConfigureAwait(false);
                progress.Report(LockLine(locked));
                progress.Report(phone.Capabilities.OutputIsLinear
                    ? "The phone reads its sensor's raw data: linear, before any of its processing (tone curve, sharpening, noise reduction)."
                    : "The phone reads its processed picture (its camera gives apps no raw data): its tone curve may follow what it sees.");
                if (!locked.CriticalControlsLocked)
                {
                    throw new MeasurementStoppedException("The phone's camera would not lock its white balance, exposure and sensitivity, so readings could not be trusted.");
                }

                if (FocusProblem(locked, link) is { } focusProblem)
                {
                    throw new MeasurementStoppedException(focusProblem);
                }

                // The frame stream ends with the run, or when the phone stops sending frames.
                await using var rig = new MeasurementRig(phone, window, new RoiSpec(), streamCancellation: cancellationToken);
                rig.PatchDisturbed += (_, what) =>
                {
                    var line = $"Took a reading again: {what}. Keep other windows and the mouse off the measured screen until the end.";
                    progress.Report(line);
                    alerts?.Report(line);
                };

                if (window.WatchProblem is { } problem)
                {
                    progress.Report($"SoftCal cannot watch {monitor.FriendlyName ?? monitor.DeviceName} for windows covering the patch ({problem}): keep other windows off it while measuring.");
                }

                // A phone can report its lens at the farthest and still see the screen sharply (a Galaxy S23 Ultra did):
                // the picture itself says whether the screen's pixels blur, as every halftone reading needs. When they do
                // not, the phone's focus settings are tried, each judged by the picture, and the best is kept.
                progress.Report("Checking that the camera sees the screen blurred…");
                var blur = await DefocusCheck.RunAsync(rig, cancellationToken).ConfigureAwait(false);
                progress.Report(BlurLine(blur));
                if (!blur.Blurred)
                {
                    progress.Report("  Not blurred enough: trying the phone's focus settings, judged by the picture…");
                    var search = await DefocusSearch.RunAsync(rig, phone, cancellationToken).ConfigureAwait(false);
                    progress.Report("  " + string.Join(", ", search.Trials.Select(t => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{phone.DescribeFocus(t.Focus)}: {t.Ripple:P0}{(t.Clipped > DefocusResult.MaxClipped ? $" ({t.Clipped:P1} clipped)" : string.Empty)}"))));
                    blur = search.BestCheck;
                    progress.Report($"  Kept {phone.DescribeFocus(search.Best)}. {BlurLine(blur).TrimStart()}");
                }

                if (!blur.Blurred)
                {
                    throw new MeasurementStoppedException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The phone's camera sees the screen's finest lines too sharply at every focus it was set to (at best {blur.Ripple:P0} ripple, {DefocusResult.MaxRipple:P0} at most), so it would read them wrong: its lens does not go where it is asked. Bring it closer than it can focus, with a tube of about {PhoneTubeCentimetres(link.Capabilities):0} cm, or measure with a webcam."));
                }

                using var white = reference is null ? null : new ScreenWhiteStage(rig, window, monitor, reference, phone, screens, progress);
                return await work(new Scope(rig, phone.Info.Model, null, progress, loadCurves, white, alerts), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                progress.Report("Putting the phone's camera back in automatic…");
                try
                {
                    await phone.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (MeasurementException ex)
                {
                    progress.Report($"The phone did not answer ({ex.Message}); it puts its camera back by itself when the link drops.");
                }
            }
        }
    }

    /// <summary>
    /// A phone whose lens stayed focused near the screen (20 cm or closer) does not blur the screen's pixels: every
    /// halftone reading would clip, a minute into the run (seen with a Galaxy S23 Ultra). Said at once instead, with
    /// what to do: closer than the lens can focus, the screen blurs whatever the lens does.
    /// </summary>
    private static string? FocusProblem(LockReport locked, PhoneClient link)
    {
        if (locked.Controls.FirstOrDefault(c => c.Control == CameraControl.Focus) is not { Outcome: LockOutcome.VerifiedMismatch or LockOutcome.Failed, Actual: >= 5 } focus)
        {
            return null;
        }

        var at = 100 / focus.Actual!.Value;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"The phone's camera kept its focus on the screen (about {at:0} cm away) instead of the farthest, so the screen's fine patterns stay sharp and every reading of them would clip. Hold the phone closer than it can focus: shorten its tube to about {PhoneTubeCentimetres(link.Capabilities):0} cm, place it again in step 2, then measure again.");
    }

    /// <summary>
    /// The tube that holds a phone's camera closer to the screen than its lens can focus: half its nearest focus, 3–5 cm
    /// (4 when the phone does not say). The screen then reaches the camera blurred whatever the lens does, as the
    /// halftone readings need; a phone's lens cannot be trusted to go to the farthest when asked.
    /// </summary>
    internal static double PhoneTubeCentimetres(PhoneCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.MinimumFocusDiopters is > 0 and var diopters ? Math.Clamp(Math.Floor(100 / diopters / 2), 3, 5) : 4;
    }

    /// <summary>The blur check on one line of the log.</summary>
    private static string BlurLine(DefocusResult blur) => string.Create(
        CultureInfo.InvariantCulture,
        $"  Blur: 1-pixel lines ripple {blur.Ripple:P1} above the camera's noise{(blur.Linear ? $" ({blur.MeasuredRipple:P1} in the raw data, which a γ 2.2 picture would show 2.2 times smaller)" : string.Empty)}, {blur.LinesClipped:P1} of their pixels clipped (allowed: {DefocusResult.MaxRipple:P0} and {DefocusResult.MaxClipped:P1}).");

    /// <summary>The lock report on one line of the log.</summary>
    private static string LockLine(LockReport locked) =>
        "  Lock: " + string.Join(", ", locked.Controls.Select(c => string.Create(
            CultureInfo.InvariantCulture,
            $"{c.Control} {c.Outcome}{(c.Requested is { } r ? $" {r:0.####}" : string.Empty)}{(c.Actual is { } a && a != c.Requested ? $" (reads {a:0.####})" : string.Empty)}{(c.Note is { Length: > 0 } n && c.Control is not CameraControl.Exposure ? $" [{n}]" : string.Empty)}")));

    /// <summary>Reads every locked control again at the end: a camera that drifted from its lock (or that another app
    /// changed) explains readings that moved between runs.</summary>
    private static void ReportLockHeld(WebcamInstrument webcam, IProgress<string> progress)
    {
        try
        {
            var moved = webcam.CheckLock().Where(c => c.Actual != c.Expected).ToList();
            progress.Report(moved.Count == 0
                ? "  Lock held: every locked camera control reads the same as at the start."
                : "  Lock moved: " + string.Join(", ", moved.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Control} {c.Expected.Value}{(c.Expected.IsAuto ? " auto" : string.Empty)} → {(c.Actual is { } a ? a.Value + (a.IsAuto ? " auto" : string.Empty) : "unreadable")}"))));
            var external = webcam.ExternalChanges;
            if (external.Count > 0)
            {
                progress.Report(string.Create(CultureInfo.InvariantCulture, $"  Another program changed camera controls {external.Count} times during the run (each re-applied: {external.All(e => e.Reapplied)})."));
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            progress.Report("  The camera's controls could not be read back at the end: " + ex.Message);
        }
    }

    private static Edid? TryParse(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return Edid.Parse(bytes);
        }
        catch (EdidFormatException)
        {
            return null;
        }
    }

    /// <summary>In the demo, what the user reports is what the simulated monitor gets.</summary>
    private sealed class SimulatedMonitorActuator(IOsdActuator user, SimulatedDisplay display) : IOsdActuator
    {
        public static void Set(SimulatedDisplay display, IReadOnlyDictionary<OsdControlId, int> settings)
        {
            foreach (var (id, value) in settings)
            {
                Set(display.Osd, id, value);
            }
        }

        /// <summary>The demo monitor's color preset: a factory preset with the questionnaire's gains, or the user mode.</summary>
        public static void SetColor(SimulatedDisplay display, ColorSettings? color)
        {
            var osd = display.Osd;
            osd.UserColorMode = color?.Kind == ColorPresetKind.User;
            if (color is { Red: { } r, Green: { } g, Blue: { } b })
            {
                if (osd.UserColorMode)
                {
                    (osd.RedGain, osd.GreenGain, osd.BlueGain) = (r, g, b);
                }
                else
                {
                    osd.PresetGains = (r, g, b);
                }
            }
        }

        public async Task<int?> ApplyAsync(OsdControl control, int value, Advice advice, CancellationToken cancellationToken)
        {
            var reported = await user.ApplyAsync(control, value, advice, cancellationToken).ConfigureAwait(false);
            if (reported is { } v)
            {
                Set(display.Osd, control.Id, v);
            }

            return reported;
        }

        private static void Set(SimulatedOsd osd, OsdControlId id, int value)
        {
            switch (id)
            {
                case OsdControlId.Brightness:
                    osd.Brightness = value;
                    break;
                case OsdControlId.Contrast:
                    osd.Contrast = value;
                    break;
                case OsdControlId.GammaPreset:
                    osd.GammaPreset = value;
                    break;
                case OsdControlId.BlackBoost:
                    osd.BlackBoost = value;
                    break;
            }
        }

        public Task<bool> ConfirmAsync(Advice advice, CancellationToken cancellationToken) => user.ConfirmAsync(advice, cancellationToken);
    }
}
