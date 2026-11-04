using SoftCal.Core.Measurement;

namespace SoftCal.Windows.Patches;

/// <summary>A patch's pixel rectangle on the display.</summary>
public readonly record struct PatchRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// Renders patches to 8-bit BGRA pixels exactly as they must reach the screen. Halftone phases are tied to the display's
/// own pixel grid (column 0, row 0 of the monitor), so a pattern never depends on where the patch sits.
/// </summary>
public static class PatchRasterizer
{
    /// <summary>The 4×4 Bayer threshold matrix; a pixel is "on" when its threshold is below the on-count (0–16).</summary>
    public static ReadOnlySpan<byte> Bayer4x4 => [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    /// <summary>The default close-up layout: a square of 60 % of the display height, centered, on black.</summary>
    public static PatchLayout DefaultLayout { get; } = new(0.5, 0.5, 0.6, DeviceRgb.Black);

    /// <summary>
    /// The square patch rectangle: <see cref="PatchLayout.Size"/> is its side as a fraction of the display height, and
    /// its center sits at (<see cref="PatchLayout.CenterX"/>·width, <see cref="PatchLayout.CenterY"/>·height). A size of
    /// width/height or more fills the display.
    /// </summary>
    public static PatchRect Rect(PatchLayout layout, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var side = (int)Math.Round(layout.Size * height);
        var x0 = (int)Math.Round((layout.CenterX * width) - (side / 2.0));
        var y0 = (int)Math.Round((layout.CenterY * height) - (side / 2.0));
        var x1 = Math.Clamp(x0 + side, 0, width);
        var y1 = Math.Clamp(y0 + side, 0, height);
        x0 = Math.Clamp(x0, 0, width);
        y0 = Math.Clamp(y0, 0, height);
        return new PatchRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Whether the halftone shows its "on" color at display pixel (x, y).</summary>
    public static bool IsOn(HalftonePatch halftone, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(halftone);
        return halftone.Geometry switch
        {
            HalftoneGeometry.Checkerboard => ((x + y) & 1) == 0,
            HalftoneGeometry.HorizontalLines => (y & 1) == 0,
            HalftoneGeometry.VerticalLines => (x & 1) == 0,
            HalftoneGeometry.Bayer4x4 => Bayer4x4[((y & 3) << 2) | (x & 3)] < halftone.OnSixteenths,
            _ => throw new NotSupportedException($"Unsupported geometry {halftone.Geometry}."),
        };
    }

    /// <summary>The color of display pixel (x, y).</summary>
    public static DeviceRgb ColorAt(PatchSpec patch, PatchRect rect, PatchLayout layout, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(layout);
        if (patch is PlacementTargetPatch)
        {
            return TargetAt(rect, x, y);
        }

        if (!rect.Contains(x, y))
        {
            return layout.Surround;
        }

        return patch switch
        {
            SolidPatch solid => solid.Color,
            HalftonePatch halftone => IsOn(halftone, x, y) ? halftone.On : halftone.Off,
            _ => throw new NotSupportedException($"Unsupported patch type {patch.GetType().Name}."),
        };
    }

    /// <summary>The aiming area inside the patch square: the square less an eighth of its side all round, so the
    /// camera's reading region stays on the patch wherever the camera rests inside the area.</summary>
    public static PatchRect AimingArea(PatchRect rect)
    {
        var margin = Math.Max(rect.Height, 60) / 8;
        return new PatchRect(rect.X + margin, rect.Y + margin, Math.Max(0, rect.Width - (2 * margin)), Math.Max(0, rect.Height - (2 * margin)));
    }

    /// <summary>
    /// The placement target: the aiming area (<see cref="AimingArea"/>), a fine, even grid of gray lines on dark gray
    /// that shows as a textured square on black. There is no mark at its center: the camera may rest anywhere inside, and
    /// nothing invites tilting it toward one point. The squares are about 5 mm on a 27" monitor (a fortieth of the patch
    /// height), so a camera seeing only a few centimetres through its hood still sees several in each direction; a
    /// tilted camera sees them as trapezoids. The lines (a fifth of a square) stay visible through the measurement's
    /// defocus.
    /// </summary>
    public static DeviceRgb TargetAt(PatchRect rect, int x, int y)
    {
        var area = AimingArea(rect);
        if (!area.Contains(x, y))
        {
            return DeviceRgb.Black;
        }

        // The outline is just the grid's outer lines: a brighter or thicker one would stand out from the rest and upset
        // the spacing the straightness check reads when the camera's view reaches the edge.
        var (dx, dy) = (x - area.X, y - area.Y);
        var step = GridStep(rect);
        var thick = Math.Max(2, step / 5);
        var onLine = dx % step < thick || dy % step < thick || area.Width - 1 - dx < thick || area.Height - 1 - dy < thick;
        return onLine ? DeviceRgb.Gray(150) : DeviceRgb.Gray(36);
    }

    /// <summary>The aiming grid's square, in pixels: a fortieth of the patch height (16 px, about 5 mm, on a 27"
    /// 1920 × 1080 monitor).</summary>
    public static int GridStep(PatchRect rect) => Math.Max(8, Math.Max(rect.Height, 60) / 40);

    /// <summary>Renders the whole display into <paramref name="bgra"/> (row pitch = width × 4).</summary>
    public static PatchRect Render(PatchSpec patch, int width, int height, Span<byte> bgra, PatchLayout? defaultLayout = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("The pixel buffer is too small.", nameof(bgra));
        }

        var layout = patch.Layout ?? defaultLayout ?? DefaultLayout;
        var rect = Rect(layout, width, height);
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                var c = ColorAt(patch, rect, layout, x, y);
                var o = x * 4;
                row[o] = c.B;
                row[o + 1] = c.G;
                row[o + 2] = c.R;
                row[o + 3] = 255;
            }
        }

        return rect;
    }

    /// <summary>The exact fraction of "on" pixels a halftone has over a rectangle (1-px patterns over odd sizes are
    /// not exactly 50 %).</summary>
    public static double OnFraction(HalftonePatch halftone, PatchRect rect)
    {
        ArgumentNullException.ThrowIfNull(halftone);
        long on = 0;
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            for (var x = rect.X; x < rect.X + rect.Width; x++)
            {
                if (IsOn(halftone, x, y))
                {
                    on++;
                }
            }
        }

        return rect.Width * rect.Height == 0 ? 0 : (double)on / (rect.Width * rect.Height);
    }
}
