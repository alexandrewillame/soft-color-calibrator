using AwesomeAssertions;
using SoftCal.Core.ToneCurves;
using Xunit;

namespace SoftCal.Core.Tests.ToneCurves;

public sealed class ToneCurveTests
{
    [Fact]
    public void Srgb_matches_known_values_and_is_continuous_at_the_breakpoint()
    {
        var srgb = SrgbCurve.Instance;

        srgb.ToLinear(0.5).Should().BeApproximately(0.214041, 1e-6);
        srgb.ToLinear(0.04045).Should().BeApproximately(0.0031308, 1e-7);
        srgb.ToLinear(0.04045 + 1e-9).Should().BeApproximately(srgb.ToLinear(0.04045), 1e-8);
        srgb.ToLinear(0).Should().Be(0);
        srgb.ToLinear(1).Should().BeApproximately(1, 1e-12);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.01)]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(1.0)]
    public void Standard_curves_round_trip(double encoded)
    {
        ITransferCurve[] curves = [SrgbCurve.Instance, new PowerCurve(2.2), new Bt1886Curve(0), new Bt1886Curve(0.001), new Bt1886Curve(0.01, 2.2)];
        foreach (var curve in curves)
        {
            curve.ToEncoded(curve.ToLinear(encoded)).Should().BeApproximately(encoded, 1e-9, curve.GetType().Name);
        }
    }

    [Fact]
    public void Bt1886_without_black_is_a_pure_2_4_power_law()
    {
        new Bt1886Curve(0).ToLinear(0.5).Should().BeApproximately(Math.Pow(0.5, 2.4), 1e-12);
    }

    [Fact]
    public void Bt1886_with_black_spans_zero_to_one_and_lifts_the_shadows()
    {
        var curve = new Bt1886Curve(0.002);

        curve.ToLinear(0).Should().BeApproximately(0, 1e-12);
        curve.ToLinear(1).Should().BeApproximately(1, 1e-12);
        curve.ToLinear(0.1).Should().BeGreaterThan(Math.Pow(0.1, 2.4));
    }

    [Fact]
    public void Monotone_cubic_interpolates_the_data_without_overshoot()
    {
        double[] x = [0, 0.2, 0.4, 0.5, 0.6, 1.0];
        double[] y = [0, 0.01, 0.02, 0.6, 0.62, 1.0]; // a sharp step that would make a natural spline overshoot
        var spline = new MonotoneCubic(x, y);

        for (var i = 0; i < x.Length; i++)
        {
            spline.Evaluate(x[i]).Should().BeApproximately(y[i], 1e-12);
        }

        for (var i = 0; i < x.Length - 1; i++)
        {
            for (var t = 0.0; t <= 1.0; t += 0.01)
            {
                var value = spline.Evaluate(x[i] + ((x[i + 1] - x[i]) * t));
                value.Should().BeInRange(y[i] - 1e-12, y[i + 1] + 1e-12);
            }
        }
    }

    [Fact]
    public void Monotone_cubic_inverse_recovers_x()
    {
        var x = Enumerable.Range(0, 17).Select(i => i / 16.0).ToArray();
        var y = x.Select(v => Math.Pow(v, 2.2)).ToArray();
        var spline = new MonotoneCubic(x, y);

        foreach (var target in new[] { 0.001, 0.05, 0.18, 0.5, 0.97 })
        {
            spline.Evaluate(spline.Inverse(target)).Should().BeApproximately(target, 1e-10);
        }
    }

    [Fact]
    public void Monotone_cubic_rejects_unsorted_x()
    {
        var act = () => new MonotoneCubic([0, 0.5, 0.4, 1], [0, 0.2, 0.3, 1]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Measured_curve_reproduces_a_sampled_power_law()
    {
        var encoded = Enumerable.Range(0, 18).Select(i => i / 17.0).ToArray();
        var linear = encoded.Select(v => 0.0005 + Math.Pow(v, 2.2)).ToArray(); // raw light including black
        var curve = new MeasuredCurve(encoded, linear);

        curve.IsMonotone.Should().BeTrue();
        for (var e = 0.03; e < 1; e += 0.05)
        {
            var truth = Math.Pow(e, 2.2) / (1.0005 - 0.0005);
            curve.ToLinear(e).Should().BeApproximately(truth, 2e-3);
        }

        GammaAnalysis.EffectiveGamma(curve).Should().BeApproximately(2.2, 0.01);
    }

    [Fact]
    public void Every_gamma_statistic_of_a_pure_power_law_is_its_exponent()
    {
        var curve = new PowerCurve(2.4);

        GammaAnalysis.EffectiveGamma(curve).Should().BeApproximately(2.4, 1e-12);
        GammaAnalysis.AverageGamma(curve).Should().BeApproximately(2.4, 1e-12);
        GammaAnalysis.LogLogSlope(curve).Should().BeApproximately(2.4, 1e-12);
        GammaAnalysis.PointGamma(0.5, Math.Pow(0.5, 2.2)).Should().BeApproximately(2.2, 1e-12);
    }

    [Fact]
    public void Srgb_gamma_statistics_reflect_its_linear_toe()
    {
        // ln(0.214041) / ln(0.5): the figure calibration tools report for an exact sRGB display.
        GammaAnalysis.EffectiveGamma(SrgbCurve.Instance).Should().BeApproximately(2.2240, 2e-4);

        // The toe pulls the dark-weighted log-log fit well below the 50 % figure; the plain average sits in between.
        var slope = GammaAnalysis.LogLogSlope(SrgbCurve.Instance);
        var average = GammaAnalysis.AverageGamma(SrgbCurve.Instance);
        slope.Should().BeApproximately(2.121, 2e-3);
        average.Should().BeInRange(slope, GammaAnalysis.EffectiveGamma(SrgbCurve.Instance));
    }
}
