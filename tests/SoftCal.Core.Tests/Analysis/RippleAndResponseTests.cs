using AwesomeAssertions;
using SoftCal.Core.Analysis;
using SoftCal.Core.ToneCurves;
using Xunit;

namespace SoftCal.Core.Tests.Analysis;

public sealed class RippleAndResponseTests
{
    [Fact]
    public void Ripple_ignores_smooth_shading_and_measures_the_pattern()
    {
        const int W = 120, H = 90;
        var pixels = new double[W * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var vignette = 1 - (0.15 * (Math.Pow((x - 60) / 60.0, 2) + Math.Pow((y - 45) / 45.0, 2)));
                var ripple = 0.02 * Math.Sqrt(2) * Math.Sin(2 * Math.PI * x / 3.0);
                pixels[(y * W) + x] = 150 * vignette * (1 + ripple);
            }
        }

        var m = RippleAnalysis.Measure(pixels, W, H);

        m.RelativeRms.Should().BeApproximately(0.02, 0.002, "the 2 % rms sinusoid is all that is left after the quadratic trend");
    }

    [Fact]
    public void The_solid_patch_noise_is_removed_in_quadrature()
    {
        var halftone = new RippleMeasurement(150, 0.025, 100, 100);
        var solid = new RippleMeasurement(150, 0.015, 100, 100);

        var r = RippleAnalysis.Evaluate(halftone, solid, 2.2);

        r.ExcessRelativeRms.Should().BeApproximately(0.02, 1e-12);
        r.LinearRelativeRms.Should().BeApproximately(0.044, 1e-12);
        r.WithinRippleLimit.Should().BeTrue();
    }

    [Theory]
    [InlineData(0.025, true)]
    [InlineData(0.035, false)]
    public void Ripple_at_the_limit_keeps_the_null_bias_under_a_tenth_of_a_percent(double encodedRipple, bool withinBias)
    {
        var r = RippleAnalysis.Evaluate(new RippleMeasurement(150, encodedRipple, 10, 10), new RippleMeasurement(150, 0, 10, 10), 2.2);

        r.WithinBiasLimit.Should().Be(withinBias);
        r.NullBias.Should().BeNegative("the camera's concave response reads a rippled patch dark");
    }

    [Fact]
    public void The_null_bias_formula_matches_a_brute_force_average()
    {
        const double Gamma = 2.2, A = 0.06;

        // A two-level ripple ±a around 1, seen through code = light^(1/γ).
        var meanCode = (Math.Pow(1 + A, 1 / Gamma) + Math.Pow(1 - A, 1 / Gamma)) / 2;
        var matchedLight = Math.Pow(meanCode, Gamma);

        RippleAnalysis.NullBias(A, Gamma).Should().BeApproximately(matchedLight - 1, 2e-5);
    }

    [Fact]
    public void Bracketing_recovers_the_response_the_exposure_ratios_and_the_display_curve()
    {
        // A camera with an sRGB curve and exposure steps that alternate 3 % long and short; a γ 2.4 display.
        var nominal = Enumerable.Range(-11, 10).Select(k => Math.Pow(2, k)).ToArray();
        var actual = nominal.Select((t, k) => t * (k % 2 == 0 ? 1.03 : 0.97)).ToArray();
        var codes = Enumerable.Range(0, 16).Select(i => i * 16).Append(255).ToArray();
        var light = codes.Select(v => 0.002 + Math.Pow(v / 255.0, 2.4)).ToList();
        var whiteIndex = codes.Length - 1;
        var anchors = new List<LightAnchor>();
        foreach (var f in new[] { 0.5, 0.25, 0.75, 1 / 16.0 })
        {
            anchors.Add(new LightAnchor(light.Count, [(whiteIndex, f), (0, 1 - f)]));
            light.Add((f * light[whiteIndex]) + ((1 - f) * light[0]));
        }

        const double Sensitivity = 60;
        var observations = new List<BracketObservation>();
        for (var j = 0; j < light.Count; j++)
        {
            for (var k = 0; k < nominal.Length; k++)
            {
                var signal = light[j] * actual[k] * Sensitivity;
                var code = SrgbCurve.Instance.ToEncoded(Math.Min(signal, 1)) * 255;
                observations.Add(new BracketObservation(j, k, code, signal >= 1));
            }
        }

        var fit = ResponseFit.Fit(observations, nominal, light.Count, anchors);

        fit.Exponent.Should().BeApproximately(1, 0.01, "on average the steps are exactly ×2");
        fit.StepRatios.Should().AllSatisfy(r => r.Should().Match(x => Math.Abs(x - (2 * 1.03 / 0.97)) < 0.03 || Math.Abs(x - (2 * 0.97 / 1.03)) < 0.03));
        fit.ResidualRms.Should().BeLessThan(0.02);
        var e0 = fit.PatchLight(0);
        var e255 = fit.PatchLight(whiteIndex);
        for (var j = 1; j < codes.Length - 1; j++)
        {
            var truth = (light[j] - light[0]) / (light[whiteIndex] - light[0]);
            var measured = ResponseFit.RelativeFraction(fit.PatchLight(j), e0, e255);
            measured.Should().BeApproximately(truth, Math.Max(0.002, truth * 0.02), $"code {codes[j]}");
        }

        fit.LocalGamma(140).Should().BeApproximately(2.18, 0.08, "the sRGB curve's local log-log slope at code 140 is 2.18");
    }

    [Fact]
    public void An_exposure_with_no_usable_reading_does_not_unpin_the_exposure_scale()
    {
        // Seen on the C920e: at the shortest exposure every patch sits under the black clamp. That exposure carries
        // no data, so it must not be able to absorb the gauge (the fit then collapsed to "every step is x1").
        var nominal = Enumerable.Range(-11, 8).Select(k => Math.Pow(2, k)).ToArray();
        var observations = new List<BracketObservation>();
        for (var j = 0; j < 20; j++)
        {
            var light = 0.002 + Math.Pow((j + 1) / 20.0, 2.4);
            for (var k = 0; k < nominal.Length; k++)
            {
                var signal = light * nominal[k] * 400;
                var code = k == 0 ? 2.0 : 255 * Math.Pow(Math.Min(1, signal), 1 / 2.2);
                observations.Add(new BracketObservation(j, k, code, signal >= 1));
            }
        }

        var fit = ResponseFit.Fit(observations, nominal, 20, null, new ResponseFitOptions { MinCode = 6 });

        fit.StepRatios.Skip(1).Should().AllSatisfy(r => r.Should().BeApproximately(2, 0.05));
        fit.LocalGamma(128).Should().BeApproximately(2.2, 0.1);
    }

    [Fact]
    public void Without_anchors_the_mean_step_is_taken_as_nominal()
    {
        var nominal = new[] { 1.0 / 64, 1.0 / 32, 1.0 / 16 };
        var observations = new List<BracketObservation>();
        for (var j = 0; j < 30; j++)
        {
            for (var k = 0; k < 3; k++)
            {
                var code = 255 * Math.Pow(Math.Min(1, (j + 1) / 40.0 * nominal[k] * 16), 1 / 2.2);
                observations.Add(new BracketObservation(j, k, code));
            }
        }

        var fit = ResponseFit.Fit(observations, nominal, 30);

        fit.Exponent.Should().Be(1);
        fit.StepRatios.Should().AllSatisfy(r => r.Should().BeApproximately(2, 0.02));
        double.IsNaN(fit.AnchorResidualRms).Should().BeTrue();
    }
}
