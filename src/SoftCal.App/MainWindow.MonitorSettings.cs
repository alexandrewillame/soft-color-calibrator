using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SoftCal.App.Services;
using SoftCal.Core.Advisor;
using SoftCal.Windows.Ddc;

namespace SoftCal.App;

/// <summary>
/// Step 1: the monitor's settings beside a profile's. The left column is the monitor now (read over DDC/CI, typed from
/// its menu, or loaded from a profile), whatever profile is chosen; the right one is the profile chosen, its values
/// that differ highlighted. Load puts the profile on the monitor, Save stores the monitor's settings in it, and
/// measuring adjusts it once they match. The monitor is read over DDC/CI once per display and session; each value says
/// where it came from.
/// </summary>
public partial class MainWindow
{
    /// <summary>The color temperature choices: the user mode (Red, Green and Blue set by hand), a preset, or not sure.
    /// Not read over DDC/CI: what monitors report for it differs by brand (the BenQ XL2720 reports "6500 K" in its user
    /// mode, the ViewSonic VX2770 the same for "Native").</summary>
    private static readonly string[] PresetChoices =
    [
        "User (R, G, B by hand)",
        "A preset (6500 K, Warm…)",
        "Not sure",
    ];

    private readonly HashSet<string> _ddcReadFor = new(StringComparer.Ordinal);

    /// <summary>The settings key the form was filled for (null: a display without a readable EDID): step 1 shown again
    /// for the same one keeps the form as it was left.</summary>
    private string? _formKey;

    /// <summary>Counts the form's fillings and readings: a DDC/CI reading that comes back for an older one is dropped.</summary>
    private int _ddcReadVersion;
    private int? _brightnessMax;
    private int? _contrastMax;

    /// <summary>The profile chosen: compared, loaded, saved, measured and calibrated.</summary>
    private string? _chosenProfile;
    private bool _loadingForm;
    private bool _settingPreset;

    /// <summary>A profile being loaded or deleted (it may install or remove a calibration): SoftCal is busy.</summary>
    private CancellationTokenSource? _applying;

    /// <summary>The color preset's name as read or saved ("6500 K", "user 1"…); the box shows its kind.</summary>
    private string? _presetName;

    /// <summary>What was just done, or went wrong, said under the table until the next change; null: it says how the
    /// profile and the monitor compare.</summary>
    private (string Text, bool Problem)? _profileNote;

    /// <summary>The profile's settings before the last Save, for Undo (null: nothing to undo).</summary>
    private (string Name, MonitorSettings Settings)? _undoSave;

    /// <summary>The profile chosen when it sets values the monitor does not have: measuring waits until one is loaded
    /// or saved onto the other (null: they match, or the profile sets nothing yet).</summary>
    private string? _profileDiffers;

    /// <summary>The status shown last, so it is rebuilt (with its Undo link) only when it changes.</summary>
    private string? _shownStatus;

    private static MonitorProfileStore Profiles => MonitorProfileStore.Shared;

    /// <summary>Where the settings are kept: per monitor, or apart for the simulator, so a demo never overwrites a real
    /// monitor's settings.</summary>
    private string? SettingsKey => Simulated ? "simulated" : SelectedDisplayKey;

    /// <summary>The profile chosen: measured, adjusted and calibrated now.</summary>
    private string CurrentProfileName => _chosenProfile ?? MonitorProfileStore.DefaultName;

    /// <summary>The profile chosen as saved (null: no EDID, or unreadable).</summary>
    private MonitorProfile? ChosenProfile
    {
        get
        {
            try
            {
                return SettingsKey is { } key ? Profiles.Find(key, CurrentProfileName) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Fills the monitor's column with its settings now and lists its profiles (<paramref name="profile"/>
    /// chosen, or the one in use), then reads the monitor over DDC/CI once per display and session.</summary>
    private void LoadMonitorSettings(string? profile = null)
    {
        // A reading still on its way belongs to the form being replaced: it is dropped when it comes back.
        _ddcReadVersion++;
        ReadDdcButton.IsEnabled = !Simulated;
        var key = SettingsKey;
        _formKey = key;
        (_profileNote, _undoSave) = (null, null);
        _loadingForm = true;
        try
        {
            // A display without a readable EDID has nothing saved: its form starts empty, not with the last display's.
            MonitorSettings? current = null;
            try
            {
                if (key is not null)
                {
                    AdoptInstalledCalibration(key);
                    current = Profiles.Current(key);
                    TnPanelCheck.IsChecked = Profiles.IsTnPanel(key) == true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _profileNote = ("Saved settings unreadable: " + ex.Message, true);
            }

            Fill(current ?? new MonitorSettings());
            ShowSources(read: null);
        }
        finally
        {
            _loadingForm = false;
        }

        ShowProfiles(profile);
        if (!Simulated && (key ?? SelectedMonitor?.DeviceName) is { } readKey && _ddcReadFor.Add(readKey))
        {
            ReadDdc_Click(this, new RoutedEventArgs());
        }

        UpdateNavigation();
    }

    /// <summary>Lists the monitor's profiles, <paramref name="select"/> chosen (or the one in use), and shows it beside
    /// the monitor; the monitor's column does not change.</summary>
    private void ShowProfiles(string? select = null)
    {
        var key = SettingsKey;
        _loadingForm = true;
        try
        {
            IReadOnlyList<MonitorProfile> profiles = [];
            MonitorProfile? chosen = null;
            try
            {
                if (key is not null)
                {
                    profiles = Profiles.List(key);
                    chosen = (select is null ? null : Profiles.Find(key, select)) ?? Profiles.Active(key);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _profileNote = ("Profiles unreadable: " + ex.Message, true);
            }

            var name = chosen?.Name ?? MonitorProfileStore.DefaultName;
            ProfileBox.ItemsSource = profiles.Count > 0 ? profiles.Select(p => p.Name).ToList() : [name];
            ProfileBox.SelectedItem = name;
            _chosenProfile = name;
        }
        finally
        {
            _loadingForm = false;
        }

        UpdateProfileButtons();
    }

    /// <summary>A calibration installed before profiles existed (labelled with what is now a profile's name) belongs to
    /// that profile from now on.</summary>
    private void AdoptInstalledCalibration(string key)
    {
        if (!Simulated && InstallKey is { } installKey && CalibrationInstaller.Current(installKey) is { } record)
        {
            Profiles.Adopt(key, record.Profile, new ProfileCalibration(record.ProfileFile, record.Target, record.InstalledAt, record.TrialStamp));
        }
    }

    private void Fill(MonitorSettings s)
    {
        static string? Text(int? value) => value?.ToString(CultureInfo.CurrentCulture);
        BrightnessBox.Text = Text(s.Brightness);
        ContrastBox.Text = Text(s.Contrast);
        SetPreset(s.ColorPreset);
        var (red, green, blue) = s.Gains();
        (RedGainBox.Text, GreenGainBox.Text, BlueGainBox.Text) = (Text(red), Text(green), Text(blue));
        HasGammaCheck.IsChecked = s.HasGammaSetting;
        GammaBox.Text = s.GammaSetting;
        GammaPresetsBox.Text = s.GammaPresets;
        BoostNameBox.Text = s.BlackBoostName;
        BoostValueBox.Text = Text(s.BlackBoost);
        BoostMaxBox.Text = Text(s.BlackBoostMax);
        (_brightnessMax, _contrastMax) = (s.BrightnessMax, s.ContrastMax);
    }

    /// <summary>Shows a preset by its kind; keeps its exact name for the result.</summary>
    private void SetPreset(string? name)
    {
        _presetName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        _settingPreset = true;
        try
        {
            PresetBox.SelectedIndex = SettingsResult.KindOf(_presetName) switch
            {
                ColorPresetKind.User => 0,
                ColorPresetKind.Factory6500 or ColorPresetKind.OtherFactory => 1,
                _ => _presetName is null ? -1 : 2,
            };
        }
        finally
        {
            _settingPreset = false;
        }
    }

    private void MonitorFormSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        // A choice made by hand replaces the name read or saved, unless it is the same kind.
        if (!_loadingForm && !_settingPreset && PresetBox.SelectedIndex >= 0)
        {
            var chosen = PresetBox.SelectedIndex switch
            {
                0 => "User",
                1 => "Preset",
                _ => "Not sure",
            };
            if (SettingsResult.KindOf(chosen) != SettingsResult.KindOf(_presetName))
            {
                _presetName = chosen;
            }

            PresetSource.Text = string.Empty;
            OnMonitorEdited();
        }

        UpdateNavigation();
    }

    /// <summary>Keeps the monitor's column as its settings now (when the form is valid).</summary>
    private void SaveMonitorSettings()
    {
        if (SettingsKey is { } key && ValidateMonitorForm() is null)
        {
            try
            {
                Profiles.SaveCurrent(key, CurrentMonitorSettings());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _profileNote = ("Not saved: " + ex.Message, true);
            }
        }
    }

    /// <summary>The monitor's settings as shown.</summary>
    private MonitorSettings CurrentMonitorSettings()
    {
        static int? Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) ? v : null;
        return new MonitorSettings
        {
            Brightness = Int(BrightnessBox.Text),
            Contrast = Int(ContrastBox.Text),
            ColorPreset = _presetName,
            RedGain = Int(RedGainBox.Text),
            GreenGain = Int(GreenGainBox.Text),
            BlueGain = Int(BlueGainBox.Text),
            HasGammaSetting = HasGammaCheck.IsChecked == true,
            GammaSetting = HasGammaCheck.IsChecked == true ? GammaBox.Text : null,
            GammaPresets = HasGammaCheck.IsChecked == true ? GammaPresetsBox.Text : null,
            BlackBoostName = string.IsNullOrWhiteSpace(BoostNameBox.Text) ? null : BoostNameBox.Text.Trim(),
            BlackBoost = Int(BoostValueBox.Text),
            BlackBoostMax = Int(BoostMaxBox.Text),
            BrightnessMax = _brightnessMax,
            ContrastMax = _contrastMax,
        };
    }

    /// <summary>The first problem in the monitor's column, or null when it can be used. Brightness and contrast are
    /// needed (the advice is about them); the rest may stay empty.</summary>
    private string? ValidateMonitorForm()
    {
        if (MonitorStep is null)
        {
            return null;
        }

        string? Check(TextBox box, string name, int max, bool required = false)
        {
            var text = box.Text.Trim();
            if (text.Length == 0)
            {
                return required ? $"Enter the {name} the monitor's menu shows." : null;
            }

            return int.TryParse(text, NumberStyles.None, CultureInfo.CurrentCulture, out var v) && v <= max
                ? null
                : string.Create(CultureInfo.CurrentCulture, $"{char.ToUpperInvariant(name[0])}{name[1..]} must be a whole number from 0 to {max}.");
        }

        var boostMax = int.TryParse(BoostMaxBox.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var bm) ? bm : 100;
        return Check(BrightnessBox, "brightness", _brightnessMax ?? 100, required: true)
            ?? Check(ContrastBox, "contrast", _contrastMax ?? 100, required: true)
            ?? Check(RedGainBox, "red", 255)
            ?? Check(GreenGainBox, "green", 255)
            ?? Check(BlueGainBox, "blue", 255)
            ?? Check(BoostMaxBox, "shadow boost's highest value", 100)
            ?? Check(BoostValueBox, "shadow boost's value", boostMax)
            ?? (BoostValueBox.Text.Trim().Length > 0 && string.IsNullOrWhiteSpace(BoostNameBox.Text) ? "Give the shadow boost's name as the menu shows it (More settings)." : null);
    }

    private void MonitorForm_Changed(object sender, TextChangedEventArgs e)
    {
        // A value typed by hand is the user's own: the tag beside it ("read", "check it") no longer applies.
        if (!_loadingForm && IsLoaded)
        {
            if (ReferenceEquals(sender, BrightnessBox))
            {
                Typed(BrightnessSource, BrightnessBox.Text.Length == 0);
            }
            else if (ReferenceEquals(sender, ContrastBox))
            {
                Typed(ContrastSource, ContrastBox.Text.Length == 0);
            }
            else if (ReferenceEquals(sender, RedGainBox) || ReferenceEquals(sender, GreenGainBox) || ReferenceEquals(sender, BlueGainBox))
            {
                Typed(GainsSource, RedGainBox.Text.Length == 0 || GreenGainBox.Text.Length == 0 || BlueGainBox.Text.Length == 0);
            }

            OnMonitorEdited();
        }

        UpdateNavigation();
    }

    /// <summary>The gamma setting or the shadow boost added or removed (More settings): their rows follow.</summary>
    private void MonitorExtras_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingForm && IsLoaded)
        {
            OnMonitorEdited();
        }

        UpdateNavigation();
    }

    /// <summary>A value changed by hand: what was just done no longer applies, nor its undo.</summary>
    private void OnMonitorEdited() => (_profileNote, _undoSave) = (null, null);

    /// <summary>After a hand edit, the tag only says when a value is still missing.</summary>
    private void Typed(TextBlock tag, bool empty)
    {
        tag.Text = empty ? "Type it" : string.Empty;
        tag.ToolTip = empty ? "Type it as the monitor's menu shows it." : null;
        tag.Foreground = (Brush)FindResource("WarnBrush");
    }

    /// <summary>Choosing another profile shows it beside the monitor, and makes it the one loaded, saved, measured and
    /// calibrated; the monitor stays as it is until the profile is loaded.</summary>
    private void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingForm || ProfileBox.SelectedItem is not string name || string.Equals(name, _chosenProfile, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _chosenProfile = name;
        (_profileNote, _undoSave) = (null, null);
        if (SettingsKey is { } key)
        {
            try
            {
                if (ValidateMonitorForm() is null)
                {
                    Profiles.SaveCurrent(key, CurrentMonitorSettings());
                }

                Profiles.SetActive(key, name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _profileNote = ("Not saved: " + ex.Message, true);
            }
        }

        // What was measured or created belongs to the profile left.
        ResetLaterSteps();
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || SettingsKey is not { } key)
        {
            return;
        }

        if (ValidateMonitorForm() is { } problem)
        {
            _profileNote = (problem, true);
            UpdateNavigation();
            return;
        }

        var taken = Profiles.List(key).Select(p => p.Name).ToList();
        if (ProfileNameDialog.AskNew(this, taken, SuggestedProfileName(taken)) is not { } name)
        {
            return;
        }

        (_profileNote, _undoSave) = (null, null);
        try
        {
            // It is the monitor's settings: they match from the start.
            var settings = CurrentMonitorSettings();
            Profiles.Create(key, name, settings);
            Profiles.SetFromMonitor(key, name, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _profileNote = ("Not made: " + ex.Message, true);
            UpdateNavigation();
            return;
        }

        ShowProfiles(name);
        ResetLaterSteps();
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || SettingsKey is not { } key || _chosenProfile is not { } current)
        {
            return;
        }

        var taken = Profiles.List(key).Select(p => p.Name).ToList();
        if (ProfileNameDialog.AskRename(this, taken, current) is not { } name || name == current)
        {
            return;
        }

        (_profileNote, _undoSave) = (null, null);
        try
        {
            Profiles.Rename(key, current, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _profileNote = ("Not renamed: " + ex.Message, true);
            UpdateNavigation();
            return;
        }

        ShowProfiles(name);
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || SettingsKey is not { } key || _chosenProfile is not { } current)
        {
            return;
        }

        var profiles = Profiles.List(key);
        if (profiles.Count < 2 || profiles.FirstOrDefault(p => string.Equals(p.Name, current, StringComparison.OrdinalIgnoreCase)) is not { } profile)
        {
            return;
        }

        var installed = !Simulated && InstallKey is { } installKey ? CalibrationInstaller.Current(installKey) : null;
        var inUse = installed is not null && string.Equals(profile.Calibration?.File, installed.ProfileFile, StringComparison.OrdinalIgnoreCase);
        if (!ConfirmDialog.Ask(
            this,
            "Delete profile",
            $"Delete the profile {current}?",
            [],
            "Delete",
            note: "The monitor keeps its current settings.",
            warning: inUse ? "Its calibration, in use now, is removed too." : null))
        {
            return;
        }

        (_profileNote, _undoSave) = (null, null);
        if (inUse && (SelectedMonitor is null || !CanChangeWindows()))
        {
            _profileNote = ("Not deleted: remove its calibration first (Advanced › Windows calibration file).", true);
            UpdateNavigation();
            return;
        }

        _applying = new CancellationTokenSource();
        UpdateNavigation();
        try
        {
            if (inUse && installed is not null && SelectedMonitor is { } monitor)
            {
                await CalibrationInstaller.RevertAsync(installed, monitor, restoreLoader: false, CancellationToken.None);
            }

            Profiles.Delete(key, current);
            ShowProfiles();
            _profileNote = ($"Deleted {current}.", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or ArgumentException)
        {
            _profileNote = ("Not deleted: " + ex.Message, true);
        }
        finally
        {
            _applying.Dispose();
            _applying = null;
            ResetLaterSteps();
        }
    }

    /// <summary>Stores the monitor's settings in the profile chosen, after asking (listing what it overwrites); it can be
    /// undone until the next change. A calibration made with the profile's old settings stays its own, said to be
    /// measured again.</summary>
    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || SettingsKey is not { } key || ChosenProfile is not { } profile)
        {
            return;
        }

        if (ValidateMonitorForm() is { } problem)
        {
            _profileNote = (problem, true);
            UpdateNavigation();
            return;
        }

        var settings = CurrentMonitorSettings();
        var empty = ProfileComparison.IsEmpty(profile.Settings);
        var changed = !empty && ProfileComparison.Changes(profile.Settings, settings).Count > 0;
        var oldCalibration = profile.Calibration is not null && changed;
        if (!ConfirmDialog.Ask(
            this,
            "Save to profile",
            $"Save the monitor's settings to {profile.Name}?",
            ProfileComparison.Describe(profile.Settings, settings),
            "Save",
            note: empty ? $"{profile.Name} has no settings yet." : null,
            warning: oldCalibration ? "Its calibration was made with the old settings: measure again afterwards to update it." : null))
        {
            return;
        }

        try
        {
            Profiles.SetFromMonitor(key, profile.Name, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _profileNote = ("Not saved: " + ex.Message, true);
            UpdateNavigation();
            return;
        }

        _undoSave = (profile.Name, profile.Settings);
        _profileNote = (oldCalibration ? "Saved. Measure again to update its calibration." : "Saved.", false);
        UpdateNavigation();
    }

    /// <summary>Puts back the profile's settings from before the last Save.</summary>
    private void UndoSave_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || SettingsKey is not { } key || _undoSave is not { } undo)
        {
            return;
        }

        (_profileNote, _undoSave) = (null, null);
        try
        {
            Profiles.SaveSettings(key, undo.Name, undo.Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _profileNote = ("Not undone: " + ex.Message, true);
        }

        UpdateNavigation();
    }

    /// <summary>
    /// Puts the profile chosen on the monitor, after asking (listing what changes, what is left to the menu, and the
    /// monitor's settings that no profile keeps, which would be lost): what differs of Brightness, Contrast (and Red,
    /// Green, Blue in the User mode) over DDC/CI, the rest said for the menu, and Windows' calibration made the profile's
    /// own (or none). Then reads the monitor back, so its side is what it has.
    /// </summary>
    private async void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || SettingsKey is not { } key || ChosenProfile is not { } profile || ProfileComparison.IsEmpty(profile.Settings))
        {
            return;
        }

        var now = CurrentMonitorSettings();
        var loaded = ProfileComparison.Loaded(now, profile.Settings);
        var changes = ProfileComparison.Describe(now, profile.Settings).ToList();
        var unsaved = ValidateMonitorForm() is null
            && !Profiles.List(key).Any(p => !ProfileComparison.IsEmpty(p.Settings) && ProfileComparison.Differences(now, p.Settings).Count == 0);
        var lost = unsaved ? "The monitor's settings are in no profile: they will be lost (New… keeps them in a profile first)." : null;
        if (Simulated)
        {
            if (!ConfirmDialog.Ask(this, "Load to the monitor", $"Load {profile.Name} to the monitor?", changes, "Load", warning: lost))
            {
                return;
            }

            // The demo monitor takes the profile's settings as they are.
            (_profileNote, _undoSave) = (null, null);
            ShowLoaded(key, loaded);
            _profileNote = ("Loaded.", false);
            UpdateNavigation();
            return;
        }

        if (SelectedMonitor is not { } monitor || InstallKey is not { } installKey)
        {
            return;
        }

        var installed = CalibrationInstaller.Current(installKey);
        var channels = DdcCiChannel.Open(monitor);
        var ddc = channels.Count > 0;
        foreach (var channel in channels)
        {
            channel.Dispose();
        }

        var plan = ProfileSwitch.Plan(profile, installed, ddc, ProfileSwitch.CalibrationFileExists, now);
        if (plan.Calibration is CalibrationChange.Install or CalibrationChange.Remove && Redirected is not null)
        {
            // Windows keeps a redirected copy's changes to itself: the calibration is left as it is.
            plan = plan with { Calibration = CalibrationChange.None };
        }

        if (plan.Calibration is CalibrationChange.Install or CalibrationChange.Remove)
        {
            var owner = installed is null ? null : Profiles.List(key).FirstOrDefault(p => string.Equals(p.Calibration?.File, installed.ProfileFile, StringComparison.OrdinalIgnoreCase))?.Name;
            var from = installed is null ? "none" : owner is null ? "not a profile's" : owner + "'s";
            changes.Add($"Calibration: {from} → {(plan.Calibration == CalibrationChange.Install ? profile.Name + "'s" : "none")}");
        }

        if (!ConfirmDialog.Ask(
            this,
            "Load to the monitor",
            $"Load {profile.Name} to the monitor?",
            changes,
            "Load",
            note: plan.ByHand.Count > 0 ? "In the monitor's menu, you then set " + string.Join("; ", plan.ByHand) + "." : null,
            warning: lost))
        {
            return;
        }

        (_profileNote, _undoSave) = (null, null);
        _applying = new CancellationTokenSource();
        _profileNote = ("Loading…", false);
        UpdateNavigation();
        try
        {
            var log = await ProfileSwitch.ApplyAsync(monitor, installKey, profile, plan, installed, _applying.Token);
            ShowLoaded(key, loaded);
            var problems = log.Where(l => !l.StartsWith("Set over DDC/CI", StringComparison.Ordinal) && !l.StartsWith("Windows uses", StringComparison.Ordinal) && !l.StartsWith("Its calibration is the one", StringComparison.Ordinal) && !l.StartsWith("Removed the calibration", StringComparison.Ordinal)).ToList();
            _profileNote = (
                "Loaded." + (plan.ByHand.Count > 0 ? " In the menu, set " + string.Join("; ", plan.ByHand) + "." : string.Empty) + (problems.Count > 0 ? " " + string.Join(" ", problems) : string.Empty),
                problems.Count > 0);

            // What the monitor has now, read back where it can be.
            ReadDdc_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or OperationCanceledException)
        {
            _profileNote = ("Loading stopped: " + ex.Message, true);
        }
        finally
        {
            _applying.Dispose();
            _applying = null;
            UpdateNavigation();
        }
    }

    /// <summary>The monitor's column after loading a profile: <paramref name="loaded"/>, kept as its settings now.</summary>
    private void ShowLoaded(string key, MonitorSettings loaded)
    {
        _loadingForm = true;
        try
        {
            Fill(loaded);
        }
        finally
        {
            _loadingForm = false;
        }

        ShowSources(read: null);
        try
        {
            Profiles.SaveCurrent(key, loaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _profileNote = ("Not saved: " + ex.Message, true);
        }
    }

    /// <summary>"Profile 2", "Profile 3"…: the first such name not taken.</summary>
    private static string SuggestedProfileName(IReadOnlyList<string> taken)
    {
        for (var n = taken.Count + 1; ; n++)
        {
            var name = string.Create(CultureInfo.CurrentCulture, $"Profile {n}");
            if (!taken.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return name;
            }
        }
    }

    private void UpdateProfileButtons()
    {
        var usable = SettingsKey is not null && !Busy;
        ProfileBox.IsEnabled = usable;
        NewProfileButton.IsEnabled = usable;
        RenameProfileButton.IsEnabled = usable;
        DeleteProfileButton.IsEnabled = usable && ProfileBox.Items.Count > 1;
        PanelLookupButton.IsEnabled = !Simulated && SelectedMonitor is not null;
        ShowComparison();
    }

    /// <summary>
    /// Shows the profile chosen beside the monitor: its values, those that differ highlighted, and in a few words where
    /// they stand (or what was just done). Save and Load are offered when they would change something.
    /// </summary>
    private void ShowComparison()
    {
        var key = SettingsKey;
        var name = CurrentProfileName;
        IReadOnlyList<MonitorProfile> profiles = [];
        InstalledCalibration? installed = null;
        try
        {
            profiles = key is null ? [] : Profiles.List(key);
            installed = InstallKey is { } installKey ? CalibrationInstaller.Current(installKey) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _profileNote ??= ("Profiles unreadable: " + ex.Message, true);
        }

        var profile = profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var saved = profile?.Settings ?? new MonitorSettings();
        var monitor = CurrentMonitorSettings();
        var empty = ProfileComparison.IsEmpty(saved);
        var differences = empty ? new HashSet<ProfileSetting>() : ProfileComparison.Differences(monitor, saved);
        var warn = (Brush)FindResource("WarnBrush");
        var normal = (Brush)FindResource("TextBrush");
        var dim = (Brush)FindResource("TextDimBrush");
        void Show(TextBlock block, string value, bool differs)
        {
            block.Text = value;
            block.Foreground = differs ? warn : value == "—" ? dim : normal;
            block.FontWeight = differs ? FontWeights.SemiBold : FontWeights.Normal;
        }

        void Value(TextBlock block, ProfileSetting setting) => Show(block, ProfileComparison.Text(setting, saved), differences.Contains(setting));
        Value(ProfileBrightnessText, ProfileSetting.Brightness);
        Value(ProfileContrastText, ProfileSetting.Contrast);
        Value(ProfilePresetText, ProfileSetting.ColorTemperature);
        Value(ProfileGainsText, ProfileSetting.Gains);
        Value(ProfileGammaText, ProfileSetting.Gamma);
        Value(ProfileBoostText, ProfileSetting.ShadowBoost);

        // Gamma and the shadow boost have rows when the menu has them (More settings) or the profile sets them.
        var boostName = string.IsNullOrWhiteSpace(BoostNameBox.Text) ? saved.BlackBoostName : BoostNameBox.Text.Trim();
        BoostLabel.Text = string.IsNullOrWhiteSpace(boostName) ? "Shadow boost" : boostName;
        ShowRow(HasGammaCheck.IsChecked == true || ProfileComparison.Gamma(saved) is not null, GammaLabel, GammaBox, ProfileGammaText);
        ShowRow(!string.IsNullOrWhiteSpace(BoostNameBox.Text) || ProfileComparison.Boost(saved) is not null, BoostLabel, BoostValueBox, ProfileBoostText);

        // Windows' calibration: not on the simulator, which changes nothing in Windows.
        var calibrationDiffers = false;
        ShowRow(!Simulated, CalibrationLabel, MonitorCalibrationText, ProfileCalibrationText);
        if (!Simulated)
        {
            var own = profile?.Calibration;
            var usable = own is not null && ProfileSwitch.CalibrationFileExists(own.File);
            calibrationDiffers = !empty && Redirected is null
                && (usable ? !string.Equals(installed?.ProfileFile, own!.File, StringComparison.OrdinalIgnoreCase) : installed is not null);
            MonitorCalibrationText.Text = installed is null
                ? "None"
                : profiles.FirstOrDefault(p => string.Equals(p.Calibration?.File, installed.ProfileFile, StringComparison.OrdinalIgnoreCase)) is { } owner ? $"{owner.Name}'s" : "Not a profile's";
            MonitorCalibrationText.ToolTip = installed is null ? null : $"{installed.Target}, installed {installed.InstalledAt.LocalDateTime:g}";
            Show(ProfileCalibrationText, own is null ? "None" : usable ? $"Made {own.CreatedAt.LocalDateTime:d}" : "File missing", calibrationDiffers);
            ProfileCalibrationText.ToolTip = own?.Target;
        }

        // Measuring waits while the profile sets values the monitor does not have: one is loaded or saved first.
        _profileDiffers = key is not null && !empty && ProfileComparison.Changes(monitor, saved).Count > 0 ? name : null;

        var differing = differences.Order().Select(d => ProfileComparison.Name(d, boostName)).ToList();
        if (calibrationDiffers)
        {
            differing.Add("calibration");
        }

        var match = profiles.FirstOrDefault(p => p != profile && !ProfileComparison.IsEmpty(p.Settings) && ProfileComparison.Differences(monitor, p.Settings).Count == 0)?.Name;
        var comparison = differing.Count == 0
            ? null
            : $"{Sentence(differing)} {(differing.Count == 1 ? "differs" : "differ")}.{(match is null ? string.Empty : $" The monitor has {match}'s settings.")}";
        string status;
        string brushKey;
        if (key is null)
        {
            (status, brushKey) = ("No EDID: profiles cannot be kept for this display.", "TextDimBrush");
        }
        else if (_profileNote is { } note)
        {
            // What was just done, and what still differs after it (a value the monitor did not take, say).
            (status, brushKey) = note.Problem || comparison is null || Busy
                ? (note.Text, note.Problem ? "WarnBrush" : "TextDimBrush")
                : ($"{note.Text} {comparison}", "WarnBrush");
        }
        else if (ValidateMonitorForm() is { } formError)
        {
            (status, brushKey) = (formError, "WarnBrush");
        }
        else if (empty)
        {
            (status, brushKey) = ($"{name} has no settings yet: Save to profile stores the monitor's.", "TextDimBrush");
        }
        else
        {
            (status, brushKey) = comparison is null ? ($"✓ {name} is on the monitor.", "GoodBrush") : (comparison, "WarnBrush");
        }

        ShowStatus(status, (Brush)FindResource(brushKey), undo: _undoSave is not null && _profileNote is not null);

        var usableNow = key is not null && !Busy;
        SaveProfileButton.IsEnabled = usableNow && ValidateMonitorForm() is null && ProfileComparison.Changes(saved, monitor).Count > 0;
        ApplyProfileButton.IsEnabled = usableNow && !empty && (ProfileComparison.Changes(monitor, saved).Count > 0 || calibrationDiffers);
    }

    /// <summary>The status under the table, with an Undo link after a Save; rebuilt only when it changes (a link being
    /// clicked is not replaced under the pointer).</summary>
    private void ShowStatus(string status, Brush brush, bool undo)
    {
        ProfileStatus.Foreground = brush;
        var shown = undo ? status + "\n(undo)" : status;
        if (shown == _shownStatus)
        {
            return;
        }

        _shownStatus = shown;
        ProfileStatus.Inlines.Clear();
        ProfileStatus.Inlines.Add(new Run(status));
        if (undo)
        {
            var link = new Hyperlink(new Run("Undo")) { ToolTip = "Puts back the profile's settings from before the Save." };
            link.Click += UndoSave_Click;
            ProfileStatus.Inlines.Add(new Run(" "));
            ProfileStatus.Inlines.Add(link);
        }
    }

    private static void ShowRow(bool visible, params UIElement[] elements)
    {
        foreach (var element in elements)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>"Contrast", "Brightness and contrast", "Brightness, contrast and RGB".</summary>
    private static string Sentence(IReadOnlyList<string> names)
    {
        var list = names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
        return char.ToUpperInvariant(list[0]) + list[1..];
    }

    /// <summary>The user's answer is kept for the monitor (all its profiles).</summary>
    private void TnPanel_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingForm || !IsLoaded || SettingsKey is not { } key)
        {
            return;
        }

        try
        {
            Profiles.SetTnPanel(key, TnPanelCheck.IsChecked == true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _profileNote = ("Not saved: " + ex.Message, true);
            UpdateNavigation();
        }
    }

    /// <summary>A web search for this monitor's panel type (TN, IPS or VA), in the user's browser: the EDID's maker and
    /// model, which the box and the menu may not show.</summary>
    private void PanelLookup_Click(object sender, RoutedEventArgs e)
    {
        if (Simulated || SelectedMonitor is not { } monitor)
        {
            return;
        }

        var query = PanelLookup.Query(monitor);
        try
        {
            Process.Start(new ProcessStartInfo(PanelLookup.SearchUrl(query)) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"No browser could be opened ({ex.Message}). Search the web for: {query}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Numeric fields take digits only (typed or pasted).</summary>
    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void Digits_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is not string text || !text.Trim().All(char.IsAsciiDigit))
        {
            e.CancelCommand();
        }
    }

    /// <summary>Says beside each value whether it was read from the monitor, must be typed, or should be checked (kept
    /// from before, not readable now). <paramref name="read"/> is null before any reading.</summary>
    private void ShowSources(IReadOnlySet<byte>? read)
    {
        var good = (Brush)FindResource("GoodBrush");
        var warn = (Brush)FindResource("WarnBrush");
        var dim = (Brush)FindResource("TextDimBrush");
        void Show(TextBlock tag, bool fromMonitor, bool empty)
        {
            (tag.Text, tag.Foreground, tag.ToolTip) = (fromMonitor, empty) switch
            {
                (true, _) => ("✓ Read", good, "Read from the monitor"),
                (false, true) => ("Type it", warn, read is null || Simulated ? "Type it as the monitor's menu shows it." : "Not readable over DDC/CI: type it as the monitor's menu shows it."),
                _ when read is not null && !Simulated => ("Check it", dim, "Not readable over DDC/CI: check it against the monitor's menu."),
                _ => (string.Empty, dim, (string?)null),
            };
        }

        static bool Has(IReadOnlySet<byte>? r, params byte[] codes) => r is not null && codes.All(r.Contains);
        Show(BrightnessSource, Has(read, 0x10), BrightnessBox.Text.Length == 0);
        Show(ContrastSource, Has(read, 0x12), ContrastBox.Text.Length == 0);
        Show(GainsSource, Has(read, 0x16, 0x18, 0x1A), RedGainBox.Text.Length == 0 || GreenGainBox.Text.Length == 0 || BlueGainBox.Text.Length == 0);

        // Never read (brands report it differently): only asked for when missing.
        (PresetSource.Text, PresetSource.Foreground, PresetSource.ToolTip) = _presetName is null
            ? ("Choose it", warn, "As the monitor's menu shows it")
            : (string.Empty, dim, (string?)null);
    }

    private async void ReadDdc_Click(object sender, RoutedEventArgs e)
    {
        if (Simulated || SelectedMonitor is not { } monitor)
        {
            DdcStatus.Text = Simulated ? "Simulator: type any values." : string.Empty;
            return;
        }

        var version = ++_ddcReadVersion;
        var readKey = SettingsKey ?? monitor.DeviceName;
        ReadDdcButton.IsEnabled = false;
        DdcStatus.Text = "Reading…";
        try
        {
            var reports = await DdcCiProbe.ProbeAsync(monitor, TimeSpan.FromSeconds(2), CancellationToken.None);
            if (version != _ddcReadVersion)
            {
                // Another display, profile or the simulator was chosen meanwhile: this reading is not theirs.
                _ddcReadFor.Remove(readKey);
                return;
            }

            // Two monitors behind one display (duplicated screens) answer each: the first that answers is read.
            var readings = reports
                .Select(r => r.Readings.Where(x => x.Supported && x.Current is not null).ToList())
                .FirstOrDefault(r => r.Count > 0)?
                .ToDictionary(r => r.Code) ?? [];
            string? Value(byte code) => readings.TryGetValue(code, out var r) ? r.Current?.ToString(CultureInfo.CurrentCulture) : null;
            int? Max(byte code) => readings.TryGetValue(code, out var r) && r.Maximum is { } m and > 0 ? (int)m : null;
            (_brightnessMax, _contrastMax) = (Max(0x10) ?? _brightnessMax, Max(0x12) ?? _contrastMax);
            var read = new HashSet<byte>(readings.Keys);
            _loadingForm = true;
            try
            {
                BrightnessBox.Text = Value(0x10) ?? BrightnessBox.Text;
                ContrastBox.Text = Value(0x12) ?? ContrastBox.Text;

                // The color preset code means different things on different brands: the user says which it is.
                read.Remove(0x14);
                RedGainBox.Text = Value(0x16) ?? RedGainBox.Text;
                GreenGainBox.Text = Value(0x18) ?? GreenGainBox.Text;
                BlueGainBox.Text = Value(0x1A) ?? BlueGainBox.Text;
            }
            finally
            {
                _loadingForm = false;
            }

            ShowSources(read);
            DdcStatus.Text = read.Overlaps([(byte)0x10, (byte)0x12, (byte)0x16, (byte)0x18, (byte)0x1A])
                ? string.Create(CultureInfo.CurrentCulture, $"Read at {DateTime.Now:t}")
                : "No answer: type the values from its menu.";
        }
#pragma warning disable CA1031 // An async void handler must never let an error crash the app; it is shown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (version == _ddcReadVersion)
            {
                DdcStatus.Text = "Reading failed: " + ex.Message;
                ShowSources(new HashSet<byte>());
            }
        }
        finally
        {
            if (version == _ddcReadVersion)
            {
                ReadDdcButton.IsEnabled = !Simulated;
                UpdateNavigation();
            }
        }
    }
}
