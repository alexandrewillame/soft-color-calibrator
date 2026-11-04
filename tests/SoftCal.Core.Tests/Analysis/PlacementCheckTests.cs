using AwesomeAssertions;
using SoftCal.Core.Analysis;
using Xunit;

namespace SoftCal.Core.Tests.Analysis;

/// <summary>
/// The straightness check on synthetic 640 × 480 camera pictures of the aiming grid (30-pixel squares). A tilted camera
/// magnifies the side nearer the screen: <c>yaw</c> and <c>pitch</c> are the magnification differences across the
/// picture, about 0.2 for a 10° tilt of a C920e 9 cm from the screen (its 640 × 480 view is about 10.4 cm wide).
/// </summary>
public sealed class PlacementCheckTests(ITestOutputHelper output)
{
    private const int Width = 640;
    private const int Height = 480;
    private const double Period = 30;

    /// <param name="tube">Radius of the hood's opening in pixels: outside it the picture is dark (0 = no hood).</param>
    /// <param name="verticalLines">False leaves only the horizontal lines.</param>
    private static double[] Picture(double yaw = 0, double pitch = 0, double tube = 0, bool verticalLines = true)
    {
        static bool On(double s) => ((s % Period) + Period) % Period < 6;
        var luma = new double[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (tube > 0 && Math.Sqrt(Math.Pow(x - (Width / 2.0), 2) + Math.Pow(y - (Height / 2.0), 2)) > tube)
                {
                    luma[(y * Width) + x] = 10;
                    continue;
                }

                var (u, v) = (((x + 0.5) / Width) - 0.5, ((y + 0.5) / Height) - 0.5);
                var magnification = 1 - (yaw * u) - (pitch * v);
                var (sx, sy) = (u * Width / magnification, v * Height / magnification);
                luma[(y * Width) + x] = (verticalLines && On(sx)) || On(sy) ? 150 : 36;
            }
        }

        return luma;
    }

    [Fact]
    public void A_straight_camera_sees_equal_squares()
    {
        var view = PlacementCheck.Analyze(Width, Height, Picture());

        view.Found.Should().BeTrue();
        view.Square.Should().BeTrue();
        view.Evenness.Should().BeGreaterThan(0.99);
    }

    [Fact]
    public void Through_the_hood_a_straight_camera_still_reads_straight()
    {
        var view = PlacementCheck.Analyze(Width, Height, Picture(tube: 130));

        view.Found.Should().BeTrue();
        view.Square.Should().BeTrue("evenness {0}", view.Evenness);
    }

    [Theory]
    [InlineData(0.2, 0)]
    [InlineData(-0.2, 0)]
    [InlineData(0, 0.2)]
    [InlineData(0, -0.2)]
    public void A_camera_tilted_by_ten_degrees_sees_trapezoids(double yaw, double pitch)
    {
        var view = PlacementCheck.Analyze(Width, Height, Picture(yaw, pitch));

        view.Found.Should().BeTrue();
        view.Square.Should().BeFalse("evenness {0}", view.Evenness);
    }

    [Fact]
    public void Through_the_hood_a_clear_tilt_still_shows()
    {
        var view = PlacementCheck.Analyze(Width, Height, Picture(yaw: 0.3, tube: 130));

        view.Found.Should().BeTrue();
        view.Square.Should().BeFalse("evenness {0}", view.Evenness);
    }

    /// <summary>In these pictures a positive yaw magnifies the left (that side of the camera is nearer the screen) and
    /// a positive pitch the top.</summary>
    [Theory]
    [InlineData(0.2, 0, -1, 0)]
    [InlineData(-0.2, 0, 1, 0)]
    [InlineData(0, 0.2, 0, -1)]
    [InlineData(0, -0.2, 0, 1)]
    public void The_check_says_which_side_is_nearer_the_screen(double yaw, double pitch, int sideways, int upDown)
    {
        foreach (var tube in new[] { 0.0, 130 })
        {
            var view = PlacementCheck.Analyze(Width, Height, Picture(yaw, pitch, tube));

            Math.Sign(Math.Round(view.Sideways!.Value, 2)).Should().Be(sideways, "sideways {0} with a hood of {1}", view.Sideways, tube);
            Math.Sign(Math.Round(view.UpDown!.Value, 2)).Should().Be(upDown, "up-down {0} with a hood of {1}", view.UpDown, tube);
        }
    }

    [Theory]
    [InlineData(0.06, 0, -1.97, 0, false)]
    [InlineData(-0.02, 0.01, 0.59, -0.29, true)]
    [InlineData(0, -0.04, 0, 1.31, false)]
    [InlineData(0, 0, 0, 0, true)]
    public void The_level_dot_goes_where_the_camera_points_and_is_in_the_circle_when_straight(
        double sideways, double upDown, double x, double y, bool straight)
    {
        var evenness = Math.Exp(-Math.Max(Math.Abs(sideways), Math.Abs(upDown)));
        var view = new PlacementView(true, evenness, sideways, upDown);

        var dot = view.Level!.Value;
        dot.X.Should().BeApproximately(x, 0.01);
        dot.Y.Should().BeApproximately(y, 0.01);
        view.Square.Should().Be(straight);
        (Math.Sqrt((dot.X * dot.X) + (dot.Y * dot.Y)) <= 1).Should().Be(straight, "the dot is in the circle exactly when straight");
    }

    [Fact]
    public void No_dot_when_straightness_was_not_judged()
    {
        new PlacementView(true, null).Level.Should().BeNull();
        new PlacementView(false, null).Level.Should().BeNull();
    }

    [Fact]
    public void A_degree_or_so_counts_as_straight()
    {
        PlacementCheck.Analyze(Width, Height, Picture(yaw: 0.02)).Square.Should().BeTrue();
    }

    [Fact]
    public void Evenness_falls_as_the_tilt_grows()
    {
        double Evenness(double yaw, double tube) => PlacementCheck.Analyze(Width, Height, Picture(yaw, tube: tube)).Evenness!.Value;
        var values = new[] { 0.0, 0.05, 0.1, 0.15, 0.2, 0.3 }.Select(yaw => (Yaw: yaw, Open: Evenness(yaw, 0), Hood: Evenness(yaw, 130))).ToList();
        foreach (var (yaw, open, hood) in values)
        {
            output.WriteLine($"yaw {yaw:0.00} (about {yaw * 50:0}°): evenness {open:0.000} in the open, {hood:0.000} through the hood");
        }

        values.Select(v => v.Open).Should().BeInDescendingOrder();
        values.Select(v => v.Hood).Should().BeInDescendingOrder();
    }

    [Fact]
    public void A_blank_picture_finds_nothing()
    {
        PlacementCheck.Analyze(Width, Height, Enumerable.Repeat(20.0, Width * Height).ToArray()).Found.Should().BeFalse();
    }

    [Fact]
    public void Lines_in_one_direction_only_are_not_the_area()
    {
        PlacementCheck.Analyze(Width, Height, Picture(verticalLines: false)).Found.Should().BeFalse();
    }
}
