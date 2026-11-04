using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;

namespace SoftCal.Windows.OsState;

/// <summary>Windows' accessibility color filters (Settings › Accessibility › Color filters).</summary>
public sealed record ColorFilterState(bool Active, int? FilterType)
{
    public string? FilterName => FilterType switch
    {
        0 => "grayscale",
        1 => "inverted",
        2 => "grayscale inverted",
        3 => "red-green (deuteranopia)",
        4 => "red-green (protanopia)",
        5 => "blue-yellow (tritanopia)",
        _ => null,
    };
}

/// <summary>Power features that can dim or change the screen during a measurement.</summary>
/// <param name="OnBattery">Null when Windows does not know.</param>
/// <param name="EnergySaverOn">Energy (battery) saver, which can lower brightness.</param>
/// <param name="AdaptiveBrightnessOnAc">"Change brightness automatically when lighting changes" while plugged in; null when the setting does not exist (no light sensor).</param>
/// <param name="AdaptiveBrightnessOnBattery">The same setting on battery.</param>
public sealed record PowerState(bool? OnBattery, bool? EnergySaverOn, bool? AdaptiveBrightnessOnAc, bool? AdaptiveBrightnessOnBattery);

/// <summary>Read-only probes of the OS settings that alter what a display shows. Nothing here writes anything.</summary>
public static class OsProbes
{
    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid AdaptiveBrightness = new("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");

    public static ColorFilterState ReadColorFilters()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\ColorFiltering", writable: false);
            if (key is null)
            {
                return new ColorFilterState(false, null);
            }

            return new ColorFilterState(key.GetValue("Active") is int active && active != 0, key.GetValue("FilterType") as int?);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new ColorFilterState(false, null);
        }
    }

    public static unsafe PowerState ReadPower()
    {
        bool? onBattery = null, saver = null;
        if (PInvoke.GetSystemPowerStatus(out var status))
        {
            onBattery = status.ACLineStatus switch
            {
                0 => true,
                1 => false,
                _ => null,
            };
            saver = (status.SystemStatusFlag & 1) != 0;
        }

        bool? ac = null, dc = null;
        if (PInvoke.PowerGetActiveScheme(null, out var scheme) == WIN32_ERROR.ERROR_SUCCESS && scheme is not null)
        {
            try
            {
                if (PInvoke.PowerReadACValueIndex(null, *scheme, VideoSubgroup, AdaptiveBrightness, out var acValue) == WIN32_ERROR.ERROR_SUCCESS)
                {
                    ac = acValue != 0;
                }

                // Unlike its AC twin, PowerReadDCValueIndex is declared as returning a plain DWORD error code.
                if (PInvoke.PowerReadDCValueIndex(null, *scheme, VideoSubgroup, AdaptiveBrightness, out var dcValue) == 0)
                {
                    dc = dcValue != 0;
                }
            }
            finally
            {
                PInvoke.LocalFree((HLOCAL)(nint)scheme);
            }
        }

        return new PowerState(onBattery, saver, ac, dc);
    }

    /// <summary>Whether Windows loads the calibration curves ('vcgt') of each display's default profile; null if unknown.</summary>
    public static bool? ReadCalibrationLoaderEnabled() =>
        PInvoke.WcsGetCalibrationManagementState(out var enabled) ? enabled : null;

    /// <summary>Overlays that pop notifications over full-screen windows (the NVIDIA App's and GeForce
    /// Experience's in-game overlays), by the name the user knows them by.</summary>
    public static IReadOnlyList<string> ReadOverlays()
    {
        var names = new List<string>();
        foreach (var (process, name) in new[] { ("NVIDIA Overlay", "The NVIDIA overlay"), ("NVIDIA Share", "The GeForce Experience overlay") })
        {
            var running = System.Diagnostics.Process.GetProcessesByName(process);
            if (running.Length > 0)
            {
                names.Add(name);
            }

            foreach (var p in running)
            {
                p.Dispose();
            }
        }

        return names;
    }
}
