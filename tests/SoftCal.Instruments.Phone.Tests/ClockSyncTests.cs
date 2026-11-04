using AwesomeAssertions;
using SoftCal.Remote;
using Xunit;

namespace SoftCal.Instruments.Phone.Tests;

public sealed class ClockSyncTests
{
    [Fact]
    public void The_fastest_ping_gives_the_offset()
    {
        // The phone's clock is 100 s ahead. The first ping was held up 50 ms on its way out, the second one was quick.
        var clock = new ClockSync();
        clock.Add(pcSent: 10, phoneTime: 110.050, pcReceived: 10.051);
        clock.Add(pcSent: 20, phoneTime: 120.002, pcReceived: 20.004);

        clock.Offset.Should().BeApproximately(100, 1e-9);
        clock.Uncertainty.Should().BeApproximately(0.002, 1e-9);
        clock.ToPc(150).Should().BeApproximately(50, 1e-9);
    }

    [Fact]
    public void Only_the_last_sixteen_pings_count_so_a_drift_is_followed()
    {
        var clock = new ClockSync();
        clock.Add(0, 100, 0.0001);
        for (var i = 1; i <= 16; i++)
        {
            // The clocks drifted 10 ms apart since; these pings are slower than the first.
            clock.Add(i, i + 100.010 + 0.001, i + 0.002);
        }

        clock.Samples.Should().Be(16);
        clock.Offset.Should().BeApproximately(100.010, 1e-9);
    }

    [Fact]
    public void Before_any_ping_times_are_left_as_they_are()
    {
        var clock = new ClockSync();
        clock.Add(5, 105, 4.9);

        clock.Offset.Should().BeNull("an answer that came back before it left is nonsense");
        clock.ToPc(42).Should().Be(42);
    }
}
