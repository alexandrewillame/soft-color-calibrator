using AwesomeAssertions;
using SoftCal.Core.Analysis;
using Xunit;

namespace SoftCal.Core.Tests.Analysis;

public sealed class SyncAndFlickerTests
{
    [Fact]
    public void The_sync_code_is_a_31_symbol_m_sequence_slow_enough_to_be_safe()
    {
        var code = OpticalSync.MSequence31();

        code.Should().HaveCount(31);
        code.Count(b => b).Should().Be(16, "an m-sequence has one more 1 than 0s");
        OpticalSync.MaxFlashRateHz(0.4).Should().BeLessThanOrEqualTo(3, "flashes must stay at 3 Hz or slower");

        // Sharp autocorrelation: every cyclic shift agrees with the original on exactly 15 symbols.
        for (var shift = 1; shift < 31; shift++)
        {
            Enumerable.Range(0, 31).Count(i => code[i] == code[(i + shift) % 31]).Should().Be(15);
        }
    }

    [Fact]
    public void On_fraction_integrates_the_level_over_a_window()
    {
        SyncTransition[] transitions = [new(0, false), new(1.0, true), new(2.0, false)];

        OpticalSync.OnFraction(transitions, 0.5, 1.5).Should().BeApproximately(0.5, 1e-12);
        OpticalSync.OnFraction(transitions, 1.2, 1.8).Should().Be(1);
        OpticalSync.OnFraction(transitions, 1.75, 2.25).Should().BeApproximately(0.5, 1e-12);
        OpticalSync.OnFraction(transitions, 3, 4).Should().Be(0);
    }

    [Theory]
    [InlineData(0.083, 1.0 / 64)]
    [InlineData(0.140, 1.0 / 128)]
    public void The_fit_recovers_the_latency_with_sub_frame_precision(double latency, double exposure)
    {
        var (transitions, samples) = Simulate(latency, exposure, frameRate: 30, noise: 0.003, seed: 3);

        var result = OpticalSync.Fit(transitions, samples, exposure);

        result.OffsetSeconds.Should().BeApproximately(latency, 0.002, "a frame lasts 33 ms; the fit must do much better");
        result.RelativeResidual.Should().BeLessThan(0.05);
        Math.Abs(result.DriftPpm).Should().BeLessThan(2000);
    }

    [Fact]
    public void A_detrended_row_profile_has_zero_mean_and_no_slope()
    {
        var rows = Enumerable.Range(0, 100).Select(i => 100 + (0.2 * i) + (0.001 * i * i) - (1e-5 * i * i * i)).ToArray();

        var detrended = FlickerAnalysis.Detrend(rows);

        detrended.Should().OnlyContain(v => Math.Abs(v) < 1e-9);
    }

    [Theory]
    [InlineData(240, 60e-6, 0.05)]
    [InlineData(480, 60e-6, 0.10)]
    public void Pwm_banding_is_detected_with_its_frequency_and_depth(double hz, double lineTime, double depth)
    {
        var random = new Random(5);
        var frames = Enumerable.Range(0, 20).Select(f =>
        {
            var phase = random.NextDouble() * 2 * Math.PI;
            return (IReadOnlyList<double>)Enumerable.Range(0, 432)
                .Select(r => 150 * (1 + (depth * Math.Sin((2 * Math.PI * hz * r * lineTime) + phase))) * (1 - (0.0005 * r)) + (random.NextDouble() - 0.5))
                .ToArray();
        }).ToList();

        var result = FlickerAnalysis.Analyze(frames, lineTime);

        result.Detected.Should().BeTrue();
        result.FrequencyHz.Should().BeApproximately(hz, hz * 0.03);
        result.ModulationDepth.Should().BeApproximately(depth, depth * 0.2);
    }

    [Fact]
    public void A_flicker_free_backlight_is_not_reported_as_flicker()
    {
        var random = new Random(9);
        var frames = Enumerable.Range(0, 20)
            .Select(_ => (IReadOnlyList<double>)Enumerable.Range(0, 432).Select(r => 150 - (0.01 * r) + (random.NextDouble() - 0.5)).ToArray())
            .ToList();

        FlickerAnalysis.Analyze(frames, 60e-6).Detected.Should().BeFalse();
    }

    [Fact]
    public void Smooth_shading_is_not_reported_as_flicker()
    {
        // Seen on the C920e: defocus shading leaves a smooth, falling spectrum whose largest bin is the lowest one
        // analysed. Only a narrow local peak is banding.
        var random = new Random(4);
        var frames = Enumerable.Range(0, 30)
            .Select(_ => (IReadOnlyList<double>)Enumerable.Range(0, 432).Select(r =>
            {
                var x = (r / 431.0 * 2) - 1;
                return 150 * (1 - (0.05 * x * x) + (0.03 * x * x * x) - (0.04 * Math.Pow(x, 4)) + (0.02 * Math.Pow(x, 5)) + (0.01 * Math.Pow(x, 6))) + (0.5 * (random.NextDouble() - 0.5));
            }).ToArray())
            .ToList();

        var result = FlickerAnalysis.Analyze(frames, 48e-6);

        result.Detected.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_line_time_comes_from_frames_caught_mid_transition(bool upright)
    {
        const double LineTime = 64e-6;
        const double Exposure = 1.0 / 256;
        var frames = new List<IReadOnlyList<double>>();
        foreach (var switchRow in new[] { 100.0, 250.5, 300 })
        {
            // Row r sees the bright level for the part of its exposure after the switch.
            var rows = Enumerable.Range(0, 480).Select(r =>
            {
                var fraction = Math.Clamp((r - switchRow) * LineTime / Exposure, 0, 1);
                return 20 + (200 * fraction);
            }).ToArray();
            frames.Add(upright ? rows : rows.Reverse().ToArray());
        }

        frames.Add(Enumerable.Repeat(20.0, 480).ToArray());

        var estimate = FlickerAnalysis.LineTimeFromTransition(frames, Exposure, 20, 220);

        estimate.Should().BeApproximately(LineTime, LineTime * 0.02);
    }

    private static (List<SyncTransition> Transitions, List<SyncSample> Samples) Simulate(double latency, double exposure, double frameRate, double noise, int seed)
    {
        var random = new Random(seed);
        var transitions = new List<SyncTransition> { new(10.0, false) };
        var t = 11.0;
        var level = false;
        foreach (var bit in OpticalSync.MSequence31())
        {
            if (bit != level)
            {
                transitions.Add(new SyncTransition(t, bit));
                level = bit;
            }

            t += 0.4 + ((random.NextDouble() - 0.5) * 0.01);
        }

        transitions.Add(new SyncTransition(t, false));
        var samples = new List<SyncSample>();
        for (var tau = 10.5; tau < t + 1; tau += 1 / frameRate)
        {
            var end = tau - latency;
            var on = OpticalSync.OnFraction(transitions, end - exposure, end);
            samples.Add(new SyncSample(tau, 0.05 + (0.4 * on) + (noise * (random.NextDouble() - 0.5))));
        }

        return (transitions, samples);
    }
}
