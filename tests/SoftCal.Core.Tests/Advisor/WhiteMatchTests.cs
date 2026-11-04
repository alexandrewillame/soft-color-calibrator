using AwesomeAssertions;
using SoftCal.Core.Advisor;
using SoftCal.Core.Color;
using Xunit;

namespace SoftCal.Core.Tests.Advisor;

/// <summary>The arithmetic of matching a monitor's white to a reference screen's: the difference read by one camera on
/// both screens, the gains that undo it, and the words that describe it.</summary>
public sealed class WhiteMatchTests
{
    private static readonly Vec3 SrgbLikeSlope = new(0.45, 0.45, 0.45);

    [Fact]
    public void The_difference_is_a_ratio_of_light_from_the_encoded_readings()
    {
        var reference = new WhiteSample(new Vec3(180, 180, 180), SrgbLikeSlope, 1.0 / 32);
        var white = new WhiteSample(new Vec3(170, 180, 160), SrgbLikeSlope, 1.0 / 32);

        var difference = WhiteMath.Difference(white, reference);

        // (170/180)^(1/0.45) and (160/180)^(1/0.45): encoded ratios become light ratios through the camera's slope.
        difference.Red.Should().BeApproximately(0.8807, 1e-3);
        difference.Blue.Should().BeApproximately(0.7697, 1e-3);
        difference.Size.Should().BeApproximately(-Math.Log(0.7697), 1e-3);
    }

    [Fact]
    public void The_exposure_cancels_out_of_the_difference()
    {
        var reference = new WhiteSample(new Vec3(180, 175, 150), SrgbLikeSlope, 1.0 / 32);
        var white = new WhiteSample(new Vec3(170, 180, 160), SrgbLikeSlope, 1.0 / 32);

        // Twice the light on the camera raises every encoded value by 2^slope alike.
        var doubled = white with { Encoded = white.Encoded * Math.Pow(2, 0.45), ExposureSeconds = 1.0 / 16 };

        var a = WhiteMath.Difference(white, reference);
        var b = WhiteMath.Difference(doubled, reference);
        b.Red.Should().BeApproximately(a.Red, 1e-9);
        b.Blue.Should().BeApproximately(a.Blue, 1e-9);
    }

    [Fact]
    public void A_green_white_is_described_in_plain_words()
    {
        new WhiteDifference(0.86, 0.82).Describe("VX2770 SERIES")
            .Should().Be("greener than VX2770 SERIES: 14 % less red and 18 % less blue for the same green");
        new WhiteDifference(1.10, 0.92).Tint.Should().Be("warmer");
        new WhiteDifference(1.001, 0.998).Describe("the reference")
            .Should().Be("about the same as the reference (the same red and the same blue for the same green)");
    }

    /// <summary>The BenQ's run of 2026-10-18 ended at 84/77/85, 2 % pinker than the ViewSonic, its gain steps moving
    /// the white about 2.2 times their own size. One step down on red and blue is predicted within 0.7 %.</summary>
    [Fact]
    public void From_the_benq_s_last_white_one_step_brings_it_within_one_percent()
    {
        var next = WhiteMath.NextGains(new RgbGains(84, 77, 85), new WhiteDifference(1.02, 1.02), WhiteJacobian.Initial(2.2), cap: 85);

        next.Should().Be(new RgbGains(83, 77, 84));
    }

    [Fact]
    public void Gains_as_close_as_whole_steps_allow_stay_as_they_are()
    {
        var current = new RgbGains(84, 78, 85);

        WhiteMath.NextGains(current, new WhiteDifference(0.992, 0.992), WhiteJacobian.Initial(2.2), cap: 85).Should().Be(current);
    }

    /// <summary>The BenQ's first white: 16 % less red and 20 % less blue than the ViewSonic, at 84/85/84.</summary>
    [Fact]
    public void No_gain_rises_above_the_highest_one_the_monitor_had()
    {
        var next = WhiteMath.NextGains(new RgbGains(84, 85, 84), new WhiteDifference(0.84, 0.80), new WhiteJacobian(2.28, 0, 0, 2.58), cap: 85);

        next.Max.Should().BeLessThanOrEqualTo(85);
        next.Should().Be(new RgbGains(83, 78, 84), "green comes down; red and blue stay near the top");
    }

    [Fact]
    public void A_weaker_effect_asks_for_a_bigger_change()
    {
        var full = WhiteMath.NextGains(new RgbGains(100, 100, 100), new WhiteDifference(0.86, 0.80), WhiteJacobian.Initial(2.2), cap: 100);
        var weaker = WhiteMath.NextGains(new RgbGains(100, 100, 100), new WhiteDifference(0.86, 0.80), WhiteJacobian.Initial(2.2).Scaled(0.75), cap: 100);

        weaker.Green.Should().BeLessThan(full.Green);
        weaker.Should().Be(new RgbGains(95, 87, 100));
    }

    [Fact]
    public void A_newton_step_undoes_what_the_matrix_predicts()
    {
        var jacobian = new WhiteJacobian(2.0, 0.3, -0.2, 2.5);

        var (red, blue) = jacobian.Apply(0.1, -0.05);
        var (undoRed, undoBlue) = jacobian.Undo(red, blue);

        undoRed.Should().BeApproximately(-0.1, 1e-12);
        undoBlue.Should().BeApproximately(0.05, 1e-12);
    }

    [Fact]
    public void A_learning_step_lost_in_noise_does_not_break_the_matrix()
    {
        var jacobian = WhiteJacobian.Initial();

        // A tiny step that "changed" the difference the wrong way round: taken at face value it would flip the sign.
        var updated = jacobian.Update(0.004, 0, -0.05, 0);

        updated.Should().Be(jacobian);
    }
}
