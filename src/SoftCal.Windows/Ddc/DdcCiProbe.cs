using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SoftCal.Windows.Displays;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace SoftCal.Windows.Ddc;

/// <summary>One VCP code read over DDC/CI.</summary>
public sealed record VcpReading(
    byte Code,
    string Name,
    bool Supported,
    uint? Current,
    uint? Maximum,
    string? CodeType,
    int Attempts,
    double Milliseconds,
    string? Interpretation,
    string? Error);

/// <summary>DDC/CI results for one physical monitor.</summary>
public sealed record DdcMonitorReport(
    string DeviceName,
    string? FriendlyName,
    string? PhysicalDescription,
    bool Responded,
    bool Stalled,
    IReadOnlyList<VcpReading> Readings,
    string? Error);

/// <summary>
/// Reads the VCP codes the OSD advisor needs, one at a time, with a timeout per call and a pause between calls
/// (DDC/CI is slow and some monitors hang). It never sends the capabilities request: a malformed capabilities reply
/// can crash Windows. Nothing is written.
/// </summary>
public static class DdcCiProbe
{
    public static IReadOnlyList<(byte Code, string Name)> Codes { get; } =
    [
        (0x10, "Brightness"),
        (0x12, "Contrast"),
        (0x14, "Select color preset"),
        (0x16, "Red video gain"),
        (0x18, "Green video gain"),
        (0x1A, "Blue video gain"),
        (0x6C, "Red video black level"),
        (0x6E, "Green video black level"),
        (0x70, "Blue video black level"),
        (0x72, "Gamma"),
        (0xDC, "Display application"),
    ];

    /// <summary>Probes every physical monitor behind a display. Each monitor's calls run serially on their own thread;
    /// a call that exceeds <paramref name="callTimeout"/> marks the monitor as stalled and the rest is skipped.</summary>
    public static async Task<IReadOnlyList<DdcMonitorReport>> ProbeAsync(DisplayMonitor monitor, TimeSpan callTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var reports = new List<DdcMonitorReport>();
        var (handles, descriptions, error) = OpenPhysicalMonitors((HMONITOR)monitor.Handle);
        if (error is not null)
        {
            return [new DdcMonitorReport(monitor.DeviceName, monitor.FriendlyName, null, false, false, [], error)];
        }

        var anyStalled = false;
        try
        {
            for (var i = 0; i < handles.Length; i++)
            {
                var readings = new List<VcpReading>();
                var stalled = false;
                foreach (var (code, name) in Codes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reading = await ReadWithRetryAsync(handles[i].hPhysicalMonitor, code, name, callTimeout).ConfigureAwait(false);
                    readings.Add(reading);
                    if (reading.Error == "timeout")
                    {
                        stalled = true;
                        break;
                    }

                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                anyStalled |= stalled;
                reports.Add(new DdcMonitorReport(
                    monitor.DeviceName,
                    monitor.FriendlyName,
                    descriptions[i],
                    readings.Any(r => r.Supported),
                    stalled,
                    readings,
                    null));
            }
        }
        finally
        {
            // A stalled call may still be using its handle inside the driver: leak rather than destroy under it.
            if (!anyStalled)
            {
                PInvoke.DestroyPhysicalMonitors(handles);
            }
        }

        return reports;
    }

    /// <summary>MCCS meaning of a value, where one is standard.</summary>
    public static string? Interpret(byte code, uint current, uint maximum) => code switch
    {
        0x14 => (current & 0xFF) switch
        {
            0x01 => "sRGB",
            0x02 => "display native",
            0x03 => "4000 K",
            0x04 => "5000 K",
            0x05 => "6500 K",
            0x06 => "7500 K",
            0x07 => "8200 K",
            0x08 => "9300 K",
            0x09 => "10000 K",
            0x0A => "11500 K",
            0x0B => "user 1",
            0x0C => "user 2",
            0x0D => "user 3",
            _ => null,
        },
        0x72 => (current & 0xFF) is >= 0x32 and <= 0xFA
            ? string.Create(CultureInfo.InvariantCulture, $"γ ≈ {((current & 0xFF) + 100) / 100.0:0.00} (MCCS encoding; monitors vary)")
            : null,
        0xDC => (current & 0xFF) switch
        {
            0x00 => "standard",
            0x01 => "productivity",
            0x02 => "mixed",
            0x03 => "movie",
            0x04 => "user defined",
            0x05 => "games",
            0x06 => "sports",
            0x07 => "professional (calibration)",
            0x08 => "standard, intermediate power",
            0x09 => "standard, low power",
            0x0A => "demonstration",
            0xF0 => "dynamic contrast",
            _ => null,
        },
        _ => maximum > 0 ? string.Create(CultureInfo.InvariantCulture, $"{100.0 * current / maximum:0}% of range") : null,
    };

    internal static unsafe (PHYSICAL_MONITOR[] Handles, string[] Descriptions, string? Error) OpenPhysicalMonitors(HMONITOR monitor)
    {
        if (!PInvoke.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0)
        {
            return ([], [], "no physical monitor handle (DDC/CI unavailable on this connection)");
        }

        var handles = new PHYSICAL_MONITOR[count];
        if (!PInvoke.GetPhysicalMonitorsFromHMONITOR(monitor, handles))
        {
            return ([], [], string.Create(CultureInfo.InvariantCulture, $"GetPhysicalMonitorsFromHMONITOR failed (error {Marshal.GetLastPInvokeError()})"));
        }

        return (handles, [.. handles.Select(h => h.szPhysicalMonitorDescription.ToString())], null);
    }

    private static async Task<VcpReading> ReadWithRetryAsync(HANDLE handle, byte code, string name, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        VcpReading? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var call = Task.Factory.StartNew(
                () => ReadOnce(handle, code),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (await Task.WhenAny(call, Task.Delay(timeout)).ConfigureAwait(false) != call)
            {
                return new VcpReading(code, name, false, null, null, null, attempt, watch.Elapsed.TotalMilliseconds, null, "timeout");
            }

            var (ok, type, current, maximum, error) = await call.ConfigureAwait(false);
            if (ok)
            {
                return new VcpReading(code, name, true, current, maximum, type, attempt, watch.Elapsed.TotalMilliseconds, Interpret(code, current, maximum), null);
            }

            last = new VcpReading(code, name, false, null, null, null, attempt, watch.Elapsed.TotalMilliseconds, null, error);
            await Task.Delay(100).ConfigureAwait(false);
        }

        return last!;
    }

    internal static unsafe (bool Ok, string? Type, uint Current, uint Maximum, string? Error) ReadOnce(HANDLE handle, byte code)
    {
        MC_VCP_CODE_TYPE type;
        uint current, maximum;
        var ok = PInvoke.GetVCPFeatureAndVCPFeatureReply(handle, code, &type, &current, &maximum) != 0;
        return ok
            ? (true, type == MC_VCP_CODE_TYPE.MC_MOMENTARY ? "momentary" : "set parameter", current, maximum, null)
            : (false, null, 0, 0, string.Create(CultureInfo.InvariantCulture, $"not supported or no reply (error 0x{Marshal.GetLastPInvokeError():X8})"));
    }
}
