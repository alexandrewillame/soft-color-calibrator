using AwesomeAssertions;
using SoftCal.Windows.Gamma;
using Xunit;

namespace SoftCal.Windows.Tests;

/// <summary>SoftCal's loader keeps the calibration in the GPU: it writes it back after a reset, leaves rounding alone,
/// and backs off when another program keeps changing the curves.</summary>
public sealed class RampKeeperTests
{
    private static readonly GammaRamp Calibration = GammaRamp.FromFunction(i => Math.Pow(i / 255.0, 1.08));

    private readonly Clock _clock = new();
    private readonly FakeDevice _benq = new("\\\\.\\DISPLAY1");

    private RampKeeper Keeper() => new(_ => _benq, _clock);

    private static KeptRamp Target => new("\\\\.\\DISPLAY1", Calibration, "SoftCal ZOWIE.icc");

    [Fact]
    public void The_first_check_loads_the_calibration_and_later_checks_stay_quiet()
    {
        var keeper = Keeper();

        keeper.Check([Target]).Should().ContainSingle(e => e.Outcome == KeepOutcome.Loaded);
        RampKeeper.Difference(_benq.Ramp, Calibration).Should().Be(0);
        keeper.Check([Target]).Should().BeEmpty();
        _benq.Writes.Should().Be(1);
    }

    [Fact]
    public void A_reset_to_linear_is_put_back_at_the_next_check()
    {
        var keeper = Keeper();
        keeper.Check([Target]);

        _benq.Ramp = GammaRamp.Identity();
        _clock.Advance(TimeSpan.FromSeconds(2));
        var events = keeper.Check([Target]);

        events.Should().ContainSingle(e => e.Outcome == KeepOutcome.Reapplied && e.Difference > RampKeeper.Tolerance);
        RampKeeper.Difference(_benq.Ramp, Calibration).Should().Be(0);
    }

    [Fact]
    public void Eight_bit_rounding_is_not_a_change()
    {
        var keeper = Keeper();
        keeper.Check([Target]);

        _benq.Ramp = new GammaRamp([.. Calibration.Red.Select(v => (ushort)(v & 0xFF00))], [.. Calibration.Green.Select(v => (ushort)(v & 0xFF00))], [.. Calibration.Blue.Select(v => (ushort)(v & 0xFF00))]);
        keeper.Check([Target]).Should().BeEmpty();
        _benq.Writes.Should().Be(1);
    }

    [Fact]
    public void Another_program_that_keeps_changing_the_curves_is_left_alone_for_a_while()
    {
        var keeper = Keeper();
        keeper.Check([Target]);
        var outcomes = new List<KeepOutcome>();
        for (var i = 0; i < 4; i++)
        {
            _benq.Ramp = GammaRamp.Scaled(0.9);
            _clock.Advance(TimeSpan.FromSeconds(5));
            outcomes.AddRange(keeper.Check([Target]).Select(e => e.Outcome));
        }

        outcomes.Should().Equal(KeepOutcome.Reapplied, KeepOutcome.Reapplied, KeepOutcome.Reapplied, KeepOutcome.BackingOff);

        _benq.Ramp = GammaRamp.Scaled(0.9);
        var writes = _benq.Writes;
        _clock.Advance(TimeSpan.FromMinutes(5));
        keeper.Check([Target]).Should().BeEmpty("it backs off for ten minutes");
        _benq.Writes.Should().Be(writes);

        _clock.Advance(TimeSpan.FromMinutes(6));
        keeper.Check([Target]).Should().ContainSingle(e => e.Outcome == KeepOutcome.Reapplied);
    }

    [Fact]
    public void Resets_spread_over_time_never_count_as_a_fight()
    {
        var keeper = Keeper();
        keeper.Check([Target]);
        for (var i = 0; i < 6; i++)
        {
            _benq.Ramp = GammaRamp.Identity();
            _clock.Advance(TimeSpan.FromMinutes(1));
            keeper.Check([Target]).Should().ContainSingle(e => e.Outcome == KeepOutcome.Reapplied);
        }
    }

    [Fact]
    public void A_driver_that_ignores_the_ramp_is_reported_once()
    {
        _benq.IgnoresWrites = true;
        _benq.Ramp = GammaRamp.Identity();
        var keeper = Keeper();

        keeper.Check([Target]).Should().ContainSingle(e => e.Outcome == KeepOutcome.Failed);
        _clock.Advance(TimeSpan.FromSeconds(2));
        keeper.Check([Target]).Should().BeEmpty();

        _benq.IgnoresWrites = false;
        _clock.Advance(TimeSpan.FromSeconds(2));
        keeper.Check([Target]).Should().ContainSingle(e => e.Outcome == KeepOutcome.Loaded);
    }

    private sealed class FakeDevice(string name) : IGammaRampDevice
    {
        public string DeviceName { get; } = name;

        public GammaRamp Ramp { get; set; } = GammaRamp.Identity();

        public bool IgnoresWrites { get; set; }

        public int Writes { get; private set; }

        public GammaRamp Read() => Ramp;

        /// <summary>Like SetDeviceGammaRamp on some drivers: reports success and keeps the old ramp.</summary>
        public bool Write(GammaRamp ramp)
        {
            Writes++;
            if (!IgnoresWrites)
            {
                Ramp = ramp;
            }

            return true;
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 18, 1, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
