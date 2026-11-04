using System.Globalization;
using SoftCal.Core.Analysis;

namespace SoftCal.Core.Advisor;

public enum AdviceKind
{
    /// <summary>Set <see cref="Advice.Control"/> to <see cref="Advice.Value"/>.</summary>
    Change,

    /// <summary>Something outside the monitor's controls must be fixed first (GPU range, picture enhancements).</summary>
    Fix,

    /// <summary>Nothing to change: only measure (after changes elsewhere, or for the final check).</summary>
    Measure,

    /// <summary>Finished; <see cref="Advice.Message"/> sums up what changed.</summary>
    Done,
}

/// <summary>One step of the advice, and what to measure once it has been carried out.</summary>
public sealed record Advice(
    AdviceKind Kind,
    string Topic,
    string Message,
    ProbeParts Then = ProbeParts.None,
    OsdControlId? Control = null,
    int? Value = null);

/// <summary>
/// The OSD advisor (plan §8, on the default native white): asks for one change at a time and judges every change by
/// measurement, never by the value the user reports, so a mistyped or misread setting is caught by the next reading.
/// Order: RGB range and picture enhancements, then contrast (clipping distorts the tone readings), then the gamma
/// preset, then the shadows (a preset moves the black crush, so it is judged on the chosen preset), then a final check
/// of everything.
/// </summary>
public sealed class OsdAdvisor
{
    /// <summary>Every user action costs the user a trip to the OSD; a run that needs more has gone wrong.</summary>
    public const int MaxSteps = 40;

    private const int MaxSearchMoves = 8;

    /// <summary>Two readings of one preset closer than this agree; two presets closer than this look like one.</summary>
    private const double PresetRepeatTolerance = 0.05;

    /// <summary>Crush up to code 4 is accepted: on a γ 2.2 display code 4 gives about 0.01 % of white, at the limit of
    /// what the camera separates at a low backlight, so chasing it would push the shadows up for nothing.</summary>
    private const int ActionableCrush = 6;

    /// <summary>A brightness change that moves the tone by this many codes shifts the signal; a backlight moves none
    /// (the null measurement cancels a uniform change of light).</summary>
    private const double OffsetToneShift = 1.0;

    private readonly MonitorControlProfile _profile;
    private readonly AdvisorTargets _targets;
    private readonly bool _enhancementsOff;
    private readonly bool _enhancementsDetected;
    private readonly List<string> _notes = [];
    private Dictionary<OsdControlId, int>? _initial;
    private int _steps;

    private int _rangeRequests;

    /// <summary>The user checked the RGB range twice and the signature stayed: Brightness and Contrast are tried.</summary>
    private bool _rangeAccepted;
    private bool _cleanUpAsked;
    private bool _boostReset;

    /// <summary>Settings to put back, one change at a time, after a search that did not help (see <see cref="PendingRevert"/>).</summary>
    private readonly Queue<(OsdControlId Control, int Value, string Topic, string Message, ProbeParts Then)> _reverts = new();

    /// <summary>The note about the tone when there is no gamma preset: replaced by the latest reading's, never piled up.</summary>
    private string? _toneNote;

    private bool _contrastDone;
    private int? _contrastClipsAt;
    private int? _contrastCleanAt;
    private bool _contrastMargin;
    private int _contrastMoves;

    /// <summary>Contrast when its search began: a search that cannot stop the clipping puts it back there.</summary>
    private int? _contrastStart;

    /// <summary>The search could not stop the clipping: it is not run again (the final check would only push Contrast
    /// further down).</summary>
    private bool _contrastGaveUp;

    private bool _gammaDone;
    private readonly Dictionary<int, List<PresetTone>> _presetReadings = [];
    private readonly HashSet<int> _presetsRechecked = [];
    private IReadOnlyList<Measurement.TreeLevel>? _lastRecordedTone;
    private int _gammaMoves;

    /// <summary>The chosen preset's gamma and the Brightness it was measured at, when the choice was made: an offset
    /// Brightness changed afterwards moves the tone, which the summary then says.</summary>
    private (double Gamma, int? Brightness)? _gammaChoice;

    private bool _shadowsDone;

    /// <summary>No setting lifted the darkest steps: the search is not run again, and what it moved is put back.</summary>
    private bool _shadowsGaveUp;

    /// <summary>The shadow boost when the shadow step first raised it.</summary>
    private int? _boostStart;
    private BrightnessEffect _brightnessEffect;
    private int _brightnessOriginal;
    private bool _crushedBeforeTest;
    private double _toneBeforeTest;
    private int? _brightnessCrushedAt;
    private int? _brightnessCleanAt;
    private int _brightnessMoves;
    private int _boostMoves;

    /// <param name="enhancementsOff">False asks first for picture enhancements to be turned off.</param>
    /// <param name="enhancementsDetected">They were detected (the brightness followed the picture), not just possible:
    /// the question then says so.</param>
    public OsdAdvisor(MonitorControlProfile profile, AdvisorTargets? targets = null, bool enhancementsOff = true, bool enhancementsDetected = false)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _targets = targets ?? new AdvisorTargets();
        _enhancementsOff = enhancementsOff;
        _enhancementsDetected = enhancementsDetected;
    }

    private enum BrightnessEffect
    {
        Unknown,
        Testing,
        Offset,
        Backlight,
    }

    /// <summary>Things the advisor could not fix, for the summary.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Whether Brightness turned out to set the backlight (true) or shift the signal (false); null when the
    /// run did not need to find out.</summary>
    public bool? BrightnessSetsBacklight => _brightnessEffect switch
    {
        BrightnessEffect.Backlight => true,
        BrightnessEffect.Offset => false,
        _ => null,
    };

    /// <summary>The gamma measured for each preset tried (the readings that agree), by preset index.</summary>
    public IReadOnlyDictionary<int, double> PresetGammas =>
        _presetReadings.Select(p => (p.Key, Tone: Settled(p.Value))).Where(p => p.Tone is not null).ToDictionary(p => p.Key, p => p.Tone!.Gamma);

    /// <summary>The next step, given the monitor's settings (as reported) and the readings taken at those settings.</summary>
    public Advice Next(IReadOnlyDictionary<OsdControlId, int> settings, AdvisorReadings readings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(readings);
        _initial ??= settings.ToDictionary(s => s.Key, s => s.Value);
        if (++_steps > MaxSteps)
        {
            Note(F($"Stopped after {MaxSteps} steps; the settings below are the last ones measured."));
            return Done(settings);
        }

        return PendingRevert(settings)
            ?? Range(readings)
            ?? CleanUp(settings)
            ?? Contrast(settings, readings)
            ?? GammaPreset(settings, readings)
            ?? Shadows(settings, readings)
            ?? Verify(readings)
            ?? Done(settings);
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>The final check re-runs every rule, so the same note can come up twice; keep it once.</summary>
    private void Note(string note)
    {
        if (!_notes.Contains(note))
        {
            _notes.Add(note);
        }
    }

    /// <summary>Replaces a note kept in <paramref name="slot"/> (a reading's latest word on one thing).</summary>
    private void ReplaceNote(ref string? slot, string note)
    {
        if (slot is not null)
        {
            _notes.Remove(slot);
        }

        slot = note;
        Note(note);
    }

    /// <summary>The next setting to put back after a search that did not help; null when none is left.</summary>
    private Advice? PendingRevert(IReadOnlyDictionary<OsdControlId, int> settings)
    {
        while (_reverts.TryDequeue(out var revert))
        {
            if (_profile.Find(revert.Control) is { } control && settings.TryGetValue(control.Id, out var now) && now != revert.Value)
            {
                return Change(control, revert.Value, revert.Topic, revert.Message, revert.Then);
            }
        }

        return null;
    }

    private static Advice Measure(ProbeParts parts, string topic, string message) => new(AdviceKind.Measure, topic, message, parts);

    private static Advice Change(OsdControl control, int value, string topic, string message, ProbeParts then) =>
        new(AdviceKind.Change, topic, message, then, control.Id, value);

    private Advice? Range(AdvisorReadings readings)
    {
        if (_rangeAccepted || !readings.IsFresh(ProbeParts.Shadows | ProbeParts.Highlights) || !readings.Steps.LooksLikeRangeMismatch)
        {
            return null;
        }

        const string Message = "The darkest and brightest steps both merge: the graphics card and the monitor may disagree on the RGB range. "
            + "Check that the graphics card sends full range (NVIDIA Control Panel › Change resolution › Output dynamic range: Full) and the monitor's "
            + "input range is full (or both limited), then click Done.";
        if (++_rangeRequests > 2)
        {
            // Checked twice and unchanged: the range is right, and the monitor's own Brightness and Contrast can make the
            // same signature (an offset brightness crushing the shadows, a high contrast merging the highlights).
            Note("The darkest and brightest steps both merged with the RGB range checked, so Brightness and Contrast were tried instead.");
            _rangeAccepted = true;
            return null;
        }

        return new Advice(AdviceKind.Fix, "RGB range", Message, ProbeParts.All);
    }

    private Advice? CleanUp(IReadOnlyDictionary<OsdControlId, int> settings)
    {
        if (!_enhancementsOff && !_cleanUpAsked)
        {
            _cleanUpAsked = true;
            return new Advice(
                AdviceKind.Fix,
                "Picture enhancements",
                _enhancementsDetected
                    ? "The monitor's brightness changed with the picture. Turn off dynamic contrast, ECO / light sensor and similar features in its menu, then click Done."
                    : "Turn off dynamic contrast, ECO / light sensor and low blue light, and set sharpness to its neutral value: they change the picture from one moment to the next.",
                ProbeParts.All);
        }

        // Start the shadows from no boost (decided once, on the starting settings); the shadow step adds back only what
        // the crush needs.
        if (_boostReset)
        {
            return null;
        }

        _boostReset = true;
        if (_profile.Find(OsdControlId.BlackBoost) is { } boost && settings.TryGetValue(boost.Id, out var b) && b > boost.Min)
        {
            return Change(boost, boost.Min, "Picture enhancements", F($"Set {boost.Name} to {boost.Describe(boost.Min)} for now (it is {boost.Describe(b)}); the shadow step adds back only what is needed."), ProbeParts.Shadows);
        }

        return null;
    }

    private Advice? Contrast(IReadOnlyDictionary<OsdControlId, int> settings, AdvisorReadings readings)
    {
        if (_contrastDone && !readings.IsFresh(ProbeParts.All))
        {
            return null;
        }

        if (!readings.IsFresh(ProbeParts.Highlights))
        {
            return Measure(ProbeParts.Highlights, "Highlights", "Checking the brightest steps.");
        }

        var steps = readings.Steps;
        var clips = steps.AnyClipping;
        if (_contrastDone)
        {
            // A search that could not stop the clipping is not run again: the final check would only push Contrast down.
            if (!clips || _contrastGaveUp)
            {
                return null;
            }

            // Clipping came back (a gamma preset can change it): search again from here.
            (_contrastDone, _contrastClipsAt, _contrastCleanAt, _contrastMargin, _contrastMoves, _contrastStart) = (false, null, null, false, 0, null);
        }

        var control = _profile.Find(OsdControlId.Contrast);
        if (control is null || !settings.TryGetValue(control.Id, out var value))
        {
            if (clips)
            {
                Note("The brightest steps merge and there is no contrast setting to lower: check the monitor's color mode and the graphics card's range.");
            }

            _contrastDone = true;
            return null;
        }

        _contrastStart ??= value;
        if (clips)
        {
            _contrastClipsAt = Math.Min(_contrastClipsAt ?? int.MaxValue, value);
        }
        else
        {
            _contrastCleanAt = Math.Max(_contrastCleanAt ?? int.MinValue, value);
        }

        if (_contrastMoves >= MaxSearchMoves)
        {
            if (clips)
            {
                return GiveUpContrast(control, value, settings, F($"Highlights still clip at {control.Name} {value}"));
            }

            _contrastDone = true;
            return null;
        }

        if (clips)
        {
            if (value <= control.Min)
            {
                return GiveUpContrast(control, value, settings, F($"Highlights clip even at the lowest {control.Name}"));
            }

            var next = _contrastCleanAt is { } clean && clean < value ? (clean + value) / 2 : value - control.SearchStep;
            next = Math.Clamp(next, control.Min, value - 1);
            _contrastMoves++;
            return Change(control, next, "Highlights", F($"Lower {control.Name} to {next} (now {value}): the brightest steps merge{Channels(steps)}, so highlight detail is lost."), ProbeParts.Highlights);
        }

        if (_contrastClipsAt is { } clipsAt && clipsAt - value > 2)
        {
            var next = (value + clipsAt) / 2;
            _contrastMoves++;
            return Change(control, next, "Highlights", F($"Raise {control.Name} to {next} (now {value}): highlights are clean here, so SoftCal looks for the highest setting that keeps them apart."), ProbeParts.Highlights);
        }

        if (_contrastClipsAt is { } edge && edge - value == 1 && !_contrastMargin && value - 1 >= control.Min)
        {
            _contrastMargin = true;
            return Change(control, value - 1, "Highlights", F($"Set {control.Name} to {value - 1}: {value} is right at the edge, one step lower leaves a margin."), ProbeParts.Highlights);
        }

        _contrastDone = true;
        return null;
    }

    /// <summary>Lowering Contrast did not stop the clipping, so it is not the cause: it goes back to where it was.</summary>
    private Advice? GiveUpContrast(OsdControl control, int value, IReadOnlyDictionary<OsdControlId, int> settings, string finding)
    {
        (_contrastDone, _contrastGaveUp) = (true, true);
        var start = _contrastStart ?? value;
        if (start == value)
        {
            Note(finding + ": check the monitor's color mode and the graphics card's range.");
            return null;
        }

        Note(F($"{finding}, so {control.Name} was put back to {control.Describe(start)}: check the monitor's color mode and the graphics card's range."));
        _reverts.Enqueue((control.Id, start, "Highlights", F($"Put {control.Name} back to {control.Describe(start)} (now {control.Describe(value)}): lowering it does not stop the brightest steps merging, so it is not the cause."), ProbeParts.Highlights));
        return PendingRevert(settings);
    }

    private static string Channels(StepSummary steps)
    {
        if (steps.WhiteClipFromCode is not null || steps.ChannelClipFromCode is null)
        {
            return string.Empty;
        }

        var names = steps.ChannelClipFromCode.Select((c, i) => c is null ? null : new[] { "red", "green", "blue" }[i]).OfType<string>().ToList();
        return names.Count > 0 ? " in " + string.Join(" and ", names) : string.Empty;
    }

    private Advice? GammaPreset(IReadOnlyDictionary<OsdControlId, int> settings, AdvisorReadings readings)
    {
        var control = _profile.Find(OsdControlId.GammaPreset);
        if (control is not { IsPreset: true } || !_targets.ChooseGammaPreset || !settings.TryGetValue(control.Id, out var current))
        {
            // Without presets there is nothing to change, but an off-target tone must not end as "nothing needed".
            if (_targets.ChooseGammaPreset && readings.IsFresh(ProbeParts.Tone) && readings.Tone.Count > 0)
            {
                var half = readings.Tone.MinBy(l => l.Level)!;
                var target = AdvisorMeter.TargetCode(half.Fraction, _targets.Gamma);
                if (Math.Abs(half.Null.MatchedCode - target) > AdvisorMeter.Tolerance(half.Fraction, _targets.Gamma))
                {
                    ReplaceNote(ref _toneNote, F($"Half of white comes at code {half.Null.MatchedCode:0} instead of {target:0} for gamma {_targets.Gamma:0.0}: mid-tones look {(half.Null.MatchedCode > target ? "darker" : "lighter")} than intended. Only a gamma setting changes that; if the monitor's menu has one, add it under 'More settings'."));
                }
                else if (_toneNote is not null)
                {
                    _notes.Remove(_toneNote);
                    _toneNote = null;
                }
            }

            return null;
        }

        if (_gammaDone && !readings.IsFresh(ProbeParts.All))
        {
            return null;
        }

        if (!readings.IsFresh(ProbeParts.Tone))
        {
            return Measure(ProbeParts.Tone, "Gamma", F($"Measuring the gamma of {control.Describe(current)}."));
        }

        // Every reading is kept per preset (as reported): a preset set wrong at the monitor, or reported wrong, shows up
        // as two readings of one preset that disagree, or as two presets that read the same.
        if (!ReferenceEquals(readings.Tone, _lastRecordedTone))
        {
            _lastRecordedTone = readings.Tone;
            if (!_presetReadings.TryGetValue(current, out var list))
            {
                _presetReadings[current] = list = [];
            }

            list.Add(PresetTone.From(readings.Tone, _targets.Gamma) with { Brightness = settings.TryGetValue(OsdControlId.Brightness, out var level) ? level : null });
        }

        if (_gammaDone)
        {
            if (Settled(_presetReadings[current]) is { } now)
            {
                // An offset Brightness changed after the choice moves the tone: the summary says so (comparing every
                // preset again would cost a trip to the menu each).
                if (_brightnessEffect == BrightnessEffect.Offset && _gammaChoice is { } choice
                    && now.Brightness != choice.Brightness && Math.Abs(now.Gamma - choice.Gamma) > PresetRepeatTolerance)
                {
                    Note(F($"Brightness changed after {control.Describe(current)} was chosen, and on this monitor it moves the tone: its gamma is now {now.Gamma:0.00} (it was {choice.Gamma:0.00}). Measure again to compare the presets at this brightness."));
                }

                return null;
            }

            _gammaDone = false;
        }

        var original = _initial!.TryGetValue(control.Id, out var first) ? first : current;
        var count = control.Presets!.Count;
        if (_gammaMoves >= 3 * count)
        {
            // Readings that never settle cannot pick a preset: the monitor goes back to the user's own.
            Note(F($"The gamma presets' readings kept disagreeing, so {control.Name} stays at {control.Describe(original)}."));
            _gammaDone = true;
            if (current != original)
            {
                return Change(control, original, "Gamma", F($"Set {control.Name} back to {control.Describe(original)}: the presets' readings kept disagreeing, so none is clearly better."), ProbeParts.Tone);
            }

            return null;
        }

        Advice Set(int preset, string why)
        {
            _gammaMoves++;
            return Change(control, preset, "Gamma", F($"Set {control.Name} to {control.Describe(preset)} {why}"), ProbeParts.Tone);
        }

        var unsettled = _presetReadings.Where(p => Settled(p.Value) is null).Select(p => (int?)p.Key).FirstOrDefault();
        if (unsettled is { } again)
        {
            return Set(again, "again: its last readings disagree, so one of them was probably taken on another preset.");
        }

        var missing = Enumerable.Range(0, count).Where(i => !_presetReadings.ContainsKey(i)).Select(i => (int?)i).FirstOrDefault();
        if (missing is { } next)
        {
            return Set(next, F($"so SoftCal can measure it ({_presetReadings.Count + 1} of {count})."));
        }

        var settled = _presetReadings.ToDictionary(p => p.Key, p => Settled(p.Value)!);

        // Every preset reads the same: they make no difference here (the monitor's other settings may fix the gamma),
        // which is no mistake to check again, and no reason to change.
        var spread = settled.Values.Max(t => t.Gamma) - settled.Values.Min(t => t.Gamma);
        if (count >= 3 && spread < PresetRepeatTolerance)
        {
            Note(F($"All {count} {control.Name} presets measure the same (gamma {settled.Values.Average(t => t.Gamma):0.00}): they make no difference with the monitor set as it is."));
            return Choose(control, original, current, settled, original == current ? null : "again: the presets all measure the same, so the one you had stays.");
        }

        var twin = settled
            .SelectMany(a => settled.Where(b => b.Key > a.Key && Math.Abs(a.Value.Gamma - b.Value.Gamma) < PresetRepeatTolerance / 2).Select(b => b.Key))
            .Where(k => _presetReadings[k].Count == 1 && !_presetsRechecked.Contains(k))
            .Select(k => (int?)k)
            .FirstOrDefault();
        if (twin is { } recheck)
        {
            _presetsRechecked.Add(recheck);
            return Set(recheck, "again: it read the same as another preset, which usually means one of them was not the preset intended.");
        }

        // The closest gamma. The user's own preset stays unless another is clearly closer: beyond what two readings of one
        // preset differ by. Among presets as close as each other, the most neutral mid-gray.
        var bestScore = settled.Values.Min(t => t.Score);
        if (settled.TryGetValue(original, out var own) && own.Score <= bestScore + PresetRepeatTolerance)
        {
            return Choose(control, original, current, settled, F($"again: no other preset is clearly closer to gamma {_targets.Gamma:0.0} (it measures {own.Gamma:0.00})."));
        }

        var best = settled.Where(p => p.Value.Score <= bestScore + 0.01).OrderBy(p => p.Value.Imbalance).ThenBy(p => p.Value.Score).First();
        return Choose(control, best.Key, current, settled, F($"because its gamma ({best.Value.Gamma:0.00}) is clearly the closest to {_targets.Gamma:0.0}."));
    }

    /// <summary>Ends the preset survey on <paramref name="chosen"/>: asks for it when the monitor is elsewhere, and keeps
    /// what it measured to tell, later, whether the tone moved.</summary>
    private Advice? Choose(OsdControl control, int chosen, int current, Dictionary<int, PresetTone> settled, string? why)
    {
        if (chosen != current)
        {
            _gammaMoves++;
            return Change(control, chosen, "Gamma", F($"Set {control.Name} to {control.Describe(chosen)} {why}"), ProbeParts.Tone);
        }

        _gammaDone = true;
        _gammaChoice = (settled[chosen].Gamma, settled[chosen].Brightness);
        return null;
    }

    /// <summary>A preset's reading: the only one, or the newest pair among its last three that agree; null when its
    /// last readings disagree.</summary>
    private static PresetTone? Settled(List<PresetTone> readings)
    {
        // A reading at another Brightness is of another tone when Brightness shifts the signal: only those at the newest
        // reading's Brightness count.
        var latest = readings[^1].Brightness;
        var same = readings.Where(r => r.Brightness == latest).ToList();
        if (same.Count == 1)
        {
            return same[0];
        }

        var recent = same.TakeLast(3).ToList();
        for (var j = recent.Count - 1; j > 0; j--)
        {
            for (var i = j - 1; i >= 0; i--)
            {
                if (Math.Abs(recent[i].Gamma - recent[j].Gamma) <= PresetRepeatTolerance)
                {
                    return new PresetTone((recent[i].Gamma + recent[j].Gamma) / 2, (recent[i].Score + recent[j].Score) / 2, (recent[i].Imbalance + recent[j].Imbalance) / 2, latest);
                }
            }
        }

        return null;
    }

    private Advice? Shadows(IReadOnlyDictionary<OsdControlId, int> settings, AdvisorReadings readings)
    {
        if (_shadowsDone && !readings.IsFresh(ProbeParts.All))
        {
            return null;
        }

        if (!readings.IsFresh(ProbeParts.Shadows))
        {
            return Measure(ProbeParts.Shadows, "Shadows", "Checking the darkest steps.");
        }

        var crush = readings.Steps.BlackCrushUpToCode;
        var crushed = crush >= ActionableCrush;
        if (_shadowsDone)
        {
            // A search that could not lift the darkest steps is not run again: it would only push the settings further.
            if (!crushed || _shadowsGaveUp)
            {
                return null;
            }

            _shadowsDone = false;
            (_brightnessCrushedAt, _brightnessCleanAt, _brightnessMoves, _boostMoves) = (null, null, 0, 0);
        }

        var brightness = _profile.Find(OsdControlId.Brightness);
        var hasBrightness = brightness is not null && settings.ContainsKey(OsdControlId.Brightness);
        if (_brightnessEffect == BrightnessEffect.Testing && hasBrightness)
        {
            if (!readings.IsFresh(ProbeParts.Tone))
            {
                return Measure(ProbeParts.Tone, "Shadows", "Measuring the tone at the raised brightness.");
            }

            if (Math.Abs(DeepestTone(readings) - _toneBeforeTest) >= OffsetToneShift)
            {
                _brightnessEffect = BrightnessEffect.Offset;
                _brightnessCrushedAt = _crushedBeforeTest ? _brightnessOriginal : null;
            }
            else
            {
                _brightnessEffect = BrightnessEffect.Backlight;
                return Change(brightness!, _brightnessOriginal, "Shadows", F($"Put {brightness!.Name} back to {_brightnessOriginal}: on this monitor it sets the backlight, which does not change the darkest steps. Choose it to taste later."), ProbeParts.Shadows);
            }
        }

        if (_brightnessEffect == BrightnessEffect.Offset && hasBrightness && _brightnessMoves < MaxSearchMoves)
        {
            // An offset is searched against every step, code 4 included: it compares settings at one backlight, and
            // the lowest setting that keeps code 4 apart is the monitor's own black point. Only this search goes that
            // far: past it, a crush up to code 4 is accepted as everywhere else (the boost is never raised for it).
            var anyCrush = crush is not null;
            var value = settings[OsdControlId.Brightness];
            if (anyCrush)
            {
                _brightnessCrushedAt = Math.Max(_brightnessCrushedAt ?? int.MinValue, value);
            }
            else
            {
                _brightnessCleanAt = Math.Min(_brightnessCleanAt ?? int.MaxValue, value);
            }

            if (anyCrush && value < brightness!.Max)
            {
                var next = _brightnessCleanAt is { } clean && clean > value ? (value + clean + 1) / 2 : value + brightness.SearchStep;
                next = Math.Clamp(next, value + 1, brightness.Max);
                _brightnessMoves++;
                return Change(brightness, next, "Shadows", F($"Raise {brightness.Name} to {next} (now {value}): codes 0–{crush} still look the same as black."), ProbeParts.Shadows);
            }

            // Clean: an offset brightness also lifts black, so come down to the lowest setting that keeps the steps.
            if (!anyCrush && _brightnessCrushedAt is { } low && value - low > 2)
            {
                var next = (value + low + 1) / 2;
                _brightnessMoves++;
                return Change(brightness!, next, "Shadows", F($"Lower {brightness!.Name} to {next} (now {value}): the darkest steps are visible, and a lower brightness keeps black deeper."), ProbeParts.Shadows);
            }

            if (!anyCrush && _brightnessCleanAt is { } lowest && lowest != value)
            {
                _brightnessMoves++;
                return Change(brightness!, lowest, "Shadows", F($"Set {brightness!.Name} to {lowest}: the lowest setting measured that keeps the darkest steps apart."), ProbeParts.Shadows);
            }
        }

        if (!crushed)
        {
            _shadowsDone = true;
            return null;
        }

        if (_brightnessEffect == BrightnessEffect.Unknown && hasBrightness)
        {
            var value = settings[OsdControlId.Brightness];
            var next = Math.Min(brightness!.Max, value + Math.Max(10, brightness.SearchStep));
            if (next > value)
            {
                if (!readings.IsFresh(ProbeParts.Tone))
                {
                    return Measure(ProbeParts.Tone, "Shadows", "Measuring the tone before testing what brightness does.");
                }

                (_brightnessEffect, _brightnessOriginal, _crushedBeforeTest, _toneBeforeTest) = (BrightnessEffect.Testing, value, crushed, DeepestTone(readings));
                return Change(brightness, next, "Shadows", F($"Raise {brightness.Name} to {next} for a moment (now {value}): codes 0–{crush} look the same as black, and SoftCal checks whether brightness lifts them."), ProbeParts.Shadows | ProbeParts.Tone);
            }

            _brightnessEffect = BrightnessEffect.Backlight;
        }

        if (_profile.Find(OsdControlId.BlackBoost) is { } boost && settings.TryGetValue(boost.Id, out var b) && b < boost.Max && _boostMoves < MaxSearchMoves)
        {
            _boostStart ??= b;
            var next = boost.Clamp(b + 1);
            _boostMoves++;
            return Change(boost, next, "Shadows", F($"Raise {boost.Name} to {boost.Describe(next)} (now {boost.Describe(b)}): codes 0–{crush} still look the same as black."), ProbeParts.Shadows);
        }

        // Nothing lifted them: what the search moved goes back, and it is not run again.
        (_shadowsDone, _shadowsGaveUp) = (true, true);
        var putBack = false;
        if (_profile.Find(OsdControlId.BlackBoost) is { } raised && _boostStart is { } boostStart && settings.TryGetValue(raised.Id, out var boostNow) && boostNow != boostStart)
        {
            _reverts.Enqueue((raised.Id, boostStart, "Shadows", F($"Put {raised.Name} back to {raised.Describe(boostStart)} (now {raised.Describe(boostNow)}): raising it does not lift the darkest steps."), ProbeParts.Shadows));
            putBack = true;
        }

        if (_brightnessEffect == BrightnessEffect.Offset && brightness is not null && settings.TryGetValue(brightness.Id, out var brightnessNow) && brightnessNow != _brightnessOriginal)
        {
            _reverts.Enqueue((brightness.Id, _brightnessOriginal, "Shadows", F($"Put {brightness.Name} back to {brightness.Describe(_brightnessOriginal)} (now {brightness.Describe(brightnessNow)}): raising it does not separate the darkest steps, and it greys black."), ProbeParts.Shadows));
            putBack = true;
        }

        Note(F($"Codes 0–{crush} look the same as black, and none of these settings lifts them{(putBack ? ", so they were put back" : string.Empty)}."));
        return PendingRevert(settings);
    }

    /// <summary>The matched code of the darkest tone level measured (the one an offset moves most).</summary>
    private static double DeepestTone(AdvisorReadings readings) => readings.Tone.Count > 0 ? readings.Tone.MaxBy(l => l.Level)!.Null.MatchedCode : double.NaN;

    private static Advice? Verify(AdvisorReadings readings) =>
        readings.IsFresh(ProbeParts.All) ? null : Measure(ProbeParts.All, "Final check", "Measuring everything once more with the new settings.");

    private Advice Done(IReadOnlyDictionary<OsdControlId, int> settings)
    {
        var changes = _profile.Controls
            .Where(c => settings.TryGetValue(c.Id, out var now) && _initial!.TryGetValue(c.Id, out var was) && now != was)
            .Select(c => F($"{c.Name} {c.Describe(_initial![c.Id])} → {c.Describe(settings[c.Id])}"))
            .ToList();
        var summary = changes.Count == 0 ? "No setting needed to change." : "Changed: " + string.Join("; ", changes) + ".";
        if (_notes.Count > 0)
        {
            summary += " " + string.Join(" ", _notes);
        }

        return new Advice(AdviceKind.Done, "Done", summary);
    }

    /// <summary>How well one gamma preset's measured tone fits the target.</summary>
    private sealed record PresetTone(double Gamma, double Score, double Imbalance, int? Brightness = null)
    {
        public static PresetTone From(IReadOnlyList<Measurement.TreeLevel> tone, double target)
        {
            var gammas = tone.Select(l => Math.Log(l.Fraction) / Math.Log(l.Null.MatchedCode / 255)).ToList();
            var score = Math.Sqrt(gammas.Average(g => (g - target) * (g - target)));
            var balance = DisplayAnalysis.GrayBalance(tone);
            return new PresetTone(gammas.Average(), score, balance.Count > 0 ? balance[0].Imbalance : 0);
        }
    }
}
