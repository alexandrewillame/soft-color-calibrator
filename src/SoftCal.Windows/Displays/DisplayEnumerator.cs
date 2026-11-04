using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace SoftCal.Windows.Displays;

/// <summary>Lists active monitors, joining GDI (HMONITOR, device name, bounds) with DisplayConfig and the registry EDID.</summary>
public static class DisplayEnumerator
{
    /// <summary>Monitors ordered primary first, then left to right. Bounds are physical pixels only when the calling
    /// thread is per-monitor DPI aware (the harness manifest and the patch window thread both are).</summary>
    public static unsafe IReadOnlyList<DisplayMonitor> List()
    {
        var gdi = new List<(HMONITOR Handle, string Name, RECT Rect, bool Primary)>();
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (monitor, _, _, _) =>
        {
            var info = default(MONITORINFOEXW);
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                gdi.Add((monitor, info.szDevice.ToString(), info.monitorInfo.rcMonitor, (info.monitorInfo.dwFlags & 1) != 0));
            }

            return true;
        }, default);

        var paths = QueryPaths();
        var monitors = new List<DisplayMonitor>();
        foreach (var (handle, name, rect, primary) in gdi.OrderByDescending(g => g.Primary).ThenBy(g => g.Rect.left).ThenBy(g => g.Rect.top))
        {
            uint dpiX = 96, dpiY = 96;
            _ = PInvoke.GetDpiForMonitor(handle, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
            paths.TryGetValue(name, out var path);
            monitors.Add(new DisplayMonitor
            {
                Number = monitors.Count + 1,
                DeviceName = name,
                Handle = (nint)handle.Value,
                Bounds = new PixelBounds(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top),
                IsPrimary = primary,
                DpiX = (int)dpiX,
                DpiY = (int)dpiY,
                FriendlyName = path?.FriendlyName,
                MonitorDevicePath = path?.DevicePath,
                OutputTechnology = path?.OutputTechnology,
                RefreshRateHz = path?.RefreshRate,
                AdapterLuid = path?.AdapterLuid,
                SourceId = path?.SourceId,
                TargetId = path?.TargetId,
                AdvancedColor = path?.AdvancedColor,
                Edid = ReadEdid(path?.DevicePath),
            });
        }

        return monitors;
    }

    /// <summary>Finds a monitor by its 1-based number, GDI name (\\.\DISPLAY2 or DISPLAY2) or a substring of its name.</summary>
    public static DisplayMonitor Find(IReadOnlyList<DisplayMonitor> monitors, string selector)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        if (int.TryParse(selector, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return monitors.FirstOrDefault(m => m.Number == number)
                ?? throw new ArgumentException($"There is no monitor {number}.", nameof(selector));
        }

        return monitors.FirstOrDefault(m => m.DeviceName.EndsWith(selector.TrimStart('\\', '.'), StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(m => m.FriendlyName?.Contains(selector, StringComparison.OrdinalIgnoreCase) == true)
            ?? throw new ArgumentException($"No monitor matches '{selector}'.", nameof(selector));
    }

    private static byte[]? ReadEdid(string? devicePath)
    {
        var key = MonitorDevicePath.ToDeviceParametersKey(devicePath);
        if (key is null)
        {
            return null;
        }

        try
        {
            using var parameters = Registry.LocalMachine.OpenSubKey(key, writable: false);
            return parameters?.GetValue("EDID") as byte[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private sealed record PathInfo(
        string FriendlyName,
        string DevicePath,
        string OutputTechnology,
        double? RefreshRate,
        long AdapterLuid,
        uint SourceId,
        uint TargetId,
        AdvancedColorState? AdvancedColor);

    private static unsafe Dictionary<string, PathInfo> QueryPaths()
    {
        var result = new Dictionary<string, PathInfo>(StringComparer.OrdinalIgnoreCase);
        if (PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != WIN32_ERROR.ERROR_SUCCESS)
        {
            return result;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        fixed (DISPLAYCONFIG_PATH_INFO* p = paths)
        fixed (DISPLAYCONFIG_MODE_INFO* m = modes)
        {
            if (PInvoke.QueryDisplayConfig(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, &pathCount, p, &modeCount, m, null) != WIN32_ERROR.ERROR_SUCCESS)
            {
                return result;
            }
        }

        foreach (var path in paths.AsSpan(0, (int)pathCount))
        {
            var source = default(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
            source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
            source.header.adapterId = path.sourceInfo.adapterId;
            source.header.id = path.sourceInfo.id;
            if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0)
            {
                continue;
            }

            var target = default(DISPLAYCONFIG_TARGET_DEVICE_NAME);
            target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
            target.header.adapterId = path.targetInfo.adapterId;
            target.header.id = path.targetInfo.id;
            var hasTarget = PInvoke.DisplayConfigGetDeviceInfo(&target.header) == 0;

            var rate = path.targetInfo.refreshRate;
            var luid = path.targetInfo.adapterId;
            result[source.viewGdiDeviceName.ToString()] = new PathInfo(
                hasTarget ? target.monitorFriendlyDeviceName.ToString() : string.Empty,
                hasTarget ? target.monitorDevicePath.ToString() : string.Empty,
                OutputTechnologyName(path.targetInfo.outputTechnology),
                rate.Denominator == 0 ? null : (double)rate.Numerator / rate.Denominator,
                ((long)luid.HighPart << 32) | luid.LowPart,
                path.sourceInfo.id,
                path.targetInfo.id,
                ReadAdvancedColor(path.targetInfo.adapterId, path.targetInfo.id));
        }

        return result;
    }

    internal static unsafe AdvancedColorState? ReadAdvancedColor(LUID adapter, uint targetId)
    {
        // ADVANCED_COLOR_INFO_2 exists from Windows 11 24H2; fall back to the older structure.
        var info2 = default(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2);
        info2.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
        info2.header.size = (uint)sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2);
        info2.header.adapterId = adapter;
        info2.header.id = targetId;
        var info = default(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO);
        info.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
        info.header.size = (uint)sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO);
        info.header.adapterId = adapter;
        info.header.id = targetId;
        var hasInfo = PInvoke.DisplayConfigGetDeviceInfo(&info.header) == 0;

        if (PInvoke.DisplayConfigGetDeviceInfo(&info2.header) == 0)
        {
            return new AdvancedColorState(
                info2.colorEncoding.ToString(),
                (int)info2.bitsPerColorChannel,
                info2.advancedColorSupported,
                info2.advancedColorActive,
                hasInfo && info.wideColorEnforced,
                info2.activeColorMode.ToString(),
                info2.highDynamicRangeSupported,
                info2.highDynamicRangeUserEnabled,
                info2.wideColorUserEnabled);
        }

        return hasInfo
            ? new AdvancedColorState(
                info.colorEncoding.ToString(),
                (int)info.bitsPerColorChannel,
                info.advancedColorSupported,
                info.advancedColorEnabled,
                info.wideColorEnforced,
                null,
                null,
                null,
                null)
            : null;
    }

    private static string OutputTechnologyName(DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY technology) => technology switch
    {
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI => "HDMI",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL => "DisplayPort",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED => "DisplayPort (embedded)",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI => "DVI",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HD15 => "VGA",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL => "Internal",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EXTERNAL => "UDI",
        DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_MIRACAST => "Miracast",
        _ => technology.ToString(),
    };
}
