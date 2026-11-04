using SoftCal.Windows.Safety;

namespace SoftCal.Windows.Gamma;

/// <summary>Restores journaled gamma ramps (journal kind <see cref="GammaRampGuard.JournalKind"/>).</summary>
public sealed class GammaRampRestorer(Func<string, IGammaRampDevice>? open = null) : IStateRestorer
{
    private readonly Func<string, IGammaRampDevice> _open = open ?? (name => new GdiGammaRampDevice(name));

    public string Kind => GammaRampGuard.JournalKind;

    public RestoreResult Restore(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var original = StateJournal.Read<GammaRamp>(entry) ?? throw new InvalidDataException("Empty gamma journal entry.");
        var device = _open(entry.Key);
        try
        {
            device.Read();
        }
        catch (InvalidOperationException)
        {
            return RestoreResult.Deferred("display not connected; its ramp will be restored when it is");
        }

        return device.Write(original) && device.Read().SameAs(original)
            ? RestoreResult.Restored("gamma ramp restored and read back")
            : RestoreResult.Failed("the driver did not accept the original ramp");
    }
}
