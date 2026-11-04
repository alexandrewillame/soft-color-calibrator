using System.Globalization;
using System.Runtime.InteropServices;
using SoftCal.Core.Advisor;
using SoftCal.Core.Analysis;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.Instruments.Simulated;
using SoftCal.Instruments.Webcam;
using SoftCal.Instruments.Webcam.Capture;
using SoftCal.Windows.Ddc;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Patches;

namespace SoftCal.App.Services;

/// <summary>What the user answered to a red/green/blue step: Done (with the values typed, when they were asked for), or
/// stop.</summary>
internal sealed record GainsAnswer(bool Done, RgbGains? Values);

/// <summary>The window's side of a measurement on two screens: it keeps SoftCal's window off the screen the camera is
/// about to rest on, and shows the camera's view while the user places it.</summary>
internal interface IScreenHost
{
    /// <summary>Moves SoftCal's window to <paramref name="to"/> when it is on <paramref name="away"/>.</summary>
    Task KeepWindowOffAsync(DisplayMonitor away, DisplayMonitor to);

    /// <summary>The camera's latest picture of the aiming area, and what it says (called from any thread).</summary>
    void ShowCameraView(RoiImage image, PlacementView view);

    /// <summary>The run's own window covers the measured screen now: what covered it before (the camera step's aiming
    /// area) can close (called from any thread).</summary>
    void RunCoversScreen();
}

/// <summary>
/// The white match on real screens. Before each camera move, the screen the camera goes to shows the aiming area and
/// SoftCal's window moves to the other screen: to the measured monitor (whose patch window steps aside) while the camera
/// is on the reference, and back to the reference screen (whose window closes) for the return. Meanwhile the camera's
/// view goes to SoftCal's window, so the user sees when the camera is on the area and straight.
/// </summary>
internal sealed class ScreenWhiteStage(
    MeasurementRig rig,
    D3DPatchWindow target,
    DisplayMonitor targetMonitor,
    DisplayMonitor reference,
    ILumaImageSource camera,
    IScreenHost? host,
    IProgress<string>? log = null) : IWhiteMatchStage, IDisposable
{
    /// <summary>The view's first exposure: it shows the aiming area's lines without clipping at 8–10 cm with the C920e
    /// (both monitors' whites read well at it on 2026-10-18). From there the view adjusts it to what it sees.</summary>
    private const double ViewExposureSeconds = 1.0 / 64;

    private D3DPatchWindow? _referenceWindow;

    /// <summary>The view's exposure as last adjusted, the next camera move's first (null: not adjusted yet).</summary>
    private double? _viewExposure;
    private CancellationTokenSource? _watch;
    private Task _watching = Task.CompletedTask;
    private PlacementView? _lastView;

    public async Task AimAtAsync(bool onReference, CancellationToken cancellationToken)
    {
        await StopWatchingAsync().ConfigureAwait(false);
        _lastView = null;
        if (onReference)
        {
            target.SetVisible(false);
            if (host is not null)
            {
                await host.KeepWindowOffAsync(reference, targetMonitor).ConfigureAwait(false);
            }

            _referenceWindow ??= D3DPatchWindow.Open(reference.DeviceName, new PatchWindowOptions { WatchForCover = true });
            await _referenceWindow.ShowAsync(new PlacementTargetPatch(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _referenceWindow?.Dispose();
            _referenceWindow = null;
            if (host is not null)
            {
                await host.KeepWindowOffAsync(targetMonitor, reference).ConfigureAwait(false);
            }

            target.SetVisible(true);
            await target.ShowAsync(new PlacementTargetPatch(), cancellationToken).ConfigureAwait(false);
        }

        StartWatching();
    }

    public async Task UseAsync(bool onReference, CancellationToken cancellationToken)
    {
        await StopWatchingAsync().ConfigureAwait(false);

        // What the camera's view said when the user confirmed, for the run's log.
        if (_lastView is { } view)
        {
            var screen = onReference ? reference : targetMonitor;
            log?.Report($"Camera on {screen.FriendlyName ?? screen.DeviceName} at Done: {Verdict(view)}.");
        }

        rig.Presenter = onReference ? _referenceWindow ?? throw new InvalidOperationException("Aim at the reference screen first.") : target;
    }

    public void Dispose()
    {
        _watch?.Cancel();
        _referenceWindow?.Dispose();
        _referenceWindow = null;
        try
        {
            target.SetVisible(true);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The patch window is already gone.
        }
    }

    /// <summary>The camera's placement for the log, in the picture's terms: the squares are larger on the side of the
    /// camera nearer the screen.</summary>
    private static string Verdict(PlacementView view) => view switch
    {
        { Found: false } => "the aiming area was not in view",
        { Square: null } => "on the area, straightness not judged",
        _ => $"on the area, {(view.Square == true ? "straight" : "tilted")} (squares {Larger(view.Sideways, "sideways", "on the picture's right", "on the picture's left")}, {Larger(view.UpDown, "up and down", "at its bottom", "at its top")})",
    };

    /// <summary>The level only a hundredth of the picture's pixels exceed.</summary>
    private static double Brightest(IReadOnlyList<double> pixels)
    {
        var sorted = pixels.ToArray();
        Array.Sort(sorted);
        return sorted.Length == 0 ? 0 : sorted[(int)(sorted.Length * 0.99)];
    }

    private static int Nearest(IReadOnlyList<double> steps, double seconds)
    {
        var best = 0;
        for (var i = 1; i < steps.Count; i++)
        {
            if (Math.Abs(Math.Log2(steps[i] / seconds)) < Math.Abs(Math.Log2(steps[best] / seconds)))
            {
                best = i;
            }
        }

        return best;
    }

    private static string Larger(double? logRatio, string axis, string positive, string negative) => logRatio is { } l
        ? string.Create(CultureInfo.InvariantCulture, $"{(Math.Exp(Math.Abs(l)) - 1) * 100:0.0} % larger {(l >= 0 ? positive : negative)}")
        : "not compared " + axis;

    private void StartWatching()
    {
        if (host is null)
        {
            return;
        }

        var watch = new CancellationTokenSource();
        _watch = watch;
        _watching = Task.Run(() => WatchAsync(host, watch.Token), CancellationToken.None);
    }

    private async Task StopWatchingAsync()
    {
        if (_watch is not { } watch)
        {
            return;
        }

        _watch = null;
        await watch.CancelAsync().ConfigureAwait(false);
        await _watching.ConfigureAwait(false);
        watch.Dispose();
    }

    /// <summary>
    /// The camera's picture of the aiming area a few times a second, until the user confirms. Its exposure is the view's
    /// own, a ×2 step longer or shorter at a time until the area's lines show (a phone's sensor can need eight times a
    /// webcam's); the white's readings that follow choose theirs. It is set through the rig, which keeps track of it.
    /// </summary>
    private async Task WatchAsync(IScreenHost view, CancellationToken cancellationToken)
    {
        try
        {
            var steps = rig.ExposureSteps;
            var index = steps.Count == 0 ? -1 : Nearest(steps, _viewExposure ?? ViewExposureSeconds);
            await rig.SetExposureAsync(index < 0 ? ViewExposureSeconds : steps[index], cancellationToken).ConfigureAwait(false);
            var whole = new RoiSpec(0.5, 0.5, 1, 1);
            var skip = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var image = await camera.CaptureLumaImageAsync(whole, 1, skip, cancellationToken).ConfigureAwait(false);
                var placement = PlacementCheck.Analyze(image.Width, image.Height, image.Pixels);
                _lastView = placement;
                view.ShowCameraView(image, placement);

                // The lines' level (the picture's brightest hundredth): too dim to see, or clipping.
                var bright = Brightest(image.Pixels);
                var next = bright < 90 ? index + 1 : bright > 235 ? index - 1 : index;
                skip = 0;
                if (index >= 0 && next != index && next >= 0 && next < steps.Count)
                {
                    index = next;
                    _viewExposure = steps[index];
                    await rig.SetExposureAsync(steps[index], cancellationToken).ConfigureAwait(false);

                    // Frames already on their way were taken at the old exposure.
                    skip = 3;
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or COMException or MeasurementException)
        {
            // Stopped, or the camera is closing: the view simply ends.
        }
    }
}

/// <summary>
/// The person at the monitors during the white match, through the window's prompts. With DDC/CI, SoftCal reads the user
/// mode's gains itself and, when it may set values, writes the new gains (journaled: kept when the match finishes, put
/// back when it stops). The simulator's demo monitor answers over DDC/CI too: its gains are read, and set when the user
/// lets SoftCal, the same way.
/// </summary>
internal sealed class WhiteMatchUser(IAdvicePrompt prompt, IProgress<string> progress) : IWhiteMatchUser
{
    /// <summary>VCP codes of the red, green and blue video gains.</summary>
    private static readonly byte[] GainCodes = [0x16, 0x18, 0x1A];

    /// <summary>VCP code of the color preset ("Select color preset").</summary>
    private const byte PresetCode = 0x14;

    /// <summary>Monitors fade some changes in; readings start after this.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    public IVcpChannel? Channel { get; init; }

    public DdcValueGuard? Guard { get; init; }

    /// <summary>The user let SoftCal set values over DDC/CI.</summary>
    public bool SetOverDdc { get; init; }

    public SimulatedDisplay? Simulated { get; init; }

    public bool SetsGainsDirectly => SetOverDdc && (Guard is not null || Simulated is not null);

    /// <summary>The color preset the monitor reports over DDC/CI after the user mode was asked for (some monitors keep
    /// reporting their factory preset); null when it cannot be read.</summary>
    public string? ReportedPreset { get; private set; }

    public Task<bool> MoveCameraAsync(Advice advice, CancellationToken cancellationToken) => prompt.ConfirmAsync(advice, cancellationToken);

    public async Task<RgbGains?> SwitchToUserModeAsync(Advice advice, CancellationToken cancellationToken)
    {
        if (Simulated is { } sim)
        {
            // The demo monitor answers over DDC/CI: its user mode's values are read, as a real one's.
            var demo = await prompt.AskGainsAsync(Then(advice, " Then click Done: SoftCal reads Red, Green and Blue itself."), null, showValues: false, cancellationToken).ConfigureAwait(false);
            if (!demo.Done)
            {
                return null;
            }

            sim.Osd.UserColorMode = true;
            var shown = new RgbGains(sim.Osd.RedGain, sim.Osd.GreenGain, sim.Osd.BlueGain);
            progress.Report($"Read Red, Green and Blue over DDC/CI: {shown}.");
            return shown;
        }

        if (Channel is { } channel)
        {
            var done = await prompt.AskGainsAsync(Then(advice, " Then click Done: SoftCal reads Red, Green and Blue itself."), null, showValues: false, cancellationToken).ConfigureAwait(false);
            if (!done.Done)
            {
                return null;
            }

            if (await ReadGainsAsync(channel, cancellationToken).ConfigureAwait(false) is { } read)
            {
                ReportedPreset = await channel.ReadAsync(PresetCode, cancellationToken).ConfigureAwait(false) is { } preset
                    ? DdcCiProbe.Interpret(PresetCode, preset.Current, preset.Maximum)
                    : null;
                progress.Report($"Read Red, Green and Blue over DDC/CI: {read}{(ReportedPreset is { } name ? $"; the monitor reports its color preset as {name}" : string.Empty)}.");
                return read;
            }

            advice = advice with { Message = "SoftCal could not read them over DDC/CI." };
        }

        var answer = await prompt.AskGainsAsync(Then(advice, " Then type the three values it shows."), null, showValues: true, cancellationToken).ConfigureAwait(false);
        return answer.Done ? answer.Values : null;
    }

    public async Task<RgbGains?> SetGainsAsync(RgbGains gains, Advice advice, CancellationToken cancellationToken)
    {
        if (SetOverDdc && Simulated is { } demo)
        {
            // The demo monitor takes every write.
            (demo.Osd.RedGain, demo.Osd.GreenGain, demo.Osd.BlueGain) = (gains.Red, gains.Green, gains.Blue);
            progress.Report($"Set Red, Green and Blue to {gains} over DDC/CI (put back if the run stops).");
            await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
            return gains;
        }

        if (SetOverDdc && Guard is { } guard)
        {
            if (await WriteAsync(guard, gains, cancellationToken).ConfigureAwait(false))
            {
                return gains;
            }

            advice = Then(advice, " SoftCal could not set them over DDC/CI: set them in the monitor's menu (on most monitors in Color temperature › User).");
        }

        var answer = await prompt.AskGainsAsync(Then(advice, " Close the menu, then click Done."), gains, showValues: true, cancellationToken).ConfigureAwait(false);
        if (!answer.Done || answer.Values is not { } set)
        {
            return null;
        }

        if (Simulated is { } sim)
        {
            (sim.Osd.RedGain, sim.Osd.GreenGain, sim.Osd.BlueGain) = (set.Red, set.Green, set.Blue);
        }

        return set;
    }

    private static Advice Then(Advice advice, string then) => advice with { Message = advice.Message + then };

    private async Task<bool> WriteAsync(DdcValueGuard guard, RgbGains gains, CancellationToken cancellationToken)
    {
        for (var c = 0; c < 3; c++)
        {
            var outcome = await guard.SetAsync(GainCodes[c], (uint)gains[c], cancellationToken).ConfigureAwait(false);
            if (outcome != DdcWriteOutcome.Applied)
            {
                progress.Report($"DDC/CI could not set Red, Green and Blue ({outcome}); please set them in the monitor's menu.");
                return false;
            }
        }

        progress.Report($"Set Red, Green and Blue to {gains} over DDC/CI (put back if the run stops).");
        await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<RgbGains?> ReadGainsAsync(IVcpChannel channel, CancellationToken cancellationToken)
    {
        var values = new int[3];
        for (var c = 0; c < 3; c++)
        {
            if (await channel.ReadAsync(GainCodes[c], cancellationToken).ConfigureAwait(false) is not { } value)
            {
                return null;
            }

            values[c] = (int)value.Current;
        }

        return new RgbGains(values[0], values[1], values[2]);
    }
}
