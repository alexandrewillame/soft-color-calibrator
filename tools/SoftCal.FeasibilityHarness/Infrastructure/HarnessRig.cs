using System.Diagnostics;
using SoftCal.Core.Color;
using SoftCal.Core.Displays;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.Instruments.Simulated;
using SoftCal.Instruments.Webcam;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Patches;

namespace SoftCal.FeasibilityHarness.Infrastructure;

/// <summary>
/// The instrument and presenter for a run: the real C920e and a D3D patch window on the chosen monitor, or the
/// simulated rig (<c>--simulated</c>). Camera controls are journaled before the lock and restored on dispose.
/// </summary>
internal sealed class HarnessRig : IAsyncDisposable
{
    private readonly List<InstrumentFault> _faults = [];
    private readonly Lock _gate = new();
    private bool _locked;

    private HarnessRig(IMeasurementInstrument instrument, IPatchPresenter presenter, Func<double> clock)
    {
        Instrument = instrument;
        Presenter = presenter;
        Clock = clock;
        Instrument.Faulted += (_, fault) =>
        {
            lock (_gate)
            {
                _faults.Add(fault);
            }

            Ui.Warn($"instrument: {fault.Kind}: {fault.Message}");
        };
    }

    public IMeasurementInstrument Instrument { get; }

    public IPatchPresenter Presenter { get; }

    public Func<double> Clock { get; }

    public WebcamInstrument? Webcam => Instrument as WebcamInstrument;

    public SimulatedRig? Simulated => Instrument as SimulatedRig;

    public D3DPatchWindow? Window => Presenter as D3DPatchWindow;

    public DisplayMonitor? Monitor { get; private init; }

    public string MonitorLabel { get; private init; } = "sim";

    public string? DisplayKey { get; private init; }

    public string? DisplayName { get; private init; }

    public IReadOnlyList<InstrumentFault> Faults
    {
        get
        {
            lock (_gate)
            {
                return [.. _faults];
            }
        }
    }

    public static double QpcSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Opens the rig. Nothing on the system changes until <see cref="LockAsync"/>.</summary>
    public static HarnessRig Open(Arguments args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Has("simulated"))
        {
            var seed = args.GetInt("seed", 7);
            var display = new SimulatedDisplay(new SimulatedDisplayOptions { Gamma = new Vec3(2.25, 2.2, 2.15) });
            var camera = new SimulatedCamera(new SimulatedCameraOptions { Seed = seed, ExposureRatioError = 0.02, Sensitivity = 40 });
            var rig = new SimulatedRig(display, camera, new SimulatedRigOptions { PwmFrequencyHz = 240, PwmDepth = 0.2 });
            return new HarnessRig(rig, rig, () => rig.Now) { MonitorLabel = "sim", DisplayName = "SimulatedDisplay" };
        }

        var monitors = DisplayEnumerator.List();
        var monitor = DisplayEnumerator.Find(monitors, args.Get("monitor") ?? throw new ArgumentException("Choose a monitor with --monitor N (see 'list')."));
        var devices = WebcamDevice.Enumerate().Where(d => d.IsUsbHardware).ToList();
        var filter = args.Get("camera");
        var device = (filter is null ? devices.FirstOrDefault() : devices.FirstOrDefault(d => d.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("No USB camera found (virtual cameras are not accepted).");
        string? key = null;
        if (monitor.Edid is { } edid)
        {
            try
            {
                key = SoftCal.Core.Displays.DisplayKey.FromEdid(Edid.Parse(edid)).Value;
            }
            catch (EdidFormatException)
            {
            }
        }

        var webcam = WebcamInstrument.Open(device);
        try
        {
            var window = D3DPatchWindow.Open(monitor.DeviceName);
            return new HarnessRig(webcam, window, QpcSeconds)
            {
                Monitor = monitor,
                MonitorLabel = "m" + monitor.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DisplayKey = key,
                DisplayName = monitor.FriendlyName,
            };
        }
        catch
        {
            webcam.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>Locks the camera. The snapshot of every control is journaled first, and the guardian is started.</summary>
    public async Task<LockReport> LockAsync(LockRequest request, CancellationToken cancellationToken)
    {
        if (Webcam is { } webcam)
        {
            Safety.Journal.Save(Safety.CameraKind, webcam.Device.StableId, webcam.Snapshot);
            Safety.EnsureGuardian();
        }

        _locked = true;
        return await Instrument.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Webcam is { } webcam)
        {
            try
            {
                if (_locked)
                {
                    await webcam.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
                    var unrestored = Faults.Any(f => f.Message.StartsWith("Some camera controls could not be restored", StringComparison.Ordinal));
                    if (!unrestored)
                    {
                        Safety.Journal.Remove(Safety.CameraKind, webcam.Device.StableId);
                    }
                }
            }
            finally
            {
                await webcam.DisposeAsync().ConfigureAwait(false);
            }
        }

        Window?.Dispose();
    }
}
