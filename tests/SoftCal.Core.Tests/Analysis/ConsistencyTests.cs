using AwesomeAssertions;
using SoftCal.Core.Analysis;
using SoftCal.Core.Color;
using SoftCal.Core.Measurement;
using Xunit;

namespace SoftCal.Core.Tests.Analysis;

/// <summary>Regressions from the first real reports (BenQ XL2720, 2026-10-17) and the gray balance that replaced them.</summary>
public sealed class ConsistencyTests
{
    private static NullResult Null(double code, int channel, Vec3? perChannel = null, double error = 0.05) =>
        new(
            "test",
            new SolidPatch(DeviceRgb.White),
            channel,
            0.03,
            (int)code,
            code,
            0.05,
            0.05,
            perChannel is { } p ? [new NullRepeat((int)code, code, Vec3.One, Vec3.One, Vec3.One, p, new Vec3(error, error, error))] : [],
            6);

    /// <summary>A two-level gray tree (½ and ¼ of white) and the three single-channel ½ nulls.</summary>
    private static DisplayMeasurement Measurement(
        double grayHalf,
        double red,
        double green,
        double blue,
        Vec3? halfPerChannel = null,
        Vec3? quarterPerChannel = null,
        double error = 0.05)
    {
        var reading = new Reading(new SolidPatch(DeviceRgb.White), 1, 0, 1, 0.03, new Vec3(200, 200, 200), new Vec3(0.1, 0.1, 0.1), Vec3.One, 8, 0, 0, 1, 0);
        var quarter = grayHalf * 0.71;
        return new DisplayMeasurement(
            HalftoneGeometry.HorizontalLines,
            [
                new TreeLevel(1, 0.5, 255, 1, Null(grayHalf, 3, halfPerChannel, error)),
                new TreeLevel(2, 0.25, (int)Math.Round(grayHalf), 0.5, Null(quarter, 3, quarterPerChannel, error)),
            ],
            [Null(red, 0), Null(green, 1), Null(blue, 2)],
            null,
            [],
            [],
            reading,
            reading,
            reading,
            []);
    }

    private static Vec3 Same(double code) => new(code, code, code);

    [Fact]
    public void Old_measurements_without_per_channel_gray_matches_flag_channels_that_disagree_with_gray()
    {
        var channels = DisplayAnalysis.Channels(Measurement(196.6, 176.1, 179.3, 178.7));

        channels.GrayConsistencyCodes.Should().BeApproximately(18.2, 0.2);
        channels.Confidence.Should().Be(Confidence.Low);
        channels.MidGrayTint.Should().BeNull();
        channels.GrayLevels.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Consistent_channels_keep_high_confidence()
    {
        DisplayAnalysis.Channels(Measurement(186.0, 184.5, 186.3, 187.9)).Confidence.Should().Be(Confidence.High);
    }

    [Fact]
    public void A_gray_matched_at_one_code_in_every_camera_channel_is_neutral_even_when_single_colors_disagree()
    {
        var measurement = Measurement(196.6, 176.1, 179.3, 178.7, Same(196.6), Same(196.6 * 0.71));

        var report = DisplayAnalysis.Analyze(measurement, new ReportContext("BenQ", null, "test"));

        report.Channels.Confidence.Should().Be(Confidence.High);
        report.Channels.MidGrayTint.Should().BeNull();
        report.Channels.MidGrayBalance.X.Should().BeApproximately(1, 1e-9);
        report.Channels.GrayLevels.Should().HaveCount(2);
        report.Findings.Should().Contain(f => f.Topic == "Gray balance" && f.Level == FindingLevel.Info && f.Message.Contains("neutral"));
        report.Findings.Should().Contain(f => f.Topic == "Panel" && f.Message.Contains("interact"));
    }

    [Fact]
    public void A_channel_that_matches_lower_is_brighter_and_the_tint_carries_down_the_tree()
    {
        // Blue reaches the line pattern's light 3.6 codes before the gray's luma does: blue is brighter at mid-gray.
        // The quarter level is matched against that (bluish) gray, so equal codes there keep the same tint.
        var measurement = Measurement(196.6, 186, 186, 186, new Vec3(197.0, 197.0, 193.0), Same(196.6 * 0.71));

        var levels = DisplayAnalysis.GrayBalance(measurement);

        levels[0].Tint.Should().Be("bluish");
        levels[0].Balance.Z.Should().BeApproximately(1.032, 0.002);
        levels[1].Tint.Should().Be("bluish");
        levels[1].Balance.Z.Should().BeApproximately(levels[0].Balance.Z, 1e-9);
    }

    [Fact]
    public void Matches_noisier_than_the_tolerance_give_no_verdict()
    {
        var measurement = Measurement(196.6, 186, 186, 186, new Vec3(197.0, 197.0, 193.0), Same(196.6 * 0.71), error: 2);

        var report = DisplayAnalysis.Analyze(measurement, new ReportContext("noisy", null, "test"));

        report.Channels.Confidence.Should().Be(Confidence.Low);
        report.Channels.MidGrayTint.Should().BeNull();
        report.Findings.Should().Contain(f => f.Topic == "Gray balance" && f.Message.StartsWith("Too noisy", StringComparison.Ordinal));
    }

    /// <summary>The BenQ at Brightness 33 (2026-10-17 16:31), camera at ¼ s: code 4 reads as black, code 6 rises in red
    /// and green but its luma (+0.33) sat at the old 0.3 floor, so the report called codes 0–6 crushed.</summary>
    [Fact]
    public void A_dark_step_that_rises_in_any_channel_is_not_called_crushed()
    {
        static StepReading Step(int code, double r, double g, double b) => new(code, new Vec3(r, g, b), new Vec3(0.02, 0.02, 0.03), 0.25);
        StepReading[] blacks =
        [
            Step(0, 2.26, 3.90, 4.58), Step(4, 2.26, 3.90, 4.59), Step(6, 2.77, 4.24, 4.38), Step(8, 4.65, 5.96, 5.42),
            Step(12, 13.24, 13.56, 9.35), Step(16, 26.36, 24.16, 14.04), Step(20, 37.16, 33.94, 19.50),
        ];

        DisplayAnalysis.Steps(blacks, []).BlackCrushUpToCode.Should().Be(4);
    }

    [Fact]
    public void A_contrast_from_an_assumed_camera_response_is_not_shown()
    {
        new ContrastEstimate(119860, false, Confidence.Low).ToString().Should().Be("not measured (Quick check)");
        new ContrastEstimate(850, true, Confidence.Low).ToString().Should().Be("black below what the camera sees");
    }
}
