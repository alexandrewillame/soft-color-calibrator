namespace SoftCal.Core.Measurement;

/// <summary>
/// A presenter that also watches its patch on the screen, so that a reading taken while something covered it is not
/// kept: another window, a notification or a taskbar preview over it, the pointer, or the patch missing from the screen
/// altogether. Times are seconds on the clock the readings' frames use.
/// </summary>
public interface IPatchWatch
{
    /// <summary>What disturbed the patch between the two times, or null when nothing did (or the screen cannot be
    /// watched). Waits, briefly, until the screen has been looked at up to <paramref name="untilSeconds"/>.</summary>
    Task<string?> DisturbanceAsync(double fromSeconds, double untilSeconds, CancellationToken cancellationToken);

    /// <summary>Waits until the patch has been seen clear for <paramref name="clearSeconds"/> and returns when that clear
    /// stretch began; null when it was not clear within <paramref name="timeoutSeconds"/>.</summary>
    Task<double?> WaitUntilClearAsync(double clearSeconds, double timeoutSeconds, CancellationToken cancellationToken);
}
