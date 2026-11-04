using SoftCal.Instruments.Webcam.Controls;
using SoftCal.Windows.AdvancedColor;
using SoftCal.Windows.Ddc;
using SoftCal.Windows.Gamma;
using SoftCal.Windows.Safety;

namespace SoftCal.FeasibilityHarness.Infrastructure;

/// <summary>
/// Crash safety for everything the harness changes: camera controls (they persist in the camera), gamma ramps, HDR/ACM
/// switches and DDC/CI values. Changes are journaled before they are applied; the shared <see cref="Guardian"/> restores
/// them if the harness dies, and every launch restores whatever an earlier, dead run left behind.
/// </summary>
internal static class Safety
{
    public const string CameraKind = CameraControlsRestorer.JournalKind;

    public static StateJournal Journal { get; } = new(StateJournal.DefaultDirectory);

    public static IReadOnlyList<IStateRestorer> Restorers { get; } =
    [
        new GammaRampRestorer(),
        new CameraControlsRestorer(),
        new AdvancedColorRestorer(),
        new DdcValueRestorer(),
    ];

    public static string GuardianLog => Path.Combine(Journal.DirectoryPath, "guardian.log");

    /// <summary>Restores journal entries whose owner process is gone. Returns a line per entry.</summary>
    public static IReadOnlyList<string> RecoverDeadRuns(bool includeLiveOwners = false) =>
        [.. StateRecovery.Recover(Journal, Restorers, includeLiveOwners).Select(r => r.ToString())];

    /// <summary>Starts the guardian (this executable, "guardian --parent pid --parent-start ticks") once per run.</summary>
    public static void EnsureGuardian() => Guardian.EnsureStarted();

    /// <summary>The guardian's entry point: wait for the harness to exit, then restore whatever it left journaled.</summary>
    public static int RunGuardian(IReadOnlyList<string> arguments)
    {
        Guardian.Run(Guardian.ParseParent(arguments), Journal, Restorers, GuardianLog);
        return 0;
    }
}
