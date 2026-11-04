using System.Text.Json;
using AwesomeAssertions;
using SoftCal.Core.Analysis;
using SoftCal.Core.Sessions;
using SoftCal.FeasibilityHarness.Commands;
using SoftCal.FeasibilityHarness.Infrastructure;
using Xunit;

namespace SoftCal.FeasibilityHarness.Tests;

public sealed class HarnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "softcal-harness-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Arguments_separate_commands_steps_options_and_flags()
    {
        var a = Arguments.Parse(["run", "tree", "ramp", "--monitor", "2", "--allow-camera", "--runs=3", "--phase", "plus"]);

        a.Command.Should().Be("run");
        a.Positional.Should().Equal("tree", "ramp");
        a.Get("monitor").Should().Be("2");
        a.GetInt("runs", 1).Should().Be(3);
        a.Get("phase").Should().Be("plus");
        a.Has("allow-camera").Should().BeTrue();
        a.Has("simulated").Should().BeFalse();
        a.GetDouble("minutes", 30).Should().Be(30);
    }

    [Theory]
    [InlineData("20261015-101500-full-m1", "m1")]
    [InlineData("20261015-101500-run-m2-2", "m2")]
    [InlineData("20261015-101500-full-sim", "sim")]
    [InlineData("20261015-101500-probe", "all")]
    public void Sessions_are_attributed_to_a_monitor_by_folder_name(string folder, string monitor) =>
        ReportCommand.MonitorOf(folder).Should().Be(monitor);

    [Fact]
    public void The_report_combines_the_latest_results_per_monitor_into_the_gate()
    {
        static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, HarnessSession.Json);
        var now = DateTimeOffset.UtcNow;
        SessionResult[] results =
        [
            new("20261015-100000-full-m1", "m1", "null", now, J(new { sigma = 0.5 })),
            new("20261015-110000-full-m1", "m1", "null", now, J(new { sigma = 0.11 })),
            new("20261015-110000-full-m1", "m1", "shapes", now, J(new { spread = 0.4 })),
            new("20261015-110000-full-m1", "m1", "tree", now, J(new { spread72 = 0.2, spread38 = 0.4, spread21 = 0.9, gammaSpread = 0.01 })),
            new("20261015-110000-full-m1", "m1", "defocus", now, J(new { worstBias = -0.0003 })),
            new("20261015-110000-full-m1", "m1", "ramp", now, J(new { gammaSpread = 0.02 })),
            new("20261015-110000-full-m1", "m1", "lockwatch", now, J(new { minutes = 52.0, held = true })),
            new("20261015-110000-full-m1", "m1", "trust", now, J(new { result = new { passed = true, whiteSequenceDeviation = 0.002, stabilityDrift = 0.001 } })),
            new("20261015-120000-run-m1", "m1", "move", now, J(new { phase = "base", codes = new[] { 186.2, 136.0, 72.4 } })),
            new("20261015-121000-run-m1", "m1", "move", now, J(new { phase = "plus", codes = new[] { 186.5, 136.3, 72.9 } })),
            new("20261015-122000-run-m1", "m1", "move", now, J(new { phase = "minus", codes = new[] { 186.0, 135.6, 72.0 } })),
            new("20261015-130000-full-m2", "m2", "null", now, J(new { sigma = 0.45 })),
            new("20261015-130000-full-m2", "m2", "ramp", now, J(new { gammaSpread = 0.015 })),
            new("20261015-090000-probe", "all", "nightlight", now, J(new { summary = "Night light is off." })),
        ];

        var m1 = ReportBuilder.Measure(results, "m1");
        var m2 = FeasibilityGate.Evaluate(ReportBuilder.Measure(results, "m2"));
        var markdown = ReportBuilder.Build(results, now);

        m1.MatchedReadingSigma.Should().Be(0.11, "the latest session wins");
        m1.MoveChange.Should().BeApproximately(0.5, 1e-9);
        m1.LockHoldMinutes.Should().Be(52);
        FeasibilityGate.Evaluate(m1).Outcome.Should().Be(GateOutcome.Go);
        m2.Outcome.Should().Be(GateOutcome.BracketingOnly);
        markdown.Should().Contain("| Matched-reading σ | ≤ 0.3 code |").And.Contain("**go**").And.Contain("**bracketing-only**").And.Contain("Night light is off.");
    }

    [Fact]
    public async Task The_full_sequence_runs_and_records_on_the_simulated_rig()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var args = Arguments.Parse(["full", "--simulated", "--sessions", _root, "--runs", "2", "--repeats", "2", "--levels", "6"]);

        var exit = await RunCommand.RunAsync(args, "full", RunCommand.FullSequence, cancel.Token);

        exit.Should().Be(0);
        var session = Directory.GetDirectories(_root).Single();
        var recording = SessionReader.Read(session);
        recording.LastLineWasTruncated.Should().BeFalse();
        recording.Events.OfType<FrameEvent>().Should().NotBeEmpty();
        recording.Events.OfType<PatchShownEvent>().Should().NotBeEmpty();
        var results = Directory.GetFiles(Path.Combine(session, "results")).Select(Path.GetFileNameWithoutExtension).ToList();
        results.Should().Contain(["lock", "trust", "sync", "flicker", "defocus", "null", "shapes", "tree", "ramp", "lockwatch"]);

        var gate = ReportBuilder.Measure(ReportCommand.Load(_root), "sim");
        gate.MatchedReadingSigma.Should().BeLessThan(0.3);
        gate.PatternShapeSpread.Should().BeLessThan(1);
        gate.GammaSpreadBracketing.Should().BeLessThan(0.03);
        gate.TrustPassed.Should().BeTrue();
    }
}
