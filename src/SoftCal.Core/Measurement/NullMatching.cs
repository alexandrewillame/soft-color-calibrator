using SoftCal.Core.Color;

namespace SoftCal.Core.Measurement;

/// <summary>How a null (matching) measurement runs.</summary>
public sealed record NullOptions
{
    /// <summary>Instrument channel compared: 0–2 = R, G, B; 3 = luma (for grays).</summary>
    public int Channel { get; init; } = 3;

    /// <summary>Palindromic A-B-C-C-B-A sequences, each giving one matched code.</summary>
    public int Repeats { get; init; } = 3;

    /// <summary>Bracketing readings. Clipped frames are accepted: a clipped candidate is still clearly above the
    /// reference, and rejecting it would stall the search.</summary>
    public ReadPolicy Coarse { get; init; } = ReadPolicy.Default with { Frames = 6, MaxClippedFraction = 1 };

    public ReadPolicy Fine { get; init; } = ReadPolicy.Default with { Frames = 15 };

    /// <summary>Choose the exposure on the reference first (the reference and candidates then share it).</summary>
    public bool AutoExpose { get; init; } = true;

    /// <summary>Auto-exposure target for the reference's brightest channel, in instrument units.</summary>
    public double ExposureTarget { get; init; } = 140;

    public int MinCode { get; init; }

    public int MaxCode { get; init; } = 255;

    /// <summary>
    /// Also match each of R, G and B inside its own pair of codes (gray balance needs every channel's match point).
    /// Without it a channel whose crossing lies outside the luma's pair is extrapolated from one step, and single-code
    /// steps are uneven on 6-bit+FRC panels: the BenQ XL2720's blue stepped 1.6 where its neighbours averaged 2.6.
    /// </summary>
    public bool BracketEveryChannel { get; init; }

    /// <summary>A palindrome whose reference changes more than this between its first and last reading (relative) saw
    /// the camera or the screen move, and is read again. A steady setup stays near 0.1 %.</summary>
    public double MovementTolerance { get; init; } = 0.01;

    /// <summary>How many moved palindromes are read again per match before the next one is accepted as it is.</summary>
    public int MovementRetries { get; init; } = 3;
}

/// <summary>A channel matched inside its own pair of codes because its crossing fell outside the luma's pair.</summary>
public sealed record ChannelBracket(
    int Channel,
    int LowerCode,
    double Reference,
    double Lower,
    double Upper,
    double MatchedCode,
    double MatchedCodeError);

/// <summary>One palindrome: reference, lower code, upper code, upper, lower, reference — all at one exposure.</summary>
/// <param name="MatchedCodeErrorPerChannel">One standard error of each channel's matched code, from the readings'
/// frame-to-frame noise divided by the step between the two codes (zero in measurements saved before it existed).</param>
/// <param name="ChannelBrackets">The extra palindromes read for channels whose crossing fell outside
/// <see cref="LowerCode"/>'s pair; <see cref="MatchedCodePerChannel"/> already holds their results.</param>
public sealed record NullRepeat(
    int LowerCode,
    double MatchedCode,
    Vec3 Reference,
    Vec3 Lower,
    Vec3 Upper,
    Vec3 MatchedCodePerChannel,
    Vec3 MatchedCodeErrorPerChannel = default,
    IReadOnlyList<ChannelBracket>? ChannelBrackets = null);

/// <summary>Result of matching a solid level against a reference patch.</summary>
public sealed record NullResult(
    string Label,
    PatchSpec Reference,
    int Channel,
    double ExposureSeconds,
    int LowerCode,
    double MatchedCode,
    double MatchedCodeStdDev,
    double MatchedCodeStdError,
    IReadOnlyList<NullRepeat> Repeats,
    int ReadingCount,
    int MovementRetries = 0)
{
    /// <summary>Mean matched code per channel (R, G, B) from the same readings, for gray balance.</summary>
    public Vec3 MatchedCodePerChannel => Repeats.Aggregate(Vec3.Zero, (s, r) => s + r.MatchedCodePerChannel) / Repeats.Count;

    /// <summary>Standard error of <see cref="MatchedCodePerChannel"/>: the repeats' own errors combined.</summary>
    public Vec3 MatchedCodeErrorPerChannel =>
        Repeats.Aggregate(Vec3.Zero, (s, r) => s + r.MatchedCodeErrorPerChannel.Scale(r.MatchedCodeErrorPerChannel)).Map(Math.Sqrt) / Repeats.Count;
}

/// <summary>Pure helpers of the null method.</summary>
public static class NullMath
{
    /// <summary>
    /// The fractional code where a solid's reading crosses the reference, by linear interpolation between the readings
    /// of two neighbouring codes: <c>lower + (ref − s(lower)) / (s(lower+1) − s(lower))</c>.
    /// </summary>
    public static double Interpolate(int lowerCode, double reference, double lower, double upper)
    {
        var span = upper - lower;
        return Math.Abs(span) < 1e-12 ? lowerCode + 0.5 : lowerCode + ((reference - lower) / span);
    }

    /// <summary>
    /// Standard error of <see cref="Interpolate"/>'s result from the standard errors of its three readings: the noise
    /// moves the crossing by noise ÷ step. A step lost in the noise gives infinity.
    /// </summary>
    public static double InterpolationError(double position, double lower, double upper, double referenceError, double lowerError, double upperError)
    {
        var span = Math.Abs(upper - lower);
        var t = Math.Clamp(position, 0, 1);
        var noise = Math.Sqrt((referenceError * referenceError) + Math.Pow((1 - t) * lowerError, 2) + Math.Pow(t * upperError, 2));
        return span < 1e-12 ? double.PositiveInfinity : noise / span;
    }

    /// <summary>
    /// The next code to try when searching for the crossing: a secant step between the two known codes nearest the
    /// target, kept strictly inside the current bracket.
    /// </summary>
    public static int NextProbe(IReadOnlyDictionary<int, double> readings, double target, int below, int above)
    {
        ArgumentNullException.ThrowIfNull(readings);
        if (above - below <= 1)
        {
            return below;
        }

        var known = readings.OrderBy(kv => Math.Abs(kv.Value - target)).Take(2).ToList();
        double estimate;
        if (known.Count == 2 && known[0].Key != known[1].Key && Math.Abs(known[1].Value - known[0].Value) > 1e-9)
        {
            var slope = (known[1].Value - known[0].Value) / (known[1].Key - known[0].Key);
            estimate = slope > 0 ? known[0].Key + ((target - known[0].Value) / slope) : (below + above) / 2.0;
        }
        else if (known.Count == 1 && known[0].Value > 1e-9 && known[0].Key > 0)
        {
            // One point: a display gamma of ~2.2 seen through a camera gamma of ~1/2.2 reads roughly in proportion to
            // the code, which is a far better second probe than the middle of the whole range.
            estimate = known[0].Key * target / known[0].Value;
        }
        else
        {
            estimate = (below + above) / 2.0;
        }

        var next = (int)Math.Round(estimate);
        next = Math.Clamp(next, below + 1, above - 1);
        if (readings.ContainsKey(next))
        {
            next = readings.ContainsKey(next + 1) || next + 1 >= above ? next - 1 : next + 1;
            next = Math.Clamp(next, below + 1, above - 1);
        }

        return next;
    }

    /// <summary>Mean and sample standard deviation.</summary>
    public static (double Mean, double StdDev) MeanAndStdDev(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return (double.NaN, double.NaN);
        }

        var mean = values.Average();
        var sd = values.Count > 1 ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1)) : 0;
        return (mean, sd);
    }
}

/// <summary>
/// Finds the solid level whose reading equals a reference patch's (a halftone of known light): the camera's response,
/// white balance and colour matrix cancel because both are read at the same exposure in A-B-B-A order.
/// </summary>
public sealed class NullMatcher(MeasurementRig rig)
{
    private readonly MeasurementRig _rig = rig ?? throw new ArgumentNullException(nameof(rig));

    public async Task<NullResult> MatchAsync(
        string label,
        PatchSpec reference,
        Func<int, PatchSpec> candidate,
        double guess,
        NullOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        var o = options ?? new NullOptions();
        if (o.AutoExpose)
        {
            await _rig.AutoExposeAsync(reference, o.ExposureTarget, cancellationToken).ConfigureAwait(false);
        }

        var readingCount = 0;
        async Task<Reading> ReadFull(PatchSpec patch, ReadPolicy policy)
        {
            readingCount++;
            return await _rig.ReadAsync(patch, policy, cancellationToken).ConfigureAwait(false);
        }

        async Task<Vec3> Read(PatchSpec patch, ReadPolicy policy) => (await ReadFull(patch, policy).ConfigureAwait(false)).Mean;

        double Pick(Vec3 v) => o.Channel == 3 ? (0.299 * v.X) + (0.587 * v.Y) + (0.114 * v.Z) : v[o.Channel];

        // The standard error of the mean of two readings, per channel: from the frames' scatter, or from how far the two
        // readings of the palindrome disagree if that is larger (flicker beats and drift the frames do not show).
        static Vec3 PairError(Reading a, Reading b)
        {
            var frames = (a.StdError.Scale(a.StdError) + b.StdError.Scale(b.StdError)).Map(Math.Sqrt) / 2;
            var spread = (a.Mean - b.Mean).Map(Math.Abs) / 2;
            return new Vec3(Math.Max(frames.X, spread.X), Math.Max(frames.Y, spread.Y), Math.Max(frames.Z, spread.Z));
        }

        // Coarse: bracket the crossing between two neighbouring codes.
        var target = Pick(await Read(reference, o.Coarse).ConfigureAwait(false));
        var coarse = new Dictionary<int, double>();
        int below = o.MinCode - 1, above = o.MaxCode + 1;
        var probe = Math.Clamp((int)Math.Round(guess), o.MinCode, o.MaxCode);
        for (var iteration = 0; iteration < 16 && above - below > 1; iteration++)
        {
            var value = Pick(await Read(candidate(probe), o.Coarse).ConfigureAwait(false));
            coarse[probe] = value;
            if (value < target)
            {
                below = Math.Max(below, probe);
            }
            else
            {
                above = Math.Min(above, probe);
            }

            if (above - below <= 1)
            {
                break;
            }

            probe = NullMath.NextProbe(coarse, target, below, above);
        }

        var lower = Math.Clamp(below, o.MinCode, o.MaxCode - 1);

        // One palindrome on the pair (a, a + 1): reference, a, a + 1, a + 1, a, reference.
        async Task<Palindrome> ReadPalindromeAsync(int a)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r1 = await ReadFull(reference, o.Fine).ConfigureAwait(false);
            var s0a = await ReadFull(candidate(a), o.Fine).ConfigureAwait(false);
            var s1a = await ReadFull(candidate(a + 1), o.Fine).ConfigureAwait(false);
            var s1b = await ReadFull(candidate(a + 1), o.Fine).ConfigureAwait(false);
            var s0b = await ReadFull(candidate(a), o.Fine).ConfigureAwait(false);
            var r2 = await ReadFull(reference, o.Fine).ConfigureAwait(false);
            var mean = Pick((r1.Mean + r2.Mean) / 2);
            return new Palindrome(
                a,
                (r1.Mean + r2.Mean) / 2,
                (s0a.Mean + s0b.Mean) / 2,
                (s1a.Mean + s1b.Mean) / 2,
                PairError(r1, r2),
                PairError(s0a, s0b),
                PairError(s1a, s1b),
                mean > 0 ? Math.Abs(Pick(r1.Mean) - Pick(r2.Mean)) / mean : 0);
        }

        // Channels whose crossing lies outside their pair get a pair of their own, moved toward the crossing until it
        // falls inside, so no channel is extrapolated from a single (possibly uneven) step.
        async Task<List<ChannelBracket>> BracketChannelsAsync(int luma, double[] codes, double[] errors)
        {
            const double Slack = 0.15;
            const int MaxRounds = 3, MaxJump = 4;
            static double Outside(double m, int a) => Math.Max(0, Math.Max(a - m, m - (a + 1)));
            var brackets = new List<ChannelBracket>();
            var own = new[] { luma, luma, luma };
            var tried = new[] { new HashSet<int> { luma }, new HashSet<int> { luma }, new HashSet<int> { luma } };
            for (var round = 0; round < MaxRounds; round++)
            {
                var moves = new SortedDictionary<int, List<int>>();
                for (var c = 0; c < 3; c++)
                {
                    if (!double.IsFinite(codes[c]) || Outside(codes[c], own[c]) <= Slack)
                    {
                        continue;
                    }

                    var next = Math.Clamp((int)Math.Floor(codes[c]), own[c] - MaxJump, own[c] + MaxJump);
                    next = Math.Clamp(next, o.MinCode, o.MaxCode - 1);
                    if (tried[c].Add(next))
                    {
                        if (!moves.TryGetValue(next, out var channels))
                        {
                            moves[next] = channels = [];
                        }

                        channels.Add(c);
                    }
                }

                if (moves.Count == 0)
                {
                    break;
                }

                foreach (var (a, channels) in moves)
                {
                    // A reference that moved between the palindrome's ends saw the camera move: read it again, as below.
                    var p = await ReadPalindromeAsync(a).ConfigureAwait(false);
                    for (var retry = 0; p.ReferenceChange > o.MovementTolerance && retry < o.MovementRetries; retry++)
                    {
                        p = await ReadPalindromeAsync(a).ConfigureAwait(false);
                    }

                    foreach (var c in channels)
                    {
                        var (m, e) = (p.Crossing(c), p.CrossingError(c));
                        brackets.Add(new ChannelBracket(c, a, p.Reference[c], p.Low[c], p.High[c], m, e));
                        if (Outside(m, a) < Outside(codes[c], own[c]))
                        {
                            (codes[c], errors[c], own[c]) = (m, e, a);
                        }
                    }
                }
            }

            return brackets;
        }

        // Fine: palindromes at the shared exposure; shift the bracket if drift moved the crossing out of it. A palindrome
        // whose reference reads differently at its start and end saw the camera (or the screen) move, e.g. a hand-held
        // camera wobbling: it is read again rather than averaged into the result.
        var repeats = new List<NullRepeat>();
        var shifts = 0;
        var movements = 0;
        while (repeats.Count < o.Repeats)
        {
            var p = await ReadPalindromeAsync(lower).ConfigureAwait(false);
            if (p.ReferenceChange > o.MovementTolerance && movements < o.MovementRetries)
            {
                movements++;
                continue;
            }

            var matched = NullMath.Interpolate(lower, Pick(p.Reference), Pick(p.Low), Pick(p.High));
            if (shifts < 6 && matched < lower - 0.5 && lower > o.MinCode)
            {
                lower--;
                shifts++;
                continue;
            }

            if (shifts < 6 && matched > lower + 1.5 && lower + 1 < o.MaxCode)
            {
                lower++;
                shifts++;
                continue;
            }

            double[] codes = [p.Crossing(0), p.Crossing(1), p.Crossing(2)];
            double[] errors = [p.CrossingError(0), p.CrossingError(1), p.CrossingError(2)];
            var brackets = o.BracketEveryChannel ? await BracketChannelsAsync(lower, codes, errors).ConfigureAwait(false) : [];
            repeats.Add(new NullRepeat(
                lower,
                matched,
                p.Reference,
                p.Low,
                p.High,
                new Vec3(codes[0], codes[1], codes[2]),
                new Vec3(errors[0], errors[1], errors[2]),
                brackets.Count > 0 ? brackets : null));
        }

        var (mean, sd) = NullMath.MeanAndStdDev([.. repeats.Select(x => x.MatchedCode)]);
        return new NullResult(label, reference, o.Channel, _rig.ExposureSeconds, lower, mean, sd, sd / Math.Sqrt(repeats.Count), repeats, readingCount, movements);
    }

    /// <summary>The averaged readings of one palindrome on the pair (<see cref="Lower"/>, <see cref="Lower"/> + 1).</summary>
    /// <param name="ReferenceChange">How much the reference's two readings (first and last) differ, relative to their
    /// mean: a steady camera and screen keep it near 0.1 %.</param>
    private readonly record struct Palindrome(int Lower, Vec3 Reference, Vec3 Low, Vec3 High, Vec3 ReferenceError, Vec3 LowError, Vec3 HighError, double ReferenceChange)
    {
        public double Crossing(int channel) => NullMath.Interpolate(Lower, Reference[channel], Low[channel], High[channel]);

        public double CrossingError(int channel) => NullMath.InterpolationError(
            Crossing(channel) - Lower,
            Low[channel],
            High[channel],
            ReferenceError[channel],
            LowError[channel],
            HighError[channel]);
    }
}

/// <summary>One level of the midpoint tree: a checkerboard of an already-matched code and black, matched by a solid.</summary>
public sealed record TreeLevel(int Level, double Fraction, int HalftoneCode, double HalftoneCodeFraction, NullResult Null);

/// <summary>
/// The midpoint tree for the shadows: a 1-px checkerboard of white and black gives ½ of the black-to-white light; a
/// checkerboard of the code matched to ½ and black gives ¼; and so on down to about 1/256 (≈ code 21 at γ 2.2).
/// </summary>
public static class MidpointTree
{
    /// <summary>The code expected for a light fraction on a γ 2.2 display, used as the search's first guess.</summary>
    public static double Guess(double fraction) => 255 * Math.Pow(Math.Clamp(fraction, 1e-6, 1), 1 / 2.2);

    /// <summary>
    /// The light fraction of integer code <paramref name="code"/> near a matched point (<paramref name="matched"/>,
    /// <paramref name="fraction"/>), using the slope of the power law through that point.
    /// </summary>
    public static double FractionNear(double matched, double fraction, int code)
    {
        if (matched <= 0 || fraction <= 0 || fraction >= 1)
        {
            return fraction;
        }

        var gamma = Math.Log(fraction) / Math.Log(matched / 255.0);
        return fraction * Math.Pow(code / matched, gamma);
    }

    /// <summary>The reference for the next level and the light fraction it represents. Any 50 % geometry works;
    /// horizontal lines avoid the horizontal subpixel crosstalk some panels (TN) show with 1-px checkerboards.</summary>
    public static (HalftonePatch Reference, int Code, double CodeFraction, double Fraction) Next(
        double matched,
        double fraction,
        Func<byte, DeviceRgb> color,
        HalftoneGeometry geometry = HalftoneGeometry.Checkerboard)
    {
        ArgumentNullException.ThrowIfNull(color);
        var code = (int)Math.Clamp(Math.Round(matched), 1, 255);
        var codeFraction = FractionNear(matched, fraction, code);
        var reference = new HalftonePatch(color((byte)code), DeviceRgb.Black, geometry);
        return (reference, code, codeFraction, codeFraction / 2);
    }

    /// <summary>The next level's starting guess: halve the light along the power law through the last matched point.</summary>
    public static double NextGuess(double matched, double fraction, double nextFraction)
    {
        if (matched <= 0 || fraction <= 0 || fraction >= 1)
        {
            return Guess(nextFraction);
        }

        var gamma = Math.Log(fraction) / Math.Log(matched / 255.0);
        return matched * Math.Pow(nextFraction / fraction, 1 / gamma);
    }

    /// <summary>
    /// Runs the tree from a white/black checkerboard: at most <paramref name="levels"/> halvings, stopping early once a
    /// matched code falls to <paramref name="stopBelowCode"/> (the deep-shadow end the gate looks at; displays with a
    /// steep gamma need more than 8 halvings to get there).
    /// </summary>
    public static async Task<IReadOnlyList<TreeLevel>> RunAsync(
        NullMatcher matcher,
        int levels,
        NullOptions options,
        Func<byte, DeviceRgb> color,
        Action<TreeLevel>? progress,
        CancellationToken cancellationToken,
        double stopBelowCode = 0,
        HalftoneGeometry geometry = HalftoneGeometry.Checkerboard)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(color);
        if (geometry == HalftoneGeometry.Bayer4x4)
        {
            throw new ArgumentException("The tree halves the light: use a 50 % geometry.", nameof(geometry));
        }

        var results = new List<TreeLevel>();
        HalftonePatch reference = new(color(255), DeviceRgb.Black, geometry);
        int code = 255;
        double codeFraction = 1, fraction = 0.5;
        var guess = Guess(fraction);
        for (var level = 1; level <= levels; level++)
        {
            var result = await matcher.MatchAsync(
                $"tree 1/{1L << level}",
                reference,
                c => new SolidPatch(color((byte)c)),
                guess,
                options,
                cancellationToken).ConfigureAwait(false);
            var node = new TreeLevel(level, fraction, code, codeFraction, result);
            results.Add(node);
            progress?.Invoke(node);
            if (result.MatchedCode <= stopBelowCode || result.MatchedCode < 2)
            {
                break;
            }

            var previous = fraction;
            (reference, code, codeFraction, fraction) = Next(result.MatchedCode, fraction, color, geometry);
            guess = NextGuess(result.MatchedCode, previous, fraction);
        }

        return results;
    }

    /// <summary>For each target code, the level whose matched code (averaged over runs) is nearest.</summary>
    public static IReadOnlyDictionary<int, int> LevelsNearest(IReadOnlyList<IReadOnlyList<TreeLevel>> runs, IEnumerable<int> targetCodes)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(targetCodes);
        var means = runs.SelectMany(r => r)
            .GroupBy(l => l.Level)
            .ToDictionary(g => g.Key, g => g.Average(l => l.Null.MatchedCode));
        return targetCodes.ToDictionary(t => t, t => means.Count == 0 ? 0 : means.OrderBy(m => Math.Abs(m.Value - t)).First().Key);
    }
}
