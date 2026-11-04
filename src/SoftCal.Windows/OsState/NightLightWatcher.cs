using Microsoft.Win32;
using SoftCal.Windows.NightLight;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace SoftCal.Windows.OsState;

/// <summary>
/// Watches Night light during a measurement and raises <see cref="Changed"/> when it switches on or off (or its
/// schedule changes), so the run can pause. It waits for registry change notifications on the CloudStore key and also
/// re-reads on a timer, because a missed notification must never let Night light tint a measurement unnoticed. Each
/// read uses <see cref="NightLightReader"/>, which takes the newest blob (the per-device key can lag behind).
/// </summary>
public sealed class NightLightWatcher : IDisposable
{
    private readonly Func<NightLightReport> _read;
    private readonly TimeSpan _pollInterval;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private readonly Lock _gate = new();
    private NightLightReport _current;
    private bool _disposed;

    private NightLightWatcher(Func<NightLightReport> read, TimeSpan pollInterval)
    {
        _read = read;
        _pollInterval = pollInterval;
        _current = read();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Night light watcher" };
    }

    /// <summary>Raised on the watcher thread with the new report.</summary>
    public event EventHandler<NightLightReport>? Changed;

    public NightLightReport Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public static NightLightWatcher Start(TimeSpan? pollInterval = null, Func<NightLightReport>? read = null)
    {
        var watcher = new NightLightWatcher(read ?? NightLightReader.Read, pollInterval ?? TimeSpan.FromSeconds(2));
        watcher._thread.Start();
        return watcher;
    }

    /// <summary>True when the difference matters to a measurement: Night light switched, or its schedule changed.</summary>
    public static bool HasChanged(NightLightReport previous, NightLightReport current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        return previous.Active != current.Active || previous.ScheduleEnabled != current.ScheduleEnabled;
    }

    /// <summary>Re-reads now and raises <see cref="Changed"/> if needed (the watcher thread calls this; tests may too).</summary>
    internal void Check()
    {
        var report = _read();
        NightLightReport previous;
        lock (_gate)
        {
            previous = _current;
            _current = report;
        }

        if (HasChanged(previous, report))
        {
            Changed?.Invoke(this, report);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Set();
        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        _stop.Dispose();
    }

    private void Loop()
    {
        using var notified = new AutoResetEvent(false);
        RegistryKey? key = null;
        try
        {
            key = Registry.CurrentUser.OpenSubKey(NightLightReader.StoreKey, writable: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            key = null;
        }

        try
        {
            var armed = false;
            while (true)
            {
                if (!armed && key is not null)
                {
                    armed = PInvoke.RegNotifyChangeKeyValue(
                        key.Handle,
                        true,
                        REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_LAST_SET,
                        notified.SafeWaitHandle,
                        true) == WIN32_ERROR.ERROR_SUCCESS;
                }

                var signal = WaitHandle.WaitAny([_stop, notified], _pollInterval);
                if (signal == 0)
                {
                    return;
                }

                if (signal == 1)
                {
                    // A notification fires once; re-arm before the next wait.
                    armed = false;
                }

                try
                {
                    Check();
                }
#pragma warning disable CA1031 // A blob that cannot be read now is read again at the next look; this thread must never die.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }
        finally
        {
            key?.Dispose();
        }
    }
}
