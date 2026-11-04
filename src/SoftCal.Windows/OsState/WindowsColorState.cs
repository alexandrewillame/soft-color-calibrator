using System.Globalization;
using SoftCal.Windows.AdvancedColor;
using SoftCal.Windows.ColorManagement;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Gamma;
using SoftCal.Windows.Gpu;
using SoftCal.Windows.NightLight;

namespace SoftCal.Windows.OsState;

public enum FindingSeverity
{
    /// <summary>Worth knowing; nothing to do.</summary>
    Info,

    /// <summary>May spoil a measurement or a calibration; the user should look at it.</summary>
    Warning,

    /// <summary>Measuring now would give wrong results.</summary>
    Blocker,
}

/// <summary>A button that opens where the user fixes a finding: a Settings page (an ms-settings: link), or
/// <see cref="NvidiaControlPanel"/> / <see cref="NvidiaApp"/>. Opening is all it does; SoftCal changes nothing there.</summary>
public sealed record PreflightAction(string Label, string Target)
{
    public const string NvidiaControlPanel = "nvidia-control-panel";

    public const string NvidiaApp = "nvidia-app";
}

/// <summary>One pre-flight finding, with what to do about it.</summary>
/// <param name="HandledBySoftCal">SoftCal deals with it itself during a measurement (HDR and Auto Color Management are
/// switched off and back on), so it does not stop one.</param>
public sealed record PreflightFinding(FindingSeverity Severity, string Area, string Message, string? Fix = null, PreflightAction? Action = null, bool HandledBySoftCal = false);

/// <summary>What Windows and the GPU do to one display's colors.</summary>
/// <param name="WindowsLoadsCurves">Whether Windows' own loader can put a profile's 'vcgt' curves in this display's GPU,
/// as its driver reports; null when Windows does not say.</param>
public sealed record DisplayColorState(
    string DeviceName,
    string? FriendlyName,
    string? MonitorDevicePath,
    AdvancedColorState? AdvancedColor,
    bool? GammaRampIsIdentity,
    bool? FullRangeRgb,
    string? LinkDescription,
    bool? WindowsLoadsCurves = null);

/// <summary>A snapshot of every OS setting that changes displayed colors, plus the findings that matter before measuring.</summary>
public sealed record WindowsColorState(
    DateTimeOffset TakenUtc,
    NightLightReport NightLight,
    ColorFilterState ColorFilters,
    PowerState Power,
    bool? CalibrationLoaderEnabled,
    IReadOnlyList<DisplayColorState> Displays,
    IReadOnlyList<PreflightFinding> Findings)
{
    public bool ReadyToMeasure => Findings.All(f => f.Severity != FindingSeverity.Blocker);
}

/// <summary>Reads the whole OS color state (read-only) and turns it into pre-flight findings.</summary>
public static class WindowsColorStateProbe
{
    public static WindowsColorState Read(IReadOnlyList<DisplayMonitor>? monitors = null, bool includeNvApi = true)
    {
        monitors ??= DisplayEnumerator.List();
        var nv = includeNvApi ? NvApiProbe.Probe(monitors.Select(m => m.DeviceName)) : null;
        var displays = monitors.Select(m =>
        {
            bool? identity = null;
            try
            {
                identity = new GdiGammaRampDevice(m.DeviceName).Read().IsIdentity;
            }
            catch (InvalidOperationException)
            {
                identity = null;
            }

            var link = nv?.Displays.FirstOrDefault(d => string.Equals(d.DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase))?.Current;
            return new DisplayColorState(
                m.DeviceName,
                m.FriendlyName,
                m.MonitorDevicePath,
                m.AdvancedColor,
                identity,
                link?.IsFullRangeRgb,
                link is null ? null : $"{link.ColorFormat}, {link.DynamicRange} range, {link.BitsPerComponent} bpc",
                DisplayTarget.Of(m) is { } target ? DisplayColorCapabilities.Of(target).LoadsVcgt : null);
        }).ToList();

        var nightLight = NightLightReader.Read();
        var filters = OsProbes.ReadColorFilters();
        var power = OsProbes.ReadPower();
        var loader = OsProbes.ReadCalibrationLoaderEnabled();
        var overlays = OsProbes.ReadOverlays();
        return new WindowsColorState(DateTimeOffset.UtcNow, nightLight, filters, power, loader, displays, Evaluate(nightLight, filters, power, loader, displays, overlays));
    }

    /// <summary>The pre-flight rules. Pure, so every rule is unit-tested.</summary>
    public static IReadOnlyList<PreflightFinding> Evaluate(
        NightLightReport nightLight,
        ColorFilterState colorFilters,
        PowerState power,
        bool? calibrationLoaderEnabled,
        IReadOnlyList<DisplayColorState> displays,
        IReadOnlyList<string>? overlays = null)
    {
        ArgumentNullException.ThrowIfNull(nightLight);
        ArgumentNullException.ThrowIfNull(colorFilters);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(displays);
        var findings = new List<PreflightFinding>();
        const string NightLightFix = "Turn it off to measure (Settings › System › Display).";
        var nightLightSettings = new PreflightAction("Open Night light settings", "ms-settings:nightlight");

        switch (nightLight.Active)
        {
            case true:
                findings.Add(new(FindingSeverity.Blocker, "Night light", "Night light is on: it tints the screen.", NightLightFix, nightLightSettings));
                break;
            case null:
                findings.Add(new(FindingSeverity.Warning, "Night light", "Night light's state could not be read.", "Check it is off.", nightLightSettings));
                break;
            default:
                if (nightLight.ScheduleEnabled == true)
                {
                    findings.Add(new(FindingSeverity.Warning, "Night light", "Night light is scheduled and may switch on while measuring.", "Turn its schedule off for now.", nightLightSettings));
                }

                break;
        }

        if (colorFilters.Active)
        {
            findings.Add(new(
                FindingSeverity.Blocker,
                "Color filters",
                $"The {colorFilters.FilterName ?? "accessibility"} color filter is on.",
                "Turn it off (Win+Ctrl+C).",
                new PreflightAction("Open Color filters settings", "ms-settings:easeofaccess-colorfilter")));
        }

        foreach (var d in displays)
        {
            var name = d.FriendlyName ?? d.DeviceName;
            if (d.AdvancedColor is { } advanced)
            {
                var switches = AdvancedColorSwitches.From(advanced);
                if (switches.Hdr)
                {
                    findings.Add(new(FindingSeverity.Blocker, name, "HDR is on.", "SoftCal can switch it off while measuring.", HandledBySoftCal: true));
                }

                if (switches.AutoColorManagement)
                {
                    findings.Add(new(
                        FindingSeverity.Blocker,
                        name,
                        "Auto Color Management is on (it ignores 'vcgt' calibration curves).",
                        "SoftCal can switch it off while measuring.",
                        HandledBySoftCal: true));
                }
            }

            if (d.FullRangeRgb == false)
            {
                findings.Add(new(
                    FindingSeverity.Warning,
                    name,
                    $"The graphics card does not send full-range RGB ({d.LinkDescription}): shadows and highlights are clipped.",
                    "Set Output dynamic range to Full (NVIDIA Control Panel › Change resolution).",
                    new PreflightAction("Open NVIDIA Control Panel", PreflightAction.NvidiaControlPanel)));
            }

            if (d.WindowsLoadsCurves == false)
            {
                // Reported, not proven: on the development PC Windows said so for both monitors, yet its loader still
                // put a profile's curves in the GPU, rounded to 8 bits.
                findings.Add(new(
                    FindingSeverity.Info,
                    name,
                    "Windows reports no support for loading calibration curves here.",
                    "SoftCal's own loader does it."));
            }

            if (d.GammaRampIsIdentity == false)
            {
                findings.Add(new(
                    FindingSeverity.Info,
                    name,
                    "A calibration curve is loaded on this display.",
                    "It is set to linear while measuring, then restored."));
            }
        }

        if (power.EnergySaverOn == true)
        {
            findings.Add(new(FindingSeverity.Warning, "Power", "Energy saver is on and may dim the screen.", "Plug in, or turn it off.", new PreflightAction("Open Energy saver settings", "ms-settings:batterysaver")));
        }

        if (power.AdaptiveBrightnessOnAc == true || (power.OnBattery == true && power.AdaptiveBrightnessOnBattery == true))
        {
            findings.Add(new(
                FindingSeverity.Warning,
                "Power",
                "Automatic brightness is on and can change the screen while measuring.",
                "Turn off 'Change brightness automatically'.",
                new PreflightAction("Open Display settings", "ms-settings:display")));
        }

        if (overlays is { Count: > 0 })
        {
            findings.Add(new(
                FindingSeverity.Warning,
                "Overlays",
                $"{string.Join(" and ", overlays)} is running and may show a pop-up when measuring starts.",
                "To be safe, turn its overlay off in the NVIDIA App.",
                new PreflightAction("Open NVIDIA App", PreflightAction.NvidiaApp)));
        }

        if (calibrationLoaderEnabled == true)
        {
            findings.Add(new(
                FindingSeverity.Info,
                "Calibration loader",
                "'Use Windows display calibration' is on: Windows loads curves rounded to 8 bits.",
                "With a SoftCal calibration, Check (Windows calibration file) offers to turn it off."));
        }

        return findings;
    }

    /// <summary>Whether a finding stops a measurement: a blocker SoftCal does not handle itself.</summary>
    public static bool BlocksMeasuring(PreflightFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.Severity == FindingSeverity.Blocker && !finding.HandledBySoftCal;
    }

    /// <summary>What stops a measurement and can switch on at any time while SoftCal is open (Night light, a color
    /// filter): quick to read, for every screen that measures, every few seconds, and when a measurement starts.</summary>
    public static IReadOnlyList<PreflightFinding> ReadMeasuringBlockers() =>
        [.. Evaluate(NightLightReader.Read(), OsProbes.ReadColorFilters(), new PowerState(null, null, null, null), null, []).Where(BlocksMeasuring)];

    /// <summary>One line per finding, blockers first, for logs and the harness.</summary>
    public static IEnumerable<string> Describe(IReadOnlyList<PreflightFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings
            .OrderByDescending(f => f.Severity)
            .Select(f => string.Create(CultureInfo.InvariantCulture, $"[{f.Severity}] {f.Area}: {f.Message}{(f.Fix is null ? string.Empty : " → " + f.Fix)}"));
    }
}
