using SoftCal.Core.Displays;
using SoftCal.Core.Measurement;
using SoftCal.FeasibilityHarness.Infrastructure;
using SoftCal.Instruments.Webcam;
using SoftCal.Windows.Ddc;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Gamma;
using SoftCal.Windows.Gpu;
using SoftCal.Windows.NightLight;
using SoftCal.Windows.Patches;

namespace SoftCal.FeasibilityHarness.Commands;

/// <summary>Read-only commands: list devices, platform probes, and the patch-window exactness test.</summary>
internal static class InfoCommands
{
    public static int List()
    {
        Ui.Title("Cameras");
        foreach (var d in WebcamDevice.Enumerate())
        {
            Ui.Info($"{d.FriendlyName}  [{d.StableId}]{(d.IsUsbHardware ? "" : "  (virtual: not accepted)")}");
        }

        Ui.Title("Monitors");
        foreach (var m in DisplayEnumerator.List())
        {
            var edid = TryParse(m.Edid);
            Ui.Info($"{m}  {m.OutputTechnology} {Ui.F(m.RefreshRateHz, "0.###")} Hz  dpi {m.DpiX}  key {(edid is null ? "—" : DisplayKey.FromEdid(edid).Value)}");
            Ui.Info($"   {(edid is null ? "no EDID" : $"EDID {edid.VersionMajor}.{edid.VersionMinor} {edid.ManufacturerId} {edid.MonitorName} gamma {Ui.F(edid.Gamma)} {edid.DigitalInterface}")}; color {m.AdvancedColor?.ColorEncoding} {m.AdvancedColor?.BitsPerColorChannel} bpc, mode {m.AdvancedColor?.ActiveColorMode}");
        }

        return 0;
    }

    /// <summary>Prints the Night light state (read-only); with --wait, polls until it reads off (up to --minutes).</summary>
    public static async Task<int> NightLightAsync(Arguments args, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(args.GetDouble("minutes", 5));
        while (true)
        {
            var report = NightLightReader.Read();
            Ui.Info($"{DateTime.Now:HH:mm:ss} {report.Summary}");
            if (!args.Has("wait") || report.Active == false)
            {
                return report.Active == false ? 0 : 1;
            }

            if (DateTime.UtcNow > deadline)
            {
                Ui.Warn("Still not off; giving up.");
                return 1;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    /// <summary>Platform probes; nothing is written. Gamma ramps are only read here (the write test is the 'gamma' step).</summary>
    public static async Task<int> ProbeAsync(Arguments args, CancellationToken cancellationToken)
    {
        var monitors = DisplayEnumerator.List();
        using var session = HarnessSession.Create(args.Get("sessions") ?? HarnessSession.DefaultRoot, "probe", null, "M1 platform probes", HarnessRig.QpcSeconds);
        Ui.Info($"session: {session.DirectoryPath}");

        Ui.Title("Night light (CloudStore, read-only)");
        var nightLight = NightLightReader.Read();
        foreach (var b in nightLight.Blobs)
        {
            var detail = b.State is { } s
                ? $"active={s.Active} code={s.TransitionCode} since {s.TransitionUtc:u}"
                : b.Settings is { } st
                    ? $"schedule={st.ScheduleEnabled} on={st.ScheduleOn} off={st.ScheduleOff} {st.ColorTemperatureKelvin} K sunset={st.SunsetTime} sunrise={st.SunriseTime}"
                    : "error: " + b.Error;
            Ui.Info($"{b.Scope,-50} {b.Kind,-9} modified {b.ModifiedUtc:u}  {detail}");
        }

        Ui.Info(nightLight.Summary);
        session.Result("nightlight", nightLight);

        Ui.Title("Displays: link, advanced color, gamma ramp (read only)");
        var displays = new List<object>();
        foreach (var m in monitors)
        {
            GammaRamp? ramp = null;
            string? rampError = null;
            try
            {
                ramp = new GdiGammaRampDevice(m.DeviceName).Read();
            }
            catch (InvalidOperationException ex)
            {
                rampError = ex.Message;
            }

            var edid = TryParse(m.Edid);
            Ui.Info($"{m}: {m.OutputTechnology} @ {Ui.F(m.RefreshRateHz, "0.###")} Hz; {m.AdvancedColor?.ColorEncoding} {m.AdvancedColor?.BitsPerColorChannel} bpc; mode {m.AdvancedColor?.ActiveColorMode}; HDR {m.AdvancedColor?.HdrUserEnabled}; WCG/ACM {m.AdvancedColor?.WideColorUserEnabled} (enforced {m.AdvancedColor?.WideColorEnforced}); ramp {(ramp is null ? rampError : ramp.IsIdentity ? "identity" : $"modified ({ramp.MaxDeviationFromIdentity()})")}");
            displays.Add(new
            {
                m.Number,
                m.DeviceName,
                m.FriendlyName,
                m.Bounds,
                m.DpiX,
                m.OutputTechnology,
                m.RefreshRateHz,
                m.AdvancedColor,
                displayKey = edid is null ? null : DisplayKey.FromEdid(edid).Value,
                edid = edid is null ? null : new { edid.ManufacturerId, edid.MonitorName, edid.Gamma, edid.DigitalInterface, edid.BitsPerColor, edid.SrgbIsDefaultColorSpace },
                gammaRampIdentity = ramp?.IsIdentity,
                gammaRampMaxDeviation = ramp?.MaxDeviationFromIdentity(),
                gammaRampError = rampError,
            });
        }

        session.Result("displays", displays);

        Ui.Title("NVAPI (read-only GET of the link color format)");
        var nv = NvApiProbe.Probe(monitors.Select(m => m.DeviceName));
        Ui.Info($"available {nv.Available}; {nv.InterfaceVersion}; driver {nv.DriverVersion}; GPUs {string.Join(", ", nv.Gpus)} {nv.Error}");
        foreach (var d in nv.Displays)
        {
            Ui.Info($"{d.DeviceName}: id {d.DisplayId:X8}; {(d.Current is { } cur ? $"{cur.ColorFormat}, {cur.Colorimetry}, range {cur.DynamicRange}, {cur.BitsPerComponent} bpc, policy {cur.SelectionPolicy}, desktop {cur.DesktopDepth} (v{cur.StructVersion}) → {(cur.IsFullRangeRgb ? "full-range RGB ✓" : "NOT full-range RGB")}" : d.Error)}");
        }

        session.Result("nvapi", nv);

        Ui.Title("DDC/CI VCP probe (one code at a time, 2 s timeout, no capabilities request)");
        var ddc = new List<DdcMonitorReport>();
        foreach (var m in monitors)
        {
            foreach (var report in await DdcCiProbe.ProbeAsync(m, TimeSpan.FromSeconds(2), cancellationToken))
            {
                ddc.Add(report);
                Ui.Info($"{report.DeviceName} {report.FriendlyName} [{report.PhysicalDescription}]: {(report.Error ?? (report.Responded ? "responds" : "no reply"))}{(report.Stalled ? " (STALLED: rest skipped)" : "")}");
                foreach (var r in report.Readings)
                {
                    Ui.Info($"   0x{r.Code:X2} {r.Name,-24} {(r.Supported ? $"{r.Current} / {r.Maximum} ({r.CodeType}){(r.Interpretation is null ? "" : "  " + r.Interpretation)}" : r.Error)}  [{r.Milliseconds:0} ms, {r.Attempts} attempt(s)]");
                }
            }
        }

        session.Result("ddc", ddc);
        return 0;
    }

    /// <summary>Shows each patch type on a monitor and reads the screen back through desktop duplication.</summary>
    public static async Task<int> WindowAsync(Arguments args, CancellationToken cancellationToken)
    {
        var monitors = DisplayEnumerator.List();
        var monitor = DisplayEnumerator.Find(monitors, args.Get("monitor") ?? throw new ArgumentException("Choose a monitor with --monitor N."));
        using var session = HarnessSession.Create(args.Get("sessions") ?? HarnessSession.DefaultRoot, "window", "m" + monitor.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), "M1 patch window pixel exactness", HarnessRig.QpcSeconds);
        Ui.Title($"Patch window on {monitor}");
        using var window = D3DPatchWindow.Open(monitor.DeviceName);
        Ui.Info($"window {window.Width}×{window.Height}, adapter {window.AdapterName}, refresh {window.RefreshPeriodSeconds * 1000:0.###} ms");
        PatchSpec[] patches =
        [
            new SolidPatch(DeviceRgb.Gray(128)),
            new SolidPatch(new DeviceRgb(255, 128, 0)),
            new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard),
            new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.HorizontalLines),
            new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.VerticalLines),
            new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 1),
            new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 4),
            new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 12),
            new HalftonePatch(DeviceRgb.Gray(186), DeviceRgb.Black, HalftoneGeometry.Checkerboard),
            new HalftonePatch(DeviceRgb.Gray(21), DeviceRgb.Gray(20), HalftoneGeometry.Checkerboard),
            new SolidPatch(DeviceRgb.Gray(1)) { Layout = new PatchLayout(0.5, 0.5, 2.0, DeviceRgb.Black) },
        ];
        var results = new List<object>();
        var allExact = true;
        foreach (var patch in patches)
        {
            var shown = await window.ShowAsync(patch, cancellationToken);
            session.OnPatchShown(shown);
            var check = await window.VerifyAsync(cancellationToken);
            allExact &= check.PixelExact;
            Ui.Info($"{MeasurementRig.Describe(patch),-48} exact {check.PixelExact} ({check.Mismatches} of {check.PixelsCompared} px differ, max {check.MaxChannelDifference}); timing {window.LastTiming?.Source}{(check.Error is null ? "" : " — " + check.Error)}");
            results.Add(new { patch, readback = check, timing = window.LastTiming });
            await Task.Delay(300, cancellationToken);
        }

        (allExact ? (Action<FormattableString>)Ui.Good : Ui.Warn)($"pixel exact for every patch: {allExact}");
        session.Result("window", new { monitor = monitor.DeviceName, monitor.FriendlyName, allExact, width = window.Width, height = window.Height, results });
        return allExact ? 0 : 1;
    }

    private static Edid? TryParse(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return Edid.Parse(bytes);
        }
        catch (EdidFormatException)
        {
            return null;
        }
    }
}
