using AwesomeAssertions;
using SoftCal.Core.Color;
using Xunit;

namespace SoftCal.Core.Tests.Color;

public sealed class DifferenceAndTemperatureTests
{
    // Sharma, Wu & Dalal (2005) CIEDE2000 test data: Lab1, Lab2, expected ΔE00.
    public static TheoryData<double, double, double, double, double, double, double> SharmaPairs => new()
    {
        { 50.0000, 2.6772, -79.7751, 50.0000, 0.0000, -82.7485, 2.0425 },
        { 50.0000, 3.1571, -77.2803, 50.0000, 0.0000, -82.7485, 2.8615 },
        { 50.0000, 2.8361, -74.0200, 50.0000, 0.0000, -82.7485, 3.4412 },
        { 50.0000, -1.3802, -84.2814, 50.0000, 0.0000, -82.7485, 1.0000 },
        { 50.0000, -1.1848, -84.8006, 50.0000, 0.0000, -82.7485, 1.0000 },
        { 50.0000, -0.9009, -85.5211, 50.0000, 0.0000, -82.7485, 1.0000 },
        { 50.0000, 0.0000, 0.0000, 50.0000, -1.0000, 2.0000, 2.3669 },
        { 50.0000, -1.0000, 2.0000, 50.0000, 0.0000, 0.0000, 2.3669 },
        { 50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0009, 7.1792 },
        { 50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0010, 7.1792 },
        { 50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0011, 7.2195 },
        { 50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0012, 7.2195 },
        { 50.0000, -0.0010, 2.4900, 50.0000, 0.0009, -2.4900, 4.8045 },
        { 50.0000, -0.0010, 2.4900, 50.0000, 0.0010, -2.4900, 4.8045 },
        { 50.0000, -0.0010, 2.4900, 50.0000, 0.0011, -2.4900, 4.7461 },
        { 50.0000, 2.5000, 0.0000, 50.0000, 0.0000, -2.5000, 4.3065 },
        { 50.0000, 2.5000, 0.0000, 73.0000, 25.0000, -18.0000, 27.1492 },
        { 50.0000, 2.5000, 0.0000, 61.0000, -5.0000, 29.0000, 22.8977 },
        { 50.0000, 2.5000, 0.0000, 56.0000, -27.0000, -3.0000, 31.9030 },
        { 50.0000, 2.5000, 0.0000, 58.0000, 24.0000, 15.0000, 19.4535 },
        { 50.0000, 2.5000, 0.0000, 50.0000, 3.1736, 0.5854, 1.0000 },
        { 50.0000, 2.5000, 0.0000, 50.0000, 3.2972, 0.0000, 1.0000 },
        { 50.0000, 2.5000, 0.0000, 50.0000, 1.8634, 0.5757, 1.0000 },
        { 50.0000, 2.5000, 0.0000, 50.0000, 3.2592, 0.3350, 1.0000 },
        { 60.2574, -34.0099, 36.2677, 60.4626, -34.1751, 39.4387, 1.2644 },
        { 63.0109, -31.0961, -5.8663, 62.8187, -29.7946, -4.0864, 1.2630 },
        { 61.2901, 3.7196, -5.3901, 61.4292, 2.2480, -4.9620, 1.8731 },
        { 35.0831, -44.1164, 3.7933, 35.0232, -40.0716, 1.5901, 1.8645 },
        { 22.7233, 20.0904, -46.6940, 23.0331, 14.9730, -42.5619, 2.0373 },
        { 36.4612, 47.8580, 18.3852, 36.2715, 50.5065, 21.2231, 1.4146 },
        { 90.8027, -2.0831, 1.4410, 91.1528, -1.6435, 0.0447, 1.4441 },
        { 90.9257, -0.5406, -0.9208, 88.6381, -0.8985, -0.7239, 1.5381 },
        { 6.7747, -0.2908, -2.4247, 5.8714, -0.0985, -2.2286, 0.6377 },
        { 2.0776, 0.0795, -1.1350, 0.9033, -0.0636, -0.5514, 0.9082 },
    };

    [Theory]
    [MemberData(nameof(SharmaPairs))]
    public void Ciede2000_matches_sharma_test_data(double l1, double a1, double b1, double l2, double a2, double b2, double expected)
    {
        DeltaE.Ciede2000(new Lab(l1, a1, b1), new Lab(l2, a2, b2)).Should().BeApproximately(expected, 5e-5);
    }

    // Note: Wacton.Unicolour 8.0.0 is NOT used as an oracle here. Its Ciede2000 disagrees with the Sharma reference data
    // above (e.g. pair 17 gives 24.83 instead of 27.1492), whereas this implementation reproduces all 34 pairs.
    [Fact]
    public void Ciede2000_is_symmetric_non_negative_and_zero_only_for_identical_colors()
    {
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var first = new Lab(random.NextDouble() * 100, (random.NextDouble() * 200) - 100, (random.NextDouble() * 200) - 100);
            var second = new Lab(random.NextDouble() * 100, (random.NextDouble() * 200) - 100, (random.NextDouble() * 200) - 100);

            var forward = DeltaE.Ciede2000(first, second);

            forward.Should().BeApproximately(DeltaE.Ciede2000(second, first), 1e-9);
            forward.Should().BeGreaterThan(0);
            DeltaE.Ciede2000(first, first).Should().Be(0);
        }
    }

    [Fact]
    public void D65_has_a_cct_near_6504_k_and_a_small_positive_duv()
    {
        var t = ColorTemperature.FromChromaticity(new Chromaticity(0.31271, 0.32902));

        t.IsValid.Should().BeTrue();
        t.Kelvin.Should().BeApproximately(6504, 3);
        t.Duv.Should().BeApproximately(0.0032, 0.0003);
    }

    [Fact]
    public void Illuminant_a_sits_on_the_planckian_locus_at_2856_k()
    {
        var t = ColorTemperature.FromChromaticity(Illuminants.A);

        t.Kelvin.Should().BeApproximately(2856, 3);
        Math.Abs(t.Duv).Should().BeLessThan(0.0005);
    }

    [Theory]
    [InlineData(3000, 0.0)]
    [InlineData(5000, 0.002)]
    [InlineData(6500, -0.003)]
    [InlineData(9300, 0.001)]
    public void Cct_and_duv_round_trip(double kelvin, double duv)
    {
        var t = ColorTemperature.FromChromaticity(ColorTemperature.ToChromaticity(kelvin, duv));

        t.Kelvin.Should().BeApproximately(kelvin, kelvin * 1e-3);
        t.Duv.Should().BeApproximately(duv, 2e-5);
    }

    [Fact]
    public void Daylight_6504_k_is_d65()
    {
        DeltaE.UvPrime(ColorTemperature.Daylight(6504), new Chromaticity(0.31271, 0.32902)).Should().BeLessThan(5e-4);
    }
}
