using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using SoftCal.App.Services;
using SoftCal.Core.Analysis;
using SoftCal.Core.Calibration;
using SoftCal.Core.Color;
using SoftCal.Core.Displays;
using SoftCal.Core.Measurement;
using SoftCal.Icc;
using SoftCal.Windows.AdvancedColor;
using SoftCal.Windows.ColorManagement;
using SoftCal.Windows.Displays;

namespace SoftCal.App;

/// <summary>Advanced, the Windows calibration file: create calibration curves (a trial with the curves loaded), then
/// install them as a Windows color profile, or remove an installed one.</summary>
public partial class MainWindow
{
    /// <summary>A Quick measurement this old or newer, of the same display, profile and camera, is the calibration's
    /// starting point (saves 2 minutes); older, the display or the camera may have changed since.</summary>
    private static readonly TimeSpan ReuseFor = TimeSpan.FromMinutes(30);

    private CancellationTokenSource? _calibrating;
    private string? _redirected;
    private bool _redirectChecked;
    private LastMeasurement? _lastMeasured;
    private CalibrationRun? _calibration;

    /// <summary>What <see cref="_calibration"/> was made for (display, simulator or not, profile): it is installed only
    /// for that.</summary>
    private string? _calibrationFor;
    private CalibrationTarget? _calibrationTarget;
    private string? _calibrationFolder;
    private string? _calibrationStamp;

    /// <summary>The latest full report's measurement (Advanced) and what it measured.</summary>
    internal sealed record LastMeasurement(DisplayMeasurement Measurement, MeasurementRequest Request, DateTime At);

    /// <summary>The app this copy of SoftCal runs inside, when Windows redirects its settings there (checked once).</summary>
    private string? Redirected
    {
        get
        {
            if (!_redirectChecked)
            {
                (_redirected, _redirectChecked) = (AppPaths.RedirectedTo(), true);
            }

            return _redirected;
        }
    }

    /// <summary>False (and says why) when this copy cannot change what Windows uses.</summary>
    private bool CanChangeWindows()
    {
        if (Redirected is not { } package)
        {
            return true;
        }

        Log($"Not done: this copy of SoftCal runs inside another app ({package}), and Windows keeps its settings changes inside that app, so Windows would not see them. Close SoftCal and start it from Explorer (run.bat) or the Start menu.");
        return false;
    }

    private string CalibratedDisplayName => Simulated ? "Simulated display" : SelectedMonitor?.FriendlyName ?? SelectedMonitor?.DeviceName ?? "display";

    private string? InstallKey => Simulated ? null : SelectedDisplayKey ?? SelectedMonitor?.DeviceName;

    private MeasurementRequest CalibrationRequest() =>
        new(SelectedMonitor, MeasuringWebcam, Simulated, TnPanelCheck.IsChecked == true, CurrentProfileName, Phone: MeasuringPhone);

    /// <summary>The display, simulator or not, and profile chosen now, as a calibration is made for them.</summary>
    private string CalibrationIdentity => $"{(Simulated ? "simulated" : InstallKey ?? "?")}|{CurrentProfileName}";

    /// <summary>Drops curves made but not installed (another display, profile or the simulator was chosen since).</summary>
    private void DiscardCalibration()
    {
        _calibration = null;
        _calibrationFor = null;
        ShowCalibrationRows(null);
        UpdateCalibrationButtons();
    }

    /// <summary>Creating or verifying measures, so the camera must have been placed in step 2 (this screen can be
    /// opened before that, when only Check and Remove make sense).</summary>
    private bool CameraPlaced => Simulated || (HasCamera && CameraReadyCheck.IsChecked == true);

    private void PrepareCalibration()
    {
        if (_calibration is not null && _calibrationFor != CalibrationIdentity)
        {
            DiscardCalibration();
            Log("The curves created earlier were for another display, profile or the simulator, so they were dropped.");
        }

        var reusable = Reusable(CalibrationRequest());
        CalibrationBaseText.Text = Simulated
            ? "Simulator: a demo display; nothing on this PC changes."
            : !CameraPlaced
                ? "Place the camera (step 2) to create or verify. Check and Remove work without it."
            : reusable is { } last
                ? string.Create(CultureInfo.CurrentCulture, $"Starts from the report measured at {last.At:HH:mm}.")
                : "Measures the display first (about 2 minutes).";
        RemeasureCheck.Visibility = reusable is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshInstalled();
    }

    /// <summary>The last Quick measurement when it is recent and measured the same display, profile and camera.</summary>
    private LastMeasurement? Reusable(MeasurementRequest request)
    {
        if (request.Simulated || _lastMeasured is not { } last || last.Request.Simulated || DateTime.Now - last.At > ReuseFor)
        {
            return null;
        }

        return last.Request.Monitor?.DeviceName == request.Monitor?.DeviceName
            && last.Request.CameraId == request.CameraId
            && string.Equals(last.Request.Profile ?? string.Empty, request.Profile ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                ? last
                : null;
    }

    private CalibrationTarget SelectedTarget() => new(
        ToneTargetBox.SelectedIndex switch
        {
            1 => ToneTarget.Srgb,
            2 => ToneTarget.KeepNative,
            _ => ToneTarget.Gamma,
        },
        2.2,
        NeutralGraysCheck.IsChecked == true);

    private static string Describe(CalibrationTarget target) =>
        (target.Tone switch
        {
            ToneTarget.Srgb => "sRGB curve",
            ToneTarget.KeepNative => "own tone",
            _ => string.Create(CultureInfo.InvariantCulture, $"gamma {target.Gamma:0.0#}"),
        }) + (target.NeutralGrays ? ", neutral grays" : ", grays as they are");

    private async void CalibrateStart_Click(object sender, RoutedEventArgs e)
    {
        if (Busy)
        {
            return;
        }

        // Busy from the first moment: a second click, or the menu, must not start anything while the camera view closes.
        var calibrating = _calibrating = new CancellationTokenSource();
        var request = CalibrationRequest();
        var identity = CalibrationIdentity;
        var before = RemeasureCheck.IsChecked == true ? null : Reusable(request);
        var target = SelectedTarget();
        _calibration = null;
        _calibrationFor = null;
        ShowCalibrationRows(null);
        CalibrationBeforeColumn.Header = "Now (no curves)";
        CalibrationAfterColumn.Header = "With the curves";
        CalibrateLog.Clear();
        SetCalibrating(true, cancellable: true);
        var progress = new Progress<string>(Log);
        Log(string.Create(CultureInfo.CurrentCulture, $"Target: {Describe(target)}, for {CalibratedDisplayName}{(request.Profile is { } profile ? $", profile {profile}" : string.Empty)}."));
        if (before is not null)
        {
            Log(string.Create(CultureInfo.CurrentCulture, $"Starting from the measurement made at {before.At:HH:mm}."));
        }

        try
        {
            await StopPreviewAsync();
            StepAside(null);
            var run = await MeasurementSession.CalibrateAsync(request, before?.Measurement, target, RefineCheck.IsChecked == true, progress, calibrating.Token);
            _calibration = run;
            _calibrationFor = identity;
            _calibrationTarget = target;
            ShowCalibration(run, target);
            Log("Done, and the graphics card's curves are back as they were. Install puts these curves in Windows; Discard drops them.");
            SaveCalibrationFiles(run);
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled. The graphics card's curves and everything else that was changed are back as they were.");
        }
        catch (Exception ex) when (ex is MeasurementStoppedException or MeasurementException)
        {
            Log("Stopped: " + ex.Message);
        }
#pragma warning disable CA1031 // An async void handler must never let an error crash the app; it is shown and logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var log = AppPaths.WriteErrorLog(ex);
            Log($"Stopped by an unexpected error: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}Details: {log}{Environment.NewLine}Everything that was changed is restored by the guardian.");
        }
        finally
        {
            calibrating.Dispose();
            if (ReferenceEquals(_calibrating, calibrating))
            {
                _calibrating = null;
            }

            StepBack();
            SetCalibrating(false, cancellable: false);
        }
    }

    private void CalibrateCancel_Click(object sender, RoutedEventArgs e) => _calibrating?.Cancel();

    private void ShowCalibration(CalibrationRun run, CalibrationTarget target)
    {
        var targetGamma = target.Tone == ToneTarget.KeepNative ? run.BeforeReport.Tone.EffectiveGamma : target.Gamma;
        var rows = ReportComparison.Compare(run.BeforeReport, run.AfterReport, targetGamma).Select(ToItem).ToList();
        rows.Add(new ComparisonItem(
            "Levels kept (red / green / blue, of 256)",
            "256 / 256 / 256",
            string.Create(CultureInfo.InvariantCulture, $"{run.Curves.LevelsKept(0)} / {run.Curves.LevelsKept(1)} / {run.Curves.LevelsKept(2)}"),
            string.Empty));
        ShowCalibrationRows(rows);
    }

    /// <summary>The before/after table, shown only when it has rows.</summary>
    private void ShowCalibrationRows(IReadOnlyList<ComparisonItem>? rows)
    {
        CalibrationTable.ItemsSource = rows;
        CalibrationTable.Visibility = rows is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The calibration's readings and log go next to the reports (named so they are not listed as reports).</summary>
    private void SaveCalibrationFiles(CalibrationRun run)
    {
        _calibrationFolder = ReportFolder(run.AfterReport.DisplayKey, Simulated);
        Directory.CreateDirectory(_calibrationFolder);
        _calibrationStamp = run.AfterReport.CreatedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var stem = System.IO.Path.Combine(_calibrationFolder, _calibrationStamp + ".calibration");
        File.WriteAllText(stem + "-before.measurement.json", ReportWriter.MeasurementToJson(run.Before));
        File.WriteAllText(stem + "-after.measurement.json", ReportWriter.MeasurementToJson(run.After));
        File.WriteAllText(stem + "-after.html", ReportWriter.ToHtml(run.AfterReport));
        SaveCalibrationLog();
    }

    private void SaveCalibrationLog()
    {
        if (_calibrationFolder is null || _calibrationStamp is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(System.IO.Path.Combine(_calibrationFolder, _calibrationStamp + ".calibration.log.txt"), CalibrateLog.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("The log could not be saved: " + ex.Message);
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || _calibration is not { } run || _calibrationTarget is not { } target)
        {
            return;
        }

        // Curves fit the display, mode and world (simulator or not) they were made for, and nothing else.
        if (_calibrationFor != CalibrationIdentity)
        {
            DiscardCalibration();
            Log("Not installed: these curves were made for another display, profile or the simulator. Create them again for this one.");
            return;
        }

        var mode = CurrentProfileName;
        var created = DateTime.Now;
        var fileName = CalibrationProfile.FileName(CalibratedDisplayName, mode, created);
        var profile = CalibrationProfile.Create(System.IO.Path.GetFileNameWithoutExtension(fileName), Primaries(), run.After, run.Curves, created.ToUniversalTime());
        if (Simulated || SelectedMonitor is not { } monitor || InstallKey is not { } key)
        {
            try
            {
                Directory.CreateDirectory(CalibrationInstaller.ProfilesFolder);
                var path = System.IO.Path.Combine(CalibrationInstaller.ProfilesFolder, fileName);
                File.WriteAllBytes(path, IccWriter.Write(profile));
                Log($"Simulator: the color profile was written to {path}, not installed.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log("The color profile could not be written: " + ex.Message);
            }

            SaveCalibrationLog();
            return;
        }

        try
        {
            if (!CanChangeWindows() || !ConfirmInstall(monitor, fileName, mode))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            Log("Not installed: " + ex.Message);
            return;
        }

        _calibrating = new CancellationTokenSource();
        SetCalibrating(true, cancellable: false);
        try
        {
            var (record, log) = await CalibrationInstaller.InstallAsync(monitor, key, mode, profile, fileName, run.Curves, Describe(target), _calibrationStamp, CancellationToken.None);
            foreach (var line in log)
            {
                Log(line);
            }

            if (record is not null)
            {
                Log(CalibrationInstaller.Check(record, monitor).Message);
                Log($"Installed, as the profile {mode}'s calibration. SoftCal's loader keeps the curves loaded from now on; click Check at any time, and Verify to measure the result.");
                _calibration = null;

                // The profile keeps it: applying the profile later makes Windows use it again.
                if (SettingsKey is { } profilesKey)
                {
                    try
                    {
                        Profiles.SetCalibration(profilesKey, mode, new ProfileCalibration(fileName, Describe(target), created, _calibrationStamp));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        Log($"The profile {mode} could not keep it ({ex.Message}): applying the profile later would not bring it back.");
                    }
                }
            }
            else
            {
                Log("Not installed. Nothing else was changed.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or IccFormatException or System.Security.SecurityException)
        {
            Log("Install stopped: " + ex.Message + (CalibrationInstaller.Current(key) is null
                ? " Nothing was recorded, so Remove cannot undo it: check the display's profile in Settings › System › Display › Color profile."
                : " Use Remove to put back what was done so far."));
        }
        finally
        {
            _calibrating?.Dispose();
            _calibrating = null;
            SetCalibrating(false, cancellable: false);
            SaveCalibrationLog();
        }
    }

    private bool ConfirmInstall(DisplayMonitor monitor, string fileName, string? mode)
    {
        var display = DisplayTarget.Of(monitor);
        var previous = display is { } d ? DisplayProfileAssociation.GetDefault(d) : null;
        var system = display is { } sd ? DisplayProfileAssociation.GetDefault(sd, systemWide: true) : null;
        var windowsLoaderBack = InstallKey is { } key && CalibrationInstaller.Current(key) is { UsesSoftCalLoader: false } earlier && CalibrationInstaller.NeedsWindowsLoaderBack(earlier);
        var advanced = DisplayConfigAdvancedColorDevice.For(monitor)?.Read() is { } state && AdvancedColorSwitches.From(state).AnyOn;
        var text = string.Join(
            Environment.NewLine,
            $"Install this calibration for {CalibratedDisplayName}, profile {mode}?",
            string.Empty,
            $"• Adds {fileName} to Windows' color profiles ({ColorProfileStore.ColorDirectory()}).",
            $"• Makes it this display's default profile for your user (now: {previous ?? "none"}), and turns on 'Use my settings for this device' so Windows uses it rather than the system's ({system ?? "none"}).",
            "• Adds SoftCal's loader to your startup apps (Task Manager › Startup apps): it loads the curves at every sign-in and puts them back whenever Windows resets them. No administrator prompt.",
            windowsLoaderBack
                ? "• Turns 'Use Windows display calibration' back off, as it was before SoftCal: it cannot load the curves here (Windows asks for administrator permission once)."
                : string.Empty,
            "• Loads the curves in the graphics card now.",
            string.Empty,
            advanced
                ? "Note: HDR or Auto Color Management is on for this display, and Windows ignores calibration curves while it is. Turn it off in Settings › System › Display to use them."
                : string.Empty,
            $"Remove puts all of it back. The curves fit the profile {mode} only: applying another profile in step 1 switches to that one's calibration, or removes this one.");
        return MessageBox.Show(this, text, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        DiscardCalibration();
        Log("Discarded: nothing was installed.");
        SaveCalibrationLog();
    }

    private async void CheckCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (InstallKey is not { } key || CalibrationInstaller.Current(key) is not { } record || SelectedMonitor is not { } monitor)
        {
            return;
        }

        try
        {
            var check = CalibrationInstaller.Check(record, monitor);
            Log(check.Message);
            if (check.Fixes.Count == 0 || !CanChangeWindows() || !ConfirmRepair(check.Fixes))
            {
                return;
            }

            _calibrating = new CancellationTokenSource();
            SetCalibrating(true, cancellable: false);
            try
            {
                var (repaired, log) = await CalibrationInstaller.RepairAsync(record, monitor, CancellationToken.None);
                foreach (var line in log)
                {
                    Log(line);
                }

                Log(CalibrationInstaller.Check(repaired, monitor).Message);
            }
            finally
            {
                _calibrating?.Dispose();
                _calibrating = null;
                SetCalibrating(false, cancellable: false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or IccFormatException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            Log("Could not check: " + ex.Message);
        }
    }

    /// <summary>Asks before fixing what Check found (the log says why each is needed).</summary>
    private bool ConfirmRepair(IReadOnlyList<string> fixes) => MessageBox.Show(
        this,
        string.Join(
            Environment.NewLine,
            [
                "Check found something to fix (see the log). Fix it now?",
                string.Empty,
                .. fixes.Select(f => "• " + f),
                string.Empty,
                "Remove puts all of it back.",
            ]),
        Title,
        MessageBoxButton.OKCancel,
        MessageBoxImage.Question,
        MessageBoxResult.Cancel) == MessageBoxResult.OK;

    /// <summary>Measures the display as Windows shows it, with the installed curves left on, and compares it with the
    /// calibration run's measurement before the curves (and, in the log, with the trial).</summary>
    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (Busy)
        {
            return;
        }

        // Busy from the first moment, and everything that can fail (the display may be off) inside the try.
        var calibrating = _calibrating = new CancellationTokenSource();
        SetCalibrating(true, cancellable: true);
        try
        {
            if (InstallKey is not { } key || CalibrationInstaller.Current(key) is not { } record || SelectedMonitor is not { } monitor)
            {
                return;
            }

            await StopPreviewAsync();
            CalibrateLog.Clear();
            var check = CalibrationInstaller.Check(record, monitor);
            Log(check.Message);
            if (!check.Loaded)
            {
                Log("Verify measures what Windows shows with the calibration loaded, so it would not tell anything now.");
                return;
            }

            StepAside(null);
            var request = CalibrationRequest() with { KeepCurves = true };
            var (before, trial) = TrialReports(record, request);
            var outcome = await MeasurementSession.VerifyAsync(request, new Progress<string>(Log), calibrating.Token);
            var now = outcome.Report;
            var targetGamma = record.Target.StartsWith("own tone", StringComparison.Ordinal) && before is not null ? before.Tone.EffectiveGamma : 2.2;
            if (before is not null)
            {
                CalibrationBeforeColumn.Header = "Before calibrating (no curves)";
                CalibrationAfterColumn.Header = string.Create(CultureInfo.CurrentCulture, $"Now, calibrated ({now.CreatedUtc.ToLocalTime():HH:mm})");
                ShowCalibrationRows(ReportComparison.Compare(before, now, targetGamma).Select(ToItem).ToList());
            }

            double? drift = null;
            if (trial is not null)
            {
                drift = Math.Abs(HalfCode(now) - HalfCode(trial));
                Log(string.Create(
                    CultureInfo.CurrentCulture,
                    $"Trial at {trial.CreatedUtc.ToLocalTime():HH:mm}: gamma {trial.Tone.EffectiveGamma:0.00}, half of white at code {HalfCode(trial):0.0}. Now: gamma {now.Tone.EffectiveGamma:0.00}, half of white at code {HalfCode(now):0.0}."));
            }

            Log(drift switch
            {
                null => "Done.",
                <= 1.5 => "Done: the display shows what the trial measured, so the installed calibration works as intended.",
                _ => "Done, but the display differs from the trial by more than a code: the monitor (or the camera's position) changed since. Create the calibration again if it stays so.",
            });
            SaveVerification(outcome);
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled. Nothing was changed.");
        }
        catch (Exception ex) when (ex is MeasurementStoppedException or MeasurementException)
        {
            Log("Stopped: " + ex.Message);
        }
#pragma warning disable CA1031 // An async void handler must never let an error crash the app; it is shown and logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var log = AppPaths.WriteErrorLog(ex);
            Log($"Stopped by an unexpected error: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}Details: {log}");
        }
        finally
        {
            calibrating.Dispose();
            if (ReferenceEquals(_calibrating, calibrating))
            {
                _calibrating = null;
            }

            StepBack();
            SetCalibrating(false, cancellable: false);
        }
    }

    /// <summary>The code giving half of white (the tree's first level).</summary>
    private static double HalfCode(DisplayReport report) =>
        report.Tone.Points.OrderBy(p => Math.Abs(p.Fraction - 0.5)).First().Code;

    /// <summary>The calibration run's reports: the display before the curves, and with the trial curves. Older records
    /// have no stamp: the newest run before the installation is taken.</summary>
    private static (DisplayReport? Before, DisplayReport? Trial) TrialReports(InstalledCalibration record, MeasurementRequest request)
    {
        var folder = System.IO.Path.Combine(AppPaths.Reports, record.DisplayKey);
        var stamp = record.TrialStamp;
        if (stamp is null && Directory.Exists(folder))
        {
            stamp = Directory.GetFiles(folder, "*.calibration-before.measurement.json")
                .Where(f => File.GetLastWriteTime(f) <= record.InstalledAt.LocalDateTime)
                .Select(f => System.IO.Path.GetFileName(f).Split('.')[0])
                .Order(StringComparer.Ordinal)
                .LastOrDefault();
        }

        if (stamp is null)
        {
            return (null, null);
        }

        var context = MeasurementSession.Context(request, request.CameraName ?? "camera");
        DisplayReport? Load(string name)
        {
            var path = System.IO.Path.Combine(folder, $"{stamp}.calibration-{name}.measurement.json");
            try
            {
                return File.Exists(path) && ReportWriter.MeasurementFromJson(File.ReadAllText(path)) is { } m
                    ? DisplayAnalysis.Analyze(m, context) with { CreatedUtc = File.GetLastWriteTimeUtc(path) }
                    : null;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                return null;
            }
        }

        return (Load("before"), Load("after"));
    }

    private void SaveVerification(MeasurementOutcome outcome)
    {
        var folder = ReportFolder(outcome.Report.DisplayKey, Simulated);
        Directory.CreateDirectory(folder);
        var stem = System.IO.Path.Combine(folder, outcome.Report.CreatedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".verify");
        File.WriteAllText(stem + ".measurement.json", ReportWriter.MeasurementToJson(outcome.Measurement));
        File.WriteAllText(stem + ".html", ReportWriter.ToHtml(outcome.Report));
        File.WriteAllText(stem + ".log.txt", CalibrateLog.Text);
    }

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (Busy)
        {
            return;
        }

        InstalledCalibration? found;
        try
        {
            found = InstallKey is { } installKey ? CalibrationInstaller.Current(installKey) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("Could not read the installed calibration: " + ex.Message);
            return;
        }

        if (found is not { } record || SelectedMonitor is not { } monitor || !CanChangeWindows())
        {
            return;
        }

        // Only a calibration from before SoftCal had its own loader turned Windows' loader on.
        var restoreLoader = RestoreLoaderCheck.IsChecked == true && !record.UsesSoftCalLoader && CalibrationInstaller.NeedsWindowsLoaderBack(record);
        var lastOne = CalibrationInstaller.All().Count <= 1;
        var text = string.Join(
            Environment.NewLine,
            $"Remove SoftCal's calibration from {CalibratedDisplayName}?",
            string.Empty,
            $"• Removes {record.ProfileFile} from the display and from Windows' color profiles.",
            $"• Makes {record.PreviousDefault ?? "no profile"} your default again{(record.PerUserWasOn ? string.Empty : ", and turns 'Use my settings for this device' back off (Windows then uses the system's profile, as before)")}.",
            "• Sets the graphics card's curves back to linear.",
            lastOne
                ? "• Removes SoftCal's loader from your startup apps and stops it."
                : "• Keeps SoftCal's loader for the other calibrated display.",
            restoreLoader
                ? $"• Turns 'Use Windows display calibration' back {(record.LoaderSwitchWasOn ? "on" : "off")}, as it was (administrator prompt)."
                : "• Leaves 'Use Windows display calibration' as it is now.");
        if (MessageBox.Show(this, text, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }

        _calibrating = new CancellationTokenSource();
        SetCalibrating(true, cancellable: false);
        try
        {
            foreach (var line in await CalibrationInstaller.RevertAsync(record, monitor, restoreLoader, CancellationToken.None))
            {
                Log(line);
            }

            // Its profile does not keep it either: applying that profile no longer brings it back.
            if (SettingsKey is { } profilesKey && OwnerOf(record) is { } owner)
            {
                try
                {
                    Profiles.SetCalibration(profilesKey, owner, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Log($"The profile {owner} still names it ({ex.Message}).");
                }
            }

            Log("Removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            Log("Remove stopped: " + ex.Message);
        }
        finally
        {
            _calibrating?.Dispose();
            _calibrating = null;
            SetCalibrating(false, cancellable: false);
            SaveCalibrationLog();
        }
    }

    /// <summary>The display's primaries and white from its EDID (the camera does not measure absolute color); sRGB's
    /// when the EDID is missing.</summary>
    private RgbPrimaries Primaries()
    {
        if (!Simulated && SelectedMonitor?.Edid is { } bytes)
        {
            try
            {
                return Edid.Parse(bytes).Chromaticities;
            }
            catch (EdidFormatException)
            {
                // Fall through to sRGB.
            }
        }

        return RgbPrimaries.Srgb;
    }

    private void RefreshInstalled()
    {
        var record = InstallKey is { } key ? CalibrationInstaller.Current(key) : null;
        var owner = record is null ? null : OwnerOf(record);
        InstalledText.Text = Simulated
            ? "Simulator: nothing is installed on this PC."
            : record is null
                ? "No SoftCal calibration installed."
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"Installed: {(owner is null ? "no profile's" : owner + "'s")} calibration ({record.Target}, {record.InstalledAt.LocalDateTime:g}).{(!string.Equals(owner, CurrentProfileName, StringComparison.OrdinalIgnoreCase) ? $" Not {CurrentProfileName}'s: load {CurrentProfileName} in step 1 to switch." : string.Empty)}");
        RestoreLoaderCheck.Visibility = record is { UsesSoftCalLoader: false } ? Visibility.Visible : Visibility.Collapsed;
        UpdateCalibrationButtons();
    }

    /// <summary>The profile an installed calibration belongs to (null: none, e.g. made before profiles for a picture
    /// mode no profile is named after).</summary>
    private string? OwnerOf(InstalledCalibration record)
    {
        if (SettingsKey is not { } key)
        {
            return null;
        }

        try
        {
            AdoptInstalledCalibration(key);
            return Profiles.List(key).FirstOrDefault(p => string.Equals(p.Calibration?.File, record.ProfileFile, StringComparison.OrdinalIgnoreCase))?.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void UpdateCalibrationButtons()
    {
        var busy = _calibrating is not null;
        CalibrateStartButton.IsEnabled = !busy && CameraPlaced && !BlockedHere;
        InstallButton.IsEnabled = !busy && _calibration is not null;
        InstallButton.Content = Simulated ? "Save the file" : "Install…";
        DiscardButton.IsEnabled = !busy && _calibration is not null;
        var installed = !busy && InstallKey is { } key && CalibrationInstaller.Current(key) is not null && SelectedMonitor is not null;
        CheckCalibrationButton.IsEnabled = installed;
        VerifyButton.IsEnabled = installed && CameraPlaced && !BlockedHere;
        RevertButton.IsEnabled = installed;
    }

    private void SetCalibrating(bool on, bool cancellable)
    {
        CalibrateCancelButton.IsEnabled = on && cancellable;
        CalibrateBusy.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        ToneTargetBox.IsEnabled = !on;
        NeutralGraysCheck.IsEnabled = !on;
        RefineCheck.IsEnabled = !on;
        RemeasureCheck.IsEnabled = !on;
        if (!on)
        {
            RefreshInstalled();
        }

        UpdateCalibrationButtons();
        UpdateNavigation();
    }

    private void Log(string line)
    {
        CalibrateLog.Visibility = Visibility.Visible;
        CalibrateLog.AppendText(line + Environment.NewLine);
        CalibrateLog.ScrollToEnd();
    }
}
