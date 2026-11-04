namespace SoftCal.Core.Analysis;

/// <summary>One bracketed reading: patch <see cref="Patch"/> at exposure <see cref="Exposure"/> gave camera code <see cref="Code"/>.</summary>
public readonly record struct BracketObservation(int Patch, int Exposure, double Code, bool Clipped = false);

/// <summary>A patch whose light is a known mix of other patches' light (a halftone): E[Patch] = Σ weight·E[part].</summary>
public sealed record LightAnchor(int Patch, IReadOnlyList<(int Patch, double Weight)> Parts);

public sealed record ResponseFitOptions
{
    /// <summary>Codes at or below this are ignored. Webcams clamp black and their response is very steep just above
    /// the clamp (the C920e reads 0–5 for anything dark), so the lowest codes say little about the curve.</summary>
    public double MinCode { get; init; } = 12;

    /// <summary>Codes at or above this are ignored (clipping).</summary>
    public double MaxCode { get; init; } = 248;

    /// <summary>Knots of the log-response, spaced evenly in log code (a power-law camera is then a straight line).</summary>
    public int Knots { get; init; } = 20;

    /// <summary>Weight of the second-difference smoothness penalty on the log-response. Kept small: near the ends the
    /// hat-weighted data are sparse, and a stronger penalty would bend the curve away from them (the sRGB toe).</summary>
    public double Smoothness { get; init; } = 0.02;

    /// <summary>Code at which the log-response is pinned to 0.</summary>
    public double ReferenceCode { get; init; } = 128;
}

/// <summary>Result of the bracketing fit.</summary>
public sealed record ResponseFitResult(
    IReadOnlyList<double> KnotCodes,
    IReadOnlyList<double> LogResponse,
    IReadOnlyList<double> NominalExposureSeconds,
    IReadOnlyList<double> FittedLogExposure,
    IReadOnlyList<double> PatchLogLight,
    double Exponent,
    double ResidualRms,
    double AnchorResidualRms,
    int Observations)
{
    /// <summary>Which patches had at least one usable reading. A patch below the camera's floor at every exposure (a
    /// deep display black) has no fitted light; it counts as zero.</summary>
    public IReadOnlyList<bool> PatchObserved { get; init; } = [];

    /// <summary>Relative linear light reaching the sensor for a camera code (1 at the reference code).</summary>
    public double Linearize(double code) => Math.Exp(Exponent * LogResponseAt(code));

    /// <summary>The fitted log-response at a code, interpolated linearly in log code and clamped to the knot range.</summary>
    public double LogResponseAt(double code)
    {
        var u = Math.Log(Math.Clamp(code, KnotCodes[0], KnotCodes[^1]));
        var i = 0;
        while (i < KnotCodes.Count - 2 && u > Math.Log(KnotCodes[i + 1]))
        {
            i++;
        }

        var u0 = Math.Log(KnotCodes[i]);
        var u1 = Math.Log(KnotCodes[i + 1]);
        var t = (u - u0) / (u1 - u0);
        return LogResponse[i] + (t * (LogResponse[i + 1] - LogResponse[i]));
    }

    /// <summary>Relative light of a patch (consistent scale across patches); 0 for a patch never seen above the
    /// camera's floor.</summary>
    public double PatchLight(int patch) => IsObserved(patch) ? Math.Exp(Exponent * PatchLogLight[patch]) : 0;

    public bool IsObserved(int patch) => PatchObserved.Count == 0 || PatchObserved[patch];

    /// <summary>Actual ratio between consecutive exposure steps (nominally 2).</summary>
    public IReadOnlyList<double> StepRatios =>
        [.. Enumerable.Range(1, FittedLogExposure.Count - 1).Select(k => Math.Exp(Exponent * (FittedLogExposure[k] - FittedLogExposure[k - 1])))];

    /// <summary>The camera's local gamma, d ln light / d ln code, at a code (2.2 for an sRGB-like camera at mid-tones).</summary>
    public double LocalGamma(double code)
    {
        var lo = Math.Max(KnotCodes[0], code * 0.95);
        var hi = Math.Min(KnotCodes[^1], code * 1.05);
        var dCode = Math.Log(hi) - Math.Log(lo);
        return dCode > 0 ? Exponent * (LogResponseAt(hi) - LogResponseAt(lo)) / dCode : double.NaN;
    }
}

/// <summary>
/// Robertson-style exposure bracketing with the exposure ratios as unknowns. Every reading obeys
/// <c>g(code) = ln E(patch) + ln t(exposure)</c> where g is the camera's log inverse response. The fit solves for g (a
/// smooth piecewise-linear curve), every patch's light and every exposure's true length at once. A free exposure scale
/// leaves one ambiguity — raising every light to a power — which is fixed either by halftone anchors of known light
/// ratio (preferred, it measures the true mean step ratio) or by assuming the steps average exactly ×2.
/// </summary>
public static class ResponseFit
{
    public static ResponseFitResult Fit(
        IReadOnlyList<BracketObservation> observations,
        IReadOnlyList<double> nominalExposureSeconds,
        int patchCount,
        IReadOnlyList<LightAnchor>? anchors = null,
        ResponseFitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(nominalExposureSeconds);
        var o = options ?? new ResponseFitOptions();
        var exposures = nominalExposureSeconds.Count;
        if (exposures < 2)
        {
            throw new ArgumentException("Bracketing needs at least two exposures.", nameof(nominalExposureSeconds));
        }

        var usable = observations.Where(x => !x.Clipped && x.Code > o.MinCode && x.Code < o.MaxCode).ToList();
        if (usable.Count < o.Knots)
        {
            throw new ArgumentException($"Only {usable.Count} usable observations; the fit needs at least {o.Knots}.", nameof(observations));
        }

        var logMin = Math.Log(o.MinCode);
        var logMax = Math.Log(o.MaxCode);
        var knots = Enumerable.Range(0, o.Knots).Select(i => Math.Exp(logMin + ((logMax - logMin) * i / (o.Knots - 1)))).ToArray();
        var log2Nominal = nominalExposureSeconds.Select(Math.Log2).ToArray();

        // The gauges must act on exposures that carry data. An exposure with no usable reading (all below the black
        // clamp, say) would otherwise absorb the "no tilt" condition, the exponent ambiguity would come back and the
        // smoothness term would flatten the response to nothing.
        var withData = usable.Select(x => x.Exposure).Distinct().OrderBy(k => nominalExposureSeconds[k]).ToArray();
        if (withData.Length < 2)
        {
            throw new ArgumentException("Bracketing needs usable readings at two exposures at least.", nameof(observations));
        }

        var reference = withData[withData.Length / 2];

        // Unknowns: g[0..M-1], x[0..J-1] (ln light), d[0..K-1] (ln t deviation from nominal).
        var m = o.Knots;
        var n = m + patchCount + exposures;
        var ata = new double[n, n];
        var atb = new double[n];

        void AddRow(ReadOnlySpan<(int Index, double Value)> row, double rhs, double weight)
        {
            var w2 = weight * weight;
            foreach (var (i, vi) in row)
            {
                atb[i] += w2 * vi * rhs;
                foreach (var (j, vj) in row)
                {
                    ata[i, j] += w2 * vi * vj;
                }
            }
        }

        var half = (o.MaxCode - o.MinCode) / 2;
        foreach (var obs in usable)
        {
            var (k0, t) = Locate(knots, obs.Code);
            var weight = Math.Max(0.05, Math.Min(obs.Code - o.MinCode, o.MaxCode - obs.Code) / half);
            (int, double)[] row = [(k0, 1 - t), (k0 + 1, t), (m + obs.Patch, -1), (m + patchCount + obs.Exposure, -1)];
            AddRow(row, Math.Log(nominalExposureSeconds[obs.Exposure]), weight);
        }

        var smooth = o.Smoothness * Math.Sqrt(usable.Count / (double)m);
        for (var i = 1; i < m - 1; i++)
        {
            AddRow([(i - 1, 1), (i, -2), (i + 1, 1)], 0, smooth);
        }

        const double Pin = 1e4;
        var (r0, rt) = Locate(knots, o.ReferenceCode);
        AddRow([(r0, 1 - rt), (r0 + 1, rt)], 0, Pin);
        AddRow([(m + patchCount + reference, 1)], 0, Pin);
        var tilt = withData.Select(k => (m + patchCount + k, log2Nominal[k] - log2Nominal[reference])).ToArray();
        AddRow(tilt, 0, Pin);
        foreach (var k in Enumerable.Range(0, exposures).Except(withData))
        {
            // No data: keep the nominal exposure (it does not enter any residual).
            AddRow([(m + patchCount + k, 1)], 0, Pin);
        }

        // Weak prior on every unknown so patches never seen in range stay finite.
        for (var i = 0; i < n; i++)
        {
            ata[i, i] += 1e-9;
        }

        var x = Solve(ata, atb);
        if (x.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("The bracketing fit is singular (too few overlapping readings).", nameof(observations));
        }

        var g = x[..m];
        var light = x[m..(m + patchCount)];
        var logT = Enumerable.Range(0, exposures).Select(k => Math.Log(nominalExposureSeconds[k]) + x[m + patchCount + k]).ToArray();

        double rss = 0;
        foreach (var obs in usable)
        {
            var (k0, t) = Locate(knots, obs.Code);
            var r = (((1 - t) * g[k0]) + (t * g[k0 + 1])) - light[obs.Patch] - logT[obs.Exposure];
            rss += r * r;
        }

        var observed = new bool[patchCount];
        foreach (var obs in usable)
        {
            observed[obs.Patch] = true;
        }

        var exponent = 1.0;
        var anchorRms = double.NaN;
        var usableAnchors = anchors?.Where(a => observed[a.Patch]).ToList() ?? [];
        if (usableAnchors.Count > 0)
        {
            exponent = FitExponent(light, observed, usableAnchors);
            anchorRms = AnchorResidual(light, observed, usableAnchors, exponent);
        }

        return new ResponseFitResult(knots, g, nominalExposureSeconds, logT, light, exponent, Math.Sqrt(rss / usable.Count), anchorRms, usable.Count)
        {
            PatchObserved = observed,
        };
    }

    /// <summary>Light fractions of display codes relative to black and white: (E(v) − E(black)) / (E(white) − E(black)).</summary>
    public static double RelativeFraction(double light, double black, double white) => (light - black) / (white - black);

    private static double FitExponent(double[] logLight, bool[] observed, IReadOnlyList<LightAnchor> anchors)
    {
        double a = 0.3, b = 3;
        var phi = (Math.Sqrt(5) - 1) / 2;
        var c = b - (phi * (b - a));
        var d = a + (phi * (b - a));
        for (var i = 0; i < 80; i++)
        {
            if (AnchorResidual(logLight, observed, anchors, c) < AnchorResidual(logLight, observed, anchors, d))
            {
                b = d;
            }
            else
            {
                a = c;
            }

            c = b - (phi * (b - a));
            d = a + (phi * (b - a));
        }

        return (a + b) / 2;
    }

    private static double AnchorResidual(double[] logLight, bool[] observed, IReadOnlyList<LightAnchor> anchors, double s)
    {
        double sum = 0;
        foreach (var anchor in anchors)
        {
            // A part never seen above the camera's floor (display black) contributes no light.
            var mix = anchor.Parts.Sum(p => observed[p.Patch] ? p.Weight * Math.Exp(s * logLight[p.Patch]) : 0);
            var r = (s * logLight[anchor.Patch]) - Math.Log(mix);
            sum += r * r;
        }

        return Math.Sqrt(sum / anchors.Count);
    }

    /// <summary>The knot interval holding a code and the position inside it, in log code.</summary>
    private static (int Index, double T) Locate(double[] knots, double code)
    {
        var u = Math.Log(Math.Clamp(code, knots[0], knots[^1]));
        var u0 = Math.Log(knots[0]);
        var step = Math.Log(knots[1]) - u0;
        var i = Math.Min(knots.Length - 2, (int)((u - u0) / step));
        var t = (u - (u0 + (i * step))) / step;
        return (i, t);
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        // Cholesky would do; Gaussian elimination with partial pivoting is plenty for ~60 unknowns.
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

            if (pivot != col)
            {
                for (var j = 0; j < n; j++)
                {
                    (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
                }

                (y[col], y[pivot]) = (y[pivot], y[col]);
            }

            var p = m[col, col];
            for (var row = col + 1; row < n; row++)
            {
                var f = m[row, col] / p;
                if (f == 0)
                {
                    continue;
                }

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

            x[i] = s / m[i, i];
        }

        return x;
    }
}
