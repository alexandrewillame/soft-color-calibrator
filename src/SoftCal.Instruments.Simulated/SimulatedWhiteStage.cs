using SoftCal.Core.Advisor;
using SoftCal.Core.Measurement;

namespace SoftCal.Instruments.Simulated;

/// <summary>A white match on the simulator: the screen the camera goes to shows the aiming area and the other goes dark
/// (on real screens its window steps aside for SoftCal's), and the camera moves when the user says it did.</summary>
public sealed class SimulatedWhiteStage(MeasurementRig rig, SimulatedRig sim) : IWhiteMatchStage
{
    private readonly MeasurementRig _rig = rig ?? throw new ArgumentNullException(nameof(rig));
    private readonly SimulatedRig _sim = sim ?? throw new ArgumentNullException(nameof(sim));

    public async Task AimAtAsync(bool reference, CancellationToken cancellationToken)
    {
        IPatchPresenter monitor = _sim;
        var (aim, other) = reference ? (_sim.ReferencePresenter, monitor) : (monitor, _sim.ReferencePresenter);
        await other.ShowAsync(WhiteMatcher.FullScreen(DeviceRgb.Black), cancellationToken).ConfigureAwait(false);
        await aim.ShowAsync(new PlacementTargetPatch(), cancellationToken).ConfigureAwait(false);
    }

    public Task UseAsync(bool reference, CancellationToken cancellationToken)
    {
        _rig.Presenter = reference ? _sim.ReferencePresenter : _sim;
        _sim.CameraOnReference = reference;
        return Task.CompletedTask;
    }
}
