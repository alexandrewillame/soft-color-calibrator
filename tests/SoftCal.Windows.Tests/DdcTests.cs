using AwesomeAssertions;
using SoftCal.Windows.Ddc;
using SoftCal.Windows.Safety;
using Xunit;

namespace SoftCal.Windows.Tests;

public sealed class DdcTests : IDisposable
{
    private const byte Brightness = 0x10;
    private const byte Contrast = 0x12;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "softcal-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Originals_are_journaled_once_writes_are_verified_and_restore_goes_newest_first()
    {
        var channel = new FakeVcpChannel { [Brightness] = 18, [Contrast] = 50 };
        var journal = new StateJournal(_directory);
        var guard = new DdcValueGuard(channel, journal);

        (await guard.SetAsync(Brightness, 30, Token)).Should().Be(DdcWriteOutcome.Applied);
        (await guard.SetAsync(Brightness, 25, Token)).Should().Be(DdcWriteOutcome.Applied);
        (await guard.SetAsync(Contrast, 55, Token)).Should().Be(DdcWriteOutcome.Applied);

        guard.Originals.Should().Equal((Brightness, 18u), (Contrast, 50u));
        journal.Pending(DdcValueGuard.JournalKind).Should().HaveCount(2);

        var results = await guard.RestoreAsync(Token);

        results.Select(r => r.Key).Should().Equal(DdcValueGuard.EntryKey(channel.Key, Contrast), DdcValueGuard.EntryKey(channel.Key, Brightness));
        results.Should().OnlyContain(r => r.Outcome == RestoreOutcome.Restored);
        channel[Brightness].Should().Be(18u);
        channel[Contrast].Should().Be(50u);
        journal.Pending().Should().BeEmpty();
    }

    [Fact]
    public async Task Unsupported_codes_are_never_written_and_ignored_writes_are_reported()
    {
        var channel = new FakeVcpChannel { [Brightness] = 18 };
        var guard = new DdcValueGuard(channel, new StateJournal(_directory));

        (await guard.SetAsync(0x72, 120, Token)).Should().Be(DdcWriteOutcome.Unsupported);
        channel.Writes.Should().BeEmpty();

        channel.IgnoreWritesTo.Add(Brightness); // accepts the command, but the value never changes
        (await guard.SetAsync(Brightness, 30, Token)).Should().Be(DdcWriteOutcome.NotApplied);
    }

    [Fact]
    public async Task After_a_crash_the_restorer_writes_the_original_back()
    {
        var channel = new FakeVcpChannel { [Brightness] = 18 };
        var journal = new StateJournal(_directory);
        await new DdcValueGuard(channel, journal).SetAsync(Brightness, 40, Token);

        // The process dies here; the guardian finds the entry.
        var entry = journal.Pending().Single();
        var result = new DdcValueRestorer(key => key == channel.Key ? channel : null).Restore(entry);

        result.Outcome.Should().Be(RestoreOutcome.Restored);
        channel[Brightness].Should().Be(18u);
        new DdcValueRestorer(_ => null).Restore(entry).Outcome.Should().Be(RestoreOutcome.Deferred);
    }

    [Fact]
    public async Task A_pending_entry_blocks_new_writes_to_that_code()
    {
        var channel = new FakeVcpChannel { [Brightness] = 18 };
        var journal = new StateJournal(_directory);
        await new DdcValueGuard(channel, journal).SetAsync(Brightness, 40, Token);

        var act = () => new DdcValueGuard(channel, journal).SetAsync(Brightness, 41, Token);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*still journaled*");
    }

    [Fact]
    public void The_write_budget_runs_out()
    {
        var budget = new WriteBudget(2);
        budget.Take();
        budget.Take();

        FluentActions.Invoking(budget.Take).Should().Throw<DdcWriteLimitException>().WithMessage("*2 writes*");
        budget.Used.Should().Be(2);
    }

    [Fact]
    public void Channel_keys_name_the_monitor_and_the_physical_index()
    {
        const string Path = @"\\?\DISPLAY#BNQ7F2B#5&3791d1ef&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        DdcCiChannel.KeyFor(Path, 1).Should().Be(Path + "|1");
        DdcValueGuard.EntryKey(DdcCiChannel.KeyFor(Path, 0), Brightness).Should().EndWith("|0|10");
    }

    private sealed class FakeVcpChannel : IVcpChannel
    {
        private readonly Dictionary<byte, uint> _values = [];

        public uint this[byte code]
        {
            get => _values[code];
            set => _values[code] = value;
        }

        public HashSet<byte> IgnoreWritesTo { get; } = [];

        public List<(byte Code, uint Value)> Writes { get; } = [];

        public string Key => @"\\?\DISPLAY#FAKE0001#1&2&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}|0";

        public Task<VcpValue?> ReadAsync(byte code, CancellationToken cancellationToken) =>
            Task.FromResult(_values.TryGetValue(code, out var value) ? new VcpValue(value, 100) : (VcpValue?)null);

        public Task<bool> WriteAsync(byte code, uint value, CancellationToken cancellationToken)
        {
            if (!_values.ContainsKey(code))
            {
                return Task.FromResult(false);
            }

            Writes.Add((code, value));
            if (!IgnoreWritesTo.Contains(code))
            {
                _values[code] = value;
            }

            return Task.FromResult(true);
        }

        public void Dispose()
        {
        }
    }
}
