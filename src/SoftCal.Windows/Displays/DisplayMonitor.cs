namespace SoftCal.Windows.Displays;

/// <summary>A rectangle in physical pixels on the virtual desktop.</summary>
public readonly record struct PixelBounds(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>Windows' advanced-color state of a display path (DisplayConfig ADVANCED_COLOR_INFO / _INFO_2).</summary>
public sealed record AdvancedColorState(
    string ColorEncoding,
    int BitsPerColorChannel,
    bool AdvancedColorSupported,
    bool AdvancedColorActive,
    bool WideColorEnforced,
    string? ActiveColorMode,
    bool? HdrSupported,
    bool? HdrUserEnabled,
    bool? WideColorUserEnabled);

/// <summary>
/// One active monitor as seen by the patch window and the probes: GDI device name and bounds (per-monitor DPI v2, so
/// physical pixels), the DisplayConfig path (adapter, source and target ids, connector, refresh rate, advanced-color
/// state) and the raw EDID from the registry.
/// </summary>
public sealed record DisplayMonitor
{
    /// <summary>GDI device name, e.g. <c>\\.\DISPLAY2</c>.</summary>
    public required string DeviceName { get; init; }

    /// <summary>HMONITOR; valid for this session only.</summary>
    public required nint Handle { get; init; }

    public required PixelBounds Bounds { get; init; }

    public required bool IsPrimary { get; init; }

    public int DpiX { get; init; } = 96;

    public int DpiY { get; init; } = 96;

    /// <summary>The EDID monitor name Windows shows (DisplayConfig target name).</summary>
    public string? FriendlyName { get; init; }

    /// <summary>Monitor device interface path, e.g. <c>\\?\DISPLAY#BNQ7F2B#…#{…}</c>.</summary>
    public string? MonitorDevicePath { get; init; }

    public string? OutputTechnology { get; init; }

    public double? RefreshRateHz { get; init; }

    public long? AdapterLuid { get; init; }

    public uint? SourceId { get; init; }

    public uint? TargetId { get; init; }

    public AdvancedColorState? AdvancedColor { get; init; }

    /// <summary>Raw EDID bytes (base block plus extensions), when the registry has them.</summary>
    public byte[]? Edid { get; init; }

    /// <summary>1-based position in <see cref="DisplayEnumerator.List"/>, used on the command line.</summary>
    public int Number { get; init; }

    public override string ToString() =>
        $"{Number}: {DeviceName} {FriendlyName ?? "(unknown)"} {Bounds.Width}×{Bounds.Height} at ({Bounds.X},{Bounds.Y}){(IsPrimary ? " primary" : string.Empty)}";
}

/// <summary>Maps a monitor device path to its registry key under <c>HKLM\SYSTEM\CurrentControlSet\Enum</c>.</summary>
public static class MonitorDevicePath
{
    /// <summary>
    /// <c>\\?\DISPLAY#BNQ7F2B#5&amp;2c0d5e9a&amp;0&amp;UID4352#{e6f07b5f-…}</c> →
    /// <c>SYSTEM\CurrentControlSet\Enum\DISPLAY\BNQ7F2B\5&amp;2c0d5e9a&amp;0&amp;UID4352\Device Parameters</c>.
    /// </summary>
    public static string? ToDeviceParametersKey(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return null;
        }

        var path = devicePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? devicePath[4..] : devicePath;
        var parts = path.Split('#');
        if (parts.Length < 3 || !parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}\Device Parameters";
    }
}
