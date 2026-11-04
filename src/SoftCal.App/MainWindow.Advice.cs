using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoftCal.App.Services;
using SoftCal.Core.Advisor;
using SoftCal.Core.Analysis;
using SoftCal.Core.Instruments;
using SoftCal.Windows.Displays;

namespace SoftCal.App;

/// <summary>Steps 3 and 4: one measurement for the monitor settings (the advisor, with the user or DDC/CI carrying out
/// any change), then the before/after table.</summary>
public partial class MainWindow
{
    /// <summary>The run's usual stretches when nothing needs changing: preparing the camera, the brightness check and
    /// the first readings (seconds; from the BenQ runs of 2026-10-18).</summary>
    private const double UsualRunSeconds = 8 + 13 + 27;

    private CancellationTokenSource? _advising;
    private AdvisorSetup? _adviceSetup;
    private TaskCompletionSource<PromptAnswer>? _pendingChange;
    private TaskCompletionSource<bool>? _pendingFix;
    private TaskCompletionSource<GainsAnswer>? _pendingGains;
    private bool _gainsAsked;

    /// <summary>Where SoftCal's window was before a run moved it off the measured screen.</summary>
    private WindowSpot? _spotBeforeRun;
    private OsdControl? _pendingControl;
    private Advice? _pendingAdvice;
    private SettingsOutcome? _result;

    private DispatcherTimer? _phaseTimer;
    private DispatcherTimer? _alertTimer;

    /// <summary>How the last run ended when it did not finish (stopped, cancelled, failed): the step keeps saying it
    /// until the next run.</summary>
    private string? _runEnding;
    private string? _phaseName;
    private double _phaseSeconds;
    private DateTime _phaseStarted;
    private double _plannedSeconds;
    private double _doneSeconds;
    private int _phaseCount;
    private double _shownProgress;

    /// <summary>The result of the last run, as shown in step 4.</summary>
    internal sealed record SettingsOutcome(IReadOnlyList<SettingRow> Rows, IReadOnlyList<string> Limits);

    private void PrepareAdvice()
    {
        _adviceSetup = AdvisorSetup.From(CurrentMonitorSettings());
        var ready = _adviceSetup.Profile.Controls.Count > 0;
        AdviceStartButton.IsEnabled = ready && !Busy && !BlockedHere;
        // The simulator's demo monitor answers over DDC/CI too.
        DdcApplyCheck.IsEnabled = ready && !Busy;
        if (_advising is null)
        {
            if (_result is null)
            {
                MeasureProgress.Value = 0;
            }

            MeasurePhaseText.Text = !ready
                ? "Enter Brightness and Contrast in step 1 first."
                : BlockedHere ? "Turn off what is shown above, then Start."
                : _runEnding ?? (_result is null ? "Ready when you are." : "Measured. Next shows the result; Start measures once more.");
        }

        UpdateNavigation();
    }

    private async void AdviceStart_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || _adviceSetup is not { } setup)
        {
            return;
        }

        // Busy from the first moment: a second click, or the menu, must not start anything while the camera view closes.
        var advising = _advising = new CancellationTokenSource();
        var started = DateTimeOffset.Now;
        var transcript = new List<AdviceRecord>();
        var automatic = DdcApplyCheck.IsChecked == true && DdcApplyCheck.IsEnabled;
        var showResult = false;
        WhiteMatchResult? white = null;
        _result = null;
        AdviceStartButton.IsEnabled = false;
        AdviceCancelButton.IsEnabled = true;
        DdcApplyCheck.IsEnabled = false;
        AdviceLog.Clear();
        MeterText.Text = string.Empty;
        BeginProgress();
        UpdateNavigation();
        void Log(string line)
        {
            AdviceLog.AppendText(line + Environment.NewLine);
            AdviceLog.ScrollToEnd();
        }

        var (reference, referenceName) = SelectedReference;
        var request = new MeasurementRequest(
            SelectedMonitor,
            MeasuringWebcam,
            Simulated,
            TnPanelCheck.IsChecked == true,
            CurrentProfileName,
            Reference: reference,
            ReferenceName: referenceName,
            Screens: Simulated ? null : new WindowScreens(this),
            Alerts: new Progress<string>(ShowMeasureAlert),
            Phone: MeasuringPhone);
        Log(MeasuredWith(request, started).Replace("Measured with", "Measuring with", StringComparison.Ordinal));
        try
        {
            await StopPreviewAsync();
            StepAside(reference);
            var outcome = await MeasurementSession.AdviseAsync(
                request,
                setup,
                new WindowPrompt(this),
                automatic,
                new Progress<string>(Log),
                new Progress<Advice>(advice =>
                {
                    Log(advice.Kind switch
                    {
                        AdviceKind.Measure => "… " + advice.Message,
                        AdviceKind.Done => "Done. " + advice.Message,
                        _ => $"→ {advice.Topic}: {advice.Message}",
                    });
                    OnAdvised(advice, automatic);
                }),
                new Progress<AdvisorStep>(step =>
                {
                    var meter = AdvisorMeter.Describe(step.Readings);
                    MeterText.Text = string.Join(Environment.NewLine, meter);
                    transcript.Add(new AdviceRecord(DateTimeOffset.Now, step.Advice, step.ReportedValue, meter, AdviceRecord.Raw(step.Readings)));
                }),
                new Progress<MeasurePhase>(phase => StartPhase(phase.Name, phase.Seconds)),
                advising.Token);
            white = outcome.White;
            if (outcome.DdcNote is { } note)
            {
                Log(note);
            }

            if (outcome.Finished)
            {
                ShowResult(setup, outcome, request, started);
                if (SettingsKey is { } key)
                {
                    try
                    {
                        // The new values are the monitor's now, and the profile measured keeps them.
                        var measured = request.Profile ?? CurrentProfileName;
                        Profiles.SetFromMonitor(key, measured, AdvisorSetup.Apply(CurrentMonitorSettings(), setup.Profile, outcome.Settings, outcome.White));
                        LoadMonitorSettings(measured);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log($"The new values could not be saved ({ex.Message}): type them in step 1 before measuring again.");
                    }
                }

                EndProgress("Done.", complete: true);
                showResult = true;
            }
            else
            {
                EndProgress("Stopped. If you changed the monitor's menu, update step 1 before measuring again.", complete: false);
            }
        }
        catch (OperationCanceledException)
        {
            EndProgress("Cancelled. Everything SoftCal changed has been put back.", complete: false);
        }
        catch (Exception ex) when (ex is MeasurementStoppedException or SoftCal.Core.Measurement.MeasurementException)
        {
            EndProgress("Stopped: " + ex.Message, complete: false);
        }
#pragma warning disable CA1031 // An async void handler must never let an error crash the app; it is shown and logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var log = AppPaths.WriteErrorLog(ex);
            Log($"Details: {log}");
            EndProgress($"Stopped by an unexpected error: {ex.GetType().Name}: {ex.Message}. Everything that was changed is put back.", complete: false);
        }
        finally
        {
            _pendingChange?.TrySetResult(new PromptAnswer(null));
            _pendingFix?.TrySetResult(false);
            _pendingGains?.TrySetResult(new GainsAnswer(false, null));
            StepBack();
            ResultSavedText.Text = SaveTranscript(started, setup, transcript, white, request);
            AdvicePanel.Visibility = Visibility.Collapsed;
            advising.Dispose();
            if (ReferenceEquals(_advising, advising))
            {
                _advising = null;
            }

            AdviceCancelButton.IsEnabled = false;
            PrepareAdvice();
        }

        if (showResult)
        {
            GoTo(ResultIndex);
        }
    }

    /// <summary>Moves the progress on as the advisor goes: a measurement starts at once, or after the user (or DDC/CI)
    /// has made the change asked for.</summary>
    private void OnAdvised(Advice advice, bool automatic)
    {
        switch (advice.Kind)
        {
            case AdviceKind.Measure:
                StartPhase(advice.Message.TrimEnd('.'), Seconds(advice.Then));
                break;
            case AdviceKind.Change when automatic && advice.Control is OsdControlId.Brightness or OsdControlId.Contrast:
                StartPhase(string.Create(CultureInfo.CurrentCulture, $"Setting {advice.Control} to {advice.Value}, then measuring"), Seconds(advice.Then) + 2);
                break;
            case AdviceKind.Change when automatic && advice.Topic == WhiteMatcher.GainsTopic:
                StartPhase("Setting Red, Green and Blue, then measuring", Seconds(advice.Then) + 4);
                break;
            case AdviceKind.Change:
            case AdviceKind.Fix:
                WaitPhase("Waiting for you: " + advice.Topic.ToLower(CultureInfo.CurrentCulture));
                break;
        }
    }

    /// <summary>About how long each part of the advisor's readings takes (BenQ runs, 2026-10-18).</summary>
    private static double Seconds(ProbeParts parts) =>
        (parts.HasFlag(ProbeParts.Shadows) ? 6 : 0) + (parts.HasFlag(ProbeParts.Highlights) ? 6 : 0) + (parts.HasFlag(ProbeParts.Tone) ? 15 : 0)
        + (parts.HasFlag(ProbeParts.White) ? 5 : 0);

    /// <summary>Says, where the progress is, that a reading is being taken again and why; it stays for a while after
    /// the last such reading.</summary>
    private void ShowMeasureAlert(string text)
    {
        MeasureAlertText.Text = text;
        MeasureAlertText.Visibility = Visibility.Visible;
        _alertTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(12), DispatcherPriority.Background, (_, _) =>
        {
            MeasureAlertText.Visibility = Visibility.Collapsed;
            _alertTimer?.Stop();
        }, Dispatcher);
        _alertTimer.Stop();
        _alertTimer.Start();
    }

    private void BeginProgress()
    {
        _runEnding = null;
        _alertTimer?.Stop();
        MeasureAlertText.Visibility = Visibility.Collapsed;
        (_plannedSeconds, _doneSeconds, _phaseCount, _shownProgress, _phaseName) = (UsualRunSeconds, 0, 0, 0, null);
        MeasureProgress.Value = 0;
        MeasurePhaseText.Text = "Starting…";
        _phaseTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => ShowProgress(), Dispatcher);
        _phaseTimer.Start();
    }

    /// <summary>A new stretch of measuring. The first three are the usual run; any further one lengthens it, so the bar
    /// slows down rather than going back.</summary>
    private void StartPhase(string name, double seconds)
    {
        if (_phaseName is not null)
        {
            _doneSeconds += _phaseSeconds;
        }

        if (++_phaseCount > 3)
        {
            _plannedSeconds += seconds;
        }

        (_phaseName, _phaseSeconds, _phaseStarted) = (name, seconds, DateTime.Now);
        ShowProgress();
    }

    private void WaitPhase(string text)
    {
        if (_phaseName is not null)
        {
            _doneSeconds += _phaseSeconds;
        }

        _phaseName = null;
        MeasurePhaseText.Text = text;
    }

    private void ShowProgress()
    {
        if (_phaseName is null)
        {
            return;
        }

        var elapsed = (DateTime.Now - _phaseStarted).TotalSeconds;
        var done = _doneSeconds + Math.Min(elapsed, _phaseSeconds * 0.97);
        _shownProgress = Math.Max(_shownProgress, Math.Min(0.98, done / Math.Max(1, _plannedSeconds)));
        MeasureProgress.Value = 100 * _shownProgress;
        var left = _plannedSeconds - _doneSeconds - elapsed;
        MeasurePhaseText.Text = left > 1.5
            ? string.Create(CultureInfo.CurrentCulture, $"{_phaseName} · about {Math.Ceiling(left):0} s left")
            : $"{_phaseName} · almost done";
    }

    /// <summary>How the run ended, in the step and in the log; an unfinished run's ending stays shown (see
    /// <see cref="_runEnding"/>).</summary>
    private void EndProgress(string text, bool complete)
    {
        _phaseTimer?.Stop();
        _phaseName = null;
        if (complete)
        {
            MeasureProgress.Value = 100;
        }

        MeasurePhaseText.Text = text;
        _runEnding = complete ? null : text;
        AdviceLog.AppendText(text + Environment.NewLine);
        AdviceLog.ScrollToEnd();
    }

    /// <summary>The before/after table: the run's own settings, and the color settings by the factory-preset rule.
    /// Above it, what was measured, with which camera, and when.</summary>
    private void ShowResult(AdvisorSetup setup, AdvisorOutcome outcome, MeasurementRequest request, DateTimeOffset started)
    {
        ResultMeasuredText.Text = MeasuredWith(request, started);
        var rows = SettingsResult.Rows(setup.Profile, setup.Settings, outcome.Settings, outcome.BrightnessSetsBacklight, outcome.FinalReadings?.Steps, setup.Color, outcome.White);
        var limits = outcome.Notes?.ToList() ?? [];
        _result = new SettingsOutcome(rows, limits);

        var good = (Brush)FindResource("GoodBrush");
        var goodSoft = (Brush)FindResource("GoodSoftBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var accentSoft = (Brush)FindResource("AccentSoftBrush");
        var text = (Brush)FindResource("TextBrush");
        var dim = (Brush)FindResource("TextDimBrush");
        var card = (Brush)FindResource("CardHoverBrush");
        ResultRows.ItemsSource = rows.Select(r => r.Action switch
        {
            SettingAction.Changed => new ResultRowView(r.Setting, r.Before, r.After, r.Why, "Changed ✓", goodSoft, good, good),
            SettingAction.ChangeInMenu => new ResultRowView(r.Setting, r.Before, r.After, r.Why, "Change in the menu", accentSoft, accent, accent),
            _ => new ResultRowView(r.Setting, r.Before, r.After, r.Why, "Keep", card, dim, text),
        }).ToList();

        static string Count(int n) => n == 1 ? "1 setting" : string.Create(CultureInfo.CurrentCulture, $"{n} settings");
        var changed = rows.Count(r => r.Action == SettingAction.Changed);
        var inMenu = rows.Count(r => r.Action == SettingAction.ChangeInMenu);
        ResultHeadline.Text = (changed, inMenu) switch
        {
            (0, 0) when limits.Count > 0 => "Nothing to change: no setting does better (see below).",
            (0, 0) => "Nothing to change: your settings are already right.",
            (_, 0) => $"Done: {Count(changed)} changed during the measurement.",
            (0, _) => $"Change {Count(inMenu)} in the monitor's menu.",
            _ => $"{Count(changed)} changed, and {Count(inMenu)} to change in the menu.",
        };
        ResultSubline.Text = inMenu > 0
            ? "Set the values in the After column in the monitor's own menu."
            : changed > 0 ? "The new values are set on the monitor now." : "Keep the monitor as it is.";
        ResultLimits.ItemsSource = limits;
        ResultLimitsCard.Visibility = limits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"Measured with the phone camera (Samsung SM-S918W, raw sensor data) · profile "2-DIM" · ZOWIE XL LCD ·
    /// 19 Oct 06:52": what the before and after belong to.</summary>
    private static string MeasuredWith(MeasurementRequest request, DateTimeOffset started)
    {
        var data = request.Phone is { Capabilities.Raw: true } ? ", raw sensor data" : string.Empty;
        var camera = request.CameraName is { Length: > 0 } name && !request.Simulated ? $"the {request.CameraKind} ({name}{data})" : $"the {request.CameraKind}";
        var mode = request.Profile is { Length: > 0 } profile ? $"profile \u201C{profile}\u201D" : "no profile";
        var display = request.Simulated ? "simulated display" : request.Monitor?.FriendlyName ?? request.Monitor?.DeviceName ?? "display";
        return string.Create(CultureInfo.CurrentCulture, $"Measured with {camera}  ·  {mode}  ·  {display}  ·  {started.LocalDateTime:g}");
    }

    private void ResultAgain_Click(object sender, RoutedEventArgs e) => GoTo(MeasureIndex);

    /// <summary>One line of the result table, with its colors.</summary>
    internal sealed record ResultRowView(string Setting, string Before, string After, string Why, string TagText, Brush TagBackground, Brush TagForeground, Brush AfterBrush);

    /// <summary>Keeps the run reviewable after the window closes: the log as shown, and every step with its readings.
    /// Returns a line saying where.</summary>
    private string SaveTranscript(DateTimeOffset started, AdvisorSetup setup, List<AdviceRecord> transcript, WhiteMatchResult? white, MeasurementRequest request)
    {
        try
        {
            var folder = System.IO.Path.Combine(AppPaths.Reports, Simulated ? "simulated" : SelectedDisplayKey ?? "unknown");
            Directory.CreateDirectory(folder);
            var stamp = started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.WriteAllText(System.IO.Path.Combine(folder, stamp + ".advice.txt"), AdviceLog.Text);
            File.WriteAllText(
                System.IO.Path.Combine(folder, stamp + ".advice.json"),
                JsonSerializer.Serialize(new { Started = started, request.CameraKind, Camera = request.CameraName, RawSensorData = request.Phone?.Capabilities.Raw, request.Profile, Controls = setup.Profile, StartSettings = setup.Settings, setup.Color, Steps = transcript, White = white }, TranscriptJson));
            return $"Details saved as {stamp}.advice.txt (report folder).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The measurement's details could not be saved: {ex.Message}";
        }
    }

    private static readonly JsonSerializerOptions TranscriptJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>One advisor step as saved: what was asked, what was reported, what the meter showed, and the readings.</summary>
    private sealed record AdviceRecord(DateTimeOffset Time, Advice Advice, int? ReportedValue, IReadOnlyList<string> Meter, object Readings)
    {
        /// <summary>The readings in brief: step means and each tone level's matched codes.</summary>
        public static object Raw(AdvisorReadings readings) => new
        {
            readings.Fresh,
            Shadows = readings.Shadows.Select(s => new { s.Code, s.Mean, s.ExposureSeconds }),
            Highlights = readings.Highlights.Select(s => new { s.Code, s.Mean, s.ExposureSeconds }),
            Tone = readings.Tone.Select(l => new { l.Level, l.Fraction, l.HalftoneCode, l.Null.MatchedCode, l.Null.MatchedCodePerChannel, l.Null.ExposureSeconds }),
        };
    }

    /// <summary>Cancel puts everything back. A prompt still open is cancelled with the run (its prompt task is cancelled
    /// by the token), never answered "stop": that answer would let the run end as if finished and keep what it set.
    /// Stop, on a prompt, is the way to end the run keeping what is done.</summary>
    private void AdviceCancel_Click(object sender, RoutedEventArgs e) => _advising?.Cancel();

    private void AdviceDone_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingGains is { } gains)
        {
            RgbGains? values = null;
            if (_gainsAsked)
            {
                static int? Read(System.Windows.Controls.TextBox box) =>
                    int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var v) && v <= 255 ? v : null;
                if (Read(AdviceRedBox) is not { } r || Read(AdviceGreenBox) is not { } g || Read(AdviceBlueBox) is not { } b)
                {
                    MessageBox.Show(this, "Type the Red, Green and Blue values the monitor shows now.", Title);
                    return;
                }

                values = new RgbGains(r, g, b);
            }

            HideAdvicePrompt();
            StartPhase("Measuring the white", Seconds(ProbeParts.White));
            gains.TrySetResult(new GainsAnswer(true, values));
            return;
        }

        if (_pendingFix is { } fix)
        {
            HideAdvicePrompt();
            StartPhase("Measuring again", Seconds(_pendingAdvice?.Then ?? ProbeParts.All));
            fix.TrySetResult(true);
            return;
        }

        if (_pendingChange is not { } change || _pendingControl is not { } control)
        {
            return;
        }

        int value;
        if (control.IsPreset)
        {
            value = AdvicePresetBox.SelectedIndex;
        }
        else if (!int.TryParse(AdviceValueBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
        {
            MessageBox.Show(this, $"Type the {control.Name} value the monitor shows now.", Title);
            return;
        }

        HideAdvicePrompt();
        StartPhase(string.Create(CultureInfo.CurrentCulture, $"Measuring with {control.Name} {control.Describe(control.Clamp(value))}"), Seconds(_pendingAdvice?.Then ?? ProbeParts.All));
        change.TrySetResult(new PromptAnswer(control.Clamp(value)));
    }

    /// <summary>Brightness or Contrast written to the monitor over DDC/CI instead of by hand.</summary>
    private void AdviceSetForMe_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingChange is { } change)
        {
            HideAdvicePrompt();
            StartPhase("Setting it over DDC/CI, then measuring", Seconds(_pendingAdvice?.Then ?? ProbeParts.All) + 2);
            change.TrySetResult(new PromptAnswer(null, SetForMe: true));
        }
    }

    private void AdviceStop_Click(object sender, RoutedEventArgs e)
    {
        HideAdvicePrompt();
        _pendingChange?.TrySetResult(new PromptAnswer(null));
        _pendingFix?.TrySetResult(false);
        _pendingGains?.TrySetResult(new GainsAnswer(false, null));
    }

    private void HideAdvicePrompt() => AdvicePanel.Visibility = Visibility.Collapsed;

    /// <summary>Scrolls the prompt into view once it is laid out: in a short window it can sit below the fold.</summary>
    private void RevealPrompt() => Dispatcher.BeginInvoke(() => AdvicePanel.BringIntoView(), DispatcherPriority.Loaded);

    private void ShowChange(OsdControl control, int value, Advice advice, bool canSetForMe, TaskCompletionSource<PromptAnswer> pending)
    {
        (_pendingControl, _pendingChange, _pendingFix, _pendingGains, _pendingAdvice) = (control, pending, null, null, advice);
        AdviceGainsPanel.Visibility = Visibility.Collapsed;
        AdviceCameraPanel.Visibility = Visibility.Collapsed;
        AdviceTopic.Text = advice.Topic;
        AdviceText.Text = advice.Message + (canSetForMe
            ? " Change it in the monitor's menu, close the menu and click Done; or click 'Set it for me'."
            : " Change it in the monitor's menu, close the menu, then click Done.");
        AdviceSetForMeButton.Visibility = canSetForMe ? Visibility.Visible : Visibility.Collapsed;
        AdviceValueLabel.Text = $"{control.Name} now set to:";
        AdviceValueLabel.Visibility = Visibility.Visible;
        AdviceValueBox.Visibility = control.IsPreset ? Visibility.Collapsed : Visibility.Visible;
        AdvicePresetBox.Visibility = control.IsPreset ? Visibility.Visible : Visibility.Collapsed;
        if (control.IsPreset)
        {
            AdvicePresetBox.ItemsSource = control.Presets;
            AdvicePresetBox.SelectedIndex = value;
        }
        else
        {
            AdviceValueBox.Text = value.ToString(CultureInfo.CurrentCulture);
        }

        AdviceStopButton.Visibility = Visibility.Visible;
        AdvicePanel.Visibility = Visibility.Visible;
        AdviceDoneButton.Focus();
        RevealPrompt();
        WaitPhase("Waiting for you: " + advice.Topic.ToLower(CultureInfo.CurrentCulture));
    }

    private void ShowFix(Advice advice, TaskCompletionSource<bool> pending)
    {
        (_pendingControl, _pendingChange, _pendingFix, _pendingGains, _pendingAdvice) = (null, null, pending, null, advice);
        AdviceGainsPanel.Visibility = Visibility.Collapsed;

        // A camera move: the camera's view shows when it rests on the aiming area, straight.
        var aiming = advice.Topic == WhiteMatcher.Topic;
        AdviceCameraPanel.Visibility = aiming ? Visibility.Visible : Visibility.Collapsed;
        if (aiming)
        {
            AdviceCameraImage.Source = null;
            AdviceCameraLevel.Clear();
            AdviceCameraStatus.Text = Simulated ? "Simulator: there is no camera view." : "Starting the camera view…";
        }
        AdviceTopic.Text = advice.Topic;
        AdviceText.Text = advice.Message;
        AdviceSetForMeButton.Visibility = Visibility.Collapsed;
        AdviceValueLabel.Visibility = Visibility.Collapsed;
        AdviceValueBox.Visibility = Visibility.Collapsed;
        AdvicePresetBox.Visibility = Visibility.Collapsed;
        AdvicePanel.Visibility = Visibility.Visible;
        AdviceDoneButton.Focus();
        RevealPrompt();
        WaitPhase("Waiting for you: " + advice.Topic.ToLower(CultureInfo.CurrentCulture));
    }

    /// <summary>A red/green/blue step of the white match: the advice, and the three values when they are asked for
    /// (filled with the values to set, or empty for the values the menu shows).</summary>
    private void ShowGains(Advice advice, RgbGains? values, bool showValues, TaskCompletionSource<GainsAnswer> pending)
    {
        (_pendingControl, _pendingChange, _pendingFix, _pendingGains, _pendingAdvice, _gainsAsked) = (null, null, null, pending, advice, showValues);
        AdviceCameraPanel.Visibility = Visibility.Collapsed;
        AdviceTopic.Text = advice.Topic;
        AdviceText.Text = advice.Message;
        AdviceSetForMeButton.Visibility = Visibility.Collapsed;
        AdviceValueBox.Visibility = Visibility.Collapsed;
        AdvicePresetBox.Visibility = Visibility.Collapsed;
        AdviceValueLabel.Text = "Now set to:";
        AdviceValueLabel.Visibility = showValues ? Visibility.Visible : Visibility.Collapsed;
        AdviceGainsPanel.Visibility = showValues ? Visibility.Visible : Visibility.Collapsed;
        string Text(int? v) => v?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        (AdviceRedBox.Text, AdviceGreenBox.Text, AdviceBlueBox.Text) = (Text(values?.Red), Text(values?.Green), Text(values?.Blue));
        AdviceStopButton.Visibility = Visibility.Visible;
        AdvicePanel.Visibility = Visibility.Visible;
        AdviceDoneButton.Focus();
        RevealPrompt();
        WaitPhase("Waiting for you: " + advice.Topic.ToLower(CultureInfo.CurrentCulture));
    }

    /// <summary>The camera's latest picture during a camera move, while its prompt is up.</summary>
    private void ShowAdviceCamera(BitmapSource bitmap, PlacementView view)
    {
        if (AdviceCameraPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        AdviceCameraImage.Source = bitmap;
        AdviceCameraLevel.Show(bitmap.PixelWidth, bitmap.PixelHeight, view);
        AdviceCameraStatus.Text = PlacementText(view);
    }

    /// <summary>The measured screen is covered by a patch window while the camera rests on it (the camera step, a run):
    /// SoftCal's window moves off it, to the reference screen when one is chosen, and comes back where it was with
    /// <see cref="StepBack"/>. Calls in a row keep the first spot.</summary>
    private void StepAside(DisplayMonitor? reference)
    {
        if (Simulated || SelectedMonitor is not { } measured)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var other = reference ?? _monitors.FirstOrDefault(m => m.DeviceName != measured.DeviceName);
        if (other is null || WindowMover.MonitorOf(hwnd) != measured.Handle)
        {
            return;
        }

        _spotBeforeRun ??= WindowMover.Remember(hwnd);
        WindowMover.CenterOn(hwnd, other);
    }

    private void StepBack()
    {
        if (_spotBeforeRun is { } spot)
        {
            WindowMover.Restore(new WindowInteropHelper(this).Handle, spot);
            _spotBeforeRun = null;
        }
    }

    /// <summary>The white match's view of this window: it moves the window between screens and shows the camera's
    /// picture in the prompt.</summary>
    private sealed class WindowScreens(MainWindow window) : IScreenHost
    {
        public Task KeepWindowOffAsync(DisplayMonitor away, DisplayMonitor to) =>
            window.Dispatcher.InvokeAsync(() =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (WindowMover.MonitorOf(hwnd) == away.Handle)
                {
                    WindowMover.CenterOn(hwnd, to);
                }
            }).Task;

        public void ShowCameraView(RoiImage image, PlacementView view)
        {
            var bitmap = ToBitmap(image);
            _ = window.Dispatcher.InvokeAsync(() => window.ShowAdviceCamera(bitmap, view));
        }

        // Once the run's window has shown its first frame over the aiming area (a moment later), the area closes.
        public void RunCoversScreen() => _ = window.Dispatcher.InvokeAsync(window.CloseAim, DispatcherPriority.Background);
    }

    /// <summary>Asks the user at the monitor, on the UI thread; the advisor waits on a background thread.</summary>
    private sealed class WindowPrompt(MainWindow window) : IAdvicePrompt
    {
        public async Task<GainsAnswer> AskGainsAsync(Advice advice, RgbGains? values, bool showValues, CancellationToken cancellationToken)
        {
            var pending = new TaskCompletionSource<GainsAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() => window.ShowGains(advice, values, showValues, pending));
            using (cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken)))
            {
                return await pending.Task.ConfigureAwait(false);
            }
        }

        public async Task<PromptAnswer> AskChangeAsync(OsdControl control, int value, Advice advice, bool canSetForMe, CancellationToken cancellationToken)
        {
            var pending = new TaskCompletionSource<PromptAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() => window.ShowChange(control, value, advice, canSetForMe, pending));
            using (cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken)))
            {
                return await pending.Task.ConfigureAwait(false);
            }
        }

        public async Task<bool> ConfirmAsync(Advice advice, CancellationToken cancellationToken)
        {
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.Dispatcher.InvokeAsync(() => window.ShowFix(advice, pending));
            using (cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken)))
            {
                return await pending.Task.ConfigureAwait(false);
            }
        }
    }
}
