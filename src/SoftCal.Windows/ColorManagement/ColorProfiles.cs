using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SoftCal.Windows.Displays;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.ColorSystem;

namespace SoftCal.Windows.ColorManagement;

/// <summary>A display as Windows' color management APIs name it: the DisplayConfig adapter LUID and source id.</summary>
public readonly record struct DisplayTarget(long AdapterLuid, uint SourceId)
{
    public static DisplayTarget? Of(DisplayMonitor monitor) =>
        monitor?.AdapterLuid is { } luid && monitor.SourceId is { } source ? new DisplayTarget(luid, source) : null;

    internal LUID Luid => new() { LowPart = unchecked((uint)(AdapterLuid & 0xFFFFFFFF)), HighPart = (int)(AdapterLuid >> 32) };
}

/// <summary>
/// The Windows color profile store (the COLOR folder, <c>%WINDIR%\System32\spool\drivers\color</c>). Users may add new
/// files to it, so installing needs no administrator unless a file of that name already exists.
/// </summary>
public static class ColorProfileStore
{
    public static unsafe string ColorDirectory()
    {
        uint size = 0;
        PInvoke.GetColorDirectory(null, null, &size);
        var buffer = new char[Math.Max(size / 2, 260)];
        fixed (char* p = buffer)
        {
            size = (uint)(buffer.Length * 2);
            if (!PInvoke.GetColorDirectory(null, p, &size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetColorDirectory failed.");
            }
        }

        return new string(buffer).TrimEnd('\0');
    }

    public static bool IsInstalled(string fileName) => File.Exists(Path.Combine(ColorDirectory(), fileName));

    /// <summary>Copies the profile into the COLOR folder. Throws <see cref="Win32Exception"/> (access denied when it
    /// needs an administrator).</summary>
    public static void Install(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!PInvoke.InstallColorProfile(null, path))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Windows would not install {Path.GetFileName(path)}.");
        }
    }

    /// <summary>Removes a profile from the store (and deletes the file when <paramref name="delete"/>).</summary>
    public static bool Uninstall(string fileName, bool delete = true) => PInvoke.UninstallColorProfile(null, fileName, delete);
}

/// <summary>
/// Which profile Windows uses for a display (Windows 10 2004 and later): the profile apps color manage with, and whose
/// 'vcgt' the Windows calibration loader puts in the GPU. SDR (standard) color mode only. The current user's
/// association counts only while <see cref="PerUserProfiles"/> is on for the display; otherwise the system-wide one does.
/// </summary>
public static class DisplayProfileAssociation
{
    /// <summary>The current user's (or, with <paramref name="systemWide"/>, the system's) default profile file name for
    /// the display, or null when it has none.</summary>
    public static unsafe string? GetDefault(DisplayTarget target, bool systemWide = false)
    {
        PWSTR name = default;
        var hr = PInvoke.ColorProfileGetDisplayDefault(
            systemWide ? WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_SYSTEM_WIDE : WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
            target.Luid,
            target.SourceId,
            COLORPROFILETYPE.CPT_ICC,
            COLORPROFILESUBTYPE.CPST_STANDARD_DISPLAY_COLOR_MODE,
            &name);
        if (hr.Failed || name.Value is null)
        {
            return null;
        }

        try
        {
            return name.ToString();
        }
        finally
        {
            PInvoke.LocalFree((HLOCAL)(nint)name.Value);
        }
    }

    /// <summary>Associates an installed profile with the display for the current user, optionally as its default.</summary>
    public static void Add(DisplayTarget target, string profileName, bool setAsDefault)
    {
        var hr = PInvoke.ColorProfileAddDisplayAssociation(
            WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
            profileName,
            target.Luid,
            target.SourceId,
            setAsDefault,
            false);
        hr.ThrowOnFailure();
    }

    public static void SetDefault(DisplayTarget target, string profileName)
    {
        var hr = PInvoke.ColorProfileSetDisplayDefaultAssociation(
            WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
            profileName,
            COLORPROFILETYPE.CPT_ICC,
            COLORPROFILESUBTYPE.CPST_STANDARD_DISPLAY_COLOR_MODE,
            target.Luid,
            target.SourceId);
        hr.ThrowOnFailure();
    }

    public static void Remove(DisplayTarget target, string profileName)
    {
        var hr = PInvoke.ColorProfileRemoveDisplayAssociation(
            WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
            profileName,
            target.Luid,
            target.SourceId,
            false);
        hr.ThrowOnFailure();
    }
}

/// <summary>What Windows says it can do with a display's profiles (Windows 10 2004 and later, from its driver): load a
/// profile's 'vcgt' curves into the GPU, and use the MHC2 hardware color pipeline; null when Windows does not say. On
/// both monitors of the development PC (NVIDIA RTX 4000 Ada, driver 32.0.15.9716) it reports no 'vcgt' support and MHC2
/// support, yet Windows' loader still put a default profile's 'vcgt' in the GPU (rounded to 8 bits): what is reported
/// is not proven.</summary>
public readonly record struct DisplayColorCapabilities(bool? LoadsVcgt, bool? HardwareCalibration)
{
    /// <remarks>Each capability has its own output: a BOOL for 'vcgt', a sized structure for MHC2. Passing a BOOL for
    /// MHC2 lets Windows write past it (it crashed the app).</remarks>
    public static unsafe DisplayColorCapabilities Of(DisplayTarget target)
    {
        const WCS_PROFILE_MANAGEMENT_SCOPE scope = WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER;
        BOOL vcgt = default;
        var vcgtResult = PInvoke.ColorProfileGetDeviceCapabilities(scope, target.Luid, target.SourceId, WCS_DEVICE_CAPABILITIES_TYPE.VideoCardGammaTable, &vcgt);
        var mhc2 = new WCS_DEVICE_MHC2_CAPABILITIES { Size = (uint)sizeof(WCS_DEVICE_MHC2_CAPABILITIES) };
        var mhc2Result = PInvoke.ColorProfileGetDeviceCapabilities(scope, target.Luid, target.SourceId, WCS_DEVICE_CAPABILITIES_TYPE.MicrosoftHardwareColorV2, &mhc2);
        return new(vcgtResult.Succeeded ? (bool)vcgt : null, mhc2Result.Succeeded ? (bool)mhc2.SupportsMhc2 : null);
    }
}

/// <summary>
/// "Use my settings for this device" (Color Management): whether Windows uses the current user's profile association for
/// a display, at sign-in and for apps, instead of the system-wide one. Per user, no administrator.
/// </summary>
public static class PerUserProfiles
{
    /// <summary>CLASS_MONITOR ('mntr').</summary>
    private const uint ClassMonitor = 0x6D6E7472;

    /// <summary>The monitor's device key, which names it for these calls:
    /// <c>\Registry\Machine\System\CurrentControlSet\Control\Class\{4d36e96e-…}\0000</c>.</summary>
    public static unsafe string? DeviceKey(DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var device = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        if (!PInvoke.EnumDisplayDevices(monitor.DeviceName, 0, ref device, 0))
        {
            return null;
        }

        var key = device.DeviceKey.ToString();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>Whether it is on; null when Windows does not say.</summary>
    public static bool? IsOn(string deviceKey) =>
        PInvoke.WcsGetUsePerUserProfiles(deviceKey, ClassMonitor, out var on) ? on : null;

    public static void Set(string deviceKey, bool on)
    {
        if (!PInvoke.WcsSetUsePerUserProfiles(deviceKey, ClassMonitor, on))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows would not change 'Use my settings for this device'.");
        }
    }
}

/// <summary>
/// Windows' calibration loader: the 'Use Windows display calibration' switch (HKLM) and the scheduled task
/// <c>\Microsoft\Windows\WindowsColorSystem\Calibration Loader</c>, which loads each display's default profile's 'vcgt'
/// at sign-in and when displays change. Both must be on; changing either needs an administrator.
/// </summary>
public static class CalibrationLoader
{
    public const string TaskName = @"\Microsoft\Windows\WindowsColorSystem\Calibration Loader";

    /// <summary>The switch's state; null when unknown.</summary>
    public static bool? IsSwitchOn() => PInvoke.WcsGetCalibrationManagementState(out var on) ? on : null;

    /// <summary>Whether the scheduled task is enabled; null when it could not be read.</summary>
    public static bool? IsTaskEnabled()
    {
        var output = RunSchtasks("/Query", "/TN", TaskName, "/FO", "CSV", "/NH");
        return output is null ? null : !output.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turns both on (administrator only). Returns what was done.</summary>
    internal static string TurnOn()
    {
        if (!PInvoke.WcsSetCalibrationManagementState(true))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows would not turn on 'Use Windows display calibration'.");
        }

        if (RunSchtasks("/Change", "/TN", TaskName, "/ENABLE") is null)
        {
            throw new InvalidOperationException("The Calibration Loader task could not be enabled.");
        }

        return "'Use Windows display calibration' is on and the Calibration Loader task is enabled.";
    }

    /// <summary>Puts both back as they were (administrator only).</summary>
    internal static string Restore(bool switchOn, bool taskEnabled)
    {
        var switched = PInvoke.WcsSetCalibrationManagementState(switchOn);
        var task = RunSchtasks("/Change", "/TN", TaskName, taskEnabled ? "/ENABLE" : "/DISABLE") is not null;
        if (!switched || !task)
        {
            throw new InvalidOperationException(
                $"{(switched ? string.Empty : $"'Use Windows display calibration' could not be turned {(switchOn ? "on" : "off")}. ")}{(task ? string.Empty : $"The Calibration Loader task could not be {(taskEnabled ? "enabled" : "disabled")}.")}".Trim());
        }

        return $"'Use Windows display calibration' {(switchOn ? "on" : "off")}, Calibration Loader task {(taskEnabled ? "enabled" : "disabled")}.";
    }

    private static string? RunSchtasks(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        return process.ExitCode == 0 ? output : null;
    }
}
