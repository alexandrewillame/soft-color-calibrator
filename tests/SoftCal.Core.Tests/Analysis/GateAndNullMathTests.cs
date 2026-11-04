using AwesomeAssertions;
using SoftCal.Core.Analysis;
using SoftCal.Core.Color;
using SoftCal.Core.Measurement;
using Xunit;

namespace SoftCal.Core.Tests.Analysis;

public sealed class GateAndNullMathTests
{
    private static GateMeasurements AllPassing() => new()
    {
        MatchedReadingSigma = 0.12,
        PatternShapeSpread = 0.4,
        TreeRepeat21 = 0.8,
        TreeRepeat38 = 0.5,
        TreeRepeat72 = 0.3,
        MoveChange = 0.6,
        RippleBias = -0.0004,
        GammaSpreadNull = 0.012,
        GammaSpreadBracketing = 0.02,
        LockHoldMinutes = 45,
        LockChangedDuringRun = false,
        TrustPassed = true,
    };

    [Fact]
    public void Every_criterion_within_its_limit_is_a_go()
    {
        var gate = FeasibilityGate.Evaluate(AllPassing());

        gate.Outcome.Should().Be(GateOutcome.Go);
        gate.Criteria.Should().HaveCount(9).And.OnlyContain(c => c.Status == CriterionStatus.Pass);
    }

    [Fact]
    public void A_deep_shadow_or_shape_failure_restricts_the_domain()
    {
        var gate = FeasibilityGate.Evaluate(AllPassing() with { TreeRepeat21 = 1.7, PatternShapeSpread = 1.4 });

        gate.Outcome.Should().Be(GateOutcome.GoWithRestrictions);
        gate.Reasons.Should().Contain(r => r.Contains("~21", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.45, -0.0004)]
    [InlineData(0.12, -0.0019)]
    public void An_unrepeatable_or_biased_null_falls_back_to_bracketing(double sigma, double bias)
    {
        FeasibilityGate.Evaluate(AllPassing() with { MatchedReadingSigma = sigma, RippleBias = bias }).Outcome.Should().Be(GateOutcome.BracketingOnly);
    }

    [Fact]
    public void A_lock_that_changed_is_a_no_go_but_a_short_watch_is_only_incomplete()
    {
        FeasibilityGate.Evaluate(AllPassing() with { LockChangedDuringRun = true }).Outcome.Should().Be(GateOutcome.NoGo);
        var shortWatch = FeasibilityGate.Evaluate(AllPassing() with { LockHoldMinutes = 12 });
        shortWatch.Outcome.Should().Be(GateOutcome.Incomplete);
        shortWatch.Criteria.Single(c => c.Id == "locks").Note.Should().Contain("12 min");
    }

    [Fact]
    public void A_failed_trust_test_leaves_bracketing_only_when_bracketing_repeats()
    {
        FeasibilityGate.Evaluate(AllPassing() with { TrustPassed = false }).Outcome.Should().Be(GateOutcome.BracketingOnly);
        FeasibilityGate.Evaluate(AllPassing() with { TrustPassed = false, GammaSpreadBracketing = 0.05 }).Outcome.Should().Be(GateOutcome.NoGo);
    }

    [Fact]
    public void Spread_is_max_minus_min_and_needs_two_values()
    {
        FeasibilityGate.Spread([21.2, 20.6, 21.0]).Should().BeApproximately(0.6, 1e-12);
        double.IsNaN(FeasibilityGate.Spread([21.2])).Should().BeTrue();
    }

    [Fact]
    public void The_trust_test_catches_white_that_moves_after_a_colored_patch()
    {
        var steady = Enumerable.Repeat(new Vec3(200, 201, 199), 40).ToList();
        TrustTest.Evaluate([new(200, 201, 199), new(200.4, 201.2, 199.1), new(199.8, 200.9, 198.9)], steady).Passed.Should().BeTrue();

        var awb = TrustTest.Evaluate([new(200, 201, 199), new(190, 201, 205)], steady);
        awb.Passed.Should().BeFalse();
        awb.WhiteSequenceDeviation.Should().BeApproximately(0.05, 1e-9);
    }

    [Fact]
    public void The_trust_test_catches_drift_during_the_hold()
    {
        var drifting = Enumerable.Range(0, 40).Select(i => new Vec3(200 + (0.1 * i), 200, 200)).ToList();

        TrustTest.Evaluate([new(200, 200, 200), new(200, 200, 200)], drifting).Passed.Should().BeFalse();
    }

    [Fact]
    public void The_matched_code_is_interpolated_between_the_bracketing_codes()
    {
        NullMath.Interpolate(186, reference: 120.0, lower: 119.2, upper: 120.8).Should().BeApproximately(186.5, 1e-12);
        NullMath.Interpolate(20, reference: 50.0, lower: 50.0, upper: 54.0).Should().Be(20);
    }

    [Fact]
    public void The_search_steps_toward_the_crossing_and_stays_inside_the_bracket()
    {
        var readings = new Dictionary<int, double> { [186] = 130 };

        NullMath.NextProbe(readings, 120, -1, 186).Should().BeInRange(160, 185);
        readings[172] = 119;
        NullMath.NextProbe(readings, 120, 172, 186).Should().Be(173);
        NullMath.NextProbe(new Dictionary<int, double> { [10] = 5, [11] = 9 }, 7, 10, 11).Should().Be(10, "a closed bracket is final");
    }

    [Fact]
    public void Tree_levels_halve_the_light_of_the_nearest_whole_code()
    {
        // At γ 2.2 the half-light code is 186.08; the checkerboard uses code 186 whose light is slightly below ½.
        var (reference, code, codeFraction, fraction) = MidpointTree.Next(186.08, 0.5, DeviceRgb.Gray);

        code.Should().Be(186);
        codeFraction.Should().BeApproximately(Math.Pow(186 / 255.0, 2.2), 1e-4);
        fraction.Should().BeApproximately(codeFraction / 2, 1e-12);
        reference.Geometry.Should().Be(HalftoneGeometry.Checkerboard);
        reference.On.Should().Be(DeviceRgb.Gray(186));
        reference.Off.Should().Be(DeviceRgb.Black);
        MidpointTree.Guess(1 / 256.0).Should().BeApproximately(20.5, 0.1, "about code 21 at γ 2.2");
    }
}
