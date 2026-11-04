using System.Globalization;
using SoftCal.Core.Color;
using SoftCal.Core.Measurement;

namespace SoftCal.Core.Advisor;

/// <summary>The red, green and blue gains of a monitor's user color mode, as its menu shows them.</summary>
public readonly record struct RgbGains(int Red, int Green, int Blue)
{
    public int this[int channel] => channel switch
    {
        0 => Red,
        1 => Green,
        2 => Blue,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel must be 0, 1 or 2."),
    };

    /// <summary>The highest of the three.</summary>
    public int Max => Math.Max(Red, Math.Max(Green, Blue));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Red} / {Green} / {Blue}");
}

/// <summary>
/// How a white differs from a reference white: its red light and its blue light, each relative to its green, as a
/// ratio to the reference's (1 = the same; 0.86 = 14 % less red for the same green). One camera reads both screens, so
/// its tone curve and its own idea of white cancel out: what is left is the difference between the screens.
/// </summary>
public readonly record struct WhiteDifference(double Red, double Blue)
{
    /// <summary>The larger of the two deviations, in natural-log units (0.01 ≈ 1 %).</summary>
    public double Size => Math.Max(Math.Abs(Math.Log(Red)), Math.Abs(Math.Log(Blue)));

    /// <summary>The tint in one word ("greener", "pinker", "warmer", "bluer"), or null when it is about the same.</summary>
    public string? Tint
    {
        get
        {
            var (red, blue) = (Math.Log(Red), Math.Log(Blue));
            var green = -(red + blue) / 2;
            var warm = (red - blue) / 2;
            if (Math.Max(Math.Abs(green), Math.Abs(warm)) < 0.01)
            {
                return null;
            }

            return Math.Abs(green) >= Math.Abs(warm)
                ? (green > 0 ? "greener" : "pinker")
                : (warm > 0 ? "warmer" : "bluer");
        }
    }

    /// <summary>In plain words, e.g. "greener than VX2770 SERIES: 14 % less red and 18 % less blue for the same green".</summary>
    public string Describe(string reference)
    {
        var parts = $"{Part(Red, "red")} and {Part(Blue, "blue")} for the same green";
        return Tint is { } tint ? $"{tint} than {reference}: {parts}" : $"about the same as {reference} ({parts})";
    }

    /// <summary>"greener (14 % less red, 18 % less blue)": for a sentence that names the reference already.</summary>
    public string Brief()
    {
        var parts = $"{Part(Red, "red")}, {Part(Blue, "blue")}";
        return Tint is { } tint ? $"{tint} ({parts})" : $"about the same ({parts})";
    }

    private static string Part(double ratio, string name) =>
        Math.Abs(ratio - 1) < 0.005
            ? $"the same {name}"
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(ratio - 1) * 100:0} % {(ratio < 1 ? "less" : "more")} {name}");
}

/// <summary>
/// A white read by the camera: its encoded red, green and blue at one exposure, and each channel's local slope there:
/// how much the encoded value rises per doubling of light (from a second reading at a shorter exposure; about 0.45 for
/// an sRGB-like camera). The slope turns differences of encoded values into differences of light.
/// </summary>
public sealed record WhiteSample(Vec3 Encoded, Vec3 Slope, double ExposureSeconds)
{
    /// <summary>A plausible slope when it cannot be measured (an sRGB-like camera's, high up its curve).</summary>
    public const double TypicalSlope = 0.45;

    /// <param name="full">The white at the measuring exposure.</param>
    /// <param name="shorter">The same white at a shorter exposure.</param>
    /// <param name="exposureRatio">How much longer the measuring exposure is (2 for one camera step).</param>
    public static WhiteSample From(Reading full, Reading shorter, double exposureRatio)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(shorter);
        double Slope(double f, double s) =>
            f > 1 && s > 1 && exposureRatio > 1 ? Math.Clamp(Math.Log(f / s) / Math.Log(exposureRatio), 0.2, 1.2) : TypicalSlope;
        var slope = new Vec3(Slope(full.Mean.X, shorter.Mean.X), Slope(full.Mean.Y, shorter.Mean.Y), Slope(full.Mean.Z, shorter.Mean.Z));
        return new WhiteSample(full.Mean, slope, full.ExposureSeconds);
    }

    /// <summary>Another reading of a white near the same level, judged with this sample's slopes.</summary>
    public WhiteSample With(Reading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return this with { Encoded = reading.Mean, ExposureSeconds = reading.ExposureSeconds };
    }
}

/// <summary>The arithmetic of a white match, kept apart so it can be tested without a camera.</summary>
public static class WhiteMath
{
    /// <summary>A gain change must promise at least this much closer to the reference (log units, 0.3 %) to be worth
    /// a trip to the menu.</summary>
    public const double MinimumImprovement = 0.003;

    /// <summary>How <paramref name="white"/> differs from <paramref name="reference"/>. Each channel's encoded ratio
    /// becomes a ratio of light through the two samples' average slope; the exposures cancel (they scale every channel
    /// alike).</summary>
    public static WhiteDifference Difference(WhiteSample white, WhiteSample reference)
    {
        ArgumentNullException.ThrowIfNull(white);
        ArgumentNullException.ThrowIfNull(reference);
        var slope = (white.Slope + reference.Slope) / 2;
        double Light(int c) => (Math.Log(Math.Max(white.Encoded[c], 1e-3)) - Math.Log(Math.Max(reference.Encoded[c], 1e-3))) / slope[c];
        var green = Light(1);
        return new WhiteDifference(Math.Exp(Light(0) - green), Math.Exp(Light(2) - green));
    }

    /// <summary>A white's tint follows its red and blue gains against green: their natural-log ratios.</summary>
    public static (double Red, double Blue) Ratios(RgbGains gains) =>
        (Math.Log((double)Math.Max(1, gains.Red) / Math.Max(1, gains.Green)), Math.Log((double)Math.Max(1, gains.Blue) / Math.Max(1, gains.Green)));

    /// <summary>
    /// The whole-step gains predicted to bring the white closest to the reference, from the difference measured at
    /// <paramref name="current"/> and how the gains move the white (<paramref name="effect"/>: the change of the white's
    /// red and blue per change of the red and blue gains against green). The exact answer is scaled so its highest gain
    /// sits on <paramref name="cap"/>: no gain rises above the highest the monitor had, so no channel can start to clip.
    /// The whole steps around it are compared by their predicted difference. Returns <paramref name="current"/> when no
    /// change is predicted to do better by <see cref="MinimumImprovement"/>.
    /// </summary>
    public static RgbGains NextGains(RgbGains current, WhiteDifference difference, WhiteJacobian effect, int cap)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap);
        var (errorRed, errorBlue) = (Math.Log(difference.Red), Math.Log(difference.Blue));
        var (red, blue) = Ratios(current);
        var (wantRed, wantBlue) = effect.Undo(errorRed, errorBlue);
        var logs = new[] { red + wantRed, 0.0, blue + wantBlue };
        var shift = Math.Log(cap) - logs.Max();
        var exact = logs.Select(l => Math.Exp(l + shift)).ToArray();

        double Predicted(RgbGains g)
        {
            var (r, b) = Ratios(g);
            var (changeRed, changeBlue) = effect.Apply(r - red, b - blue);
            return Math.Max(Math.Abs(errorRed + changeRed), Math.Abs(errorBlue + changeBlue));
        }

        static int[] Around(double value, int cap) => [.. Enumerable.Range((int)Math.Floor(value) - 1, 4).Where(x => x >= 1 && x <= cap)];

        var best = current;
        var bestScore = Predicted(current) - MinimumImprovement;
        foreach (var r in Around(exact[0], cap))
        {
            foreach (var g in Around(exact[1], cap))
            {
                foreach (var b in Around(exact[2], cap))
                {
                    // Of equal predictions, the one fewest steps away.
                    var candidate = new RgbGains(r, g, b);
                    var steps = Math.Abs(r - current.Red) + Math.Abs(g - current.Green) + Math.Abs(b - current.Blue);
                    var score = Predicted(candidate) + (1e-4 * steps);
                    if (score < bestScore)
                    {
                        (best, bestScore) = (candidate, score);
                    }
                }
            }
        }

        return best;
    }
}

/// <summary>
/// How the white's red and blue (log ratios to the reference) move per change of the red and blue drive, or gains,
/// against green (log ratios): a 2 × 2 matrix, learned from each reading (Broyden's update).
/// </summary>
public readonly record struct WhiteJacobian(double RedRed, double RedBlue, double BlueRed, double BlueBlue)
{
    public double Determinant => (RedRed * BlueBlue) - (RedBlue * BlueRed);

    /// <summary>Before any reading: each channel's light follows its drive with the panel's gamma, and the camera's
    /// red and blue see only their own channel.</summary>
    public static WhiteJacobian Initial(double gamma = 2.2) => new(gamma, 0, 0, gamma);

    /// <summary>The change of the white's red and blue for a change of the red and blue against green.</summary>
    public (double Red, double Blue) Apply(double changeRed, double changeBlue) =>
        ((RedRed * changeRed) + (RedBlue * changeBlue), (BlueRed * changeRed) + (BlueBlue * changeBlue));

    /// <summary>The change of the red and blue against green that undoes a difference (a Newton step).</summary>
    public (double Red, double Blue) Undo(double errorRed, double errorBlue)
    {
        var det = Determinant;
        return ((-(BlueBlue * errorRed) + (RedBlue * errorBlue)) / det, ((BlueRed * errorRed) - (RedRed * errorBlue)) / det);
    }

    /// <summary>The same matrix for something that acts <paramref name="factor"/> times as strongly.</summary>
    public WhiteJacobian Scaled(double factor) => new(RedRed * factor, RedBlue * factor, BlueRed * factor, BlueBlue * factor);

    /// <summary>Learns from a change and the change of the difference it caused. A step lost in the noise could make the
    /// matrix nonsense (a channel moving the wrong way), so an update that would is skipped.</summary>
    public WhiteJacobian Update(double changeRed, double changeBlue, double differenceRed, double differenceBlue)
    {
        var norm = (changeRed * changeRed) + (changeBlue * changeBlue);
        if (norm < 1e-6)
        {
            return this;
        }

        var (predictedRed, predictedBlue) = Apply(changeRed, changeBlue);
        var (missRed, missBlue) = (differenceRed - predictedRed, differenceBlue - predictedBlue);
        var updated = new WhiteJacobian(
            RedRed + (missRed * changeRed / norm),
            RedBlue + (missRed * changeBlue / norm),
            BlueRed + (missBlue * changeRed / norm),
            BlueBlue + (missBlue * changeBlue / norm));
        return updated.RedRed > 0.2 && updated.BlueBlue > 0.2 && updated.Determinant > 0.04 ? updated : this;
    }
}

/// <summary>Where the patches and the camera are during a white match.</summary>
public interface IWhiteMatchStage
{
    /// <summary>Readies a screen for the camera before the user moves it there (true = the reference): the aiming
    /// area on that screen, SoftCal's window out of its way, and the camera's view shown to the user.</summary>
    Task AimAtAsync(bool reference, CancellationToken cancellationToken);

    /// <summary>The user says the camera rests on that screen: patches go there from now on (a simulator moves its
    /// camera there too).</summary>
    Task UseAsync(bool reference, CancellationToken cancellationToken);
}

/// <summary>The person at the monitors during a white match (or DDC/CI, or a simulated user in tests).</summary>
public interface IWhiteMatchUser
{
    /// <summary>True when SoftCal sets the gains itself (over DDC/CI). The match then tries them in the monitor's
    /// current color mode first, and asks for the user mode only if the monitor ignores them there.</summary>
    bool SetsGainsDirectly => false;

    /// <summary>Asks for the camera to be moved; false stops the match.</summary>
    Task<bool> MoveCameraAsync(Advice advice, CancellationToken cancellationToken);

    /// <summary>Asks for the monitor's user color mode (or only for its values when it is already in it) and returns
    /// the gains it shows; null stops the match.</summary>
    Task<RgbGains?> SwitchToUserModeAsync(Advice advice, CancellationToken cancellationToken);

    /// <summary>Has the gains set (in the menu, or over DDC/CI) and returns the gains now set; null stops the match.</summary>
    Task<RgbGains?> SetGainsAsync(RgbGains gains, Advice advice, CancellationToken cancellationToken);
}

public sealed record WhiteMatchOptions
{
    /// <summary>Close enough: red and blue each within this of the reference (log units; 0.012 ≈ 1.2 %, well below
    /// what the eye sees side by side).</summary>
    public double Tolerance { get; init; } = 0.012;

    /// <summary>Also close enough when no whole gain step can do better: a step moves the white by about 2–3 %, so red
    /// and blue can end up to about one step apart, one each way (log units).</summary>
    public double StepTolerance { get; init; } = 0.025;

    /// <summary>Gain changes at most (each one is measured): four when the user makes them in the menu; the app allows
    /// more when it sets them itself over DDC/CI, each one taking a few seconds.</summary>
    public int MaxGainRounds { get; init; } = 4;

    /// <summary>How strongly a gain step moves the white, as a fraction of the same step of the signal: 1 when the
    /// gains scale the signal, about 0.45 when they scale the light; the BenQ XL2720's act at about 0.7. Only the first
    /// change relies on it: every change measured refines it.</summary>
    public double InitialGainEffect { get; init; } = 0.75;

    /// <summary>Readings of the automatic drive search at most (about a second each).</summary>
    public int MaxNullReadings { get; init; } = 8;

    /// <summary>The drive search stops this close to the reference.</summary>
    public double NullTolerance { get; init; } = 0.004;

    /// <summary>Encoded level the white is exposed to (of 255).</summary>
    public double TargetLevel { get; init; } = 170;

    public ReadPolicy Readings { get; init; } = ReadPolicy.Default with { Frames = 12 };
}

/// <summary>One white reading of a match, kept in the run's transcript.</summary>
/// <param name="What">"reference", "monitor", "user mode", "drive" or "gains".</param>
/// <param name="Gains">The gains set when it was read, when known.</param>
/// <param name="Drive">The codes shown, for the drive search.</param>
/// <param name="Difference">Against the reference (none for the reference itself).</param>
public sealed record WhiteStep(string What, RgbGains? Gains, DeviceRgb? Drive, Vec3 Encoded, Vec3 Slope, double ExposureSeconds, WhiteDifference? Difference);

/// <summary>What a white match found and did.</summary>
/// <param name="Before">The white as it was, against the reference (null when the camera never read it).</param>
/// <param name="After">The white at the end (null when the match stopped before it was read again).</param>
/// <param name="Matched">The white ended within the tolerance of the reference.</param>
/// <param name="Finished">False when the user stopped it (or the camera could not see a screen).</param>
/// <param name="GainsBefore">The gains before the run (in the preset it started in), when known.</param>
/// <param name="GainsAfter">The gains at the end, when they were set.</param>
/// <param name="Steps">Every white reading, for the transcript.</param>
public sealed record WhiteMatchResult(
    string ReferenceName,
    WhiteDifference? Before,
    WhiteDifference? After,
    bool Matched,
    bool Finished,
    string? PresetBefore,
    string? PresetAfter,
    RgbGains? GainsBefore,
    RgbGains? GainsAfter,
    string? Note = null,
    IReadOnlyList<WhiteStep>? Steps = null,
    bool LimitReached = false);

/// <summary>
/// Sets a monitor's white to a reference screen's with its red, green and blue gains. The camera reads the reference's
/// white, then this monitor's (the user moves it between the two screens): one camera on both, so its own color errors
/// cancel. If they differ, an automatic search scales this monitor's red and blue drive against green until the camera
/// sees the reference's white; that search also measures how the white follows the drive, which turns each remaining
/// difference into whole-step gains. Each gain change is measured, and what it really did refines the next one.
/// </summary>
public sealed class WhiteMatcher(MeasurementRig rig, IWhiteMatchStage stage, IWhiteMatchUser user, WhiteMatchOptions? options = null)
{
    public const string Topic = "White";

    public const string PresetTopic = "Color temperature";

    public const string GainsTopic = "Red, green, blue";

    private const string UserPreset = "User";

    private readonly MeasurementRig _rig = rig ?? throw new ArgumentNullException(nameof(rig));
    private readonly IWhiteMatchStage _stage = stage ?? throw new ArgumentNullException(nameof(stage));
    private readonly IWhiteMatchUser _user = user ?? throw new ArgumentNullException(nameof(user));
    private readonly WhiteMatchOptions _options = options ?? new WhiteMatchOptions();

    /// <summary>A color over the whole screen, so the camera reads it wherever it rests.</summary>
    public static PatchSpec FullScreen(DeviceRgb color) => new SolidPatch(color) { Layout = new PatchLayout(Surround: color) };

    /// <param name="displayName">The monitor being set, as the user knows it.</param>
    /// <param name="referenceName">The reference screen, as the user knows it (e.g. its model name).</param>
    /// <param name="current">Its color preset and gains before the run (from the settings questionnaire).</param>
    /// <param name="gainMax">The gains' highest value in the menu.</param>
    public async Task<WhiteMatchResult> RunAsync(
        string displayName,
        string referenceName,
        ColorSettings current,
        int gainMax,
        IProgress<Advice>? advised,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        var o = _options;
        var steps = new List<WhiteStep>();
        RgbGains? gainsBefore = current is { Red: { } red0, Green: { } green0, Blue: { } blue0 } ? new RgbGains(red0, green0, blue0) : null;
        WhiteMatchResult Stopped(WhiteDifference? before, WhiteDifference? after, string? presetAfter, RgbGains? gainsAfter, string note) =>
            new(referenceName, before, after, Matched: false, Finished: false, current.Preset, presetAfter, gainsBefore, gainsAfter, note, steps);

        // The reference first. A camera left on this monitor cannot pass for it: its white must read far above its black.
        var reference = await SampleOnAsync(
            onReference: true,
            referenceName,
            new Advice(AdviceKind.Fix, Topic, $"Move the camera to {referenceName}: rest it flat against the screen, anywhere inside the outlined area. SoftCal then shows white there for a few seconds.", ProbeParts.White),
            advised,
            cancellationToken).ConfigureAwait(false);
        if (reference is null)
        {
            return Stopped(null, null, current.Preset, null, $"The camera did not read the white of {referenceName}.");
        }

        steps.Add(new WhiteStep("reference", null, null, reference.Encoded, reference.Slope, reference.ExposureSeconds, null));
        var target = await SampleOnAsync(
            onReference: false,
            displayName,
            new Advice(AdviceKind.Fix, Topic, $"Put the camera back on {displayName}: rest it flat against the screen, anywhere inside the outlined area.", ProbeParts.White),
            advised,
            cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return Stopped(null, null, current.Preset, null, $"The camera did not read the white of {displayName}.");
        }

        var before = WhiteMath.Difference(target, reference);
        steps.Add(new WhiteStep("monitor", gainsBefore, null, target.Encoded, target.Slope, target.ExposureSeconds, before));
        log?.Report($"White read unclipped, each at its own exposure: {Level(referenceName, reference)}, {Level(displayName, target)}.");
        log?.Report($"White: {displayName} is {before.Describe(referenceName)}.");
        if (before.Size <= o.Tolerance)
        {
            return new WhiteMatchResult(referenceName, before, before, Matched: true, Finished: true, current.Preset, current.Preset, gainsBefore, gainsBefore, Steps: steps);
        }

        // Where the gains are set: where the monitor is now when SoftCal sets them itself (the first change shows
        // whether the monitor takes them there), otherwise in the user color mode, the only one where most monitors
        // let them be set.
        var presetAfter = current.Preset;
        var (sample, difference) = (target, before);
        RgbGains start;
        var direct = _user.SetsGainsDirectly && gainsBefore is not null;
        if ((current.Kind == ColorPresetKind.User || direct) && gainsBefore is { } known)
        {
            start = known;
        }
        else
        {
            if (await UserModeAsync(current, reference, referenceName, steps, advised, log, cancellationToken).ConfigureAwait(false) is not { } switched)
            {
                return Stopped(before, null, current.Preset, null, "Stopped before the user color mode.");
            }

            (start, sample, difference) = switched;
            presetAfter = current.Kind == ColorPresetKind.User ? current.Preset : UserPreset;
            if (difference.Size <= o.Tolerance)
            {
                return new WhiteMatchResult(referenceName, before, difference, Matched: true, Finished: true, current.Preset, presetAfter, gainsBefore, start, Steps: steps);
            }
        }

        // How the white follows the drive, found by changing the picture: nothing for the user to do.
        advised?.Report(new Advice(AdviceKind.Measure, Topic, "Finding the red, green and blue that match", ProbeParts.White));
        var drive = await SearchDriveAsync(sample, reference, steps, cancellationToken).ConfigureAwait(false);
        log?.Report(string.Create(CultureInfo.InvariantCulture, $"White: matching drive found: red {Math.Exp(drive.Red):0.000} and blue {Math.Exp(drive.Blue):0.000} of green."));

        var effect = drive.Jacobian.Scaled(o.InitialGainEffect);
        var cap = Math.Min(gainMax, start.Max);
        var gains = start;
        var rounds = 0;
        string? note = null;
        while (difference.Size > o.Tolerance && rounds < o.MaxGainRounds)
        {
            var next = WhiteMath.NextGains(gains, difference, effect, cap);
            if (next == gains)
            {
                note = "Whole gain steps cannot get closer.";
                break;
            }

            rounds++;
            var change = new Advice(
                AdviceKind.Change,
                GainsTopic,
                direct ? $"Set Red, Green and Blue to {next}." : $"Set Red, Green and Blue to {next} (in the monitor's User color mode).",
                ProbeParts.White);
            advised?.Report(change);
            if (await _user.SetGainsAsync(next, change, cancellationToken).ConfigureAwait(false) is not { } now)
            {
                return Stopped(before, difference, presetAfter, gains, "Stopped while setting Red, Green and Blue.");
            }

            var measured = await ReadAtGainsAsync(now, sample, reference, steps, advised, cancellationToken).ConfigureAwait(false);
            var (fromRed, fromBlue) = WhiteMath.Ratios(gains);
            var (toRed, toBlue) = WhiteMath.Ratios(now);
            var (expectRed, expectBlue) = effect.Apply(toRed - fromRed, toBlue - fromBlue);
            var (movedRed, movedBlue) = (Math.Log(measured.Red / difference.Red), Math.Log(measured.Blue / difference.Blue));
            var expected = Math.Max(Math.Abs(expectRed), Math.Abs(expectBlue));
            if (direct && expected > 0.01 && Math.Max(Math.Abs(movedRed), Math.Abs(movedBlue)) < 0.25 * expected)
            {
                // The monitor took the values, but its white did not move: this mode keeps its own gains. Put its values
                // back and go on in the user mode.
                log?.Report($"White: the monitor ignores Red, Green and Blue in its current mode; putting {start} back.");
                direct = false;
                _ = await _user.SetGainsAsync(start, new Advice(AdviceKind.Change, GainsTopic, $"Set Red, Green and Blue back to {start}.", ProbeParts.White), cancellationToken).ConfigureAwait(false);
                if (await UserModeAsync(current, reference, referenceName, steps, advised, log, cancellationToken).ConfigureAwait(false) is not { } switched)
                {
                    return Stopped(before, null, current.Preset, start, "Stopped before the user color mode.");
                }

                (gains, sample, difference) = switched;
                (start, presetAfter, cap, rounds) = (gains, current.Kind == ColorPresetKind.User ? current.Preset : UserPreset, Math.Min(gainMax, gains.Max), 0);
                effect = drive.Jacobian.Scaled(o.InitialGainEffect);
                continue;
            }

            effect = effect.Update(toRed - fromRed, toBlue - fromBlue, movedRed, movedBlue);
            (gains, difference) = (now, measured);
            log?.Report(string.Create(CultureInfo.InvariantCulture, $"White at {now}: {measured.Describe(referenceName)} (a gain step acts {effect.RedRed:0.00} / {effect.BlueBlue:0.00})."));
        }

        var atBest = WhiteMath.NextGains(gains, difference, effect, cap) == gains;
        var matched = difference.Size <= o.Tolerance || (atBest && difference.Size <= o.StepTolerance);
        if (!matched && note is null)
        {
            note = atBest
                ? "Whole gain steps cannot get closer."
                : $"After {rounds} changes the white is still {difference.Describe(referenceName)}.";
        }

        return new WhiteMatchResult(referenceName, before, difference, matched, Finished: true, current.Preset, presetAfter, gainsBefore, gains, matched ? null : note, steps, LimitReached: !matched && !atBest);
    }

    /// <summary>Asks for the user color mode (or its values), and reads the white there; null when the user stops.</summary>
    private async Task<(RgbGains Start, WhiteSample Sample, WhiteDifference Difference)?> UserModeAsync(
        ColorSettings current,
        WhiteSample reference,
        string referenceName,
        List<WhiteStep> steps,
        IProgress<Advice>? advised,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var ask = current.Kind == ColorPresetKind.User
            ? new Advice(AdviceKind.Change, PresetTopic, "Open the monitor's User color mode in its menu (Color temperature › User).", ProbeParts.White)
            : new Advice(AdviceKind.Change, PresetTopic, "Set Color temperature to User (on some monitors: User Define or Custom). Only there can Red, Green and Blue be set.", ProbeParts.White);
        advised?.Report(ask);
        if (await _user.SwitchToUserModeAsync(ask, cancellationToken).ConfigureAwait(false) is not { } shown)
        {
            return null;
        }

        // In the user mode the white has changed: read it again, with fresh slopes.
        advised?.Report(new Advice(AdviceKind.Measure, Topic, "Reading the white in the user color mode", ProbeParts.White));
        var sample = await SampleHereAsync(cancellationToken).ConfigureAwait(false);
        var difference = WhiteMath.Difference(sample, reference);
        steps.Add(new WhiteStep("user mode", shown, null, sample.Encoded, sample.Slope, sample.ExposureSeconds, difference));
        log?.Report($"White in the user mode ({shown}): {difference.Describe(referenceName)}.");
        return (shown, sample, difference);
    }

    /// <summary>Reads the white after a gain change, judged with the slopes of the white read before.</summary>
    private async Task<WhiteDifference> ReadAtGainsAsync(RgbGains gains, WhiteSample sample, WhiteSample reference, List<WhiteStep> steps, IProgress<Advice>? advised, CancellationToken cancellationToken)
    {
        advised?.Report(new Advice(AdviceKind.Measure, Topic, F($"Reading the white at {gains}"), ProbeParts.White));
        var white = FullScreen(DeviceRgb.White);
        await _rig.AutoExposeAsync(white, _options.TargetLevel, cancellationToken).ConfigureAwait(false);
        var reading = await _rig.ReadAsync(white, _options.Readings, cancellationToken).ConfigureAwait(false);
        var measured = WhiteMath.Difference(sample.With(reading), reference);
        steps.Add(new WhiteStep("gains", gains, null, reading.Mean, sample.Slope, reading.ExposureSeconds, measured));
        return measured;
    }

    /// <summary>Asks for the camera on one screen (its aiming area shown, the camera's view with it), checks the camera
    /// sees that screen (its white must read far above its black), and reads its white. Asks once more if the camera sees
    /// something else; null when it still does, or the user stops.</summary>
    private async Task<WhiteSample?> SampleOnAsync(bool onReference, string name, Advice move, IProgress<Advice>? advised, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await _stage.AimAtAsync(onReference, cancellationToken).ConfigureAwait(false);
            advised?.Report(move);
            if (!await _user.MoveCameraAsync(move, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            await _stage.UseAsync(onReference, cancellationToken).ConfigureAwait(false);
            advised?.Report(new Advice(AdviceKind.Measure, Topic, $"Reading the white of {name}", ProbeParts.White));
            var sample = await SampleHereAsync(cancellationToken).ConfigureAwait(false);
            var black = await _rig.ReadAsync(FullScreen(DeviceRgb.Black), _options.Readings with { MaxClippedFraction = 1 }, cancellationToken).ConfigureAwait(false);
            if (black.Channel(1) <= sample.Encoded.Y * 0.5)
            {
                return sample;
            }

            move = move with { Message = $"The camera does not see {name}. Rest it flat against that screen, anywhere inside the outlined area." };
        }

        return null;
    }

    /// <summary>"VX2770 SERIES at 43 % of the camera's range (1/8 s)": how high a white read, by its brightest channel.
    /// The comparison takes ratios, so any level well clear of black and of clipping serves; the target is about two
    /// thirds.</summary>
    private string Level(string name, WhiteSample sample)
    {
        var highest = Math.Max(sample.Encoded.X, Math.Max(sample.Encoded.Y, sample.Encoded.Z));
        var seconds = sample.ExposureSeconds;
        var exposure = seconds >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#} s")
            : string.Create(CultureInfo.InvariantCulture, $"1/{1 / seconds:0} s");
        return string.Create(CultureInfo.InvariantCulture, $"{name} at {highest / _rig.Instrument.Capabilities.FullScale * 100:0} % of the camera's range ({exposure})");
    }

    /// <summary>Reads the white where the camera is now: exposed to the target level, then once more one exposure
    /// step shorter for the slopes.</summary>
    private async Task<WhiteSample> SampleHereAsync(CancellationToken cancellationToken)
    {
        var white = FullScreen(DeviceRgb.White);
        var exposure = await _rig.AutoExposeAsync(white, _options.TargetLevel, cancellationToken).ConfigureAwait(false);
        var full = await _rig.ReadAsync(white, _options.Readings, cancellationToken).ConfigureAwait(false);
        var steps = _rig.ExposureSteps;
        var index = Nearest(steps, exposure.NominalSeconds);
        if (index == 0)
        {
            // A camera whose readings are linear (raw data) rises one for one with the light.
            var slope = _rig.Instrument.Capabilities.OutputIsLinear ? 1 : WhiteSample.TypicalSlope;
            var typical = new Vec3(slope, slope, slope);
            return new WhiteSample(full.Mean, typical, full.ExposureSeconds);
        }

        await _rig.SetExposureAsync(steps[index - 1], cancellationToken).ConfigureAwait(false);
        var shorter = await _rig.ReadAsync(white, _options.Readings, cancellationToken).ConfigureAwait(false);
        await _rig.SetExposureAsync(steps[index], cancellationToken).ConfigureAwait(false);
        return WhiteSample.From(full, shorter, steps[index] / steps[index - 1]);
    }

    /// <summary>
    /// Scales the red and blue drive against green (the brightest channel staying at 255) until the camera sees the
    /// reference's white: Newton steps with a matrix learned as it goes. Returns the best drive found (log ratios to
    /// green) and the matrix: how the white follows the drive, the start for how it follows the gains.
    /// </summary>
    private async Task<(double Red, double Blue, WhiteJacobian Jacobian)> SearchDriveAsync(WhiteSample start, WhiteSample reference, List<WhiteStep> steps, CancellationToken cancellationToken)
    {
        var o = _options;
        var jacobian = WhiteJacobian.Initial();
        var start0 = WhiteMath.Difference(start, reference);
        var (driveRed, driveBlue, errorRed, errorBlue) = (0.0, 0.0, Math.Log(start0.Red), Math.Log(start0.Blue));
        var best = (Red: 0.0, Blue: 0.0, Size: start0.Size);
        for (var i = 0; i < o.MaxNullReadings && Math.Max(Math.Abs(errorRed), Math.Abs(errorBlue)) > o.NullTolerance; i++)
        {
            var (stepRed, stepBlue) = jacobian.Undo(errorRed, errorBlue);
            var codes = Drive(Math.Clamp(driveRed + Math.Clamp(stepRed, -0.3, 0.3), -0.8, 0.8), Math.Clamp(driveBlue + Math.Clamp(stepBlue, -0.3, 0.3), -0.8, 0.8));
            var (red, blue) = Ratios(codes);
            if (Math.Abs(red - driveRed) < 1e-9 && Math.Abs(blue - driveBlue) < 1e-9)
            {
                break;
            }

            var reading = await _rig.ReadAsync(FullScreen(codes), o.Readings, cancellationToken).ConfigureAwait(false);
            var measured = WhiteMath.Difference(start.With(reading), reference);
            steps.Add(new WhiteStep("drive", null, codes, reading.Mean, start.Slope, reading.ExposureSeconds, measured));
            var (newRed, newBlue) = (Math.Log(measured.Red), Math.Log(measured.Blue));
            jacobian = jacobian.Update(red - driveRed, blue - driveBlue, newRed - errorRed, newBlue - errorBlue);
            (driveRed, driveBlue, errorRed, errorBlue) = (red, blue, newRed, newBlue);
            if (measured.Size < best.Size)
            {
                best = (red, blue, measured.Size);
            }
        }

        return (best.Red, best.Blue, jacobian);
    }

    /// <summary>Codes for a drive (log ratios to green), the brightest channel at 255.</summary>
    private static DeviceRgb Drive(double red, double blue)
    {
        var m = new[] { Math.Exp(red), 1.0, Math.Exp(blue) };
        var top = m.Max();
        byte Code(double v) => (byte)Math.Clamp(Math.Round(255 * v / top), 1, 255);
        return new DeviceRgb(Code(m[0]), Code(m[1]), Code(m[2]));
    }

    /// <summary>The drive (log ratios to green) that whole codes really give.</summary>
    private static (double Red, double Blue) Ratios(DeviceRgb codes) =>
        (Math.Log((double)codes.R / codes.G), Math.Log((double)codes.B / codes.G));

    private static int Nearest(IReadOnlyList<double> steps, double seconds)
    {
        var best = 0;
        for (var i = 1; i < steps.Count; i++)
        {
            if (Math.Abs(Math.Log(steps[i] / seconds)) < Math.Abs(Math.Log(steps[best] / seconds)))
            {
                best = i;
            }
        }

        return best;
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
