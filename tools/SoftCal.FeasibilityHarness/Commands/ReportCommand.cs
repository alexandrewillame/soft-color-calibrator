using System.Globalization;
using System.Text;
using System.Text.Json;
using SoftCal.Core.Analysis;
using SoftCal.FeasibilityHarness.Infrastructure;

namespace SoftCal.FeasibilityHarness.Commands;

/// <summary>A step result found in a session folder.</summary>
internal sealed record SessionResult(string Session, string Monitor, string Step, DateTimeOffset Created, JsonElement Value);

/// <summary>Collects the recorded results and writes the M1 feasibility report.</summary>
internal static class ReportCommand
{
    public static int Run(Arguments args)
    {
        var root = args.Get("sessions") ?? HarnessSession.DefaultRoot;
        var results = Load(root);
        // Defaults next to the sessions: docs/m1-feasibility-report.md is the curated report and is not overwritten.
        var output = args.Get("out") ?? Path.Combine(root, "gate-report.md");
        var markdown = ReportBuilder.Build(results, DateTimeOffset.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, markdown, new UTF8Encoding(false));
        Ui.Good($"wrote {output} from {results.Select(r => r.Session).Distinct().Count()} sessions");
        return 0;
    }

    public static IReadOnlyList<SessionResult> Load(string root)
    {
        var results = new List<SessionResult>();
        if (!Directory.Exists(root))
        {
            return results;
        }

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(folder);
            var monitor = MonitorOf(name);
            var created = Directory.GetCreationTimeUtc(folder);
            var resultsFolder = Path.Combine(folder, "results");
            if (!Directory.Exists(resultsFolder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(resultsFolder, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    results.Add(new SessionResult(name, monitor, Path.GetFileNameWithoutExtension(file), created, doc.RootElement.Clone()));
                }
                catch (JsonException)
                {
                }
            }

            if (ReadSnapshot(folder, "display") is { } display)
            {
                results.Add(new SessionResult(name, monitor, "display", created, display));
            }
        }

        return results;
    }

    /// <summary>The first state snapshot of a kind from a session's event log (context recorded at the start of a run).</summary>
    private static JsonElement? ReadSnapshot(string folder, string kind)
    {
        var events = Path.Combine(folder, "events.jsonl");
        if (!File.Exists(events))
        {
            return null;
        }

        using var stream = new FileStream(events, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        for (var i = 0; i < 50 && reader.ReadLine() is { } line; i++)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "state"
                    && root.TryGetProperty("kind", out var k) && k.GetString() == kind
                    && root.TryGetProperty("state", out var state))
                {
                    return state.Clone();
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Session folders are named yyyyMMdd-HHmmss-command[-mN]; probes cover every monitor.</summary>
    internal static string MonitorOf(string folder)
    {
        var parts = folder.Split('-');
        return parts.Length >= 4 && parts[3].StartsWith('m') ? parts[3] : parts.Length >= 4 && parts[3] == "sim" ? "sim" : "all";
    }
}

/// <summary>Pure: turns recorded results into gate numbers and the Markdown report.</summary>
internal static class ReportBuilder
{
    public static GateMeasurements Measure(IReadOnlyList<SessionResult> results, string monitor)
    {
        ArgumentNullException.ThrowIfNull(results);
        var mine = results.Where(r => r.Monitor == monitor).OrderBy(r => r.Session, StringComparer.Ordinal).ToList();
        JsonElement? Latest(string step) => mine.LastOrDefault(r => r.Step == step && !Skipped(r.Value))?.Value;

        var lockRuns = mine.Where(r => r.Step == "lockwatch").Select(r => r.Value).ToList();
        var holds = mine.Where(r => r.Step == "hold").Select(r => r.Value).ToList();
        var heldMinutes = lockRuns.Where(v => Bool(v, "held") == true).Select(v => Num(v, "minutes") ?? 0)
            .Concat(holds.Where(v => Bool(v, "held") == true).Select(v => Num(v, "minutes") ?? 0))
            .DefaultIfEmpty(double.NaN).Max();
        var changed = lockRuns.Any(v => Bool(v, "held") == false) || holds.Any(v => Bool(v, "held") == false);

        return new GateMeasurements
        {
            MatchedReadingSigma = Num(Latest("null"), "sigma"),
            PatternShapeSpread = Num(Latest("shapes"), "spread"),
            TreeRepeat72 = Num(Latest("tree"), "spread72"),
            TreeRepeat38 = Num(Latest("tree"), "spread38"),
            TreeRepeat21 = Num(Latest("tree"), "spread21"),
            MoveChange = MoveChange(mine),
            RippleBias = Num(Latest("defocus"), "worstBias"),
            GammaSpreadNull = Num(Latest("tree"), "gammaSpread"),
            GammaSpreadBracketing = Num(Latest("ramp"), "gammaSpread"),
            LockHoldMinutes = double.IsNaN(heldMinutes) ? null : heldMinutes,
            LockChangedDuringRun = lockRuns.Count + holds.Count == 0 ? null : changed,
            TrustPassed = Latest("trust") is { } t && t.TryGetProperty("result", out var tr) ? Bool(tr, "passed") : null,
        };
    }

    /// <summary>Largest change of any move-test code between the base phase and the ±1 cm phases.</summary>
    internal static double? MoveChange(IReadOnlyList<SessionResult> results)
    {
        var phases = results.Where(r => r.Step == "move")
            .Select(r => (Phase: r.Value.GetProperty("phase").GetString() ?? "base", Codes: r.Value.GetProperty("codes").EnumerateArray().Select(e => e.GetDouble()).ToArray()))
            .GroupBy(p => p.Phase)
            .ToDictionary(g => g.Key, g => g.Last().Codes);
        if (!phases.TryGetValue("base", out var baseline) || phases.Count < 2)
        {
            return null;
        }

        return phases.Where(p => p.Key != "base").SelectMany(p => p.Value.Zip(baseline, (a, b) => Math.Abs(a - b))).DefaultIfEmpty(double.NaN).Max();
    }

    public static string Build(IReadOnlyList<SessionResult> results, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(results);
        var md = new StringBuilder();
        void Line(FormattableString text) => md.AppendLine(text.ToString(CultureInfo.InvariantCulture));
        md.AppendLine("# M1 feasibility report (webcam instrument)");
        md.AppendLine();
        Line($"Generated {now:yyyy-MM-dd HH:mm} from {results.Select(r => r.Session).Distinct().Count()} recorded harness sessions (`tools/SoftCal.FeasibilityHarness`).");
        md.AppendLine();

        var monitors = results.Select(r => r.Monitor).Where(m => m != "all").Distinct().Order(StringComparer.Ordinal).ToList();
        var outcomes = new List<(string Monitor, GateResult Gate)>();
        foreach (var monitor in monitors)
        {
            outcomes.Add((monitor, FeasibilityGate.Evaluate(Measure(results, monitor))));
        }

        md.AppendLine("## Gate");
        md.AppendLine();
        if (outcomes.Count == 0)
        {
            md.AppendLine("No measurement sessions yet.");
        }
        else
        {
            md.Append("| Criterion | Limit |");
            foreach (var (monitor, _) in outcomes)
            {
                md.Append(CultureInfo.InvariantCulture, $" {MonitorName(results, monitor)} |");
            }

            md.AppendLine();
            md.Append("|---|---|");
            md.AppendLine(string.Concat(outcomes.Select(_ => "---|")));
            var first = outcomes[0].Gate.Criteria;
            for (var i = 0; i < first.Count; i++)
            {
                var c = first[i];
                md.Append(CultureInfo.InvariantCulture, $"| {c.Description} | {Limit(c)} |");
                foreach (var (_, gate) in outcomes)
                {
                    var x = gate.Criteria[i];
                    md.Append(CultureInfo.InvariantCulture, $" {Value(x)} {Mark(x.Status)} |");
                }

                md.AppendLine();
            }

            md.Append("| **Outcome** | |");
            foreach (var (_, gate) in outcomes)
            {
                md.Append(CultureInfo.InvariantCulture, $" **{Outcome(gate.Outcome)}** |");
            }

            md.AppendLine();
            md.AppendLine();
            foreach (var (monitor, gate) in outcomes)
            {
                Line($"- {MonitorName(results, monitor)}: {string.Join(" ", gate.Reasons)}");
            }
        }

        md.AppendLine();
        md.AppendLine("## Supporting measurements");
        md.AppendLine();
        foreach (var monitor in monitors)
        {
            Line($"**{MonitorName(results, monitor)}**");
            md.AppendLine();
            foreach (var line in Details(results.Where(r => r.Monitor == monitor).OrderBy(r => r.Session, StringComparer.Ordinal).ToList()))
            {
                md.AppendLine("- " + line);
            }

            md.AppendLine();
        }

        var probes = results.Where(r => r.Monitor == "all").OrderBy(r => r.Session, StringComparer.Ordinal).ToList();
        if (probes.Count > 0)
        {
            md.AppendLine("## Platform probes");
            md.AppendLine();
            foreach (var line in ProbeLines(probes))
            {
                md.AppendLine("- " + line);
            }

            md.AppendLine();
        }

        return md.ToString();
    }

    private static IEnumerable<string> Details(IReadOnlyList<SessionResult> mine)
    {
        JsonElement? Latest(string step) => mine.LastOrDefault(r => r.Step == step)?.Value;
        if (Latest("lock") is { } lockResult)
        {
            yield return Invariant($"Lock: critical controls locked = {Bool(lockResult, "criticalLocked")}.");
        }

        if (Latest("trust") is { } trust && trust.TryGetProperty("result", out var t))
        {
            yield return Invariant($"Trust test: white after colors moved {Pct(Num(t, "whiteSequenceDeviation"))}, 10 s drift {Pct(Num(t, "stabilityDrift"))} → {(Bool(t, "passed") == true ? "pass" : "fail")}.");
        }

        if (Latest("sync") is { } sync && sync.TryGetProperty("result", out var s))
        {
            yield return Invariant($"Optical latency {Ms(Num(s, "offsetSeconds"))}, residual {Pct(Num(s, "relativeResidual"))} of contrast.");
        }

        if (Latest("flicker") is { } flicker && flicker.TryGetProperty("result", out var f))
        {
            var refresh = Num(Latest("display"), "refreshRateHz");
            var hz = Num(f, "frequencyHz");
            var note = hz is { } h && refresh is { } r && Math.Abs(h - r) <= 0.3 * r
                ? Invariant($" — within the line-time uncertainty of the {r:0} Hz refresh, so most likely the panel's refresh signature rather than backlight PWM")
                : string.Empty;
            yield return Invariant($"Flicker: {(Bool(f, "detected") == true ? "banding detected" : "none detected")}, depth {Pct(Num(f, "modulationDepth"))}{(hz is { } x ? $", ≈ {x:0} Hz" : "")}; line time {Us(Num(flicker, "lineTimeSeconds"))}{note}.");
        }

        if (Latest("defocus") is { } defocus && !Skipped(defocus))
        {
            yield return Invariant($"Defocus: focus {Num(defocus, "focus")}, residual ripple {Pct(Num(defocus, "worstRipple"))}, null bias {Pct(Num(defocus, "worstBias"), "0.000")}.");
        }

        if (Latest("null") is { } n && n.TryGetProperty("result", out var nr))
        {
            yield return Invariant($"50 % null: code {F(Num(nr, "matchedCode"), "0.00")} ± {F(Num(n, "sigma"), "0.000")} (σ over repeats).");
        }

        if (Latest("tree") is { } tree)
        {
            yield return Invariant($"Midpoint tree: gamma (log-log) {Join(tree, "gammas")}, effective {Join(tree, "effectiveGammas")}; worst level σ {F(Num(tree, "worstLevelSigma"), "0.000")} code.");
        }

        if (Latest("ramp") is { } ramp)
        {
            yield return Invariant($"Bracketed 17-step ramp: gamma {Join(ramp, "gammas")} (camera local γ {F(Num(ramp, "cameraGamma"), "0.00")}).");
        }

        if (Latest("gamma") is { } gamma && !Skipped(gamma))
        {
            yield return Invariant($"Gamma ramp: written {Bool(gamma, "written")}, takes effect {Bool(gamma, "tookEffect")} (code 128 seen as {F(Num(gamma, "seenAsCode"), "0.00")}, expected 112), restored {Bool(gamma, "restoredOk")}.");
        }

        if (Latest("window") is { } window)
        {
            yield return Invariant($"Patch window: pixel-exact read-back for every pattern = {Bool(window, "allExact")}.");
        }
    }

    private static IEnumerable<string> ProbeLines(IReadOnlyList<SessionResult> probes)
    {
        JsonElement? Latest(string step) => probes.LastOrDefault(r => r.Step == step)?.Value;
        if (Latest("nightlight") is { } nl)
        {
            yield return "Night light: " + (nl.TryGetProperty("summary", out var s) ? s.GetString() : "?");
        }

        if (Latest("nvapi") is { } nv && nv.TryGetProperty("displays", out var displays))
        {
            foreach (var d in displays.EnumerateArray())
            {
                var current = d.TryGetProperty("current", out var c) && c.ValueKind == JsonValueKind.Object
                    ? $"{Str(c, "colorFormat")}, range {Str(c, "dynamicRange")}, {Str(c, "bitsPerComponent")} bpc"
                    : Str(d, "error");
                yield return $"NVAPI {Str(d, "deviceName")}: {current}";
            }
        }

        if (Latest("ddc") is { } ddc)
        {
            foreach (var m in ddc.EnumerateArray())
            {
                var supported = m.GetProperty("readings").EnumerateArray().Count(r => Bool(r, "supported") == true);
                yield return Invariant($"DDC/CI {Str(m, "friendlyName")}: {supported} of {m.GetProperty("readings").GetArrayLength()} codes answered{(Bool(m, "stalled") == true ? " (stalled)" : "")}.");
            }
        }
    }

    private static string MonitorName(IReadOnlyList<SessionResult> results, string monitor) =>
        results.FirstOrDefault(r => r.Monitor == monitor && r.Step is "window" or "display")?.Value.TryGetProperty("friendlyName", out var n) == true
            ? $"{monitor} ({n.GetString()})"
            : monitor;

    private static bool Skipped(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("skipped", out var s) && s.ValueKind == JsonValueKind.True;

    private static double? Num(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) ? v.ToString() : null;

    private static string Join(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? string.Join(", ", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble().ToString("0.000", CultureInfo.InvariantCulture)))
            : "—";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string F(double? v, string format) => v is null ? "—" : v.Value.ToString(format, CultureInfo.InvariantCulture);

    private static string Pct(double? v, string format = "0.00") => v is null ? "—" : (v.Value * 100).ToString(format, CultureInfo.InvariantCulture) + " %";

    private static string Ms(double? v) => v is null ? "—" : (v.Value * 1000).ToString("0.0", CultureInfo.InvariantCulture) + " ms";

    private static string Us(double? v) => v is null ? "unknown" : (v.Value * 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " µs";

    private static string Limit(GateCriterion c) => c.Id switch
    {
        "ripple" => "< 0.1 %",
        "locks" => "≥ 30 min",
        "gamma" => "≤ 0.03",
        _ => Invariant($"≤ {c.Limit:0.#} {c.Unit}"),
    };

    private static string Value(GateCriterion c) => c.Value is null ? "not measured" : c.Id switch
    {
        "ripple" => Invariant($"{c.Value:0.000} %"),
        "locks" => Invariant($"{c.Value:0} min"),
        "gamma" => Invariant($"{c.Value:0.000}"),
        _ => Invariant($"{c.Value:0.00}"),
    };

    private static string Mark(CriterionStatus status) => status switch
    {
        CriterionStatus.Pass => "✅",
        CriterionStatus.Fail => "❌",
        _ => "⚪",
    };

    private static string Outcome(GateOutcome outcome) => outcome switch
    {
        GateOutcome.Go => "go",
        GateOutcome.GoWithRestrictions => "go with restrictions",
        GateOutcome.BracketingOnly => "bracketing-only",
        GateOutcome.Incomplete => "incomplete",
        _ => "no go",
    };
}
