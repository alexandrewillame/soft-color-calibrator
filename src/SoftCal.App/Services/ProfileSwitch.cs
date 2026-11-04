using System.Globalization;
using System.IO;
using SoftCal.Core.Advisor;
using SoftCal.Windows.Ddc;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Safety;

namespace SoftCal.App.Services;

/// <summary>What applying a profile does to Windows' calibration for the display.</summary>
internal enum CalibrationChange
{
    /// <summary>The profile has none, and none is installed.</summary>
    None,

    /// <summary>The profile's own calibration is the one installed already.</summary>
    Keep,

    /// <summary>The profile's own calibration is installed in place of what is there.</summary>
    Install,

    /// <summary>The calibration installed belongs to another profile (or to none) and does not fit this one: removed.</summary>
    Remove,
}

/// <summary>One value written to the monitor over DDC/CI.</summary>
internal sealed record DdcWrite(byte Code, string Name, int Value);

/// <summary>
/// What applying a profile does: the values written over DDC/CI, what only the monitor's menu can set (said to the
/// user), and the change to Windows' calibration. <paramref name="MissingFile"/>: the profile's calibration file is gone
/// from SoftCal's folder (it then counts as having none).
/// </summary>
internal sealed record ProfileApplyPlan(IReadOnlyList<DdcWrite> Writes, IReadOnlyList<string> ByHand, CalibrationChange Calibration, bool MissingFile = false)
{
    /// <summary>Nothing for SoftCal to do: no value to write, no calibration to change.</summary>
    public bool Empty => Writes.Count == 0 && Calibration is CalibrationChange.None or CalibrationChange.Keep;
}

/// <summary>
/// Puts a profile on the monitor: Brightness and Contrast over DDC/CI, and Red, Green and Blue too when the profile's
/// color temperature is the monitor's User mode (in a preset they mean nothing, and some monitors would leave the
/// preset); the rest is said, for the menu. What the monitor has already is left alone. Windows' calibration becomes the
/// profile's own, or none when it has none: another profile's curves do not fit it. The DDC/CI values are journaled
/// while they are written (a crash puts the old ones back), then kept.
/// </summary>
internal static class ProfileSwitch
{
    /// <summary>What loading <paramref name="profile"/> does; <paramref name="now"/>: the monitor's settings now (null:
    /// not known, everything the profile sets is written or said).</summary>
    public static ProfileApplyPlan Plan(MonitorProfile profile, InstalledCalibration? installed, bool ddc, Func<string, bool> fileExists, MonitorSettings? now = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(fileExists);
        var s = profile.Settings;
        var changes = now is null ? null : ProfileComparison.Changes(now, s);
        bool Changes(ProfileSetting setting) => changes?.Contains(setting) != false;
        var writes = new List<DdcWrite>();
        var byHand = new List<string>();
        static string N(int value) => value.ToString(CultureInfo.CurrentCulture);

        void Value(ProfileSetting setting, byte code, string name, int? value)
        {
            if (value is not { } v || !Changes(setting))
            {
                return;
            }

            if (ddc)
            {
                writes.Add(new DdcWrite(code, name, v));
            }
            else
            {
                byHand.Add($"{name}: {N(v)}");
            }
        }

        Value(ProfileSetting.Brightness, 0x10, "Brightness", s.Brightness);
        Value(ProfileSetting.Contrast, 0x12, "Contrast", s.Contrast);
        var kind = SettingsResult.KindOf(s.ColorPreset);
        var (red, green, blue) = s.Gains();
        if (kind == ColorPresetKind.User && red is { } r && green is { } g && blue is { } b)
        {
            if (Changes(ProfileSetting.ColorTemperature))
            {
                byHand.Add("color temperature: the User mode (Red, Green and Blue by hand)");
            }

            if (Changes(ProfileSetting.Gains) && ddc)
            {
                writes.AddRange([new DdcWrite(0x16, "Red", r), new DdcWrite(0x18, "Green", g), new DdcWrite(0x1A, "Blue", b)]);
            }
            else if (Changes(ProfileSetting.Gains))
            {
                byHand.Add($"Red, Green, Blue: {N(r)} / {N(g)} / {N(b)}");
            }
        }
        else if (kind is ColorPresetKind.Factory6500 or ColorPresetKind.OtherFactory && s.ColorPreset is { } preset && Changes(ProfileSetting.ColorTemperature))
        {
            byHand.Add($"color temperature: {preset}");
        }

        if (s.HasGammaSetting && s.GammaSetting is { Length: > 0 } gamma && Changes(ProfileSetting.Gamma))
        {
            byHand.Add($"gamma: {gamma}");
        }

        if (s.BlackBoostName is { Length: > 0 } boost && s.BlackBoost is { } level && Changes(ProfileSetting.ShadowBoost))
        {
            byHand.Add($"{boost}: {N(level)}");
        }

        var own = profile.Calibration?.File;
        var missing = own is not null && !fileExists(own);
        var change = own is not null && !missing
            ? string.Equals(installed?.ProfileFile, own, StringComparison.OrdinalIgnoreCase) ? CalibrationChange.Keep : CalibrationChange.Install
            : installed is not null ? CalibrationChange.Remove : CalibrationChange.None;
        return new ProfileApplyPlan(writes, byHand, change, missing);
    }

    /// <summary>Applies <paramref name="plan"/> (made by <see cref="Plan"/> for <paramref name="profile"/>); returns
    /// what was done, line by line.</summary>
    public static async Task<IReadOnlyList<string>> ApplyAsync(
        DisplayMonitor monitor,
        string displayKey,
        MonitorProfile profile,
        ProfileApplyPlan plan,
        InstalledCalibration? installed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(plan);
        var log = new List<string>();
        if (plan.Writes.Count > 0)
        {
            var channels = DdcCiChannel.Open(monitor);
            try
            {
                await WriteAsync(channels, plan, log, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                foreach (var open in channels)
                {
                    open.Dispose();
                }
            }
        }

        switch (plan.Calibration)
        {
            case CalibrationChange.Install when profile.Calibration is { } own:
                var (record, lines) = await CalibrationInstaller.InstallSavedAsync(monitor, displayKey, profile.Name, own.File, own.Target, own.TrialStamp, cancellationToken).ConfigureAwait(false);
                log.Add(record is null
                    ? "Its calibration could not be installed: " + string.Join(" ", lines)
                    : $"Windows uses its calibration now ({own.File}, {own.Target}).");
                break;
            case CalibrationChange.Remove when installed is not null:
                await CalibrationInstaller.RevertAsync(installed, monitor, restoreLoader: false, cancellationToken).ConfigureAwait(false);
                log.Add(plan.MissingFile
                    ? $"Removed the calibration that was installed ({installed.ProfileFile}): this profile's own file is gone, so it has none now."
                    : $"Removed the calibration that was installed ({installed.ProfileFile}): it was made for another profile, and this one has none.");
                break;
            case CalibrationChange.Keep:
                log.Add("Its calibration is the one Windows uses already.");
                break;
        }

        return log;
    }

    /// <summary>Whether SoftCal's copy of a calibration file is there.</summary>
    public static bool CalibrationFileExists(string fileName) => File.Exists(Path.Combine(CalibrationInstaller.ProfilesFolder, fileName));

    /// <summary>The plan's values over DDC/CI, to the first monitor behind the display, through the journaled guard (a
    /// crash while writing puts the old values back), then kept.</summary>
    private static async Task WriteAsync(IReadOnlyList<DdcCiChannel> channels, ProfileApplyPlan plan, List<string> log, CancellationToken cancellationToken)
    {
        if (channels.Count == 0)
        {
            log.Add("The monitor did not answer over DDC/CI: set " + string.Join(", ", plan.Writes.Select(w => $"{w.Name} to {w.Value.ToString(CultureInfo.CurrentCulture)}")) + " in its menu.");
            return;
        }

        Guardian.EnsureStarted();
        var guard = new DdcValueGuard(channels[0], AppSafety.Journal);
        var set = new List<string>();
        foreach (var write in plan.Writes)
        {
            var outcome = await guard.SetAsync(write.Code, (uint)write.Value, cancellationToken).ConfigureAwait(false);
            if (outcome == DdcWriteOutcome.Applied)
            {
                set.Add($"{write.Name} {write.Value.ToString(CultureInfo.CurrentCulture)}");
            }
            else
            {
                log.Add($"{write.Name} could not be set over DDC/CI ({outcome}): set it to {write.Value.ToString(CultureInfo.CurrentCulture)} in the menu.");
            }
        }

        guard.Keep();
        if (set.Count > 0)
        {
            log.Insert(0, "Set over DDC/CI: " + string.Join(", ", set) + ".");
        }
    }
}
