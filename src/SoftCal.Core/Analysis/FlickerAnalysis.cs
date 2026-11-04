namespace SoftCal.Core.Analysis;

/// <summary>Result of the rolling-shutter flicker analysis.</summary>
public sealed record FlickerResult(
    bool Detected,
    double PeakCyclesPerRow,
    double PeakPeriodRows,
    double ModulationDepth,
    double NoiseFloorDepth,
    double? FrequencyHz,
    double? LineTimeSeconds,
    int Frames,
    int Rows)
{
    /// <summary>A peak counts as flicker when it stands well above the spectrum's noise floor.</summary>
    public const double DetectionRatio = 6;
}

/// <summary>
/// Backlight PWM shows up as horizontal bands in a rolling-shutter frame: each row starts its exposure one line time
/// after the previous one, so a light that flickers at f Hz produces bands with a period of 1/(f·line time) rows. The
/// mean of every row of a uniform patch is detrended and Fourier-analysed; power spectra are averaged over frames so
/// bands that drift from frame to frame still add up.
/// </summary>
public static class FlickerAnalysis
{
    /// <param name="frames">Per-frame row means (top to bottom), one value per row of a uniform patch.</param>
    /// <param name="lineTimeSeconds">Rolling-shutter line time, when known, to convert cycles/row to Hz.</param>
    /// <param name="minPeriodRows">Shortest band period considered (shorter ones are sensor noise and texture).</param>
    public static FlickerResult Analyze(IReadOnlyList<IReadOnlyList<double>> frames, double? lineTimeSeconds, double minPeriodRows = 4)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0 || frames[0].Count < 16)
        {
            throw new ArgumentException("Need frames with at least 16 rows.", nameof(frames));
        }

        var rows = frames.Min(f => f.Count);
        var bins = rows / 2;
        var power = new double[bins + 1];
        foreach (var frame in frames)
        {
            var detrended = Detrend(frame.Take(rows).ToArray());
            var spectrum = AmplitudeSpectrum(detrended);
            for (var k = 0; k <= bins; k++)
            {
                power[k] += spectrum[k] * spectrum[k];
            }
        }

        for (var k = 0; k <= bins; k++)
        {
            power[k] /= frames.Count;
        }

        // Ignore the lowest bins (shading left after detrending, slow drift) and periods shorter than minPeriodRows.
        var firstBin = 2;
        var lastBin = Math.Min(bins, (int)Math.Floor(rows / minPeriodRows));
        if (lastBin <= firstBin + 2)
        {
            return new FlickerResult(false, 0, 0, 0, 0, null, lineTimeSeconds, frames.Count, rows);
        }

        // Banding is a narrow peak: a strict local maximum, not the edge of a falling low-frequency spectrum.
        var peak = -1;
        for (var k = firstBin + 1; k < lastBin; k++)
        {
            if (power[k] > power[k - 1] && power[k] > power[k + 1] && (peak < 0 || power[k] > power[peak]))
            {
                peak = k;
            }
        }

        var floor = Median([.. Enumerable.Range(firstBin, lastBin - firstBin + 1).Select(k => power[k])]);
        var floorDepth = Math.Sqrt(floor) * 4 / rows;
        if (peak < 0)
        {
            return new FlickerResult(false, 0, 0, 0, floorDepth, null, lineTimeSeconds, frames.Count, rows);
        }

        // Parabolic interpolation of the peak position, in bins.
        double a = power[peak - 1], b = power[peak], c = power[peak + 1];
        var denominator = a - (2 * b) + c;
        var delta = Math.Abs(denominator) > 1e-30 ? 0.5 * (a - c) / denominator : 0;
        var cyclesPerRow = (peak + delta) / rows;

        // Amplitude of a sinusoid of relative amplitude A over N samples with a Hann window: |X| = A·N/4.
        var depth = Math.Sqrt(power[peak]) * 4 / rows;

        // Prominent: far above the floor and above the bins two away (a Hann window spreads a tone over ±1 bin).
        var shoulder = Math.Max(power[Math.Max(firstBin, peak - 2)], power[Math.Min(lastBin, peak + 2)]);
        var detected = power[peak] > floor * FlickerResult.DetectionRatio * FlickerResult.DetectionRatio
            && power[peak] > 4 * shoulder
            && depth > 0.002;
        double? hz = lineTimeSeconds is > 0 ? cyclesPerRow / lineTimeSeconds.Value : null;
        return new FlickerResult(detected, cyclesPerRow, cyclesPerRow > 0 ? 1 / cyclesPerRow : 0, depth, floorDepth, hz, lineTimeSeconds, frames.Count, rows);
    }

    /// <summary>
    /// Estimates the rolling-shutter line time from frames caught mid-transition (dark → bright): the rows of such a
    /// frame ramp from dark to bright over exposure/line-time rows. Returns null when no frame shows a clean ramp.
    /// </summary>
    public static double? LineTimeFromTransition(IReadOnlyList<IReadOnlyList<double>> frames, double exposureSeconds, double darkLevel, double brightLevel)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var span = brightLevel - darkLevel;
        if (span <= 0 || exposureSeconds <= 0)
        {
            return null;
        }

        var estimates = new List<double>();
        foreach (var frame in frames)
        {
            var fraction = frame.Select(v => (v - darkLevel) / span).ToArray();

            // A frame caught mid-transition is dark at one end and bright at the other (either way up, depending on
            // how the camera is mounted); the rows strictly inside the ramp (10–90 %) are fitted by a straight line.
            var inside = fraction.Select((f, i) => (f, i)).Where(p => p.f is > 0.1 and < 0.9).ToList();
            if (inside.Count < 8 || fraction.Min() > 0.1 || fraction.Max() < 0.9)
            {
                continue;
            }

            var mx = inside.Average(p => p.i);
            var my = inside.Average(p => p.f);
            var sxx = inside.Sum(p => (p.i - mx) * (p.i - mx));
            var sxy = inside.Sum(p => (p.i - mx) * (p.f - my));
            if (sxx <= 0)
            {
                continue;
            }

            var slope = Math.Abs(sxy / sxx); // fraction per row = line time / exposure
            if (slope > 0)
            {
                estimates.Add(slope * exposureSeconds);
            }
        }

        return estimates.Count == 0 ? null : Median(estimates);
    }

    /// <summary>Removes a low-order polynomial trend (vignetting, defocus shading), then divides by the mean (relative
    /// modulation). A quartic leaves bands of a few cycles per frame almost untouched.</summary>
    public static double[] Detrend(double[] values, int order = 4)
    {
        ArgumentNullException.ThrowIfNull(values);
        var n = values.Length;
        var mean = values.Average();
        if (mean == 0 || n == 0)
        {
            return new double[n];
        }

        // Least squares in the Legendre-like variable x ∈ [-1, 1]: normal equations of the monomials 1, x, …, x^order.
        var m = Math.Min(order, n - 1) + 1;
        var ata = new double[m, m];
        var atb = new double[m];
        var basis = new double[m];
        for (var i = 0; i < n; i++)
        {
            var x = n == 1 ? 0 : (2.0 * i / (n - 1)) - 1;
            FillPowers(basis, x);
            for (var p = 0; p < m; p++)
            {
                atb[p] += basis[p] * values[i];
                for (var q = 0; q < m; q++)
                {
                    ata[p, q] += basis[p] * basis[q];
                }
            }
        }

        var coefficients = Solve(ata, atb);
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            var x = n == 1 ? 0 : (2.0 * i / (n - 1)) - 1;
            FillPowers(basis, x);
            var fit = 0.0;
            for (var p = 0; p < m; p++)
            {
                fit += coefficients[p] * basis[p];
            }

            result[i] = (values[i] - fit) / mean;
        }

        return result;
    }

    private static void FillPowers(double[] basis, double x)
    {
        var v = 1.0;
        for (var p = 0; p < basis.Length; p++)
        {
            basis[p] = v;
            v *= x;
        }
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var y = (double[])b.Clone();
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

            for (var j = 0; j < n; j++)
            {
                (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            }

            (y[col], y[pivot]) = (y[pivot], y[col]);
            for (var row = col + 1; row < n; row++)
            {
                var f = m[row, col] / m[col, col];
                for (var j = col; j < n; j++)
                {
                    m[row, j] -= f * m[col, j];
                }

                y[row] -= f * y[col];
            }
        }

        var x = new double[n];
        for (var i = n - 1; i >= 0; i--)
        {
            var s = y[i];
            for (var j = i + 1; j < n; j++)
            {
                s -= m[i, j] * x[j];
            }

            x[i] = Math.Abs(m[i, i]) < 1e-300 ? 0 : s / m[i, i];
        }

        return x;
    }

    /// <summary>Hann-windowed DFT magnitude for bins 0…N/2.</summary>
    public static double[] AmplitudeSpectrum(double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var n = values.Length;
        var windowed = new double[n];
        for (var i = 0; i < n; i++)
        {
            windowed[i] = values[i] * 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
        }

        var bins = n / 2;
        var magnitude = new double[bins + 1];
        for (var k = 0; k <= bins; k++)
        {
            double re = 0, im = 0;
            var w = 2 * Math.PI * k / n;
            for (var i = 0; i < n; i++)
            {
                re += windowed[i] * Math.Cos(w * i);
                im -= windowed[i] * Math.Sin(w * i);
            }

            magnitude[k] = Math.Sqrt((re * re) + (im * im));
        }

        return magnitude;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var n = values.Count;
        return n == 0 ? 0 : n % 2 == 1 ? values[n / 2] : (values[(n / 2) - 1] + values[n / 2]) / 2;
    }
}
