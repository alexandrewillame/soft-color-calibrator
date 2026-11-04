using AwesomeAssertions;
using SoftCal.Core.Analysis;
using SoftCal.Core.Measurement;
using SoftCal.Windows.Patches;
using Xunit;

namespace SoftCal.Windows.Tests;

/// <summary>
/// The aiming area as the camera sees it on a 27" 1920 × 1080 monitor (0.311 mm pixels): a C920e at 9 cm streaming
/// 640 × 480 sees about 10.4 × 7.8 cm of screen (334 × 251 pixels), through a hood whose 4 cm opening shows only the
/// middle of that, and slightly defocused during a measurement (a 2 mm blur). Wherever the camera rests on the area, a
/// straight camera reads straight; a tilted one reads tilted; off the area it finds nothing.
/// </summary>
public sealed class AimingAreaTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    private static readonly byte[] Screen = Render();

    private static byte[] Render()
    {
        var bgra = new byte[Width * Height * 4];
        PatchRasterizer.Render(new PlacementTargetPatch(), Width, Height, bgra);
        return bgra;
    }

    /// <summary>The camera's 640 × 480 picture of the screen around (<paramref name="cx"/>, <paramref name="cy"/>). A
    /// tilted camera magnifies the side nearer the screen: <paramref name="yaw"/> and <paramref name="pitch"/> are the
    /// magnification differences across the picture (about 0.2 for 10°).</summary>
    private static PlacementView View(int cx, int cy, double yaw = 0, double pitch = 0, bool hood = false, int blur = 3)
    {
        const int PictureWidth = 640;
        const int PictureHeight = 480;
        const double SeenWidth = 334;
        const double SeenHeight = 251;
        const double HoodRadius = 130;
        var luma = new double[PictureWidth * PictureHeight];
        for (var y = 0; y < PictureHeight; y++)
        {
            for (var x = 0; x < PictureWidth; x++)
            {
                if (hood && Math.Sqrt(Math.Pow(x - (PictureWidth / 2.0), 2) + Math.Pow(y - (PictureHeight / 2.0), 2)) > HoodRadius)
                {
                    luma[(y * PictureWidth) + x] = 6;
                    continue;
                }

                var (u, v) = (((x + 0.5) / PictureWidth) - 0.5, ((y + 0.5) / PictureHeight) - 0.5);
                var magnification = 1 - (yaw * u) - (pitch * v);
                var (sx, sy) = (cx + (u * SeenWidth / magnification), cy + (v * SeenHeight / magnification));
                var (sum, n) = (0.0, 0);
                for (var dy = -blur; dy <= blur; dy++)
                {
                    for (var dx = -blur; dx <= blur; dx++)
                    {
                        var px = (int)Math.Clamp(sx + dx, 0, Width - 1);
                        var py = (int)Math.Clamp(sy + dy, 0, Height - 1);
                        var o = ((py * Width) + px) * 4;
                        sum += (0.299 * Screen[o + 2]) + (0.587 * Screen[o + 1]) + (0.114 * Screen[o]);
                        n++;
                    }
                }

                luma[(y * PictureWidth) + x] = sum / n;
            }
        }

        return PlacementCheck.Analyze(PictureWidth, PictureHeight, luma);
    }

    [Theory]
    [InlineData(960, 540, false)]
    [InlineData(820, 420, false)]
    [InlineData(1100, 660, false)]
    [InlineData(960, 540, true)]
    [InlineData(800, 380, true)]
    [InlineData(1140, 700, true)]
    public void A_straight_camera_reads_straight_anywhere_on_the_area(int cx, int cy, bool hood)
    {
        var view = View(cx, cy, hood: hood);

        view.Found.Should().BeTrue();
        view.Square.Should().BeTrue("evenness {0}", view.Evenness);
    }

    [Theory]
    [InlineData(0.3, 0)]
    [InlineData(0, 0.3)]
    public void A_camera_tilted_by_fifteen_degrees_reads_tilted_even_through_the_hood(double yaw, double pitch)
    {
        var view = View(960, 540, yaw, pitch, hood: true);

        view.Found.Should().BeTrue();
        view.Square.Should().BeFalse("evenness {0}", view.Evenness);
    }

    /// <summary>A positive yaw magnifies the picture's left: the camera's left side is nearer the screen and it points
    /// to the right, where the level's dot must go (and likewise down for a positive pitch).</summary>
    [Theory]
    [InlineData(0.3, 0, 1, 0)]
    [InlineData(-0.3, 0, -1, 0)]
    [InlineData(0, 0.3, 0, 1)]
    [InlineData(0, -0.3, 0, -1)]
    public void The_level_dot_goes_to_the_side_farther_from_the_screen(double yaw, double pitch, int x, int y)
    {
        foreach (var hood in new[] { false, true })
        {
            var dot = View(960, 540, yaw, pitch, hood).Level!.Value;

            Math.Sign(Math.Round(dot.X)).Should().Be(x, "x {0} (hood {1})", dot.X, hood);
            Math.Sign(Math.Round(dot.Y)).Should().Be(y, "y {0} (hood {1})", dot.Y, hood);
        }
    }

    [Fact]
    public void A_camera_tilted_by_five_degrees_reads_tilted_in_the_open()
    {
        View(960, 540, yaw: 0.1).Square.Should().BeFalse();
    }

    [Fact]
    public void Off_the_area_the_camera_finds_nothing()
    {
        View(300, 540).Found.Should().BeFalse();
    }

    [Fact]
    public void The_area_has_no_mark_at_its_center()
    {
        // The middle of the screen looks like any other square: the same pixels one square away.
        var rect = PatchRasterizer.Rect(PatchRasterizer.DefaultLayout, Width, Height);
        var step = PatchRasterizer.GridStep(rect);
        for (var dy = -20; dy <= 20; dy++)
        {
            for (var dx = -20; dx <= 20; dx++)
            {
                var (x, y) = ((Width / 2) + dx, (Height / 2) + dy);
                PatchRasterizer.TargetAt(rect, x, y).Should().Be(PatchRasterizer.TargetAt(rect, x - step, y - step));
            }
        }
    }

    [Fact]
    public void The_squares_are_about_five_millimetres_on_a_27_inch_monitor()
    {
        var step = PatchRasterizer.GridStep(PatchRasterizer.Rect(PatchRasterizer.DefaultLayout, Width, Height));

        (step * 0.311).Should().BeInRange(4, 6, "millimetres per square at 0.311 mm per pixel");
    }
}
