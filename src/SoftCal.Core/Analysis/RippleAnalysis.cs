namespace SoftCal.Core.Analysis;

/// <summary>Spatial ripple of a frame-averaged image of one patch.</summary>
public sealed record RippleMeasurement(double Mean, double RelativeRms, int Width, int Height);

/// <summary>The halftone's ripple once the solid patch's own texture and noise are taken out, and the bias it causes.</summary>
public sealed record RippleResult(
    double HalftoneRelativeRms,
    double SolidRelativeRms,
    double ExcessRelativeRms,
    double CameraGamma,
    double LinearRelativeRms,
    double NullBias)
{
    /// <summary>The defocus criterion: residual ripple (in the camera's encoded units) at or below 2.5 %.</summary>
    public const double RippleLimit = 0.025;

    /// <summary>The gate: bias of a halftone null below 0.1 % of the light.</summary>
    public const double BiasLimit = 0.001;

    public bool WithinRippleLimit => ExcessRelativeRms <= RippleLimit;

    public bool WithinBiasLimit => Math.Abs(NullBias) < BiasLimit;
}

/// <summary>
/// The null method needs the camera to integrate a halftone into its mean light. Any ripple left in the (defocused) image
/// is averaged after the camera's nonlinear encoding, which biases the reading: for a response <c>c = L^(1/g)</c> and a
/// relative ripple <c>a</c> in linear light, the mean code corresponds to light lower by about <c>½·(1 − 1/g)·a²</c>.
/// </summary>
public static class RippleAnalysis
{
    /// <summary>Relative RMS of an image (row-major) after removing a 2-D quadratic trend (vignetting, gradients).</summary>
    public static RippleMeasurement Measure(IReadOnlyList<double> pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Count < width * height || width < 3 || height < 3)
        {
            throw new ArgumentException("The image is too small or does not match its size.", nameof(pixels));
        }

        // Normal equations for z = c0 + c1·x + c2·y + c3·x² + c4·x·y + c5·y² (x, y in [-1, 1]).
        var ata = new double[6, 6];
        var atb = new double[6];
        Span<double> basis = stackalloc double[6];
        double sum = 0;
        for (var y = 0; y < height; y++)
        {
            var v = (2.0 * y / (height - 1)) - 1;
            for (var x = 0; x < width; x++)
            {
                var u = (2.0 * x / (width - 1)) - 1;
                var z = pixels[(y * width) + x];
                sum += z;
                Fill(basis, u, v);
                for (var i = 0; i < 6; i++)
                {
                    atb[i] += basis[i] * z;
                    for (var j = 0; j < 6; j++)
                    {
                        ata[i, j] += basis[i] * basis[j];
                    }
                }
            }
        }

        var c = SolveSymmetric(ata, atb);
        var mean = sum / (width * height);
        double rss = 0;
        for (var y = 0; y < height; y++)
        {
            var v = (2.0 * y / (height - 1)) - 1;
            for (var x = 0; x < width; x++)
            {
                var u = (2.0 * x / (width - 1)) - 1;
                Fill(basis, u, v);
                var fit = 0.0;
                for (var i = 0; i < 6; i++)
                {
                    fit += c[i] * basis[i];
                }

                var r = pixels[(y * width) + x] - fit;
                rss += r * r;
            }
        }

        var rms = Math.Sqrt(rss / ((width * height) - 6));
        return new RippleMeasurement(mean, mean > 0 ? rms / mean : double.PositiveInfinity, width, height);
    }

    /// <summary>
    /// Combines a halftone image with a solid image of similar brightness: the solid's ripple (sensor noise left after
    /// averaging, panel texture) is removed in quadrature, the remainder is converted to linear light with the
    /// camera's local gamma, and the resulting null bias is computed.
    /// </summary>
    public static RippleResult Evaluate(RippleMeasurement halftone, RippleMeasurement solid, double cameraGamma = 2.2)
    {
        ArgumentNullException.ThrowIfNull(halftone);
        ArgumentNullException.ThrowIfNull(solid);
        var excess = Math.Sqrt(Math.Max(0, (halftone.RelativeRms * halftone.RelativeRms) - (solid.RelativeRms * solid.RelativeRms)));
        var linear = excess * cameraGamma;
        return new RippleResult(halftone.RelativeRms, solid.RelativeRms, excess, cameraGamma, linear, NullBias(linear, cameraGamma));
    }

    /// <summary>
    /// Relative bias of a halftone null for a relative linear ripple <paramref name="linearRms"/> seen through a camera
    /// whose code is proportional to light^(1/<paramref name="cameraGamma"/>): the matched solid is darker than the
    /// halftone's true mean by <c>½·(1 − 1/g)·a²</c> (negative = reads dark).
    /// </summary>
    public static double NullBias(double linearRms, double cameraGamma) =>
        -0.5 * (1 - (1 / cameraGamma)) * linearRms * linearRms;

    private static void Fill(Span<double> basis, double u, double v)
    {
        basis[0] = 1;
        basis[1] = u;
        basis[2] = v;
        basis[3] = u * u;
        basis[4] = u * v;
        basis[5] = v * v;
    }

    private static double[] SolveSymmetric(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = new double[n, n + 1];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                m[i, j] = a[i, j];
            }

            m[i, n] = b[i];
        }

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
            {
                if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = row;
                }
            }

            for (var j = 0; j <= n; j++)
            {
                (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            }

            var p = m[col, col];
            if (Math.Abs(p) < 1e-300)
            {
                continue;
            }

            for (var row = 0; row < n; row++)
            {
                if (row == col)
                {
                    continue;
                }

                var f = m[row, col] / p;
                for (var j = col; j <= n; j++)
                {
                    m[row, j] -= f * m[col, j];
                }
            }
        }

        var x = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = Math.Abs(m[i, i]) < 1e-300 ? 0 : m[i, n] / m[i, i];
        }

        return x;
    }
}
