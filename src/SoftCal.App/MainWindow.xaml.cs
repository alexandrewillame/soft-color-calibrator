using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SoftCal.App.Services;
using SoftCal.Core.Analysis;
using SoftCal.Core.Displays;
using SoftCal.Instruments.Webcam;
using SoftCal.Windows.Displays;
using SoftCal.Windows.OsState;

namespace SoftCal.App;

/// <summary>
/// The window: four steps for the monitor settings (your monitor → camera → measure → result), and two advanced
/// screens (the full display report and the Windows calibration file) reached from the menu's Advanced section.
/// </summary>
public partial class MainWindow : Window
{
    private const int MonitorIndex = 0;
    private const int CameraIndex = 1;
    private const int MeasureIndex = 2;
    private const int ResultIndex = 3;
    private const int ReportIndex = 4;
    private const int CalibrateIndex = 5;

    /// <summary>The steps of the main flow (the advanced screens come after them).</summary>
    private const int MainSteps = 4;

    private readonly FrameworkElement[] _steps;

    /// <summary>Looks every two seconds for what must be off to measure (see <see cref="CheckBlockers"/>).</summary>
    private readonly DispatcherTimer _blockerTimer;
    private IReadOnlyList<PreflightFinding> _blockers = [];
    private IReadOnlyList<DisplayMonitor> _monitors = [];
    private IReadOnlyList<DisplayMonitor> _references = [];
    private IReadOnlyList<WebcamDevice> _cameras = [];
    private WindowsColorState? _preflight;
    private DisplayReport? _report;
    private string? _reportHtmlPath;
    private CancellationTokenSource? _measuring;
    private int _step;
    private int _reached;
    private bool _showingStep;

    /// <summary>The main step an advanced screen was opened from (Back returns there).</summary>
    private int _cameFrom;

    /// <summary>The user asked to close while SoftCal was busy: the window closes once the run has stopped and put back
    /// what it changed.</summary>
    private bool _closeWhenIdle;

    /// <summary>Windows reported a display change: the display list is read again as soon as nothing runs.</summary>
    private bool _monitorsStale;

    /// <summary>The display list is being refilled (the chosen display kept): its selection events are not choices.</summary>
    private bool _refreshingMonitors;

    public MainWindow()
    {
        InitializeComponent();
        _steps = [MonitorStep, CameraStep, MeasureStep, ResultStep, ReportStep, CalibrateStep];
        _monitors = DisplayEnumerator.List();
        MonitorList.ItemsSource = MonitorNames(_monitors);
        MonitorList.SelectedIndex = DefaultMonitor();
        PresetBox.ItemsSource = PresetChoices;
        _blockerTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => LookForBlockers(), Dispatcher);
        Closing += OnClosing;

        // A monitor switched off and on, or the layout changed: the display list is read again.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        // Back from Settings (e.g. after turning Night light off): look again.
        Activated += (_, _) =>
        {
            if (_step == MonitorIndex && !Busy)
            {
                RefreshPreflight();
            }
            else if (Measures(_step))
            {
                CheckBlockers();
            }
        };
        ShowStep(MonitorIndex);
        _blockerTimer.Start();

        // Step 2 opens with the camera used last time (a webcam, or the phone, which is then looked for again).
        RestoreCameraChoice();

        // Step 1 reads the monitor (await): only once the window runs, so the reply comes back on the UI thread.
        Loaded += (_, _) => OnEnter(MonitorIndex);
    }

    /// <summary>The display measured last (its saved settings are the newest), else the one that is not the main
    /// display (this window usually sits on the main one).</summary>
    private int DefaultMonitor()
    {
        var newest = _monitors
            .Select((m, i) => (Index: i, Saved: MonitorProfileStore.Shared.LastSaved(KeyOf(m))))
            .Where(m => m.Saved is not null)
            .OrderByDescending(m => m.Saved)
            .Select(m => (int?)m.Index)
            .FirstOrDefault();
        return newest ?? (_monitors.Count > 1 ? _monitors.ToList().FindIndex(m => !m.IsPrimary) : 0);

        static string? KeyOf(DisplayMonitor monitor)
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
    }

    private DisplayMonitor? SelectedMonitor => MonitorList.SelectedIndex >= 0 && MonitorList.SelectedIndex < _monitors.Count ? _monitors[MonitorList.SelectedIndex] : null;

    private static List<string> MonitorNames(IReadOnlyList<DisplayMonitor> monitors) =>
        [.. monitors.Select(m => $"{m.FriendlyName ?? m.DeviceName} — {m.Bounds.Width}×{m.Bounds.Height}{(m.IsPrimary ? " (main display)" : string.Empty)}")];

    /// <summary>Closing while SoftCal measures or changes settings asks first, then stops the run and closes once it has
    /// put back what it changed (closing at once would leave that to the guardian, or leave an install half done).</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Busy)
        {
            e.Cancel = true;
            if (!_closeWhenIdle && MessageBox.Show(
                    this,
                    "SoftCal is measuring or changing settings. Stop and close? It puts back what it changed first.",
                    Title,
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question,
                    MessageBoxResult.Cancel) == MessageBoxResult.OK)
            {
                _closeWhenIdle = true;
                _measuring?.Cancel();
                _advising?.Cancel();
                _calibrating?.Cancel();
            }

            return;
        }

        _blockerTimer.Stop();
        StopPreview();
        CloseAim();
        ClosePhone();
    }

    /// <summary>Raised on a system thread: noted here, and the list is read again on the next look (see
    /// <see cref="LookForBlockers"/>), when nothing runs.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => _monitorsStale = true);

    /// <summary>Reads the displays again (handles and bounds go stale when a monitor is switched off and on, or the layout
    /// changes). The chosen display stays chosen while it is there; when it is gone, the default one is chosen and step 1
    /// starts again for it.</summary>
    private void RefreshMonitors()
    {
        var chosen = SelectedMonitor;
        var monitors = DisplayEnumerator.List();
        var index = -1;
        for (var i = 0; i < monitors.Count && chosen is not null; i++)
        {
            if (chosen.MonitorDevicePath is { } path
                ? string.Equals(path, monitors[i].MonitorDevicePath, StringComparison.OrdinalIgnoreCase)
                : chosen.DeviceName == monitors[i].DeviceName)
            {
                index = i;
                break;
            }
        }

        _refreshingMonitors = true;
        try
        {
            _monitors = monitors;
            MonitorList.ItemsSource = MonitorNames(monitors);
            MonitorList.SelectedIndex = index >= 0 ? index : DefaultMonitor();
        }
        finally
        {
            _refreshingMonitors = false;
        }

        if (index < 0)
        {
            OnDisplayChosen();
        }
        else
        {
            ListReferences();
        }
    }

    /// <summary>Another display: what was measured, placed or shown for the earlier one no longer applies. The form's
    /// edits go into the profile they were made in first.</summary>
    private void OnDisplayChosen()
    {
        if (_formKey is { } previousKey && ValidateMonitorForm() is null)
        {
            try
            {
                Profiles.SaveCurrent(previousKey, CurrentMonitorSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _profileNote = ("The settings of the display left could not be saved: " + ex.Message, true);
            }
        }

        // The aiming area was on the display left.
        CloseAim();
        ResetLaterSteps();
        RefreshPreflight();
        ListReferences();
        LoadMonitorSettings();
    }

    /// <summary>Another display or profile: the steps after this one must be done again, and nothing measured or created
    /// for the earlier choice stays on show.</summary>
    private void ResetLaterSteps()
    {
        _result = null;
        _runEnding = null;
        _reached = _step < MainSteps ? Math.Min(_reached, _step) : MonitorIndex;
        CameraReadyCheck.IsChecked = false;
        ResultRows.ItemsSource = null;
        ResultLimits.ItemsSource = null;
        ResultLimitsCard.Visibility = Visibility.Collapsed;
        ResultHeadline.Text = string.Empty;
        ResultSubline.Text = string.Empty;
        ResultMeasuredText.Text = string.Empty;
        ResultSavedText.Text = string.Empty;
        DiscardCalibration();
        UpdateNavigation();
    }

    /// <summary>The screen whose white the monitor's is matched to (the simulator's has no monitor), or none.</summary>
    private (DisplayMonitor? Monitor, string? Name) SelectedReference
    {
        get
        {
            var index = ReferenceList.SelectedIndex;
            if (Simulated)
            {
                return index == 0 ? (null, "the reference screen") : (null, null);
            }

            return index >= 0 && index < _references.Count ? (_references[index], _references[index].FriendlyName ?? _references[index].DeviceName) : (null, null);
        }
    }

    /// <summary>Lists the screens the white can be matched to: the other displays, then "don't change the white". The
    /// choice is kept when the list is made again.</summary>
    private void ListReferences()
    {
        var previous = ReferenceList.SelectedItem as string;
        _references = Simulated ? [] : [.. _monitors.Where(m => m.DeviceName != SelectedMonitor?.DeviceName)];
        List<string> names = Simulated ? ["The simulator's reference screen"] : [.. _references.Select(m => m.FriendlyName ?? m.DeviceName)];
        names.Add("Don't change the white");
        ReferenceList.ItemsSource = names;
        ReferenceList.SelectedIndex = previous is null ? 0 : Math.Max(0, names.IndexOf(previous));
    }

    private WebcamDevice? SelectedCamera => CameraList.SelectedIndex >= 0 && CameraList.SelectedIndex < _cameras.Count ? _cameras[CameraList.SelectedIndex] : null;

    private bool Simulated => SimulatedCheck.IsChecked == true;

    private bool Busy => _measuring is not null || _advising is not null || _calibrating is not null || _applying is not null;

    /// <summary>The screens that measure (and need Night light and the like off): the camera, the measurement and the
    /// two advanced screens. The result is not one: Night light may go back on once the measuring is over.</summary>
    private static bool Measures(int step) => step is CameraIndex or MeasureIndex or ReportIndex or CalibrateIndex;

    /// <summary>Something that spoils every reading is on, and this screen measures: it shows what, and its measuring
    /// buttons (and Next, on the camera step) wait.</summary>
    private bool BlockedHere => !Simulated && Measures(_step) && _blockers.Count > 0;

    private string? SelectedDisplayKey
    {
        get
        {
            if (SelectedMonitor?.Edid is not { } bytes)
            {
                return null;
            }

            try
            {
                return DisplayKey.FromEdid(Edid.Parse(bytes)).Value;
            }
            catch (EdidFormatException)
            {
                return null;
            }
        }
    }

    private void ShowStep(int step, bool reach = true)
    {
        _step = step;
        if (reach && step < MainSteps)
        {
            _reached = Math.Max(_reached, step);
        }

        for (var i = 0; i < _steps.Length; i++)
        {
            _steps[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        }

        _showingStep = true;
        StepList.SelectedIndex = step < MainSteps ? step : -1;
        _showingStep = false;
        ReportNavButton.Tag = step == ReportIndex ? "Current" : null;
        CalibrationNavButton.Tag = step == CalibrateIndex ? "Current" : null;
        UpdateNavigation();
    }

    /// <summary>Back, Next and the menu follow the current step: Next is greyed out (with the reason beside it) until
    /// the step is complete, and only steps already reached can be clicked.</summary>
    private void UpdateNavigation()
    {
        if (_steps is null)
        {
            return;
        }

        UpdateProfileButtons();

        var busy = Busy;
        var advanced = _step >= MainSteps;
        BackButton.IsEnabled = !busy && (advanced || _step > 0);
        NextButton.Visibility = !advanced && _step < MainSteps - 1 ? Visibility.Visible : Visibility.Collapsed;
        var canLeave = CanLeave(_step, out var why);
        NextButton.IsEnabled = !busy && canLeave;
        NextHint.Text = busy || canLeave || advanced ? string.Empty : why;
        for (var i = 0; i < StepList.Items.Count; i++)
        {
            if (StepList.Items[i] is ListBoxItem item)
            {
                item.IsEnabled = !busy && i <= _reached;
            }
        }

        ReportNavButton.IsEnabled = !busy;
        CalibrationNavButton.IsEnabled = !busy;
        SimulatedCheck.IsEnabled = !busy;
        StartButton.IsEnabled = !busy && !BlockedHere;

        // Asked to close while busy: now that the run has stopped, close.
        if (_closeWhenIdle && !busy)
        {
            _closeWhenIdle = false;
            Dispatcher.BeginInvoke(Close);
        }
    }

    /// <summary>Whether the user can move on from <paramref name="step"/>, and if not, why.</summary>
    private bool CanLeave(int step, out string? why)
    {
        // Checked first, whatever else holds the step: the form shows its own message.
        var formError = step == MonitorIndex ? ValidateMonitorForm() : null;
        why = step switch
        {
            MonitorIndex when SelectedMonitor is null && !Simulated => "Choose a display.",
            MonitorIndex when !Simulated && _preflight?.Findings.Any(BlocksMeasuring) == true => "Fix what is shown above first.",
            MonitorIndex when formError is not null => formError,
            MonitorIndex when _profileDiffers is { } profile => $"{profile} differs from the monitor: load it or save to it first.",
            CameraIndex when BlockedHere => "Turn off what is shown above first.",
            CameraIndex when !Simulated && _monitors.Count < 2 => SecondScreenNeeded,
            CameraIndex when !Simulated && UsePhone && MeasuringPhone is null => "Connect the phone first: open SoftCal Camera on it and type the code it shows.",
            CameraIndex when !Simulated && !UsePhone && _cameras.Count == 0 => "No USB camera was found (virtual cameras are not used). A phone can be used instead.",
            CameraIndex when !Simulated && CameraReadyCheck.IsChecked != true => "Place the camera, then tick 'The camera is in place'.",
            MeasureIndex when _result is null => "Click Start to measure.",
            _ => null,
        };
        return why is null;
    }

    private void NavInput_Changed(object sender, RoutedEventArgs e) => UpdateNavigation();

    private void Back_Click(object sender, RoutedEventArgs e) =>
        GoTo(_step >= MainSteps ? _cameFrom : Math.Max(0, _step - 1));

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (CanLeave(_step, out _))
        {
            GoTo(Math.Min(MainSteps - 1, _step + 1));
        }
    }

    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingStep || StepList.SelectedIndex < 0 || StepList.SelectedIndex == _step)
        {
            return;
        }

        var target = StepList.SelectedIndex;
        var reachable = !Busy && target <= _reached;

        // Moving on, or coming from an advanced screen: every step before the target must be complete.
        if (reachable && (target > _step || _step >= MainSteps))
        {
            for (var s = 0; s < target && reachable; s++)
            {
                reachable = CanLeave(s, out _);
            }
        }

        if (!reachable)
        {
            // Not reachable from here: put the selection back on the current step.
            ShowStep(_step, reach: false);
            return;
        }

        GoTo(target);
    }

    private void ReportNav_Click(object sender, RoutedEventArgs e) => GoToAdvanced(ReportIndex);

    private void CalibrationNav_Click(object sender, RoutedEventArgs e) => GoToAdvanced(CalibrateIndex);

    /// <summary>Opens an advanced screen without unlocking any step; Back returns to the step it was opened from.</summary>
    private void GoToAdvanced(int screen)
    {
        if (Busy || _step == screen)
        {
            return;
        }

        if (_step < MainSteps)
        {
            _cameFrom = _step;
        }

        OnLeave(_step);
        ShowStep(screen, reach: false);
        OnEnter(screen);
    }

    /// <summary>Leaves the current step (saving what it holds) and enters <paramref name="step"/>.</summary>
    private void GoTo(int step)
    {
        OnLeave(_step);
        ShowStep(step);
        OnEnter(step);
    }

    private void OnLeave(int step)
    {
        switch (step)
        {
            case MonitorIndex:
                SaveMonitorSettings();
                break;
            case CameraIndex:
                StopPreview();
                StopFindingPhones();
                break;
        }
    }

    private void OnEnter(int step)
    {
        if (Measures(step))
        {
            CheckBlockers();
        }
        else
        {
            BlockerBanner.Visibility = Visibility.Collapsed;
        }

        // SoftCal's window stays off the measured screen only while the camera rests on it (steps 2 and 3), and the
        // aiming area keeps that screen dark until a measurement covers it.
        if (step is not (CameraIndex or MeasureIndex))
        {
            CloseAim();
            StepBack();
        }

        switch (step)
        {
            case MonitorIndex:
                RefreshPreflight();
                ListReferences();

                // Back to the same display: the form stays as it was left, edits not saved yet included.
                if (_formKey is null || _formKey != SettingsKey)
                {
                    LoadMonitorSettings();
                }

                break;
            case CameraIndex:
                ListCameras();
                StartPreview();
                break;
            case MeasureIndex:
                PrepareAdvice();
                break;
            case CalibrateIndex:
                PrepareCalibration();
                break;
        }

        UpdateNavigation();
    }

    /// <summary>
    /// The simulator and the real display are two worlds: switching starts again from step 1 for the one chosen, and
    /// nothing measured, placed or typed for the other is used (a real run never starts from the demo's values, nor saves
    /// them as the real monitor's). Step 1's edits are kept for the mode they were made in; an advanced screen is
    /// prepared again for the new mode.
    /// </summary>
    private void Simulated_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (_step == MonitorIndex && _formKey is { } previousKey && ValidateMonitorForm() is null)
        {
            Profiles.SaveCurrent(previousKey, CurrentMonitorSettings());
        }

        ShowSimulatorState();
        CloseAim();
        ResetLaterSteps();

        // Every step after the first is to be done again in the other world.
        _reached = MonitorIndex;
        if (_step is CameraIndex or MeasureIndex or ResultIndex)
        {
            GoTo(MonitorIndex);
        }
        else
        {
            RefreshPreflight();
            ListReferences();
            LoadMonitorSettings();
            if (_step >= MainSteps)
            {
                OnEnter(_step);
            }
        }

        CheckBlockers();
        UpdateNavigation();
    }

    private void MonitorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The constructor picks the default display: step 1's work then waits for Loaded (see the constructor).
        if (!IsLoaded || _refreshingMonitors)
        {
            return;
        }

        OnDisplayChosen();
    }

    /// <summary>HDR and ACM are handled for the user during the measurement; everything else that blocks must be fixed.</summary>
    private static bool BlocksMeasuring(PreflightFinding finding) => WindowsColorStateProbe.BlocksMeasuring(finding);

    /// <summary>
    /// Looks again at what must be off to measure (Night light, a color filter), and on a screen that measures shows it
    /// above everything, with the button that opens where to turn it off; the screen's measuring buttons wait meanwhile.
    /// Run on entering such a screen, when the window comes back to the front, and every two seconds.
    /// </summary>
    private void CheckBlockers()
    {
        var wasBlocked = BlockedHere;
        _blockers = Simulated ? [] : WindowsColorStateProbe.ReadMeasuringBlockers();
        var blocked = BlockedHere;
        BlockerList.ItemsSource = blocked
            ? _blockers.Select(f => new PreflightItem($"{f.Message}{(f.Fix is null ? string.Empty : " " + f.Fix)}", f.Action)).ToList()
            : null;
        BlockerBanner.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        if (blocked != wasBlocked)
        {
            // The screen's own buttons follow.
            if (_step == MeasureIndex && _advising is null)
            {
                PrepareAdvice();
            }
            else if (_step == CalibrateIndex && _calibrating is null)
            {
                UpdateCalibrationButtons();
            }
        }

        UpdateNavigation();
    }

    /// <summary>The two-second look: on a screen that measures, the banner; on step 1, its list, when what blocks has
    /// changed since it was read. Not while measuring (the measurement watches for itself) or on the simulator.</summary>
    private void LookForBlockers()
    {
        if (Busy)
        {
            return;
        }

        if (_monitorsStale)
        {
            _monitorsStale = false;
            RefreshMonitors();
        }

        if (Simulated)
        {
            return;
        }

        if (Measures(_step))
        {
            CheckBlockers();
        }
        else if (_step == MonitorIndex && _preflight is not null)
        {
            var now = WindowsColorStateProbe.ReadMeasuringBlockers().Select(f => f.Message).Order().ToList();
            var shown = _preflight.Findings.Where(f => BlocksMeasuring(f) && f.Area is "Night light" or "Color filters").Select(f => f.Message).Order().ToList();
            if (!now.SequenceEqual(shown))
            {
                RefreshPreflight();
            }
        }
    }

    /// <summary>On the simulator, the display list and the DDC/CI reading have nothing to do: they are greyed out and
    /// step 1 says the values are a pretend monitor's.</summary>
    private void ShowSimulatorState()
    {
        MonitorList.IsEnabled = !Simulated;
        ReadDdcButton.IsEnabled = !Simulated;
        ReadDdcButton.Visibility = Simulated ? Visibility.Collapsed : Visibility.Visible;
        if (Simulated)
        {
            DdcStatus.Text = "Simulator: type any values.";
        }
        else if (DdcStatus.Text.StartsWith("Simulator", StringComparison.Ordinal))
        {
            DdcStatus.Text = string.Empty;
        }
    }

    /// <summary>Only what the user must act on is shown: blockers and warnings. What SoftCal handles itself (HDR and
    /// ACM during the measurement) and plain information stay out of the way.</summary>
    private void RefreshPreflight()
    {
        if (Simulated)
        {
            // Nothing is measured on the simulator, so nothing on this PC needs fixing for it.
            _preflight = null;
            FindingList.ItemsSource = null;
            UpdateNavigation();
            return;
        }

        _preflight = WindowsColorStateProbe.Read(SelectedMonitor is { } m ? [m] : null);
        var items = _preflight.Findings
            .Where(f => BlocksMeasuring(f) || f.Severity == FindingSeverity.Warning)
            .OrderByDescending(f => f.Severity)
            .Select(f => new PreflightItem($"{f.Message}{(f.Fix is null ? string.Empty : " " + f.Fix)}", f.Action))
            .ToList();
        if (Redirected is { } package)
        {
            items.Insert(0, new PreflightItem(
                $"This copy runs inside another app ({package}): measuring works, installing a calibration does not. Start SoftCal from Explorer or the Start menu.",
                null));
        }

        FindingList.ItemsSource = items;
        UpdateNavigation();
    }

    /// <summary>Opens where the user fixes a finding; SoftCal changes nothing there itself.</summary>
    private void PreflightAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PreflightItem { Action: { } action })
        {
            return;
        }

        try
        {
            var start = action.Target switch
            {
                PreflightAction.NvidiaControlPanel => new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel"),
                PreflightAction.NvidiaApp => new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe")),
                _ => new ProcessStartInfo(action.Target),
            };
            start.UseShellExecute = true;
            Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(this, $"Could not open it ({ex.Message}). Open it from the Start menu instead.", Title);
        }
    }

    /// <summary>One thing to fix before measuring, with its optional button.</summary>
    internal sealed record PreflightItem(string Text, PreflightAction? Action)
    {
        public Visibility ActionVisibility => Action is null ? Visibility.Collapsed : Visibility.Visible;

        public string? ActionLabel => Action?.Label;
    }

    /// <summary>The full display report (Advanced): the Quick plan, about 3 minutes.</summary>
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Busy)
        {
            return;
        }

        if (!Simulated && (!HasCamera || CameraReadyCheck.IsChecked != true))
        {
            ReportTitle.Text = "Place the camera first (step 2), then come back here.";
            return;
        }

        // Busy from the first moment: a second click, or the menu, must not start anything while the camera view closes.
        var measuring = _measuring = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        UpdateNavigation();
        BusyBar.Visibility = Visibility.Visible;
        ReportTitle.Text = "Measuring… (about 3 minutes)";
        LogBox.Clear();
        var progress = new Progress<string>(line => LogBox.AppendText(line + Environment.NewLine));
        var request = new MeasurementRequest(SelectedMonitor, MeasuringWebcam, Simulated, TnPanelCheck.IsChecked == true, CurrentProfileName, Phone: MeasuringPhone);
        try
        {
            await StopPreviewAsync();
            StepAside(null);
            var outcome = await MeasurementSession.RunAsync(request, progress, measuring.Token);
            _lastMeasured = new LastMeasurement(outcome.Measurement, request, DateTime.Now);
            ShowReport(outcome, request.Simulated);
        }
        catch (OperationCanceledException)
        {
            ReportTitle.Text = "Cancelled. Everything that was changed has been restored.";
        }
        catch (Exception ex) when (ex is MeasurementStoppedException or SoftCal.Core.Measurement.MeasurementException)
        {
            ReportTitle.Text = "Stopped: " + ex.Message;
        }
#pragma warning disable CA1031 // An async void handler must never let an error crash the app; it is shown and logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var log = AppPaths.WriteErrorLog(ex);
            ReportTitle.Text = $"Stopped by an unexpected error: {ex.GetType().Name}: {ex.Message}";
            LogBox.AppendText($"Details: {log}{Environment.NewLine}Everything that was changed is restored by the guardian.{Environment.NewLine}");
        }
        finally
        {
            measuring.Dispose();
            if (ReferenceEquals(_measuring, measuring))
            {
                _measuring = null;
            }

            StepBack();
            CancelButton.IsEnabled = false;
            BusyBar.Visibility = Visibility.Hidden;
            UpdateNavigation();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _measuring?.Cancel();

    /// <summary>The report folder of a display: its EDID key; "simulated" for the simulator's, "unknown" for a real
    /// display without a readable EDID (never mixed with the simulator's).</summary>
    private static string ReportFolder(string? displayKey, bool simulated) =>
        System.IO.Path.Combine(AppPaths.Reports, displayKey ?? (simulated ? "simulated" : "unknown"));

    private void ShowReport(MeasurementOutcome outcome, bool simulated)
    {
        var report = outcome.Report;
        _report = report;
        var folder = ReportFolder(report.DisplayKey, simulated);
        Directory.CreateDirectory(folder);
        var stamp = report.CreatedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _reportHtmlPath = System.IO.Path.Combine(folder, stamp + ".html");
        File.WriteAllText(_reportHtmlPath, ReportWriter.ToHtml(report));
        File.WriteAllText(System.IO.Path.Combine(folder, stamp + ".json"), ReportWriter.ToJson(report));
        File.WriteAllText(System.IO.Path.Combine(folder, stamp + ".measurement.json"), ReportWriter.MeasurementToJson(outcome.Measurement));
        File.WriteAllText(System.IO.Path.Combine(folder, stamp + ".log.txt"), LogBox.Text);

        var grays = report.Channels.MidGrayTint ?? (report.Channels.Confidence == Confidence.Low ? "not judged" : "neutral");
        var mode = string.IsNullOrWhiteSpace(report.Profile) ? string.Empty : $" (profile {report.Profile})";
        ReportTitle.Text = string.Create(CultureInfo.CurrentCulture, $"{report.DisplayName}{mode}: gamma {report.Tone.EffectiveGamma:0.00}, mid-grays {grays}, contrast {report.Contrast}");
        ReportFindings.ItemsSource = report.Findings.OrderByDescending(f => f.Level).Select(f => $"{f.Level} · {f.Topic}: {f.Message}").ToList();
        OpenReportButton.IsEnabled = true;
        ReportCompare.Visibility = Visibility.Visible;
        ReportCharts.Visibility = Visibility.Visible;
        ListEarlierReports(report);
        DrawTone();
    }

    private void ToneCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTone();

    /// <summary>Measured tone points (accent) against the gamma 2.2 target (dashed), on linear axes.</summary>
    private void DrawTone()
    {
        ToneCanvas.Children.Clear();
        if (_report is null || ToneCanvas.ActualWidth < 20 || ToneCanvas.ActualHeight < 20)
        {
            return;
        }

        double w = ToneCanvas.ActualWidth, h = ToneCanvas.ActualHeight;
        Point At(double code, double fraction) => new(code / 255 * w, (1 - fraction) * h);
        var target = new Polyline { Stroke = (Brush)FindResource("TextOffBrush"), StrokeDashArray = [4, 4], StrokeThickness = 1 };
        for (var c = 0; c <= 255; c += 5)
        {
            target.Points.Add(At(c, Math.Pow(c / 255.0, 2.2)));
        }

        ToneCanvas.Children.Add(target);
        DrawEarlierTone(At);
        ToneEarlierLegend.Visibility = _compareWith is null ? Visibility.Collapsed : Visibility.Visible;
        var accent = (Brush)FindResource("AccentBrush");
        var measured = new Polyline { Stroke = accent, StrokeThickness = 2 };
        measured.Points.Add(At(0, 0));
        foreach (var p in _report.Tone.Points)
        {
            measured.Points.Add(At(p.Code, p.Fraction));
        }

        measured.Points.Add(At(255, 1));
        ToneCanvas.Children.Add(measured);
        foreach (var p in _report.Tone.Points)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Fill = accent };
            var at = At(p.Code, p.Fraction);
            Canvas.SetLeft(dot, at.X - 3.5);
            Canvas.SetTop(dot, at.Y - 3.5);
            ToneCanvas.Children.Add(dot);
        }
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_reportHtmlPath is not null)
        {
            OpenWithWindows(new ProcessStartInfo(_reportHtmlPath) { UseShellExecute = true });
        }
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        var target = _reportHtmlPath ?? AppPaths.Reports;
        OpenWithWindows(new ProcessStartInfo("explorer.exe", _reportHtmlPath is null ? $"\"{target}\"" : $"/select,\"{target}\"") { UseShellExecute = true });
    }

    /// <summary>Opens a file or folder with Windows; says so when it cannot (the file was deleted, no app opens it).</summary>
    private void OpenWithWindows(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show(this, $"Could not open it: {ex.Message}", Title);
        }
    }
}
