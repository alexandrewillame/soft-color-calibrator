using AwesomeAssertions;
using SoftCal.Core.Advisor;
using SoftCal.Core.Color;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using Xunit;

namespace SoftCal.Instruments.Simulated.Tests;

/// <summary>
/// Matching a monitor's white to a reference screen on the simulator: the BenQ after its firmware reset (a green white
/// in its 6500 K preset) against a neutral screen. Success is judged on the ground truth: the light the two simulated
/// screens emit, not what the camera reads.
/// </summary>
public sealed class WhiteMatchTests(ITestOutputHelper output)
{
    private static readonly ColorSettings Benq6500 = new("6500 K", 84, 85, 84);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The panel's own white: red 0.85 and blue 0.80 of green's light. Its 6500 K preset has the gains a
    /// firmware reset left (84/85/84); its user mode starts at 100/100/100.</summary>
    private static SimulatedDisplay GreenBenq(bool gainsScaleLight = false)
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions
        {
            ChannelGain = new Vec3(0.85, 1.0, 0.80),
            Controls = new SimulatedOsdOptions { GainsScaleLight = gainsScaleLight },
        });
        display.Osd.PresetGains = (84, 85, 84);
        display.Osd.UserColorMode = false;
        return display;
    }

    /// <summary>The BenQ as measured on 2026-10-18: a steep panel near white, gains that act at about 0.7 of a signal
    /// gain, and gains that DDC/CI sets in the 6500 K mode itself (no user mode needed).</summary>
    private static SimulatedDisplay GentleBenq()
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions
        {
            ChannelGain = new Vec3(0.85, 1.0, 0.80),
            Gamma = new Vec3(3.0, 3.0, 3.0),
            Controls = new SimulatedOsdOptions { GainExponent = 0.7 },
        });
        (display.Osd.RedGain, display.Osd.GreenGain, display.Osd.BlueGain, display.Osd.UserColorMode) = (84, 85, 84, true);
        return display;
    }

    private static SimulatedDisplay NeutralReference() => new(new SimulatedDisplayOptions());

    private async Task<(WhiteMatchResult Result, SimulatedUser User)> RunAsync(
        SimulatedDisplay display,
        SimulatedDisplay reference,
        SimulatedCameraOptions? camera = null,
        bool cameraMoves = true,
        bool direct = false,
        WhiteMatchOptions? options = null)
    {
        var sim = new SimulatedRig(display, new SimulatedCamera(camera ?? new SimulatedCameraOptions())) { ReferenceDisplay = reference };
        await using var rig = new MeasurementRig(sim, sim, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };
        await sim.PrepareAsync(new LockRequest(), Token);
        var user = new SimulatedUser(display, direct);
        IWhiteMatchStage stage = cameraMoves ? new SimulatedWhiteStage(rig, sim) : new StuckCamera(rig, sim);
        var log = new Log(output);
        var result = await new WhiteMatcher(rig, stage, user, options).RunAsync("BenQ", "the reference", Benq6500, 100, log, log, Token);
        output.WriteLine($"Result: matched {result.Matched}, finished {result.Finished}, {result.PresetBefore} → {result.PresetAfter}, {result.GainsBefore} → {result.GainsAfter}; {result.Steps?.Count ?? 0} readings; virtual time {sim.Now:0} s.");
        return (result, user);
    }

    /// <summary>Red and blue light against green, as a ratio to the reference's: the truth the camera tries to see.</summary>
    private static (double Red, double Blue) TrueDifference(SimulatedDisplay display, SimulatedDisplay reference)
    {
        var w = display.Emit(DeviceRgb.White);
        var r = reference.Emit(DeviceRgb.White);
        return (w.X / w.Y / (r.X / r.Y), w.Z / w.Y / (r.Z / r.Y));
    }

    [Fact]
    public async Task A_green_white_is_matched_to_the_reference_in_the_user_mode()
    {
        var (display, reference) = (GreenBenq(), NeutralReference());

        var (result, user) = await RunAsync(display, reference);

        result.Finished.Should().BeTrue();
        result.Matched.Should().BeTrue();
        result.Before!.Value.Tint.Should().Be("greener");
        result.PresetAfter.Should().Be("User");
        result.GainsBefore.Should().Be(new RgbGains(84, 85, 84));
        result.GainsAfter!.Value.Blue.Should().Be(100, "blue needs the most, so it stays at the top");
        var (red, blue) = TrueDifference(display, reference);
        red.Should().BeApproximately(1, 0.02);
        blue.Should().BeApproximately(1, 0.02);
        (user.Switches, user.Moves).Should().Be((1, 2));
        user.GainChanges.Should().BeLessThanOrEqualTo(3);
        result.Steps!.Select(s => s.What).Should().StartWith(["reference", "monitor", "user mode", "drive"]);
    }

    /// <summary>The webcam's own white is off (it would call both screens warm), and its curve is S-shaped: neither
    /// matters, because the same camera reads both screens.</summary>
    [Fact]
    public async Task The_camera_s_own_white_balance_and_curve_do_not_matter()
    {
        var (display, reference) = (GreenBenq(), NeutralReference());
        var camera = new SimulatedCameraOptions
        {
            WhiteBalanceGains = new Vec3(1 / 0.55 * 1.15, 1.0, 1 / 0.70 * 0.80),
            Response = CameraResponse.Filmic(2),
        };

        var (result, _) = await RunAsync(display, reference, camera);

        result.Matched.Should().BeTrue();
        var (red, blue) = TrueDifference(display, reference);
        red.Should().BeApproximately(1, 0.025);
        blue.Should().BeApproximately(1, 0.025);
    }

    /// <summary>Gains that scale the light move the white 2.2 times less per step than the signal: the first change falls
    /// short, and the next ones correct with what was learned.</summary>
    [Fact]
    public async Task Gains_that_scale_the_light_are_learned_from_the_first_change()
    {
        var (display, reference) = (GreenBenq(gainsScaleLight: true), NeutralReference());

        var (result, user) = await RunAsync(display, reference);

        result.Matched.Should().BeTrue();
        var (red, blue) = TrueDifference(display, reference);
        red.Should().BeApproximately(1, 0.02);
        blue.Should().BeApproximately(1, 0.02);
        user.GainChanges.Should().BeInRange(2, 3);
    }

    /// <summary>The BenQ's case: SoftCal sets the gains over DDC/CI in the mode the monitor is in, with no trip to the
    /// menu, and gentle gains on a steep panel still end within the tolerance.</summary>
    [Fact]
    public async Task Gains_set_over_ddc_ci_are_matched_in_the_current_mode()
    {
        var (display, reference) = (GentleBenq(), NeutralReference());

        var (result, user) = await RunAsync(display, reference, direct: true, options: new WhiteMatchOptions { MaxGainRounds = 6 });

        result.Matched.Should().BeTrue();
        result.PresetAfter.Should().Be("6500 K", "the gains were set where the monitor was");
        user.Switches.Should().Be(0);
        user.GainChanges.Should().BeLessThanOrEqualTo(4);
        var (red, blue) = TrueDifference(display, reference);
        red.Should().BeApproximately(1, 0.02);
        blue.Should().BeApproximately(1, 0.02);
    }

    /// <summary>A preset that takes the values over DDC/CI but keeps its own gains: the first change moves nothing, so
    /// the preset's values are put back and the user mode is asked for.</summary>
    [Fact]
    public async Task A_preset_that_ignores_the_gains_leads_to_the_user_mode()
    {
        var (display, reference) = (GreenBenq(), NeutralReference());
        (display.Osd.RedGain, display.Osd.GreenGain, display.Osd.BlueGain) = (84, 85, 84);

        var (result, user) = await RunAsync(display, reference, direct: true, options: new WhiteMatchOptions { MaxGainRounds = 6 });

        result.Matched.Should().BeTrue();
        result.PresetAfter.Should().Be("User");
        user.Switches.Should().Be(1);
        user.GainsSet.Should().Contain(new RgbGains(84, 85, 84), "the preset's own values were put back");
        var (red, blue) = TrueDifference(display, reference);
        red.Should().BeApproximately(1, 0.02);
        blue.Should().BeApproximately(1, 0.02);
    }

    [Fact]
    public async Task A_white_that_already_matches_asks_for_nothing()
    {
        var display = new SimulatedDisplay(new SimulatedDisplayOptions { Controls = new SimulatedOsdOptions() });
        (display.Osd.PresetGains, display.Osd.UserColorMode) = ((100, 100, 100), false);

        var (result, user) = await RunAsync(display, NeutralReference());

        result.Matched.Should().BeTrue();
        result.PresetAfter.Should().Be("6500 K");
        (user.Switches, user.GainChanges).Should().Be((0, 0));
    }

    [Fact]
    public async Task A_camera_left_on_the_wrong_screen_stops_the_match_after_asking_twice()
    {
        var (display, reference) = (GreenBenq(), NeutralReference());

        var (result, user) = await RunAsync(display, reference, cameraMoves: false);

        result.Finished.Should().BeFalse();
        result.Before.Should().BeNull();
        result.Note.Should().Be("The camera did not read the white of the reference.");
        user.Moves.Should().Be(2);
        display.Osd.UserColorMode.Should().BeFalse("nothing was asked of the monitor");
    }

    /// <summary>Does what it is asked, at once. With <paramref name="direct"/> it stands for DDC/CI: the gains go to the
    /// monitor's current mode, which may or may not use them.</summary>
    private sealed class SimulatedUser(SimulatedDisplay display, bool direct) : IWhiteMatchUser
    {
        private readonly List<RgbGains> _gainsSet = [];

        public bool SetsGainsDirectly => direct;

        public int Moves { get; private set; }

        public int Switches { get; private set; }

        public int GainChanges { get; private set; }

        public IReadOnlyList<RgbGains> GainsSet => _gainsSet;

        public Task<bool> MoveCameraAsync(Advice advice, CancellationToken cancellationToken)
        {
            Moves++;
            return Task.FromResult(true);
        }

        public Task<RgbGains?> SwitchToUserModeAsync(Advice advice, CancellationToken cancellationToken)
        {
            Switches++;
            display.Osd.UserColorMode = true;
            (display.Osd.RedGain, display.Osd.GreenGain, display.Osd.BlueGain) = (100, 100, 100);
            return Task.FromResult<RgbGains?>(new RgbGains(100, 100, 100));
        }

        public Task<RgbGains?> SetGainsAsync(RgbGains gains, Advice advice, CancellationToken cancellationToken)
        {
            GainChanges++;
            _gainsSet.Add(gains);
            (display.Osd.RedGain, display.Osd.GreenGain, display.Osd.BlueGain) = (gains.Red, gains.Green, gains.Blue);
            return Task.FromResult<RgbGains?>(gains);
        }
    }

    /// <summary>A user who says the camera moved, but left it where it was.</summary>
    private sealed class StuckCamera(MeasurementRig rig, SimulatedRig sim) : IWhiteMatchStage
    {
        private readonly SimulatedWhiteStage _screens = new(rig, sim);

        public Task AimAtAsync(bool reference, CancellationToken cancellationToken) => _screens.AimAtAsync(reference, cancellationToken);

        public Task UseAsync(bool reference, CancellationToken cancellationToken)
        {
            rig.Presenter = reference ? sim.ReferencePresenter : sim;
            return Task.CompletedTask;
        }
    }

    /// <summary>Writes the match's lines and advice to the test output as they come (no synchronization context).</summary>
    private sealed class Log(ITestOutputHelper output) : IProgress<string>, IProgress<Advice>
    {
        public void Report(string value) => output.WriteLine(value);

        public void Report(Advice value) => output.WriteLine($"  [{value.Kind} · {value.Topic}] {value.Message}");
    }
}
