namespace SoftCal.Windows.Safety;

public enum RestoreOutcome
{
    /// <summary>Put back and verified; the journal entry is removed.</summary>
    Restored,

    /// <summary>Tried and failed; the entry stays so a later attempt (or the user) can deal with it.</summary>
    Failed,

    /// <summary>Not possible right now (device unplugged, no restorer registered); the entry stays.</summary>
    Deferred,
}

public sealed record RestoreResult(RestoreOutcome Outcome, string Message)
{
    public static RestoreResult Restored(string message) => new(RestoreOutcome.Restored, message);

    public static RestoreResult Failed(string message) => new(RestoreOutcome.Failed, message);

    public static RestoreResult Deferred(string message) => new(RestoreOutcome.Deferred, message);
}

/// <summary>Puts one kind of journaled state back: gamma ramps, camera controls, HDR/ACM switches, DDC/CI values…</summary>
public interface IStateRestorer
{
    /// <summary>The journal kind this restorer handles.</summary>
    string Kind { get; }

    RestoreResult Restore(JournalEntry entry);
}

public sealed record RecoveryResult(string Kind, string Key, RestoreOutcome Outcome, string Message)
{
    public override string ToString() => $"{Kind} {Key}: {Outcome} — {Message}";
}

/// <summary>Restores what dead runs left in the journal, newest change first (undo order).</summary>
public static class StateRecovery
{
    /// <param name="journal">The journal to restore from.</param>
    /// <param name="restorers">One restorer per kind; entries of other kinds are kept and reported as deferred.</param>
    /// <param name="includeLiveOwners">Also restore entries whose owner is still running (the owner's own shutdown).</param>
    /// <param name="filter">Optional extra selection, e.g. only one owner's entries.</param>
    public static IReadOnlyList<RecoveryResult> Recover(
        StateJournal journal,
        IEnumerable<IStateRestorer> restorers,
        bool includeLiveOwners = false,
        Func<JournalEntry, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(restorers);
        var byKind = restorers.ToDictionary(r => r.Kind, StringComparer.Ordinal);
        var results = new List<RecoveryResult>();
        var entries = journal.Pending()
            .Where(e => includeLiveOwners || !e.Owner.IsAlive())
            .Where(e => filter?.Invoke(e) ?? true)
            .OrderByDescending(e => e.CreatedUtc);
        foreach (var entry in entries)
        {
            RestoreResult result;
            if (!byKind.TryGetValue(entry.Kind, out var restorer))
            {
                result = RestoreResult.Deferred("no restorer for this kind in this process; kept in the journal");
            }
            else
            {
                try
                {
                    result = restorer.Restore(entry);
                }
#pragma warning disable CA1031 // One failing restorer must never stop the others from running.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    result = RestoreResult.Failed($"{ex.GetType().Name}: {ex.Message}");
                }
            }

            if (result.Outcome == RestoreOutcome.Restored)
            {
                journal.Remove(entry.Kind, entry.Key);
            }

            results.Add(new RecoveryResult(entry.Kind, entry.Key, result.Outcome, result.Message));
        }

        return results;
    }
}
