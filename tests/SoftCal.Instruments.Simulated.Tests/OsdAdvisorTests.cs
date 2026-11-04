using System.Globalization;
using AwesomeAssertions;
using SoftCal.Core.Advisor;
using SoftCal.Core.Color;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using Xunit;

namespace SoftCal.Instruments.Simulated.Tests;

/// <summary>
/// M4's exit criterion on the simulator: monitors whose controls behave like the BenQ XL2720 and the ViewSonic VX2770
/// reach the targets with a simulated user, including one who sometimes sets or reports the wrong value.
/// </summary>
public sealed class OsdAdvisorTests(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly double[] BenqPresetGammas = [1.8, 2.0, 2.2, 2.4, 2.6];

    /// <summary>TN-like: backlight brightness, contrast set too high, five gamma presets on the steepest one, a black
    /// boost, and a panel that crushes its first codes.</summary>
    private static (SimulatedDisplay Display, MonitorControlProfile Profile, Dictionary<OsdControlId, int> Settings) BenqLike()
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions
        {
            CrushCode = 5,
            Controls = new SimulatedOsdOptions { BrightnessKind = BrightnessKind.Backlight, GammaPresets = BenqPresetGammas, BoostPerStep = 1.0 },
        });
        (display.Osd.Brightness, display.Osd.Contrast, display.Osd.GammaPreset, display.Osd.BlackBoost) = (18, 60, 4, 0);
        var profile = new MonitorControlProfile(
        [
            new OsdControl(OsdControlId.Brightness, "Brightness", 0, 100),
            new OsdControl(OsdControlId.Contrast, "Contrast", 0, 100),
            new OsdControl(OsdControlId.GammaPreset, "Gamma", 0, 4, ["Gamma 1", "Gamma 2", "Gamma 3", "Gamma 4", "Gamma 5"]),
            new OsdControl(OsdControlId.BlackBoost, "Black eQualizer", 0, 20),
        ]);
        return (display, profile, Read(display));
    }

    /// <summary>VA/IPS-like: brightness is a signal offset set too low (crushed shadows), no gamma control.</summary>
    private static (SimulatedDisplay Display, MonitorControlProfile Profile, Dictionary<OsdControlId, int> Settings) ViewSonicLike()
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions
        {
            Gamma = new Vec3(1.9, 1.9, 1.9),
            Controls = new SimulatedOsdOptions { BrightnessKind = BrightnessKind.Offset, NeutralBrightness = 50, OffsetPerStep = 0.4 },
        });
        (display.Osd.Brightness, display.Osd.Contrast) = (30, 50);
        var profile = new MonitorControlProfile(
        [
            new OsdControl(OsdControlId.Brightness, "Brightness", 0, 100),
            new OsdControl(OsdControlId.Contrast, "Contrast", 0, 100),
        ]);
        return (display, profile, Read(display));
    }

    private static Dictionary<OsdControlId, int> Read(SimulatedDisplay display) => new()
    {
        [OsdControlId.Brightness] = display.Osd.Brightness,
        [OsdControlId.Contrast] = display.Osd.Contrast,
        [OsdControlId.GammaPreset] = display.Osd.GammaPreset,
        [OsdControlId.BlackBoost] = display.Osd.BlackBoost,
    };

    private async Task<(IReadOnlyList<AdvisorStep> Steps, SimulatedUser User, OsdAdvisor Advisor)> RunAsync(
        SimulatedDisplay display,
        MonitorControlProfile profile,
        Dictionary<OsdControlId, int> settings,
        double typoRate = 0,
        int seed = 1,
        bool enhancementsOff = true)
    {
        var sim = new SimulatedRig(display, new SimulatedCamera(new SimulatedCameraOptions { Seed = seed }));
        await using var rig = new MeasurementRig(sim, sim, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };
        await sim.PrepareAsync(new LockRequest(), Token);
        foreach (var id in settings.Keys.Where(k => profile.Find(k) is null).ToList())
        {
            settings.Remove(id);
        }

        var user = new SimulatedUser(display, typoRate, seed);
        var advisor = new OsdAdvisor(profile, enhancementsOff: enhancementsOff);
        var steps = await new AdvisorLoop(new AdvisorProbe(rig), advisor, user, profile).RunAsync(settings, null, null, Token);
        foreach (var step in steps)
        {
            output.WriteLine($"{step.Advice.Kind,-8} {step.Advice.Topic,-20} {step.Advice.Message}{(step.ReportedValue is { } v ? $"  [reported {v}]" : string.Empty)}");
        }

        output.WriteLine($"Virtual time {sim.Now / 60:0.0} min, {user.Actions} user actions, {user.Typos} typos.");
        return (steps, user, advisor);
    }

    [Fact]
    public async Task A_tn_like_monitor_gets_clean_highlights_the_closest_gamma_preset_and_visible_shadows()
    {
        var (display, profile, settings) = BenqLike();

        var (steps, user, _) = await RunAsync(display, profile, settings);

        var final = steps[^1];
        final.Advice.Kind.Should().Be(AdviceKind.Done);
        final.Readings.IsFresh(ProbeParts.All).Should().BeTrue();
        final.Readings.Steps.AnyClipping.Should().BeFalse();
        final.Readings.Steps.BlackCrushUpToCode.GetValueOrDefault().Should().BeLessThan(6, "code 4 may merge with black, code 6 must not");
        display.Osd.GammaPreset.Should().Be(2, "Gamma 3 is the 2.2 preset");
        display.Osd.Contrast.Should().BeInRange(48, 51, "252 × the contrast gain must stay below 255, as close to it as the search gets");
        display.Osd.Brightness.Should().Be(18, "a backlight brightness is the user's choice");
        display.Osd.BlackBoost.Should().BePositive();
        user.Actions.Should().BeLessThanOrEqualTo(20);
    }

    [Fact]
    public async Task An_offset_brightness_is_raised_to_the_lowest_setting_that_keeps_the_shadows()
    {
        var (display, profile, settings) = ViewSonicLike();

        var (steps, _, _) = await RunAsync(display, profile, settings);

        steps[^1].Advice.Kind.Should().Be(AdviceKind.Done);
        steps[^1].Readings.Steps.BlackCrushUpToCode.Should().BeNull("an offset is searched against code 4 too");
        // Each step is 0.4 code: below 40 code 4 falls into black (offset ≤ −4); above the neutral 50 black lifts.
        display.Osd.Brightness.Should().BeInRange(41, 50, "the lowest setting that keeps code 4 apart, without lifting black");
        display.Osd.Contrast.Should().Be(50, "nothing clipped");
    }

    /// <summary>The first real BenQ walkthrough (2026-10-17): only Brightness and Contrast entered, enhancements not
    /// confirmed off, shadows crushed to code 6. The advisor must ask about the enhancements, find that brightness is
    /// the backlight, put it back, and say once that nothing here lifts the shadows.</summary>
    [Fact]
    public async Task With_only_a_backlight_brightness_the_crush_is_reported_once_and_brightness_is_put_back()
    {
        var (display, _, settings) = BenqLike();
        (display.Osd.Brightness, display.Osd.Contrast) = (33, 50);
        settings[OsdControlId.Brightness] = 33;
        settings[OsdControlId.Contrast] = 50;
        var profile = new MonitorControlProfile([new OsdControl(OsdControlId.Brightness, "Brightness", 0, 100), new OsdControl(OsdControlId.Contrast, "Contrast", 0, 100)]);

        var (steps, user, _) = await RunAsync(display, profile, settings, enhancementsOff: false);

        steps[0].Advice.Kind.Should().Be(AdviceKind.Fix);
        steps[0].Advice.Topic.Should().Be("Picture enhancements");
        steps.Where(s => s.Advice.Control == OsdControlId.Brightness).Select(s => s.Advice.Value).Should().Equal(43, 33);
        display.Osd.Brightness.Should().Be(33);
        var done = steps[^1].Advice;
        done.Kind.Should().Be(AdviceKind.Done);
        done.Message.Split("look the same as black").Should().HaveCount(2, "the crush note appears once: {0}", done.Message);
        done.Message.Should().Contain("Half of white comes at code", "the steep preset (γ 2.6) is off target even though no gamma setting was entered");
        AdvisorMeter.TargetCode(0.5, 2.2).Should().BeApproximately(186.1, 0.1);
        user.Actions.Should().Be(2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_user_who_mistypes_still_ends_at_settings_that_measure_right(int seed)
    {
        var (display, profile, settings) = BenqLike();

        var (steps, user, _) = await RunAsync(display, profile, settings, typoRate: 0.2, seed: seed);

        user.Typos.Should().BePositive("the test needs typos to be meaningful");
        steps[^1].Advice.Kind.Should().Be(AdviceKind.Done);
        steps[^1].Readings.Steps.AnyClipping.Should().BeFalse();
        steps[^1].Readings.Steps.BlackCrushUpToCode.GetValueOrDefault().Should().BeLessThan(6);
        display.Osd.GammaPreset.Should().Be(2);
        steps.Should().HaveCountLessThanOrEqualTo(OsdAdvisor.MaxSteps);
    }

    /// <summary>Highlights clipped by the panel itself, and a Contrast that changes nothing (locked by the picture
    /// mode): the search gives up after its moves, puts Contrast back where it was, and the final check does not start
    /// it again.</summary>
    [Fact]
    public async Task A_contrast_that_does_not_stop_the_clipping_is_put_back()
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions
        {
            ClipCode = 246,
            Controls = new SimulatedOsdOptions { BrightnessKind = BrightnessKind.Backlight, GainPerContrastStep = 0 },
        });
        (display.Osd.Brightness, display.Osd.Contrast) = (40, 100);
        var profile = new MonitorControlProfile([new OsdControl(OsdControlId.Brightness, "Brightness", 0, 100), new OsdControl(OsdControlId.Contrast, "Contrast", 0, 100)]);

        var (steps, user, _) = await RunAsync(display, profile, Read(display));

        steps[^1].Advice.Kind.Should().Be(AdviceKind.Done);
        display.Osd.Contrast.Should().Be(100, "lowering it did not help, so it goes back");
        steps.Count(s => s.Advice.Control == OsdControlId.Contrast).Should().BeLessThanOrEqualTo(9, "one search of 8 moves and the way back, never a second search");
        steps[^1].Advice.Message.Should().Contain("put back to 100");
        user.Actions.Should().BeLessThanOrEqualTo(9);
    }

    /// <summary>Shadows crushed to code 8 by the panel, and a black boost that lifts nothing: the boost goes back to
    /// where it was, once.</summary>
    [Fact]
    public async Task A_boost_that_does_not_lift_the_shadows_is_put_back()
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions
        {
            CrushCode = 8,
            Controls = new SimulatedOsdOptions { BrightnessKind = BrightnessKind.Backlight, BoostPerStep = 0 },
        });
        (display.Osd.Brightness, display.Osd.Contrast, display.Osd.BlackBoost) = (40, 50, 0);
        var profile = new MonitorControlProfile(
        [
            new OsdControl(OsdControlId.Brightness, "Brightness", 0, 100),
            new OsdControl(OsdControlId.Contrast, "Contrast", 0, 100),
            new OsdControl(OsdControlId.BlackBoost, "Black eQualizer", 0, 20),
        ]);

        var (steps, _, _) = await RunAsync(display, profile, Read(display));

        steps[^1].Advice.Kind.Should().Be(AdviceKind.Done);
        display.Osd.BlackBoost.Should().Be(0, "raising it lifted nothing, so it goes back");
        display.Osd.Brightness.Should().Be(40, "a backlight brightness goes back after its test");
        steps.Count(s => s.Advice.Control == OsdControlId.BlackBoost).Should().BeLessThanOrEqualTo(9, "one search and the way back, never a second search");
        steps[^1].Advice.Message.Should().Contain("so they were put back");
    }

    /// <summary>Five presets that all give the same gamma (the monitor's other settings fix it): every one is measured once, no
    /// preset is measured again for reading "the same as another", and the user's own preset stays.</summary>
    [Fact]
    public async Task Presets_that_all_measure_the_same_change_nothing()
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions
        {
            Controls = new SimulatedOsdOptions { BrightnessKind = BrightnessKind.Backlight, GammaPresets = [2.43, 2.43, 2.43, 2.43, 2.43] },
        });
        (display.Osd.Brightness, display.Osd.Contrast, display.Osd.GammaPreset) = (40, 50, 3);
        var profile = new MonitorControlProfile(
        [
            new OsdControl(OsdControlId.Brightness, "Brightness", 0, 100),
            new OsdControl(OsdControlId.Contrast, "Contrast", 0, 100),
            new OsdControl(OsdControlId.GammaPreset, "Gamma", 0, 4, ["Gamma 1", "Gamma 2", "Gamma 3", "Gamma 4", "Gamma 5"]),
        ]);

        var (steps, user, _) = await RunAsync(display, profile, Read(display));

        steps[^1].Advice.Kind.Should().Be(AdviceKind.Done);
        display.Osd.GammaPreset.Should().Be(3, "no preset is closer, so the user's stays");
        steps[^1].Advice.Message.Should().Contain("make no difference");
        user.Actions.Should().BeLessThanOrEqualTo(6, "the four other presets once each, and back to the user's");
    }

    /// <summary>Two presets closer than two readings of one preset differ: the user's own stays.</summary>
    [Fact]
    public async Task The_users_preset_stays_unless_another_is_clearly_closer()
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions
        {
            Controls = new SimulatedOsdOptions { BrightnessKind = BrightnessKind.Backlight, GammaPresets = [1.9, 2.21, 2.20, 2.5] },
        });
        (display.Osd.Brightness, display.Osd.Contrast, display.Osd.GammaPreset) = (40, 50, 1);
        var profile = new MonitorControlProfile(
        [
            new OsdControl(OsdControlId.Brightness, "Brightness", 0, 100),
            new OsdControl(OsdControlId.Contrast, "Contrast", 0, 100),
            new OsdControl(OsdControlId.GammaPreset, "Gamma", 0, 3, ["Gamma 1", "Gamma 2", "Gamma 3", "Gamma 4"]),
        ]);

        var (steps, _, _) = await RunAsync(display, profile, Read(display));

        steps[^1].Advice.Kind.Should().Be(AdviceKind.Done);
        display.Osd.GammaPreset.Should().Be(1, "Gamma 3 (2.20) is not clearly closer than Gamma 2 (2.21)");
    }

    /// <summary>A person at the monitor: sets what the advice says, but sometimes sets a wrong value (and reports the
    /// intended one) or sets it right and reports a wrong one.</summary>
    private sealed class SimulatedUser(SimulatedDisplay display, double typoRate, int seed) : IOsdActuator
    {
        private readonly Random _random = new(seed);

        public int Actions { get; private set; }

        public int Typos { get; private set; }

        public Task<int?> ApplyAsync(OsdControl control, int value, Advice advice, CancellationToken cancellationToken)
        {
            Actions++;
            var (set, reported) = (value, value);
            if (_random.NextDouble() < typoRate)
            {
                Typos++;
                set = Typo(control, value);
            }
            else if (_random.NextDouble() < typoRate / 2)
            {
                Typos++;
                reported = Typo(control, value);
            }

            var osd = display.Osd;
            switch (control.Id)
            {
                case OsdControlId.Brightness:
                    osd.Brightness = set;
                    break;
                case OsdControlId.Contrast:
                    osd.Contrast = set;
                    break;
                case OsdControlId.GammaPreset:
                    osd.GammaPreset = set;
                    break;
                case OsdControlId.BlackBoost:
                    osd.BlackBoost = set;
                    break;
            }

            return Task.FromResult<int?>(reported);
        }

        public Task<bool> ConfirmAsync(Advice advice, CancellationToken cancellationToken) => Task.FromResult(true);

        /// <summary>Swapped digits (45 → 54), or one off for presets and single digits.</summary>
        private int Typo(OsdControl control, int value)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            var typo = !control.IsPreset && text.Length == 2 && text[0] != text[1]
                ? int.Parse(string.Concat(text[1], text[0]), CultureInfo.InvariantCulture)
                : value + (_random.Next(2) == 0 ? -1 : 1);
            return control.Clamp(typo);
        }
    }
}
