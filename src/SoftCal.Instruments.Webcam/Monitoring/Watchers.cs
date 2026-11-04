using System.Runtime.InteropServices;
using SoftCal.Instruments.Webcam.Controls;
using SoftCal.Instruments.Webcam.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.KernelStreaming;
using Windows.Win32.Media.MediaFoundation;

namespace SoftCal.Instruments.Webcam.Monitoring;

/// <summary>
/// IMFCameraControlMonitor: tells us when any process (Windows Settings, the Logitech app, a video-call app) changes one
/// of the camera's controls, so the lock can be re-applied and the affected reading redone.
/// </summary>
internal sealed class ControlChangeWatcher : IMFCameraControlNotify, IDisposable
{
    private IMFCameraControlMonitor? _monitor;

    private ControlChangeWatcher()
    {
    }

    /// <summary>Raised on a Media Foundation worker thread.</summary>
    public event Action<UvcControlId>? Changed;

    public event Action<int>? Error;

    /// <summary>Null when the monitor is unavailable (it needs Windows 11).</summary>
    public static ControlChangeWatcher? TryStart(string symbolicLink, IEnumerable<UvcControlId> controls, out string? failure)
    {
        var watcher = new ControlChangeWatcher();
        try
        {
            PInvoke.MFCreateCameraControlMonitor(symbolicLink, watcher, out var monitor).ThrowOnFailure();
            watcher._monitor = monitor;
            foreach (var id in controls)
            {
                monitor.AddControlSubscription(SetGuid(id.Set), (uint)id.Property);
            }

            monitor.Start();
            failure = null;
            return watcher;
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException)
        {
            watcher.Dispose();
            failure = ex.Message;
            return null;
        }
    }

    unsafe void IMFCameraControlNotify.OnChange(Guid* controlSet, uint id)
    {
        try
        {
            var set = *controlSet == typeof(PROPSETID_VIDCAP_VIDEOPROCAMP).GUID ? UvcControlSet.VideoProcAmp : UvcControlSet.CameraControl;
            Changed?.Invoke(new UvcControlId(set, (int)id));
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // A callback must never throw back into Media Foundation.
        }
    }

    void IMFCameraControlNotify.OnError(HRESULT hrStatus) => Error?.Invoke(hrStatus.Value);

    public void Dispose()
    {
        var monitor = Interlocked.Exchange(ref _monitor, null);
        if (monitor is null)
        {
            return;
        }

        try
        {
            monitor.Stop();
        }
        catch (COMException)
        {
        }

        monitor.Shutdown();
        MfInterop.Release(monitor);
    }

    private static Guid SetGuid(UvcControlSet set) =>
        set == UvcControlSet.VideoProcAmp ? typeof(PROPSETID_VIDCAP_VIDEOPROCAMP).GUID : typeof(PROPSETID_VIDCAP_CAMERACONTROL).GUID;
}

/// <summary>A process that has the camera open.</summary>
public sealed record SensorProcess(int ProcessId, bool Streaming, bool Shared);

/// <summary>IMFSensorActivityMonitor: reports which processes stream from the camera.</summary>
internal sealed class SensorActivityWatcher : IMFSensorActivitiesReportCallback, IDisposable
{
    private readonly string _symbolicLink;
    private IMFSensorActivityMonitor? _monitor;

    private SensorActivityWatcher(string symbolicLink) => _symbolicLink = symbolicLink;

    /// <summary>Raised on a Media Foundation worker thread with the processes using this camera.</summary>
    public event Action<IReadOnlyList<SensorProcess>>? Report;

    public static SensorActivityWatcher? TryStart(string symbolicLink, out string? failure)
    {
        var watcher = new SensorActivityWatcher(symbolicLink);
        try
        {
            PInvoke.MFCreateSensorActivityMonitor(watcher, out var monitor).ThrowOnFailure();
            watcher._monitor = monitor;
            monitor.Start();
            failure = null;
            return watcher;
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException)
        {
            watcher.Dispose();
            failure = ex.Message;
            return null;
        }
    }

    unsafe void IMFSensorActivitiesReportCallback.OnActivitiesReport(IMFSensorActivitiesReport sensorActivitiesReport)
    {
        IMFSensorActivityReport? report = null;
        try
        {
            fixed (char* link = _symbolicLink)
            {
                sensorActivitiesReport.GetActivityReportByDeviceName(new PCWSTR(link), out report);
            }

            report.GetProcessCount(out var count);
            var processes = new List<SensorProcess>((int)count);
            for (uint i = 0; i < count; i++)
            {
                report.GetProcessActivity(i, out var activity);
                try
                {
                    activity.GetProcessId(out var pid);
                    BOOL streaming;
                    activity.GetStreamingState(&streaming);
                    MFSensorDeviceMode mode;
                    activity.GetStreamingMode(&mode);
                    processes.Add(new SensorProcess((int)pid, streaming, mode == MFSensorDeviceMode.MFSensorDeviceMode_Shared));
                }
                finally
                {
                    MfInterop.Release(activity);
                }
            }

            Report?.Invoke(processes);
        }
        catch (COMException)
        {
            // No report for this camera in this batch.
        }
        finally
        {
            MfInterop.Release(report);
            MfInterop.Release(sensorActivitiesReport);
        }
    }

    public void Dispose()
    {
        var monitor = Interlocked.Exchange(ref _monitor, null);
        if (monitor is null)
        {
            return;
        }

        try
        {
            monitor.Stop();
        }
        catch (COMException)
        {
        }

        MfInterop.Release(monitor);
    }
}
