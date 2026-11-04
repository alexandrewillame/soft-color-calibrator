using SoftCal.Instruments.Simulated;
using SoftCal.Remote;

namespace SoftCal.Instruments.Phone.Tests;

/// <summary>A phone on this PC's loopback: the link's server around a simulated camera, listening on a free port.</summary>
internal sealed class TestPhone : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;

    private TestPhone(SimulatedRigOptions? options, double offset, bool manualSensor, SimulatedCameraOptions? camera)
    {
        Rig = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera(camera), options ?? new SimulatedRigOptions { PaceInRealTime = true, FrameRate = 60, LatencySeconds = 0.05 });
        Offset = offset;
        Camera = new SimulatedPhoneCamera(Rig, offset, manualSensor);
        Server = new PhoneServer(Camera, new PhoneDescription("Simulated", "Phone", "SoftCal Camera (test)"), port: 0);
        _serving = Task.Run(() => Server.RunAsync(_stop.Token));
    }

    public SimulatedRig Rig { get; }

    public SimulatedPhoneCamera Camera { get; }

    public PhoneServer Server { get; }

    /// <summary>The phone's clock minus the PC's.</summary>
    public double Offset { get; }

    public Uri Address => PhoneAddress.ToUri("127.0.0.1", Server.Port);

    /// <summary>The PC's clock: the rig's, as the patch window's display times are on the PC's.</summary>
    public Func<double> PcClock => () => Rig.Now;

    /// <summary>A phone listening; with a linear <paramref name="camera"/> it measures from raw data, as a phone whose
    /// camera offers it.</summary>
    public static TestPhone Start(SimulatedRigOptions? options = null, double offset = 1234.5, bool manualSensor = true, SimulatedCameraOptions? camera = null) =>
        new(options, offset, manualSensor, camera);

    public Task<PhoneClient> ConnectAsync(CancellationToken cancellationToken, string? code = null) =>
        PhoneClient.ConnectAsync(Address, code ?? Server.Code, "Test PC", PcClock, cancellationToken);

    /// <summary>The phone app closes: every link drops.</summary>
    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        await _serving;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Server.Dispose();
        _stop.Dispose();
    }

    /// <summary>A code that is not the phone's.</summary>
    public static string Wrong(string code) => code == "000000" ? "111111" : "000000";
}
