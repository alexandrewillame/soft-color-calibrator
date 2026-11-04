using SoftCal.Windows.Displays;
using SoftCal.Windows.Safety;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace SoftCal.Windows.AdvancedColor;

/// <summary>
/// The two user switches under which Windows color-manages everything a display shows, so measurement patches would
/// not reach the panel as sent: HDR, and Auto Color Management (ACM, "wide color gamut" in the APIs).
/// </summary>
public sealed record AdvancedColorSwitches(bool Hdr, bool AutoColorManagement)
{
    public bool AnyOn => Hdr || AutoColorManagement;

    /// <summary>
    /// Reads the switches from DisplayConfig's state. Windows 11 24H2 and later report both user switches directly; on
    /// 22H2/23H2 "advanced color enabled" is also true under ACM, so <c>wideColorEnforced</c> tells the two apart.
    /// </summary>
    public static AdvancedColorSwitches From(AdvancedColorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.HdrUserEnabled is { } hdr)
        {
            return new(hdr, state.WideColorUserEnabled ?? state.WideColorEnforced);
        }

        return new(state.AdvancedColorActive && !state.WideColorEnforced, state.WideColorEnforced);
    }
}

/// <summary>Reads and sets one display's advanced-color switches.</summary>
public interface IAdvancedColorDevice
{
    /// <summary>Stable key: the monitor device path, which survives reboots (adapter LUIDs do not).</summary>
    string Key { get; }

    AdvancedColorState? Read();

    bool SetHdr(bool enabled);

    bool SetAutoColorManagement(bool enabled);
}

/// <summary>The real switches, through DisplayConfigSetDeviceInfo. No administrator rights are needed.</summary>
public sealed class DisplayConfigAdvancedColorDevice : IAdvancedColorDevice
{
    private readonly LUID _adapter;
    private readonly uint _targetId;

    private DisplayConfigAdvancedColorDevice(string key, LUID adapter, uint targetId)
    {
        Key = key;
        _adapter = adapter;
        _targetId = targetId;
    }

    public string Key { get; }

    public static DisplayConfigAdvancedColorDevice? For(DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (monitor.MonitorDevicePath is not { Length: > 0 } path || monitor.AdapterLuid is not { } luid || monitor.TargetId is not { } target)
        {
            return null;
        }

        return new DisplayConfigAdvancedColorDevice(path, new LUID { LowPart = (uint)(luid & 0xFFFFFFFF), HighPart = (int)(luid >> 32) }, target);
    }

    /// <summary>Finds a connected display by its monitor device path.</summary>
    public static DisplayConfigAdvancedColorDevice? Find(string monitorDevicePath) =>
        DisplayEnumerator.List()
            .Where(m => string.Equals(m.MonitorDevicePath, monitorDevicePath, StringComparison.OrdinalIgnoreCase))
            .Select(For)
            .FirstOrDefault(d => d is not null);

    public AdvancedColorState? Read() => DisplayEnumerator.ReadAdvancedColor(_adapter, _targetId);

    public unsafe bool SetHdr(bool enabled)
    {
        var set = default(DISPLAYCONFIG_SET_HDR_STATE);
        set.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE;
        set.header.size = (uint)sizeof(DISPLAYCONFIG_SET_HDR_STATE);
        set.header.adapterId = _adapter;
        set.header.id = _targetId;
        set.enableHdr = enabled;
        if (PInvoke.DisplayConfigSetDeviceInfo(&set.header) == 0)
        {
            return true;
        }

        // Before 24H2 only the older advanced-color switch exists, and it means HDR.
        var legacy = default(DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE);
        legacy.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
        legacy.header.size = (uint)sizeof(DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE);
        legacy.header.adapterId = _adapter;
        legacy.header.id = _targetId;
        legacy.enableAdvancedColor = enabled;
        return PInvoke.DisplayConfigSetDeviceInfo(&legacy.header) == 0;
    }

    public unsafe bool SetAutoColorManagement(bool enabled)
    {
        var set = default(DISPLAYCONFIG_SET_WCG_STATE);
        set.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_SET_WCG_STATE;
        set.header.size = (uint)sizeof(DISPLAYCONFIG_SET_WCG_STATE);
        set.header.adapterId = _adapter;
        set.header.id = _targetId;
        set.enableWcg = enabled;
        return PInvoke.DisplayConfigSetDeviceInfo(&set.header) == 0;
    }
}

/// <summary>
/// Turns HDR and ACM off for SDR measurement. The original switches are journaled before anything changes and put back
/// (and read back) on <see cref="Restore"/> or <see cref="Dispose"/>; after a crash the guardian does it. These are
/// persistent user settings, so leaving them changed would be very visible.
/// </summary>
public sealed class AdvancedColorGuard : IDisposable
{
    public const string JournalKind = "advanced-color";

    private readonly IAdvancedColorDevice _device;
    private readonly StateJournal _journal;
    private bool _changed;

    private AdvancedColorGuard(IAdvancedColorDevice device, StateJournal journal, AdvancedColorSwitches original)
    {
        _device = device;
        _journal = journal;
        Original = original;
    }

    public AdvancedColorSwitches Original { get; }

    /// <summary>Settle time Windows needs after a switch before the new state reads back.</summary>
    public static TimeSpan SwitchTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public static AdvancedColorGuard Begin(IAdvancedColorDevice device, StateJournal journal)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Contains(JournalKind, device.Key))
        {
            throw new InvalidOperationException(
                $"The HDR/ACM switches of {device.Key} from an earlier run are still journaled; restore them first.");
        }

        var state = device.Read() ?? throw new InvalidOperationException($"The advanced-color state of {device.Key} is unavailable.");
        var original = AdvancedColorSwitches.From(state);
        journal.Save(JournalKind, device.Key, original);
        return new AdvancedColorGuard(device, journal, original);
    }

    public AdvancedColorSwitches Current() =>
        AdvancedColorSwitches.From(_device.Read() ?? throw new InvalidOperationException($"The advanced-color state of {_device.Key} is unavailable."));

    /// <summary>Switches HDR and ACM off. Throws when Windows refuses or the change does not read back.</summary>
    public AdvancedColorSwitches MakeSdr() => Apply(new AdvancedColorSwitches(false, false));

    /// <summary>Sets both switches. Throws when Windows refuses or the change does not read back.</summary>
    public AdvancedColorSwitches Apply(AdvancedColorSwitches target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var current = Current();
        if (current.AutoColorManagement != target.AutoColorManagement)
        {
            _changed = true;
            if (!_device.SetAutoColorManagement(target.AutoColorManagement))
            {
                throw new InvalidOperationException($"Windows refused to turn Auto Color Management {(target.AutoColorManagement ? "on" : "off")} for {_device.Key}.");
            }
        }

        if (current.Hdr != target.Hdr)
        {
            _changed = true;
            if (!_device.SetHdr(target.Hdr))
            {
                throw new InvalidOperationException($"Windows refused to turn HDR {(target.Hdr ? "on" : "off")} for {_device.Key}.");
            }
        }

        return AdvancedColorRestorer.WaitFor(_device, target) ?? throw new InvalidOperationException(
            $"HDR/ACM on {_device.Key} read as {Current()} instead of {target}.");
    }

    /// <summary>Puts the original switches back; the journal entry is removed only when they read back correctly.</summary>
    public RestoreResult Restore()
    {
        var result = _changed ? AdvancedColorRestorer.Apply(_device, Original) : RestoreResult.Restored("nothing was changed");
        if (result.Outcome == RestoreOutcome.Restored)
        {
            _journal.Remove(JournalKind, _device.Key);
            _changed = false;
        }

        return result;
    }

    public void Dispose() => Restore();
}

/// <summary>Restores journaled HDR/ACM switches (journal kind <see cref="AdvancedColorGuard.JournalKind"/>).</summary>
public sealed class AdvancedColorRestorer(Func<string, IAdvancedColorDevice?>? find = null) : IStateRestorer
{
    private readonly Func<string, IAdvancedColorDevice?> _find = find ?? DisplayConfigAdvancedColorDevice.Find;

    public string Kind => AdvancedColorGuard.JournalKind;

    public RestoreResult Restore(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var original = StateJournal.Read<AdvancedColorSwitches>(entry) ?? throw new InvalidDataException("Empty HDR/ACM journal entry.");
        var device = _find(entry.Key);
        return device is null
            ? RestoreResult.Deferred("display not connected; its HDR/ACM switches will be restored when it is")
            : Apply(device, original);
    }

    internal static RestoreResult Apply(IAdvancedColorDevice device, AdvancedColorSwitches original)
    {
        if (device.Read() is not { } state)
        {
            return RestoreResult.Failed("the advanced-color state is unavailable");
        }

        var current = AdvancedColorSwitches.From(state);
        if (current.AutoColorManagement != original.AutoColorManagement && !device.SetAutoColorManagement(original.AutoColorManagement))
        {
            return RestoreResult.Failed($"Windows refused to set Auto Color Management back to {(original.AutoColorManagement ? "on" : "off")}");
        }

        if (current.Hdr != original.Hdr && !device.SetHdr(original.Hdr))
        {
            return RestoreResult.Failed($"Windows refused to set HDR back to {(original.Hdr ? "on" : "off")}");
        }

        return WaitFor(device, original) is not null
            ? RestoreResult.Restored($"HDR {(original.Hdr ? "on" : "off")}, ACM {(original.AutoColorManagement ? "on" : "off")} restored and read back")
            : RestoreResult.Failed($"the switches did not read back as {original}");
    }

    /// <summary>Polls until the switches read <paramref name="target"/>, or returns null after <see cref="AdvancedColorGuard.SwitchTimeout"/>.</summary>
    internal static AdvancedColorSwitches? WaitFor(IAdvancedColorDevice device, AdvancedColorSwitches target)
    {
        var deadline = DateTime.UtcNow + AdvancedColorGuard.SwitchTimeout;
        while (true)
        {
            if (device.Read() is { } state && AdvancedColorSwitches.From(state) == target)
            {
                return target;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            Thread.Sleep(100);
        }
    }
}
