using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.FeasibilityHarness.Infrastructure;

namespace SoftCal.FeasibilityHarness.Steps;

/// <summary>Everything a step needs, plus what earlier steps learned (latency, the matched half-light level, …).</summary>
internal sealed class StepContext
{
    public required HarnessSession Session { get; init; }

    public required HarnessRig Rig { get; init; }

    public required MeasurementRig Measure { get; init; }

    public required Arguments Args { get; init; }

    public required CancellationToken Token { get; init; }

    public RoiSpec Roi => Measure.Roi;

    /// <summary>The camera's local gamma used to turn encoded ripple into linear ripple (updated by bracketing).</summary>
    public double CameraGamma { get; set; } = 2.2;

    /// <summary>The 50 % null (white/black checkerboard), reused by later steps.</summary>
    public NullResult? HalfNull { get; set; }

    /// <summary>--quick: fewer frames at long exposures and fewer repeats, for runs a user can wait through.</summary>
    public bool Quick => Args.Has("quick");

    /// <summary>Policy for the many short readings of a step, shortened in quick mode.</summary>
    public ReadPolicy Readings(int frames) => Quick
        ? ReadPolicy.Default with { Frames = Math.Max(3, frames / 2), MinFrames = 2, TargetIntegrationSeconds = 0.3, SettleMarginSeconds = 0.06 }
        : ReadPolicy.Default with { Frames = frames };

    public NullOptions NullOptions(int? repeats = null) => Quick
        ? new()
        {
            Repeats = repeats ?? Args.GetInt("repeats", 1),
            Coarse = ReadPolicy.Default with { Frames = 4, MinFrames = 2, TargetIntegrationSeconds = 0.25, MaxClippedFraction = 1, SettleMarginSeconds = 0.06 },
            Fine = ReadPolicy.Default with { Frames = Args.GetInt("frames", 8), MinFrames = 3, TargetIntegrationSeconds = 0.5, SettleMarginSeconds = 0.06 },
        }
        : new()
        {
            Repeats = repeats ?? Args.GetInt("repeats", 3),
            Fine = ReadPolicy.Default with { Frames = Args.GetInt("frames", 15) },
        };

    /// <summary>A step that cannot run on this instrument records why and returns.</summary>
    public void Skip(string step, string reason)
    {
        Ui.Warn($"{step}: skipped ({reason})");
        Session.Result(step, new { skipped = true, reason });
    }
}

/// <summary>A step's bookkeeping, recorded alongside its result.</summary>
internal sealed record StepRecord(string Step, double StartSeconds, double EndSeconds, int FaultsDuringStep, string? Error);
