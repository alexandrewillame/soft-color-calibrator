using System.Diagnostics;
using System.Globalization;
using SoftCal.Windows.Displays;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace SoftCal.Windows.Ddc;

/// <summary>A VCP value as the monitor reports it.</summary>
public readonly record struct VcpValue(uint Current, uint Maximum);

/// <summary>Read/write access to one physical monitor's VCP codes over DDC/CI.</summary>
public interface IVcpChannel : IDisposable
{
    /// <summary>Stable identity: monitor device path plus the physical-monitor index behind it.</summary>
    string Key { get; }

    /// <summary>The value, or null when the monitor does not support the code or did not answer.</summary>
    Task<VcpValue?> ReadAsync(byte code, CancellationToken cancellationToken);

    /// <summary>False when the monitor rejected the write. Writes are not verified here; see <see cref="DdcValueGuard"/>.</summary>
    Task<bool> WriteAsync(byte code, uint value, CancellationToken cancellationToken);
}

/// <summary>Thrown when a channel has used up its write budget.</summary>
public sealed class DdcWriteLimitException : InvalidOperationException
{
    public DdcWriteLimitException()
    {
    }

    public DdcWriteLimitException(string message)
        : base(message)
    {
    }

    public DdcWriteLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A thread-safe count of the writes a channel may still make.</summary>
public sealed class WriteBudget(int limit)
{
    private int _used;

    public int Limit { get; } = limit >= 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));

    public int Used => Volatile.Read(ref _used);

    /// <summary>Takes one write from the budget, or throws <see cref="DdcWriteLimitException"/> when none is left.</summary>
    public void Take()
    {
        if (Interlocked.Increment(ref _used) > Limit)
        {
            Interlocked.Decrement(ref _used);
            throw new DdcWriteLimitException(string.Create(
                CultureInfo.InvariantCulture,
                $"The DDC/CI write budget of {Limit} writes for this monitor is used up (it protects the monitor's settings memory)."));
        }
    }
}

/// <summary>
/// One physical monitor's DDC/CI link: every call is serialized, spaced, and given a timeout on its own thread (DDC/CI
/// is slow, and some monitors hang). After a timeout the channel is marked stalled and refuses further calls. Writes are
/// capped by a <see cref="WriteBudget"/>. It never sends the capabilities request, which can crash Windows.
/// </summary>
public sealed class DdcCiChannel : IVcpChannel
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HANDLE _handle;
    private long _lastCall;
    private volatile bool _stalled;
    private bool _disposed;

    private DdcCiChannel(string key, string description, HANDLE handle, int writeLimit)
    {
        Key = key;
        Description = description;
        _handle = handle;
        Writes = new WriteBudget(writeLimit);
    }

    public string Key { get; }

    /// <summary>The physical-monitor description the driver reports.</summary>
    public string Description { get; }

    public WriteBudget Writes { get; }

    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MinimumSpacing { get; init; } = TimeSpan.FromMilliseconds(50);

    public bool IsStalled => _stalled;

    /// <summary>Opens a channel per physical monitor behind a display (usually one). Empty when DDC/CI is unavailable.</summary>
    public static IReadOnlyList<DdcCiChannel> Open(DisplayMonitor monitor, int writeLimit = 64)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var (handles, descriptions, error) = DdcCiProbe.OpenPhysicalMonitors((HMONITOR)monitor.Handle);
        if (error is not null)
        {
            return [];
        }

        var path = monitor.MonitorDevicePath is { Length: > 0 } p ? p : monitor.DeviceName;
        return [.. handles.Select((h, i) => new DdcCiChannel(KeyFor(path, i), descriptions[i], h.hPhysicalMonitor, writeLimit))];
    }

    /// <summary>Opens the channel with this key on the currently connected displays, or returns null.</summary>
    public static DdcCiChannel? OpenByKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var separator = key.LastIndexOf('|');
        if (separator <= 0 || !int.TryParse(key.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }

        var path = key[..separator];
        var monitor = DisplayEnumerator.List().FirstOrDefault(m =>
            string.Equals(m.MonitorDevicePath, path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.DeviceName, path, StringComparison.OrdinalIgnoreCase));
        if (monitor is null)
        {
            return null;
        }

        var channels = Open(monitor);
        foreach (var other in channels.Where((_, i) => i != index))
        {
            other.Dispose();
        }

        return index < channels.Count ? channels[index] : null;
    }

    public static string KeyFor(string monitorDevicePath, int physicalIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"{monitorDevicePath}|{physicalIndex}");

    public async Task<VcpValue?> ReadAsync(byte code, CancellationToken cancellationToken)
    {
        var (ok, _, current, maximum, _) = await CallAsync(() => DdcCiProbe.ReadOnce(_handle, code), cancellationToken).ConfigureAwait(false);
        return ok ? new VcpValue(current, maximum) : null;
    }

    public Task<bool> WriteAsync(byte code, uint value, CancellationToken cancellationToken)
    {
        Writes.Take();
        return CallAsync(() => PInvoke.SetVCPFeature(_handle, code, value) != 0, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // A stalled call may still be using the handle inside the driver: leak it rather than destroy it under the call.
        if (!_stalled)
        {
            PInvoke.DestroyPhysicalMonitor(_handle);
        }

        _gate.Dispose();
    }

    private async Task<T> CallAsync<T>(Func<T> call, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stalled)
            {
                throw new TimeoutException($"{Description} stopped answering DDC/CI earlier in this session.");
            }

            var wait = MinimumSpacing - Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCall));
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            // Once started, the call runs to its end (or its timeout) whatever the token says: releasing the channel under
            // it would let the next call overlap it on the same handle, or Dispose destroy the handle while the driver
            // still uses it. The token is honoured at the caller's next await.
            var task = Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (await Task.WhenAny(task, Task.Delay(CallTimeout, CancellationToken.None)).ConfigureAwait(false) != task)
            {
                _stalled = true;
                throw new TimeoutException($"{Description} did not answer DDC/CI within {CallTimeout.TotalSeconds:0.#} s.");
            }

            return await task.ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _lastCall, Stopwatch.GetTimestamp());
            _gate.Release();
        }
    }
}
