namespace SoftCal.Core.Analysis;

/// <summary>What the camera's picture of the aiming area says about where the camera is.</summary>
/// <param name="Found">The area's grid is in view in both directions: the camera rests on the area.</param>
/// <param name="Evenness">How equal the grid's squares look across the picture (1 = equal): the smaller square over the
/// larger, between the picture's left and right halves and between its top and bottom halves, the worse of the two. A
/// camera that looks straight at the screen sees equal squares; a tilted one sees the grid as trapezoids, larger on the
/// side nearer the screen. Null when the halves could not be compared.</param>
/// <param name="Sideways">Which way the camera is tilted sideways: how much larger the squares are on the picture's right
/// than on its left (natural log; 0.05 ≈ 5 % larger on the right, whose side of the camera is then nearer the screen;
/// negative: the left). Null when not judged.</param>
/// <param name="UpDown">The same between the picture's bottom and its top (positive: larger at the bottom).</param>
public sealed record PlacementView(bool Found, double? Evenness, double? Sideways = null, double? UpDown = null)
{
    /// <summary>Squares equal within 3 %: a camera 8–10 cm from the screen, tilted by more than a few degrees, shows more.</summary>
    public const double StraightLimit = 0.97;

    /// <summary>Straight at the screen: the squares look the same size everywhere in the picture.</summary>
    public bool? Square => Evenness is { } e ? e >= StraightLimit : null;

    /// <summary>
    /// Where a level drawn over the picture puts its dot, in units of its circle's radius from the picture's middle (x to
    /// the right, y down): toward the side that is farther from the screen, which is where the camera points, so pressing
    /// that side of the camera toward the screen brings the dot back. It is inside the circle exactly when the camera is
    /// straight (<see cref="Square"/>). Null when straightness was not judged.
    /// </summary>
    public (double X, double Y)? Level
    {
        get
        {
            if (Evenness is null)
            {
                return null;
            }

            // The larger of the two tilts sets the distance (the check's own measure), the two together the direction.
            var (sideways, upDown) = (Sideways ?? 0, UpDown ?? 0);
            var length = Math.Sqrt((sideways * sideways) + (upDown * upDown));
            if (length < 1e-12)
            {
                return (0, 0);
            }

            var distance = Math.Max(Math.Abs(sideways), Math.Abs(upDown)) / -Math.Log(StraightLimit);
            return (-sideways / length * distance, -upDown / length * distance);
        }
    }
}

/// <summary>
/// Reads the aiming area in a camera picture. The area is a fine, even grid with no center mark, so the camera may rest
/// anywhere on it; what matters is that it looks straight at the screen. A tilted camera sees the grid's squares as
/// trapezoids: larger on the side nearer the screen, smaller on the far side. So the check measures the squares' size
/// separately in the picture's left and right halves (a sideways tilt) and its top and bottom halves (an up or down
/// tilt), each from the spacing of many lines at once, and compares them. Picture coordinates only: no direction words
/// are needed.
/// </summary>
public static class PlacementCheck
{
    /// <summary>A grid is seen when its lines repeat this clearly (the autocorrelation at their spacing; 1 = perfectly).</summary>
    private const double MinimumClarity = 0.3;

    /// <summary>Within a half of a picture where the grid is already seen, a weaker repeat will do: a strongly tilted
    /// camera sees the squares grow across each half, which blurs the repeat.</summary>
    private const double MinimumClarityInHalf = 0.12;

    public static PlacementView Analyze(int width, int height, IReadOnlyList<double> luma)
    {
        ArgumentNullException.ThrowIfNull(luma);
        if (width < 32 || height < 32 || luma.Count < width * height)
        {
            return new PlacementView(false, null);
        }

        // Only the lit part of the picture counts: the hood's walls and the black around the area carry no lines.
        var (left, right, top, bottom) = Lit(luma, width, height);
        if (right - left < 32 || bottom - top < 32)
        {
            return new PlacementView(false, null);
        }

        var (w, h) = (right - left, bottom - top);

        // Is the grid there, and how far apart are its lines? The whole lit part first; then only its middle half,
        // where a strongly tilted camera's squares change size less.
        (double Across, double Down)? Grid(int x0, int x1, int y0, int y1)
        {
            var (c, r) = Profiles(luma, width, x0, x1, y0, y1);
            return Spacing(c, Math.Max(4, (x1 - x0) / 12), MinimumClarity) is { } a && Spacing(r, Math.Max(4, (y1 - y0) / 12), MinimumClarity) is { } d
                ? (a, d)
                : null;
        }

        if ((Grid(left, right, top, bottom) ?? Grid(left + (w / 4), right - (w / 4), top + (h / 4), bottom - (h / 4))) is not { } spacing)
        {
            return new PlacementView(false, null);
        }

        // Then the squares' size in each half, the shading taken out over about three line spacings. Where the halves
        // blur (a strong tilt), the two middle quarters.
        var (smoothAcross, smoothDown) = ((int)Math.Ceiling(1.5 * spacing.Across), (int)Math.Ceiling(1.5 * spacing.Down));
        double? SquareIn(int x0, int x1, int y0, int y1)
        {
            var (c, r) = Profiles(luma, width, x0, x1, y0, y1);
            return Spacing(c, smoothAcross, MinimumClarityInHalf) is { } a && Spacing(r, smoothDown, MinimumClarityInHalf) is { } d ? (a + d) / 2 : null;
        }

        // Each comparison: the smaller square over the larger, and how much larger the second is (natural log).
        static (double Ratio, double Larger)? Compare(double? first, double? second) =>
            first is { } a && second is { } b ? (Math.Min(a, b) / Math.Max(a, b), Math.Log(b / a)) : null;
        var (cx, cy, w4, h4) = (left + (w / 2), top + (h / 2), w / 4, h / 4);
        var sideways = Compare(SquareIn(left, cx, top, bottom), SquareIn(cx, right, top, bottom))
            ?? Compare(SquareIn(left + w4, cx, top + h4, bottom - h4), SquareIn(cx, right - w4, top + h4, bottom - h4));
        var upDown = Compare(SquareIn(left, right, top, cy), SquareIn(left, right, cy, bottom))
            ?? Compare(SquareIn(left + w4, right - w4, top + h4, cy), SquareIn(left + w4, right - w4, cy, bottom - h4));
        double? evenness = (sideways, upDown) switch
        {
            ({ } s, { } u) => Math.Min(s.Ratio, u.Ratio),
            ({ } s, null) => s.Ratio,
            (null, { } u) => u.Ratio,
            _ => null,
        };
        return new PlacementView(true, evenness, sideways?.Larger, upDown?.Larger);
    }

    /// <summary>The box around the picture's lit columns and rows: those whose mean stands above the darkest by a quarter
    /// of the way to the typical lit level; the whole picture when it is one even level.</summary>
    private static (int Left, int Right, int Top, int Bottom) Lit(IReadOnlyList<double> luma, int width, int height)
    {
        var (columns, rows) = Profiles(luma, width, 0, width, 0, height);
        (int From, int To)? Span(double[] sums)
        {
            var sorted = sums.Order().ToArray();
            var (dark, typical) = (sorted[0], sorted[(sorted.Length * 3) / 4]);
            if (typical - dark <= 1e-9 * Math.Max(1, typical))
            {
                return null;
            }

            var threshold = dark + ((typical - dark) / 4);
            var from = Array.FindIndex(sums, v => v > threshold);
            var to = Array.FindLastIndex(sums, v => v > threshold);
            return from < 0 ? null : (from, to + 1);
        }

        // A picture of one even level has nothing lit apart from the rest: all of it is the box.
        var across = Span(columns) ?? (0, width);
        var down = Span(rows) ?? (0, height);
        return (across.From, across.To, down.From, down.To);
    }

    /// <summary>A region's column sums (left to right) and row sums (top to bottom).</summary>
    private static (double[] Columns, double[] Rows) Profiles(IReadOnlyList<double> luma, int stride, int x0, int x1, int y0, int y1)
    {
        var columns = new double[x1 - x0];
        var rows = new double[y1 - y0];
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var v = luma[(y * stride) + x];
                columns[x - x0] += v;
                rows[y - y0] += v;
            }
        }

        return (columns, rows);
    }

    /// <summary>
    /// The spacing of a profile's repeating lines, to a fraction of a pixel: the first clear peak of its autocorrelation
    /// (refined by a parabola), after slow changes such as the hood's shading are taken out by subtracting a moving
    /// average <paramref name="half"/> pixels either side. Null when the lines do not repeat at least as clearly as
    /// <paramref name="clarity"/>, or fewer than three fit in the profile.
    /// </summary>
    private static double? Spacing(double[] profile, int half, double clarity)
    {
        var n = profile.Length;
        if (n < 24)
        {
            return null;
        }

        var detail = new double[n];
        for (var i = 0; i < n; i++)
        {
            var (from, to) = (Math.Max(0, i - half), Math.Min(n - 1, i + half));
            var sum = 0.0;
            for (var j = from; j <= to; j++)
            {
                sum += profile[j];
            }

            detail[i] = profile[i] - (sum / (to - from + 1));
        }

        var energy = detail.Sum(d => d * d);
        if (energy <= 1e-9)
        {
            return null;
        }

        // Autocorrelation, each lag scaled to the full length so the repeats keep their height.
        var maxLag = n / 3;
        var r = new double[maxLag + 2];
        for (var lag = 0; lag <= maxLag + 1; lag++)
        {
            var sum = 0.0;
            for (var i = 0; i + lag < n; i++)
            {
                sum += detail[i] * detail[i + lag];
            }

            r[lag] = sum * n / (n - lag) / energy;
        }

        // The first peak after the autocorrelation has dipped below zero: one line spacing.
        var lagAt = 1;
        while (lagAt <= maxLag && r[lagAt] >= 0)
        {
            lagAt++;
        }

        for (var lag = lagAt + 1; lag <= maxLag; lag++)
        {
            if (r[lag] >= r[lag - 1] && r[lag] >= r[lag + 1] && r[lag] >= clarity)
            {
                var curvature = r[lag - 1] - (2 * r[lag]) + r[lag + 1];
                var offset = curvature < 0 ? 0.5 * (r[lag - 1] - r[lag + 1]) / curvature : 0;
                return lag + Math.Clamp(offset, -0.5, 0.5);
            }
        }

        return null;
    }
}
