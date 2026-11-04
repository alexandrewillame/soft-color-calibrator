using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SoftCal.Core.Measurement;
using SoftCal.Windows.Displays;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Power;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SoftCal.Windows.Patches;

public sealed record PatchWindowOptions
{
    /// <summary>Layout used for patches that do not carry their own.</summary>
    public PatchLayout DefaultLayout { get; init; } = PatchRasterizer.DefaultLayout;

    /// <summary>How long to wait for DXGI to report the vblank at which a new patch was scanned out.</summary>
    public TimeSpan FrameStatisticsTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Keep the display and system awake while the window is open (SetThreadExecutionState).</summary>
    public bool KeepDisplayAwake { get; init; } = true;

    /// <summary>Watch the screen for anything covering the patch (another window, a notification, a taskbar preview, the
    /// pointer) or the patch missing from it, so readings taken meanwhile are taken again (<see cref="IPatchWatch"/>).
    /// Measurements turn it on; the camera's placement view does not need it.</summary>
    public bool WatchForCover { get; init; }
}

/// <summary>How the display time of a patch was obtained.</summary>
public enum PresentTimingSource
{
    /// <summary>IDXGISwapChain::GetFrameStatistics: the QPC time of the vblank at which the frame was shown.</summary>
    FrameStatistics,

    /// <summary>No statistics in time: the QPC after Present plus one refresh period (an estimate).</summary>
    PresentCallEstimate,
}

/// <summary>Diagnostics of a patch's first present.</summary>
public sealed record PresentTiming(
    long Sequence,
    uint PresentCount,
    PresentTimingSource Source,
    double DisplayTimeSeconds,
    double PresentCallSeconds,
    double RefreshPeriodSeconds,
    uint? PresentRefreshCount,
    uint? SyncRefreshCount);

/// <summary>Outcome of comparing the screen with the pixels the patch should produce.</summary>
public sealed record ReadbackResult(
    string Method,
    int Width,
    int Height,
    long PixelsCompared,
    long Mismatches,
    int MaxChannelDifference,
    long PatchMismatches,
    int? FirstMismatchX,
    int? FirstMismatchY,
    bool PointerVisibleOverPatch,
    string? Error = null)
{
    public bool PixelExact => Error is null && Mismatches == 0;

    /// <summary>True when nothing but our pixels cover the patch (no toast, cursor or other overlay).</summary>
    public bool PatchClean => Error is null && PatchMismatches == 0 && !PointerVisibleOverPatch;
}

/// <summary>
/// A borderless, top-most, non-activating window covering one monitor, drawn with a D3D11 flip-model swap chain
/// (B8G8R8A8_UNORM, no scaling, no sRGB conversion) so every pixel reaches the display exactly as rendered. The window
/// thread presents the current patch at every vblank, so DXGI frame statistics keep flowing: each new patch is stamped
/// with the QPC time of the vblank at which it was first scanned out. The screen can be read back through DXGI desktop
/// duplication to prove pixel exactness and to catch overlays, and with <see cref="PatchWindowOptions.WatchForCover"/>
/// it is watched the whole time, so a reading taken while something covered the patch is taken again.
/// </summary>
public sealed class D3DPatchWindow : IPatchPresenter, IPatchWatch, IDisposable
{
    private const uint WmInvoke = PInvoke.WM_APP + 1;
    private const uint WmQuit = PInvoke.WM_APP + 2;
    private static int _classCounter;

    private readonly PatchWindowOptions _options;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WNDPROC _wndProc;
    private readonly List<PendingTiming> _pending = [];
    private HWND _hwnd;
    private string _className = string.Empty;
    private IDXGIFactory2? _factory;
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput? _output;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _patchTexture;
    private ID3D11Texture2D? _stagingTexture;
    private byte[] _pixels = [];
    private PatchCoverWatch? _watch;
    private byte[]? _watchedPixels;
    private PatchSpec? _current;
    private double _currentDisplayTime;
    private PendingTiming? _awaitingPresent;
    private long _sequence;

    /// <summary>When presents started failing in a row (0: they succeed); after a few seconds of it, waiting patches
    /// fail instead of waiting for ever (the graphics driver was reset or removed).</summary>
    private long _failingSinceTicks;
    private bool _quit;
    private bool _disposed;
    private volatile bool _hidden;

    private D3DPatchWindow(string deviceName, PatchWindowOptions options)
    {
        DeviceName = deviceName;
        _options = options;
        _wndProc = WindowProc;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "SoftCal patch window " + deviceName };
    }

    /// <summary>GDI name of the monitor the window covers.</summary>
    public string DeviceName { get; }

    /// <summary>The monitor as seen from the (per-monitor DPI v2) window thread: physical pixel bounds.</summary>
    public DisplayMonitor? Monitor { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public double RefreshPeriodSeconds { get; private set; } = 1 / 60.0;

    public PresentTiming? LastTiming { get; private set; }

    public string? AdapterName { get; private set; }

    /// <summary>Why the screen cannot be watched for anything covering the patch (desktop duplication refused); null
    /// while it can or when it is not watched.</summary>
    public string? WatchProblem => _watch?.Problem;

    /// <summary>Opens the window on the monitor with the given GDI device name (e.g. \\.\DISPLAY2).</summary>
    public static D3DPatchWindow Open(string deviceName, PatchWindowOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        var window = new D3DPatchWindow(deviceName, options ?? new PatchWindowOptions());
        window._thread.Start();
        try
        {
            window._ready.Task.GetAwaiter().GetResult();
        }
        catch
        {
            window.Dispose();
            throw;
        }

        return window;
    }

    /// <summary>Shows a patch; completes once DXGI reports the vblank at which it was first scanned out.</summary>
    public Task<PresentedPatch> ShowAsync(PatchSpec patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new PendingTiming(patch, Interlocked.Increment(ref _sequence));
        if (cancellationToken.CanBeCanceled)
        {
            // The run stopped: the patch is no longer awaited, whether or not it was shown.
            var registration = cancellationToken.Register(() => pending.Cancel(cancellationToken));
            pending.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        Post(() =>
        {
            Upload(patch);
            _awaitingPresent?.Complete(null);
            _awaitingPresent = pending;
        });
        return pending.Task;
    }

    /// <summary>
    /// Hides the window, so the monitor shows the desktop (and SoftCal's own window, when it is moved there), or shows it
    /// again with its current patch. Hidden, it presents nothing: show it before showing a patch and waiting for it.
    /// </summary>
    public void SetVisible(bool visible) => Post(() =>
    {
        _hidden = !visible;
        _watch?.Expect(visible ? _watchedPixels : null);
        if (visible)
        {
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            PInvoke.SetWindowPos(_hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
        else
        {
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
        }
    });

    public Task<string?> DisturbanceAsync(double fromSeconds, double untilSeconds, CancellationToken cancellationToken) =>
        _watch?.DisturbanceAsync(fromSeconds, untilSeconds, cancellationToken) ?? Task.FromResult<string?>(null);

    public Task<double?> WaitUntilClearAsync(double clearSeconds, double timeoutSeconds, CancellationToken cancellationToken) =>
        _watch?.WaitUntilClearAsync(clearSeconds, timeoutSeconds, cancellationToken)
        ?? Task.FromResult<double?>(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    /// <summary>Reads the monitor back through desktop duplication and compares it with the current patch.</summary>
    public async Task<ReadbackResult> VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<ReadbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => completion.TrySetResult(Readback()));
        return await completion.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hwnd != HWND.Null)
        {
            PInvoke.PostMessage(_hwnd, WmQuit, default, default);
        }

        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private void Post(Action work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _queue.Enqueue(work);
        if (!PInvoke.PostMessage(_hwnd, WmInvoke, default, default))
        {
            throw new InvalidOperationException("The patch window is gone.");
        }
    }

    private void ThreadMain()
    {
        try
        {
            PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (_options.KeepDisplayAwake)
            {
                PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_DISPLAY_REQUIRED | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
            }

            Monitor = DisplayEnumerator.List().FirstOrDefault(m => m.DeviceName.Equals(DeviceName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No active monitor is named {DeviceName}.");
            Width = Monitor.Bounds.Width;
            Height = Monitor.Bounds.Height;
            if (Monitor.RefreshRateHz is > 1)
            {
                RefreshPeriodSeconds = 1 / Monitor.RefreshRateHz.Value;
            }

            CreateWindow(Monitor.Bounds);
            CreateGraphics();
            if (_options.WatchForCover)
            {
                // The square where the camera rests: the default patch's, whatever a patch's own layout.
                _watch = new PatchCoverWatch(DeviceName, Monitor.Bounds, PatchRasterizer.Rect(_options.DefaultLayout, Width, Height));
            }

            Upload(new SolidPatch(_options.DefaultLayout.Surround) { Layout = _options.DefaultLayout });
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            Cleanup();
            return;
        }

        try
        {
            RenderLoop();
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>Pumps messages and presents the current patch at every vblank (Present(1) paces the loop).</summary>
    private void RenderLoop()
    {
        while (!_quit)
        {
            while (PInvoke.PeekMessage(out var msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                if (msg.message == PInvoke.WM_QUIT)
                {
                    _quit = true;
                    break;
                }

                PInvoke.TranslateMessage(msg);
                PInvoke.DispatchMessage(msg);
            }

            if (_quit)
            {
                break;
            }

            if (_hidden)
            {
                // A hidden flip-model window's Present returns at once: wait instead of spinning.
                Thread.Sleep(15);
                continue;
            }

            PresentFrame();
            ResolveTimings();
        }
    }

    private unsafe void CreateWindow(PixelBounds bounds)
    {
        _className = "SoftCalPatchWindow" + Interlocked.Increment(ref _classCounter).ToString(CultureInfo.InvariantCulture);
        var instance = PInvoke.GetModuleHandle((string?)null);
        fixed (char* className = _className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = (HINSTANCE)instance.DangerousGetHandle(),
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(wc) == 0)
            {
                throw new InvalidOperationException("RegisterClassEx failed.");
            }
        }

        _hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP,
            _className,
            "SoftCal patch",
            WINDOW_STYLE.WS_POPUP,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            HWND.Null,
            null,
            instance,
            null);
        if (_hwnd == HWND.Null)
        {
            throw new InvalidOperationException("CreateWindowEx failed.");
        }

        // Hovering a taskbar thumbnail ("peek") fades every other window, this one included, in the middle of a
        // reading. Excluded from peek it stays opaque; best effort (DWM can refuse on unusual setups).
        BOOL on = true;
        PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXCLUDED_FROM_PEEK, &on, (uint)sizeof(BOOL));
        PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_DISALLOW_PEEK, &on, (uint)sizeof(BOOL));

        PInvoke.SetWindowPos(_hwnd, HWND.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        if (!PInvoke.GetClientRect(_hwnd, out var client) || client.right - client.left != bounds.Width || client.bottom - client.top != bounds.Height)
        {
            throw new InvalidOperationException(
                $"The patch window is {client.right - client.left}×{client.bottom - client.top} px, not the monitor's {bounds.Width}×{bounds.Height}: DPI virtualization is active.");
        }
    }

    private unsafe void CreateGraphics()
    {
        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        for (uint a = 0; _factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (output.Description.DeviceName.Equals(DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    _adapter = adapter;
                    _output = output;
                    break;
                }

                output.Dispose();
            }

            if (_output is not null)
            {
                break;
            }

            adapter.Dispose();
        }

        if (_adapter is null || _output is null)
        {
            throw new InvalidOperationException($"No DXGI output drives {DeviceName}.");
        }

        AdapterName = _adapter.Description1.Description;
        D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out _device,
            out _context).CheckError();
        using (var dxgiDevice = _device!.QueryInterface<IDXGIDevice1>())
        {
            // One queued frame: a new patch reaches the screen at the next vblank.
            dxgiDevice.MaximumFrameLatency = 1;
        }

        var description = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.None,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };
        _swapChain = _factory.CreateSwapChainForHwnd(_device, (nint)_hwnd.Value, description, null, null);
        _factory.MakeWindowAssociation((nint)_hwnd.Value, WindowAssociationFlags.IgnoreAll);

        var texture = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.None,
        };
        _patchTexture = _device.CreateTexture2D(texture);
        texture.Usage = ResourceUsage.Staging;
        texture.CPUAccessFlags = CpuAccessFlags.Read;
        _stagingTexture = _device.CreateTexture2D(texture);
        _pixels = new byte[Width * Height * 4];
    }

    private void Upload(PatchSpec patch)
    {
        PatchRasterizer.Render(patch, Width, Height, _pixels, _options.DefaultLayout);
        _context!.UpdateSubresource(new ReadOnlySpan<byte>(_pixels), _patchTexture!, 0, (uint)(Width * 4), 0, null);
        _current = patch;
        if (_watch is { } watch)
        {
            // What the watched square must show from now on (the watch keeps a copy: _pixels changes with each patch).
            var region = watch.Region;
            var rowBytes = region.Width * 4;
            var pixels = new byte[rowBytes * region.Height];
            for (var y = 0; y < region.Height; y++)
            {
                Buffer.BlockCopy(_pixels, (((region.Y + y) * Width) + region.X) * 4, pixels, y * rowBytes, rowBytes);
            }

            _watchedPixels = pixels;
            if (!_hidden)
            {
                watch.Expect(pixels);
            }
        }
    }

    private void PresentFrame()
    {
        var swapChain = _swapChain!;
        using (var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0))
        {
            _context!.CopyResource(backBuffer, _patchTexture!);
        }

        var result = swapChain.Present(1, PresentFlags.None);
        var presentCall = Stopwatch.GetTimestamp();
        if (result.Failure)
        {
            // A device removed or reset: wait a little and keep the loop alive; after a few seconds of it, what waits for
            // a patch fails instead of waiting for ever.
            if (_failingSinceTicks == 0)
            {
                _failingSinceTicks = presentCall;
            }
            else if (presentCall - _failingSinceTicks > 3 * Stopwatch.Frequency)
            {
                var error = new InvalidOperationException($"The graphics card stopped showing the measurement patch ({result.Description}); its driver may have been reset.");
                _awaitingPresent?.Fail(error);
                _awaitingPresent = null;
                foreach (var p in _pending)
                {
                    p.Fail(error);
                }

                _pending.Clear();
            }

            Thread.Sleep(10);
            return;
        }

        _failingSinceTicks = 0;

        if (_awaitingPresent is { } pending)
        {
            pending.PresentCount = swapChain.LastPresentCount;
            pending.PresentCallTicks = presentCall;
            _awaitingPresent = null;
            _pending.Add(pending);
        }
    }

    /// <summary>
    /// Matches DXGI frame statistics to patches waiting for their scan-out time. Statistics describe the most recent
    /// present: it went out at vblank PresentRefreshCount, and vblank SyncRefreshCount happened at SyncQPCTime.
    /// </summary>
    private void ResolveTimings()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var frequency = (double)Stopwatch.Frequency;
        var haveStats = _swapChain!.GetFrameStatistics(out var stats).Success && stats.SyncQPCTime != 0 && stats.PresentCount != 0;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            PresentTiming? timing = null;
            if (haveStats && stats.PresentCount >= p.PresentCount)
            {
                // Earlier presents went out one vblank apart (one present per vblank, latency 1).
                var refresh = (long)stats.PresentRefreshCount - (stats.PresentCount - p.PresentCount);
                var ticks = stats.SyncQPCTime + (long)((refresh - stats.SyncRefreshCount) * RefreshPeriodSeconds * frequency);
                timing = new PresentTiming(
                    p.Sequence, p.PresentCount, PresentTimingSource.FrameStatistics, ticks / frequency, p.PresentCallTicks / frequency,
                    RefreshPeriodSeconds, (uint)refresh, stats.SyncRefreshCount);
            }
            else if (Stopwatch.GetTimestamp() - p.PresentCallTicks > _options.FrameStatisticsTimeout.TotalSeconds * frequency)
            {
                timing = new PresentTiming(
                    p.Sequence, p.PresentCount, PresentTimingSource.PresentCallEstimate, (p.PresentCallTicks / frequency) + RefreshPeriodSeconds,
                    p.PresentCallTicks / frequency, RefreshPeriodSeconds, null, null);
            }

            if (timing is not null)
            {
                _pending.RemoveAt(i);
                LastTiming = timing;
                _currentDisplayTime = timing.DisplayTimeSeconds;
                p.Complete(timing);
            }
        }
    }

    private ReadbackResult Readback()
    {
        if (_current is null || _output is null || _device is null || _context is null || _stagingTexture is null)
        {
            return new ReadbackResult("desktop duplication", Width, Height, 0, 0, 0, 0, null, null, false, "nothing shown yet");
        }

        IDXGIOutputDuplication? duplication = null;
        try
        {
            using (var output5 = _output.QueryInterfaceOrNull<IDXGIOutput5>())
            {
                duplication = output5 is not null
                    ? output5.DuplicateOutput1(_device, [Format.B8G8R8A8_UNorm])
                    : _output.QueryInterface<IDXGIOutput1>().DuplicateOutput(_device);
            }

            // The first desktop frame can predate the current patch (the window may be in independent flip, which the
            // compositor does not see until duplication starts): keep presenting until a frame newer than the patch.
            var needed = (long)(_currentDisplayTime * Stopwatch.Frequency);
            var deadline = Stopwatch.GetTimestamp() + (2 * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                PresentFrame();
                var result = duplication.AcquireNextFrame(50, out var info, out var resource);
                if (result.Failure || resource is null)
                {
                    continue;
                }

                try
                {
                    if (info.LastPresentTime == 0 || info.LastPresentTime < needed)
                    {
                        continue;
                    }

                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    var description = texture.Description;
                    if (description.Width != Width || description.Height != Height || description.Format != Format.B8G8R8A8_UNorm)
                    {
                        return new ReadbackResult(
                            "desktop duplication", Width, Height, 0, 0, 0, 0, null, null, false,
                            $"desktop image is {description.Width}×{description.Height} {description.Format}");
                    }

                    _context.CopyResource(_stagingTexture, texture);
                    var layout = _current.Layout ?? _options.DefaultLayout;
                    var rect = PatchRasterizer.Rect(layout, Width, Height);
                    var pointer = info.PointerPosition;
                    var pointerOverPatch = pointer.Visible && rect.Contains(pointer.Position.X, pointer.Position.Y);
                    var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        return Compare(mapped, rect, pointerOverPatch);
                    }
                    finally
                    {
                        _context.Unmap(_stagingTexture, 0);
                    }
                }
                finally
                {
                    resource.Dispose();
                    duplication.ReleaseFrame();
                }
            }

            return new ReadbackResult("desktop duplication", Width, Height, 0, 0, 0, 0, null, null, false, "no desktop frame newer than the patch");
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            return new ReadbackResult("desktop duplication", Width, Height, 0, 0, 0, 0, null, null, false, ex.Message);
        }
        finally
        {
            duplication?.Dispose();
        }
    }

    private unsafe ReadbackResult Compare(MappedSubresource mapped, PatchRect rect, bool pointerOverPatch)
    {
        long mismatches = 0, patchMismatches = 0;
        var maxDifference = 0;
        int? firstX = null, firstY = null;
        var source = (byte*)mapped.DataPointer;
        for (var y = 0; y < Height; y++)
        {
            var actual = new ReadOnlySpan<byte>(source + (y * mapped.RowPitch), Width * 4);
            var expected = new ReadOnlySpan<byte>(_pixels, y * Width * 4, Width * 4);
            if (actual.SequenceEqual(expected))
            {
                continue;
            }

            for (var x = 0; x < Width; x++)
            {
                var o = x * 4;
                var d = Math.Max(Math.Abs(actual[o] - expected[o]), Math.Max(Math.Abs(actual[o + 1] - expected[o + 1]), Math.Abs(actual[o + 2] - expected[o + 2])));
                if (d == 0)
                {
                    continue;
                }

                mismatches++;
                maxDifference = Math.Max(maxDifference, d);
                firstX ??= x;
                firstY ??= y;
                if (rect.Contains(x, y))
                {
                    patchMismatches++;
                }
            }
        }

        return new ReadbackResult("desktop duplication", Width, Height, (long)Width * Height, mismatches, maxDifference, patchMismatches, firstX, firstY, pointerOverPatch);
    }

    private LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case WmInvoke:
                while (_queue.TryDequeue(out var work))
                {
                    work();
                }

                return new LRESULT(0);
            case WmQuit:
                PInvoke.DestroyWindow(hwnd);
                return new LRESULT(0);
            case PInvoke.WM_DESTROY:
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);
            case PInvoke.WM_SETCURSOR:
                // Hide the pointer over the patch window.
                PInvoke.SetCursor(null);
                return new LRESULT(1);
            case PInvoke.WM_MOUSEACTIVATE:
                return new LRESULT((nint)PInvoke.MA_NOACTIVATE);
            case PInvoke.WM_ERASEBKGND:
                return new LRESULT(1);
            default:
                return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private void Cleanup()
    {
        while (_queue.TryDequeue(out var work))
        {
            work();
        }

        _watch?.Dispose();

        _awaitingPresent?.Fail(new ObjectDisposedException(nameof(D3DPatchWindow)));
        foreach (var p in _pending)
        {
            p.Fail(new ObjectDisposedException(nameof(D3DPatchWindow)));
        }

        _pending.Clear();
        _stagingTexture?.Dispose();
        _patchTexture?.Dispose();
        _swapChain?.Dispose();
        _context?.ClearState();
        _context?.Dispose();
        _device?.Dispose();
        _output?.Dispose();
        _adapter?.Dispose();
        _factory?.Dispose();
        if (_hwnd != HWND.Null)
        {
            PInvoke.DestroyWindow(_hwnd);
            _hwnd = HWND.Null;
        }

        if (_className.Length > 0)
        {
            PInvoke.UnregisterClass(_className, PInvoke.GetModuleHandle((string?)null));
        }

        if (_options.KeepDisplayAwake)
        {
            PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }
    }

    /// <summary>A patch waiting for its first present and then for the vblank it went out on.</summary>
    private sealed class PendingTiming(PatchSpec patch, long sequence)
    {
        private readonly TaskCompletionSource<PresentedPatch> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PatchSpec Patch { get; } = patch;

        public long Sequence { get; } = sequence;

        public uint PresentCount { get; set; }

        public long PresentCallTicks { get; set; }

        public Task<PresentedPatch> Task => _completion.Task;

        /// <summary>Completes with the timing, or (null) as superseded by a newer patch before it was presented.</summary>
        public void Complete(PresentTiming? timing)
        {
            if (timing is null)
            {
                _completion.TrySetException(new OperationCanceledException("A newer patch replaced this one before it was presented."));
                return;
            }

            _completion.TrySetResult(new PresentedPatch(Patch, Sequence, timing.DisplayTimeSeconds));
        }

        public void Fail(Exception error) => _completion.TrySetException(error);

        public void Cancel(CancellationToken cancellationToken) => _completion.TrySetCanceled(cancellationToken);
    }
}
