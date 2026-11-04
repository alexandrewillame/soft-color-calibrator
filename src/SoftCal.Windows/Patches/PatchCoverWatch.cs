using System.Diagnostics;
using System.Globalization;
using SharpGen.Runtime;
using SoftCal.Windows.Displays;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SoftCal.Windows.Patches;

/// <summary>
/// Watches the screen under a patch window through DXGI desktop duplication, and remembers when anything but the patch
/// reached the part of the screen where the camera rests (the default patch square): another window or a notification
/// over it, a taskbar preview or "peek", the pointer, or the patch window missing altogether (Show desktop, a UAC prompt,
/// the lock screen, a display change). About 30 times a second it compares that square of the desktop image with the
/// pixels the window drew. Times are QPC seconds: the clock of the window's present times and of the camera's frames.
/// </summary>
internal sealed class PatchCoverWatch : IDisposable
{
    /// <summary>A pixel is covered when a channel differs from the patch by more than this. The desktop image is exact;
    /// this only allows for setups that convert it slightly.</summary>
    private const int PixelTolerance = 6;

    /// <summary>Covered pixels that make a disturbance: a few stray ones never do, and any window covers far more.</summary>
    private const int MinCoveredPixels = 24;

    /// <summary>After a new patch, an image that shows neither it nor the one before is not judged for this long: the
    /// window may not have presented the new one yet.</summary>
    private const double ChangeGraceSeconds = 0.25;

    /// <summary>The watch looks at the desktop at most this often.</summary>
    private const double IntervalSeconds = 1 / 30.0;

    /// <summary>A pointer's size: one whose box reaches the square is over it.</summary>
    private const int PointerSize = 32;

    private readonly string _deviceName;
    private readonly PixelBounds _monitor;
    private readonly PatchRect _region;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly List<Episode> _past = [];
    private volatile Expectation? _expected;
    private volatile string? _problem;
    private volatile bool _stop;

    // Under _gate: how far the screen has been judged, the last clean image, since when it has been clear, and a
    // disturbance still going on.
    private double _checkedThrough = double.NegativeInfinity;
    private double _lastClean;
    private double _clearSince;
    private Episode? _open;

    /// <param name="deviceName">The monitor's GDI name (e.g. \\.\DISPLAY2).</param>
    /// <param name="monitor">The monitor's bounds on the desktop, in physical pixels.</param>
    /// <param name="region">The square watched, in the monitor's pixels.</param>
    public PatchCoverWatch(string deviceName, PixelBounds monitor, PatchRect region)
    {
        _deviceName = deviceName;
        _monitor = monitor;
        _region = region;
        _lastClean = _clearSince = Now();
        _thread = new Thread(Run) { IsBackground = true, Name = "SoftCal patch watch " + deviceName };
        _thread.Start();
    }

    /// <summary>The square of the screen that is watched, in the monitor's pixels.</summary>
    public PatchRect Region => _region;

    /// <summary>Why the screen cannot be watched (desktop duplication refused), or null while it can.</summary>
    public string? Problem => _problem;

    /// <summary>What the square should show from now on (its BGRA rows, as the window drew them), or null while the
    /// window is hidden and nothing is expected there.</summary>
    public void Expect(byte[]? regionPixels) =>
        _expected = regionPixels is null ? null : new Expectation(regionPixels, _expected?.Current, Now());

    public async Task<string?> DisturbanceAsync(double fromSeconds, double untilSeconds, CancellationToken cancellationToken)
    {
        var deadline = Now() + 0.5;
        while (CheckedThrough < untilSeconds && _problem is null && !_stop && Now() < deadline)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_open is { } open && open.Start < untilSeconds)
            {
                return open.What;
            }

            for (var i = _past.Count - 1; i >= 0; i--)
            {
                var past = _past[i];
                if (past.Start < untilSeconds && past.End > fromSeconds)
                {
                    return past.What;
                }
            }

            return null;
        }
    }

    public async Task<double?> WaitUntilClearAsync(double clearSeconds, double timeoutSeconds, CancellationToken cancellationToken)
    {
        var deadline = Now() + timeoutSeconds;
        while (Now() < deadline)
        {
            if (_problem is not null || _stop)
            {
                return Now();
            }

            lock (_gate)
            {
                if (_open is null && _checkedThrough - _clearSince >= clearSeconds)
                {
                    return _clearSince;
                }
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private double CheckedThrough
    {
        get
        {
            lock (_gate)
            {
                return _checkedThrough;
            }
        }
    }

    private void Run()
    {
        // Physical pixels for the pointer's position, like the window's.
        PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        IDXGIFactory2? factory = null;
        IDXGIAdapter1? adapter = null;
        IDXGIOutput1? output = null;
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        ID3D11Texture2D? staging = null;
        IDXGIOutputDuplication? duplication = null;
        try
        {
            (factory, adapter, output) = FindOutput(_deviceName);
            D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                out device,
                out context).CheckError();
            staging = device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_region.Width,
                Height = (uint)_region.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });

            // The last desktop image's verdict (null: clean), and the expectation it was judged against.
            string? image = null;
            Expectation? judgedAgainst = null;
            var interval = (long)(IntervalSeconds * Stopwatch.Frequency);
            var next = Stopwatch.GetTimestamp();
            while (!_stop)
            {
                var wait = next - Stopwatch.GetTimestamp();
                if (wait > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(wait / (double)Stopwatch.Frequency));
                }

                next = Stopwatch.GetTimestamp() + interval;
                var expected = _expected;
                if (expected is null)
                {
                    // Hidden: nothing to watch, and the desktop runs without duplication meanwhile.
                    duplication?.Dispose();
                    duplication = null;
                    judgedAgainst = null;
                    continue;
                }

                if (duplication is null)
                {
                    try
                    {
                        duplication = Duplicate(output!, device!);
                        judgedAgainst = null;
                    }
                    catch (SharpGenException ex) when (ex.ResultCode == Result.AccessDenied || ex.ResultCode == Vortice.DXGI.ResultCode.AccessDenied)
                    {
                        // Only the system may look at the secure desktop: a UAC prompt or the lock screen is up.
                        Judge(Now(), "the screen showed a UAC prompt or the lock screen");
                        continue;
                    }
                }

                var result = duplication.AcquireNextFrame(100, out var info, out var resource);
                var now = Now();
                if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                {
                    // The desktop image did not change: its last verdict still holds (unless a new patch awaits one).
                    if (ReferenceEquals(judgedAgainst, expected))
                    {
                        Judge(now, image ?? PointerOverRegion());
                    }

                    continue;
                }

                if (result.Failure || resource is null)
                {
                    resource?.Dispose();
                    duplication.Dispose();
                    duplication = null;
                    Judge(now, "the screen changed (a display change, a UAC prompt or the lock screen)");
                    continue;
                }

                try
                {
                    if (info.LastPresentTime != 0 || !ReferenceEquals(judgedAgainst, expected))
                    {
                        if (!TryJudgeImage(resource, context!, staging, expected, now, out image))
                        {
                            continue;
                        }

                        judgedAgainst = expected;
                    }

                    Judge(now, image ?? PointerOverRegion());
                }
                finally
                {
                    resource.Dispose();
                    duplication.ReleaseFrame();
                }
            }
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
        {
            _problem = ex.Message;
        }
        finally
        {
            duplication?.Dispose();
            staging?.Dispose();
            context?.ClearState();
            context?.Dispose();
            device?.Dispose();
            output?.Dispose();
            adapter?.Dispose();
            factory?.Dispose();
        }
    }

    /// <summary>The square of one desktop image against what it should show. False while a new patch may not be on
    /// screen yet and the image shows neither it nor the one before.</summary>
    private bool TryJudgeImage(IDXGIResource resource, ID3D11DeviceContext context, ID3D11Texture2D staging, Expectation expected, double now, out string? verdict)
    {
        using var texture = resource.QueryInterface<ID3D11Texture2D>();
        var description = texture.Description;
        if (description.Format != Format.B8G8R8A8_UNorm || description.Width < _region.X + _region.Width || description.Height < _region.Y + _region.Height)
        {
            verdict = "the screen changed mode";
            return true;
        }

        context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0, new Box(_region.X, _region.Y, 0, _region.X + _region.Width, _region.Y + _region.Height, 1));
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var covered = Covered(mapped, expected.Current);
            if (covered <= MinCoveredPixels || (expected.Previous is { } previous && Covered(mapped, previous) <= MinCoveredPixels))
            {
                verdict = null;
                return true;
            }

            if (now - expected.Since < ChangeGraceSeconds)
            {
                verdict = null;
                return false;
            }

            var fraction = covered / (double)((long)_region.Width * _region.Height);
            verdict = fraction >= 0.9
                ? "the patch was not on screen (hidden by Show desktop, a taskbar preview or another window)"
                : string.Create(CultureInfo.InvariantCulture, $"something covered {Math.Max(1, Math.Round(fraction * 100)):0} % of the patch (a window, a notification or a taskbar preview)");
            return true;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    /// <summary>Pixels of the mapped square that differ from <paramref name="expected"/> by more than the tolerance.</summary>
    private unsafe long Covered(MappedSubresource mapped, byte[] expected)
    {
        var rowBytes = _region.Width * 4;
        var source = (byte*)mapped.DataPointer;
        long covered = 0;
        for (var y = 0; y < _region.Height; y++)
        {
            var actual = new ReadOnlySpan<byte>(source + ((long)y * mapped.RowPitch), rowBytes);
            var wanted = new ReadOnlySpan<byte>(expected, y * rowBytes, rowBytes);
            if (actual.SequenceEqual(wanted))
            {
                continue;
            }

            for (var o = 0; o < rowBytes; o += 4)
            {
                if (Math.Abs(actual[o] - wanted[o]) > PixelTolerance
                    || Math.Abs(actual[o + 1] - wanted[o + 1]) > PixelTolerance
                    || Math.Abs(actual[o + 2] - wanted[o + 2]) > PixelTolerance)
                {
                    covered++;
                }
            }
        }

        return covered;
    }

    /// <summary>"The pointer" when a visible pointer reaches the square (the patch window hides it over itself, so one
    /// shows only when another window or a drag sets it).</summary>
    private unsafe string? PointerOverRegion()
    {
        var info = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (!PInvoke.GetCursorInfo(ref info) || (info.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) == 0 || info.hCursor == default)
        {
            return null;
        }

        var (x, y) = (info.ptScreenPos.X - _monitor.X, info.ptScreenPos.Y - _monitor.Y);
        var over = x + PointerSize > _region.X && x < _region.X + _region.Width && y + PointerSize > _region.Y && y < _region.Y + _region.Height;
        return over ? "the mouse pointer was over the patch" : null;
    }

    /// <summary>Records the verdict of the screen at <paramref name="at"/>: null for clean, else what disturbed it.</summary>
    private void Judge(double at, string? what)
    {
        lock (_gate)
        {
            if (what is null)
            {
                if (_open is { } open)
                {
                    _past.Add(open with { End = at });
                    if (_past.Count > 400)
                    {
                        _past.RemoveRange(0, 100);
                    }

                    _open = null;
                    _clearSince = at;
                }

                _lastClean = at;
            }
            else
            {
                // It began after the last clean image.
                _open ??= new Episode(_lastClean, double.PositiveInfinity, what);
            }

            _checkedThrough = Math.Max(_checkedThrough, at);
        }
    }

    private static (IDXGIFactory2 Factory, IDXGIAdapter1 Adapter, IDXGIOutput1 Output) FindOutput(string deviceName)
    {
        var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                using (output)
                {
                    if (output.Description.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return (factory, adapter, output.QueryInterface<IDXGIOutput1>());
                    }
                }
            }

            adapter.Dispose();
        }

        factory.Dispose();
        throw new InvalidOperationException($"No DXGI output drives {deviceName}.");
    }

    private static IDXGIOutputDuplication Duplicate(IDXGIOutput1 output, ID3D11Device device)
    {
        using var output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
        return output5 is not null ? output5.DuplicateOutput1(device, [Format.B8G8R8A8_UNorm]) : output.DuplicateOutput(device);
    }

    /// <summary>What the square should show: the current patch, the one before (images around a change may still show
    /// it), and since when the current one is expected.</summary>
    private sealed record Expectation(byte[] Current, byte[]? Previous, double Since);

    /// <summary>A stretch of time during which something disturbed the patch.</summary>
    private sealed record Episode(double Start, double End, string What);
}
