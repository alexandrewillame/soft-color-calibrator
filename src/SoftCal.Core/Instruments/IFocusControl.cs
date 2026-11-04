namespace SoftCal.Core.Instruments;

/// <summary>
/// An instrument whose focus SoftCal may move while it is locked, to find the setting that blurs the screen best. A
/// phone's lens cannot be trusted to go where it is asked (a Galaxy S23 Ultra reported the farthest focus and still
/// saw the screen sharply), so the picture says which setting blurs, not the phone.
/// </summary>
public interface IFocusControl
{
    /// <summary>The settings to try, from the farthest focus to the nearest, in the instrument's own units.</summary>
    IReadOnlyList<double> FocusCandidates { get; }

    /// <summary>A setting as the log shows it.</summary>
    string DescribeFocus(double value);

    /// <summary>Moves the focus, the exposure unchanged; returns the instrument time from which frames show it.</summary>
    Task<double> SetFocusAsync(double value, CancellationToken cancellationToken);
}
