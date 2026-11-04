using System.Diagnostics;
using AwesomeAssertions;
using SoftCal.Windows.Gamma;
using SoftCal.Windows.Safety;
using Xunit;

namespace SoftCal.Windows.Tests;

public sealed class SafetyLayerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "softcal-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void The_current_process_is_alive_but_its_id_with_another_start_time_is_not()
    {
        var me = ProcessIdentity.Current;

        me.StartTimeUtc.Should().NotBeNull();
        me.IsAlive().Should().BeTrue();
        (me with { StartTimeUtc = me.StartTimeUtc!.Value.AddMinutes(-5) }).IsAlive().Should().BeFalse("a reused id belongs to a different process");
        new ProcessIdentity(me.Id, null).IsAlive().Should().BeTrue("entries without a start time fall back to the id alone");
    }

    [Fact]
    public void A_process_that_has_exited_is_not_alive()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        var identity = ProcessIdentity.Of(process.Id) ?? new ProcessIdentity(process.Id, DateTimeOffset.UtcNow);
        process.WaitForExit();

        identity.IsAlive().Should().BeFalse();
    }

    [Fact]
    public void Entries_record_their_owner_and_older_entries_without_a_start_time_still_load()
    {
        var journal = new StateJournal(_directory);
        journal.Save("fake", "a", new { value = 1 });

        // An entry as M1 wrote it, before owner start times were recorded.
        File.WriteAllText(
            Path.Combine(_directory, "fake-b.json"),
            """{ "kind": "fake", "key": "b", "ownerProcessId": 4242, "createdUtc": "2026-10-15T07:00:00+00:00", "original": { "value": 2 } }""");

        var entries = journal.Pending();
        entries.Single(e => e.Key == "a").Owner.Should().Be(ProcessIdentity.Current);
        entries.Single(e => e.Key == "b").OwnerStartTimeUtc.Should().BeNull();
        journal.Contains("fake", "a").Should().BeTrue();
        journal.Contains("fake", "zzz").Should().BeFalse();
    }

    [Fact]
    public void Recovery_dispatches_by_kind_newest_first_and_keeps_what_it_cannot_restore()
    {
        var deadOwner = ProcessIdentity.Current with { StartTimeUtc = DateTimeOffset.UnixEpoch };
        var journal = new StateJournal(_directory, deadOwner);
        foreach (var (kind, key) in new[] { ("ok", "1"), ("ok", "2"), ("fails", "3"), ("later", "4"), ("throws", "5"), ("unknown", "6") })
        {
            journal.Save(kind, key, key);
            Thread.Sleep(5);
        }

        var order = new List<string>();
        IStateRestorer[] restorers =
        [
            new FakeRestorer("ok", e => { order.Add(e.Key); return RestoreResult.Restored("ok"); }),
            new FakeRestorer("fails", _ => RestoreResult.Failed("no")),
            new FakeRestorer("later", _ => RestoreResult.Deferred("unplugged")),
            new FakeRestorer("throws", _ => throw new InvalidOperationException("boom")),
        ];

        var results = StateRecovery.Recover(new StateJournal(_directory), restorers);

        order.Should().Equal("2", "1");
        results.Should().HaveCount(6);
        results.Single(r => r.Kind == "throws").Should().Match<RecoveryResult>(r => r.Outcome == RestoreOutcome.Failed && r.Message.Contains("boom"));
        results.Single(r => r.Kind == "unknown").Outcome.Should().Be(RestoreOutcome.Deferred);
        new StateJournal(_directory).Pending().Select(e => e.Kind).Should().BeEquivalentTo(["fails", "later", "throws", "unknown"]);
    }

    [Fact]
    public void Entries_of_a_live_owner_are_left_alone_unless_asked()
    {
        var journal = new StateJournal(_directory);
        journal.Save("ok", "1", 1);
        var restorer = new FakeRestorer("ok", _ => RestoreResult.Restored("ok"));

        StateRecovery.Recover(journal, [restorer]).Should().BeEmpty();
        StateRecovery.Recover(journal, [restorer], includeLiveOwners: true).Should().ContainSingle(r => r.Outcome == RestoreOutcome.Restored);
        journal.Pending().Should().BeEmpty();
    }

    [Fact]
    public void Guardian_command_lines_round_trip()
    {
        var parent = new ProcessIdentity(1234, new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));

        var start = Guardian.CreateStartInfo(parent, ["--journal", @"C:\journal"]);
        var arguments = start.ArgumentList.SkipWhile(a => a != Guardian.Verb).ToList();

        arguments[0].Should().Be(Guardian.Verb);
        Guardian.ParseParent(arguments).Should().Be(parent);
        arguments.Should().ContainInOrder("--journal", @"C:\journal");
        start.UseShellExecute.Should().BeFalse();
        start.CreateNoWindow.Should().BeTrue();
        FluentActions.Invoking(() => Guardian.ParseParent([Guardian.Verb])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_gamma_restorer_defers_when_the_display_is_gone_and_restores_when_it_is_back()
    {
        var journal = new StateJournal(_directory, ProcessIdentity.Current with { StartTimeUtc = DateTimeOffset.UnixEpoch });
        journal.Save(GammaRampGuard.JournalKind, @"\\.\DISPLAY9", GammaRamp.Identity());
        var entry = journal.Pending().Single();
        var device = new SwitchableRampDevice { Connected = false, Current = GammaRamp.Scaled(0.5) };
        var restorer = new GammaRampRestorer(_ => device);

        restorer.Restore(entry).Outcome.Should().Be(RestoreOutcome.Deferred);
        device.Connected = true;
        restorer.Restore(entry).Outcome.Should().Be(RestoreOutcome.Restored);
        device.Current.IsIdentity.Should().BeTrue();
    }

    private sealed class FakeRestorer(string kind, Func<JournalEntry, RestoreResult> restore) : IStateRestorer
    {
        public string Kind => kind;

        public RestoreResult Restore(JournalEntry entry) => restore(entry);
    }

    private sealed class SwitchableRampDevice : IGammaRampDevice
    {
        public bool Connected { get; set; }

        public GammaRamp Current { get; set; } = GammaRamp.Identity();

        public string DeviceName => @"\\.\DISPLAY9";

        public GammaRamp Read() => Connected ? Current : throw new InvalidOperationException("CreateDC failed.");

        public bool Write(GammaRamp ramp)
        {
            if (!Connected)
            {
                return false;
            }

            Current = ramp;
            return true;
        }
    }
}
