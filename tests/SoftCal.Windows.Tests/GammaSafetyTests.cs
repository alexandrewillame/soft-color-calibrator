using AwesomeAssertions;
using SoftCal.Windows.Gamma;
using SoftCal.Windows.Safety;
using Xunit;

namespace SoftCal.Windows.Tests;

public sealed class GammaSafetyTests : IDisposable
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
    public void Ramps_know_whether_they_are_the_identity()
    {
        GammaRamp.Identity().IsIdentity.Should().BeTrue();
        GammaRamp.Identity().Red[255].Should().Be(65535);
        var scaled = GammaRamp.Scaled(0.875);
        scaled.IsIdentity.Should().BeFalse();
        scaled.Green[128].Should().Be(28784, "code 128 leaves the GPU as 112.0 in 8 bits");
        scaled.SameAs(GammaRamp.Scaled(0.875)).Should().BeTrue();
        scaled.SameAs(GammaRamp.Identity()).Should().BeFalse();
    }

    [Fact]
    public void The_original_ramp_is_journaled_before_the_first_write_and_restored_after()
    {
        var device = new FakeRampDevice(GammaRamp.FromFunction(i => Math.Pow(i / 255.0, 1.1)));
        var journal = new StateJournal(_directory);

        using (var guard = GammaRampGuard.Begin(device, journal))
        {
            journal.Pending(GammaRampGuard.JournalKind).Should().ContainSingle(e => e.Key == "\\\\.\\DISPLAY9");
            guard.Apply(GammaRamp.Scaled(0.875)).Should().BeTrue();
            device.Current.SameAs(GammaRamp.Scaled(0.875)).Should().BeTrue();
        }

        device.Current.SameAs(GammaRamp.FromFunction(i => Math.Pow(i / 255.0, 1.1))).Should().BeTrue();
        journal.Pending().Should().BeEmpty();
    }

    [Fact]
    public void A_crashed_run_is_restored_from_the_journal()
    {
        var original = GammaRamp.Identity();
        var device = new FakeRampDevice(original);
        var journal = new StateJournal(_directory);
        var guard = GammaRampGuard.Begin(device, journal);
        guard.Apply(GammaRamp.Scaled(0.5));

        // The process dies here: no Restore, no Dispose. A guardian (or the next launch) finds the entry.
        var (restored, failed) = GammaRampGuard.RestorePending(new StateJournal(_directory), _ => device);

        restored.Should().Equal("\\\\.\\DISPLAY9");
        failed.Should().BeEmpty();
        device.Current.SameAs(original).Should().BeTrue();
        journal.Pending().Should().BeEmpty();
    }

    [Fact]
    public void A_pending_entry_blocks_a_new_guard_and_survives_a_failed_restore()
    {
        var device = new FakeRampDevice(GammaRamp.Identity());
        var journal = new StateJournal(_directory);
        GammaRampGuard.Begin(device, journal).Apply(GammaRamp.Scaled(0.5));

        FluentActions.Invoking(() => GammaRampGuard.Begin(device, journal)).Should().Throw<InvalidOperationException>().WithMessage("*recover*");

        device.RejectWrites = true;
        var (_, failed) = GammaRampGuard.RestorePending(journal, _ => device);
        failed.Should().ContainSingle();
        journal.Pending().Should().ContainSingle("the entry stays until the ramp really is restored");
    }

    [Fact]
    public void The_journal_skips_unreadable_files_and_keeps_entries_separate()
    {
        var journal = new StateJournal(_directory);
        journal.Save("camera", "usb-046d-08b6", new { white = 4000 });
        journal.Save("gamma", @"\\.\DISPLAY1", GammaRamp.Identity());
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{ not json");

        journal.Pending().Should().HaveCount(2);
        journal.Pending("camera").Single().Original.GetProperty("white").GetInt32().Should().Be(4000);
        StateJournal.Read<GammaRamp>(journal.Pending("gamma").Single())!.IsIdentity.Should().BeTrue();
        journal.Remove("camera", "usb-046d-08b6");
        journal.Pending().Should().ContainSingle();
    }

    private sealed class FakeRampDevice(GammaRamp initial) : IGammaRampDevice
    {
        public GammaRamp Current { get; private set; } = initial;

        public bool RejectWrites { get; set; }

        public string DeviceName => @"\\.\DISPLAY9";

        public GammaRamp Read() => Current;

        public bool Write(GammaRamp ramp)
        {
            if (RejectWrites)
            {
                return false;
            }

            Current = ramp;
            return true;
        }
    }
}
