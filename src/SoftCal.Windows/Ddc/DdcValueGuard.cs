using System.Globalization;
using SoftCal.Windows.Safety;

namespace SoftCal.Windows.Ddc;

/// <summary>A VCP value as journaled before the first write to it.</summary>
public sealed record DdcJournalValue(string ChannelKey, byte Code, uint Value);

public enum DdcWriteOutcome
{
    /// <summary>Written and read back.</summary>
    Applied,

    /// <summary>Rejected by the monitor, or the value did not read back.</summary>
    NotApplied,

    /// <summary>The monitor does not support the code (nothing was written).</summary>
    Unsupported,
}

/// <summary>
/// Changes OSD settings over DDC/CI safely: each code's original value is read and journaled before its first write,
/// every write is verified by reading it back, and <see cref="RestoreAsync"/> puts the originals back. After a crash the
/// guardian does it through <see cref="DdcValueRestorer"/>.
/// </summary>
public sealed class DdcValueGuard(IVcpChannel channel, StateJournal journal)
{
    public const string JournalKind = "ddc";

    private readonly IVcpChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    private readonly StateJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly List<(byte Code, uint Value)> _originals = [];

    /// <summary>Originals of every code written so far, in first-write order.</summary>
    public IReadOnlyList<(byte Code, uint Value)> Originals => _originals;

    public static string EntryKey(string channelKey, byte code) =>
        string.Create(CultureInfo.InvariantCulture, $"{channelKey}|{code:X2}");

    public async Task<DdcWriteOutcome> SetAsync(byte code, uint value, CancellationToken cancellationToken)
    {
        if (!_originals.Any(o => o.Code == code))
        {
            var key = EntryKey(_channel.Key, code);
            if (_journal.Contains(JournalKind, key))
            {
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"VCP 0x{code:X2} of {_channel.Key} from an earlier run is still journaled; restore it first."));
            }

            if (await _channel.ReadAsync(code, cancellationToken).ConfigureAwait(false) is not { } original)
            {
                return DdcWriteOutcome.Unsupported;
            }

            _journal.Save(JournalKind, key, new DdcJournalValue(_channel.Key, code, original.Current));
            _originals.Add((code, original.Current));
        }

        return await _channel.WriteAsync(code, value, cancellationToken).ConfigureAwait(false)
            && await ReadsBackAsync(_channel, code, value, cancellationToken).ConfigureAwait(false)
                ? DdcWriteOutcome.Applied
                : DdcWriteOutcome.NotApplied;
    }

    /// <summary>Writes every original back, newest first; entries are removed only for values that read back.</summary>
    public async Task<IReadOnlyList<RecoveryResult>> RestoreAsync(CancellationToken cancellationToken)
    {
        var results = new List<RecoveryResult>();
        for (var i = _originals.Count - 1; i >= 0; i--)
        {
            var (code, value) = _originals[i];
            var key = EntryKey(_channel.Key, code);
            var ok = await _channel.WriteAsync(code, value, cancellationToken).ConfigureAwait(false)
                && await ReadsBackAsync(_channel, code, value, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                _journal.Remove(JournalKind, key);
                _originals.RemoveAt(i);
            }

            results.Add(new RecoveryResult(
                JournalKind,
                key,
                ok ? RestoreOutcome.Restored : RestoreOutcome.Failed,
                string.Create(CultureInfo.InvariantCulture, $"VCP 0x{code:X2} {(ok ? "back to" : "could not be set back to")} {value}")));
        }

        return results;
    }

    /// <summary>The user accepted the new values: forget the originals (nothing is put back, now or by the guardian).</summary>
    public void Keep()
    {
        foreach (var (code, _) in _originals)
        {
            _journal.Remove(JournalKind, EntryKey(_channel.Key, code));
        }

        _originals.Clear();
    }

    /// <summary>Monitors apply some writes with a delay: read back a few times before giving up.</summary>
    internal static async Task<bool> ReadsBackAsync(IVcpChannel channel, byte code, uint expected, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(attempt == 0 ? 100 : 250, cancellationToken).ConfigureAwait(false);
            if ((await channel.ReadAsync(code, cancellationToken).ConfigureAwait(false))?.Current == expected)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Restores journaled VCP values (journal kind <see cref="DdcValueGuard.JournalKind"/>).</summary>
public sealed class DdcValueRestorer(Func<string, IVcpChannel?>? open = null) : IStateRestorer
{
    private readonly Func<string, IVcpChannel?> _open = open ?? DdcCiChannel.OpenByKey;

    public string Kind => DdcValueGuard.JournalKind;

    public RestoreResult Restore(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var value = StateJournal.Read<DdcJournalValue>(entry) ?? throw new InvalidDataException("Empty DDC/CI journal entry.");
        using var channel = _open(value.ChannelKey);
        if (channel is null)
        {
            return RestoreResult.Deferred("monitor not connected or DDC/CI unavailable; the value will be restored when it is");
        }

        var ok = channel.WriteAsync(value.Code, value.Value, CancellationToken.None).GetAwaiter().GetResult()
            && DdcValueGuard.ReadsBackAsync(channel, value.Code, value.Value, CancellationToken.None).GetAwaiter().GetResult();
        return ok
            ? RestoreResult.Restored(string.Create(CultureInfo.InvariantCulture, $"VCP 0x{value.Code:X2} back to {value.Value}"))
            : RestoreResult.Failed(string.Create(CultureInfo.InvariantCulture, $"VCP 0x{value.Code:X2} could not be set back to {value.Value}"));
    }
}
