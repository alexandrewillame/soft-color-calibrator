using AwesomeAssertions;
using SoftCal.Core.Color;
using Xunit;

namespace SoftCal.Core.Tests.Color;

public sealed class ColorMathTests
{
    [Fact]
    public void Matrix_inverse_times_matrix_is_identity()
    {
        var m = new Matrix3(0.9, 0.2, -0.1, 0.05, 1.1, 0.3, -0.2, 0.15, 0.8);

        var product = m * m.Inverse();

        AssertMatrix(product, Matrix3.Identity, 1e-12);
    }

    [Fact]
    public void Singular_matrix_cannot_be_inverted()
    {
        var singular = Matrix3.FromRows(new(1, 2, 3), new(2, 4, 6), new(0, 1, 1));

        var act = () => singular.Inverse();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Srgb_rgb_to_xyz_matches_the_published_matrix()
    {
        // Matrix derived from the sRGB primaries and D65 (0.3127, 0.3290), as published on Wikipedia's sRGB page.
        var expected = new Matrix3(
            0.41239080, 0.35758434, 0.18048079,
            0.21263901, 0.71516868, 0.07219232,
            0.01933082, 0.11919478, 0.95053215);

        AssertMatrix(RgbPrimaries.Srgb.RgbToXyz(), expected, 1e-6);
    }

    [Fact]
    public void Rgb_white_maps_to_the_white_point_and_primary_luminances_sum_to_one()
    {
        var m = RgbPrimaries.DisplayP3.RgbToXyz();

        var white = Xyz.FromVec3(m * Vec3.One);

        white.ToChromaticity().X.Should().BeApproximately(0.3127, 1e-12);
        white.ToChromaticity().Y.Should().BeApproximately(0.3290, 1e-12);
        RgbPrimaries.DisplayP3.PrimaryLuminances().Sum.Should().BeApproximately(1, 1e-12);
    }

    [Fact]
    public void Bradford_d65_to_d50_matches_lindbloom()
    {
        var d65 = new Xyz(0.95047, 1.0, 1.08883);
        var d50 = new Xyz(0.96422, 1.0, 0.82521);
        var expected = new Matrix3(
            1.0478112, 0.0228866, -0.0501270,
            0.0295424, 0.9904844, -0.0170491,
            -0.0092345, 0.0150436, 0.7521316);

        AssertMatrix(ChromaticAdaptation.Bradford(d65, d50), expected, 2e-6);
    }

    [Fact]
    public void Adapting_the_source_white_gives_the_destination_white()
    {
        var source = Illuminants.D65.ToXyz();
        var destination = Illuminants.IccD50;

        var adapted = ChromaticAdaptation.Adapt(source, source, destination);

        adapted.X.Should().BeApproximately(destination.X, 1e-12);
        adapted.Y.Should().BeApproximately(destination.Y, 1e-12);
        adapted.Z.Should().BeApproximately(destination.Z, 1e-12);
    }

    [Fact]
    public void Chromaticity_round_trips_through_xyz_and_uv_prime()
    {
        var xy = new Chromaticity(0.3457, 0.3585);

        var viaXyz = xy.ToXyz(42).ToChromaticity();
        var viaUv = Chromaticity.FromUvPrime(xy.ToUvPrime());

        viaXyz.X.Should().BeApproximately(xy.X, 1e-12);
        viaXyz.Y.Should().BeApproximately(xy.Y, 1e-12);
        viaUv.X.Should().BeApproximately(xy.X, 1e-12);
        viaUv.Y.Should().BeApproximately(xy.Y, 1e-12);
        xy.ToXyz().ToUvPrime().DistanceTo(xy.ToUvPrime()).Should().BeLessThan(1e-12);
    }

    [Fact]
    public void Lab_of_the_reference_white_is_100_0_0_and_round_trips()
    {
        var white = Illuminants.IccD50;
        var color = new Xyz(0.3, 0.25, 0.6);

        var labWhite = Lab.FromXyz(white, white);
        var back = Lab.FromXyz(color, white).ToXyz(white);

        labWhite.L.Should().BeApproximately(100, 1e-9);
        labWhite.A.Should().BeApproximately(0, 1e-9);
        labWhite.B.Should().BeApproximately(0, 1e-9);
        back.X.Should().BeApproximately(color.X, 1e-12);
        back.Y.Should().BeApproximately(color.Y, 1e-12);
        back.Z.Should().BeApproximately(color.Z, 1e-12);
    }

    [Fact]
    public void Lab_matches_unicolour_for_d50()
    {
        var config = new Wacton.Unicolour.Configuration(xyzConfig: Wacton.Unicolour.XyzConfiguration.D50);
        var oracle = new Wacton.Unicolour.Unicolour(config, Wacton.Unicolour.ColourSpace.Xyz, 0.3, 0.25, 0.6).Lab;
        var white = new Xyz(
            Wacton.Unicolour.XyzConfiguration.D50.WhitePoint.X,
            Wacton.Unicolour.XyzConfiguration.D50.WhitePoint.Y,
            Wacton.Unicolour.XyzConfiguration.D50.WhitePoint.Z);

        var lab = Lab.FromXyz(new Xyz(0.3, 0.25, 0.6), white);

        lab.L.Should().BeApproximately(oracle.L, 1e-6);
        lab.A.Should().BeApproximately(oracle.A, 1e-6);
        lab.B.Should().BeApproximately(oracle.B, 1e-6);
    }

    private static void AssertMatrix(Matrix3 actual, Matrix3 expected, double tolerance)
    {
        double[] a = [actual.M11, actual.M12, actual.M13, actual.M21, actual.M22, actual.M23, actual.M31, actual.M32, actual.M33];
        double[] e = [expected.M11, expected.M12, expected.M13, expected.M21, expected.M22, expected.M23, expected.M31, expected.M32, expected.M33];
        for (var i = 0; i < 9; i++)
        {
            a[i].Should().BeApproximately(e[i], tolerance, $"element {i}");
        }
    }
}
