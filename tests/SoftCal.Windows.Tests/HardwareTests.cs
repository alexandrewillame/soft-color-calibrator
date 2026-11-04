using AwesomeAssertions;
using SoftCal.Core.Displays;
using SoftCal.Core.Measurement;
using SoftCal.Windows.ColorManagement;
using SoftCal.Windows.Ddc;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Gamma;
using SoftCal.Windows.Gpu;
using SoftCal.Windows.NightLight;
using SoftCal.Windows.Patches;
using SoftCal.Windows.Safety;
using Xunit;

namespace SoftCal.Windows.Tests;

/// <summary>
/// Tests against the real displays. They skip unless SOFTCAL_HARDWARE=1; the gamma write test also needs
/// SOFTCAL_HARDWARE_WRITE=1 and Night light off. The patch-window test briefly covers each monitor.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class HardwareTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void RequireHardware(bool writes = false)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SOFTCAL_HARDWARE") == "1", "Set SOFTCAL_HARDWARE=1 to run hardware tests.");
        if (writes)
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("SOFTCAL_HARDWARE_WRITE") == "1", "Set SOFTCAL_HARDWARE_WRITE=1 to allow display changes.");
        }
    }

    /// <summary>Read-only: what Windows says it can do with each display's profiles. On both monitors of the development
    /// PC: 'vcgt' loading False, MHC2 True.</summary>
    [Fact]
    public void Display_color_capabilities_read_without_errors()
    {
        RequireHardware();

        foreach (var monitor in DisplayEnumerator.List())
        {
            var target = DisplayTarget.Of(monitor);
            target.Should().NotBeNull();
            var caps = DisplayColorCapabilities.Of(target!.Value);
            TestContext.Current.SendDiagnosticMessage($"{monitor.DeviceName} {monitor.FriendlyName}: loads vcgt {caps.LoadsVcgt?.ToString() ?? "unknown"}, MHC2 {caps.HardwareCalibration?.ToString() ?? "unknown"}");
            caps.LoadsVcgt.Should().NotBeNull();
        }
    }

    [Fact]
    public void Monitors_enumerate_with_their_edid_and_link()
    {
        RequireHardware();

        var monitors = DisplayEnumerator.List();

        monitors.Should().NotBeEmpty();
        monitors.Should().ContainSingle(m => m.IsPrimary);
        foreach (var m in monitors)
        {
            m.Edid.Should().NotBeNull($"{m.DeviceName} should have a registry EDID");
            DisplayKey.FromEdid(Edid.Parse(m.Edid!)).Value.Should().NotBeNullOrEmpty();
            m.AdvancedColor.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task The_patch_window_is_pixel_exact_on_every_monitor()
    {
        RequireHardware();
        foreach (var monitor in DisplayEnumerator.List())
        {
            using var window = D3DPatchWindow.Open(monitor.DeviceName);
            foreach (var patch in new PatchSpec[]
                     {
                         new SolidPatch(DeviceRgb.Gray(77)),
                         new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard),
                         new HalftonePatch(DeviceRgb.Gray(21), DeviceRgb.Gray(20), HalftoneGeometry.Bayer4x4, 5),
                     })
            {
                var shown = await window.ShowAsync(patch, Token);
                var check = await window.VerifyAsync(Token);

                check.PixelExact.Should().BeTrue($"{monitor.DeviceName}: {check}");
                window.LastTiming!.Source.Should().Be(PresentTimingSource.FrameStatistics);
                shown.PresentTimeSeconds.Should().BeGreaterThan(0);
            }
        }
    }

    /// <summary>
    /// The watch over a measuring patch window, on the screen that is not the main display (briefly covered): clear
    /// while nothing covers the patch, disturbed while another window does (a second patch window opened over it),
    /// clear again once that window closes, and nothing reported while the window is hidden.
    /// </summary>
    [Fact]
    public async Task The_patch_watch_sees_a_window_over_the_patch()
    {
        RequireHardware();
        var monitor = DisplayEnumerator.List().OrderBy(m => m.IsPrimary).First();
        static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        using var window = D3DPatchWindow.Open(monitor.DeviceName, new PatchWindowOptions { WatchForCover = true });
        await window.ShowAsync(new SolidPatch(DeviceRgb.Gray(128)), Token);
        await Task.Delay(800, Token);

        var clear = Now();
        window.WatchProblem.Should().BeNull();
        (await window.DisturbanceAsync(clear - 0.4, clear, Token)).Should().BeNull("nothing covers the patch");

        string? covered;
        using (var cover = D3DPatchWindow.Open(monitor.DeviceName))
        {
            await cover.ShowAsync(new SolidPatch(DeviceRgb.White), Token);
            await Task.Delay(500, Token);
            var during = Now();
            covered = await window.DisturbanceAsync(during - 0.2, during, Token);
        }

        var closed = Now();
        var clearAgain = await window.WaitUntilClearAsync(0.5, 5, Token);
        TestContext.Current.SendDiagnosticMessage($"{monitor.DeviceName}: covered → {covered}; clear again from {clearAgain - closed:+0.000;-0.000} s after the cover closed");
        covered.Should().Contain("not on screen");
        clearAgain.Should().NotBeNull().And.BeGreaterThan(closed - 0.2);

        window.SetVisible(false);
        await Task.Delay(400, Token);
        window.SetVisible(true);
        await window.ShowAsync(new SolidPatch(DeviceRgb.Gray(64)), Token);
        await Task.Delay(600, Token);
        var shownAgain = Now();
        (await window.DisturbanceAsync(shownAgain - 0.3, shownAgain, Token)).Should().BeNull("shown again, nothing covers it");
    }

    [Fact]
    public void Night_light_reads_without_errors()
    {
        RequireHardware();

        var report = NightLightReader.Read();

        report.Blobs.Should().NotBeEmpty().And.OnlyContain(b => b.Error == null);
        report.Active.Should().NotBeNull();
    }

    [Fact]
    public void Nvapi_reports_the_link_format_of_every_display()
    {
        RequireHardware();
        var monitors = DisplayEnumerator.List();

        var report = NvApiProbe.Probe(monitors.Select(m => m.DeviceName));

        Assert.SkipUnless(report.Available, "No NVIDIA driver.");
        report.Displays.Should().HaveCount(monitors.Count).And.OnlyContain(d => d.Current != null);
    }

    [Fact]
    public async Task Ddc_ci_reads_answer_or_fail_cleanly_within_the_timeout()
    {
        RequireHardware();
        foreach (var monitor in DisplayEnumerator.List())
        {
            var reports = await DdcCiProbe.ProbeAsync(monitor, TimeSpan.FromSeconds(2), Token);

            reports.Should().NotBeEmpty();
            reports.SelectMany(r => r.Readings).Should().OnlyContain(r => r.Milliseconds < 6000);
        }
    }

    [Fact]
    public void A_test_ramp_is_written_and_the_original_restored()
    {
        RequireHardware(writes: true);
        Assert.SkipUnless(NightLightReader.Read().Active == false, "Night light must be off.");
        var monitor = DisplayEnumerator.List().First(m => m.IsPrimary);
        var device = new GdiGammaRampDevice(monitor.DeviceName);
        var journal = new StateJournal(Path.Combine(Path.GetTempPath(), "softcal-hw", Guid.NewGuid().ToString("N")));
        var original = device.Read();

        using (var guard = GammaRampGuard.Begin(device, journal))
        {
            guard.Apply(GammaRamp.Scaled(0.875)).Should().BeTrue();
            device.Read().SameAs(GammaRamp.Scaled(0.875)).Should().BeTrue();
        }

        device.Read().SameAs(original).Should().BeTrue();
        journal.Pending().Should().BeEmpty();
    }

    [Fact]
    public void The_windows_color_state_reads_without_errors()
    {
        RequireHardware();

        var state = SoftCal.Windows.OsState.WindowsColorStateProbe.Read();

        state.Displays.Should().HaveCount(DisplayEnumerator.List().Count);
        state.Displays.Should().OnlyContain(d => d.AdvancedColor != null && d.GammaRampIsIdentity != null);
        state.CalibrationLoaderEnabled.Should().NotBeNull();
        state.NightLight.Active.Should().NotBeNull();
    }

    [Fact]
    public async Task The_night_light_watcher_reads_the_real_registry()
    {
        RequireHardware();

        using var watcher = SoftCal.Windows.OsState.NightLightWatcher.Start(TimeSpan.FromMilliseconds(200));
        await Task.Delay(700, Token);

        watcher.Current.Active.Should().Be(NightLightReader.Read().Active);
    }

    [Fact]
    public void Auto_color_management_is_switched_and_restored_on_the_primary_display()
    {
        RequireHardware(writes: true);
        var monitor = DisplayEnumerator.List().First(m => m.IsPrimary);
        var device = SoftCal.Windows.AdvancedColor.DisplayConfigAdvancedColorDevice.For(monitor)!;
        var original = SoftCal.Windows.AdvancedColor.AdvancedColorSwitches.From(device.Read()!);
        Assert.SkipWhen(original.Hdr, "HDR is on; this test only flips ACM on an SDR display.");
        var journal = new StateJournal(Path.Combine(Path.GetTempPath(), "softcal-hw", Guid.NewGuid().ToString("N")));

        using (var guard = SoftCal.Windows.AdvancedColor.AdvancedColorGuard.Begin(device, journal))
        {
            guard.Apply(original with { AutoColorManagement = !original.AutoColorManagement })
                .AutoColorManagement.Should().Be(!original.AutoColorManagement);
        }

        SoftCal.Windows.AdvancedColor.AdvancedColorSwitches.From(device.Read()!).Should().Be(original);
        journal.Pending().Should().BeEmpty();
    }

    [Fact]
    public async Task Ddc_ci_brightness_moves_one_step_and_comes_back()
    {
        RequireHardware(writes: true);
        var journal = new StateJournal(Path.Combine(Path.GetTempPath(), "softcal-hw", Guid.NewGuid().ToString("N")));
        var tested = 0;
        foreach (var monitor in DisplayEnumerator.List())
        {
            foreach (var channel in DdcCiChannel.Open(monitor))
            {
                using (channel)
                {
                    if (await channel.ReadAsync(0x10, Token) is not { } before)
                    {
                        continue;
                    }

                    var target = before.Current > 0 ? before.Current - 1 : before.Current + 1;
                    var guard = new DdcValueGuard(channel, journal);

                    (await guard.SetAsync(0x10, target, Token)).Should().Be(DdcWriteOutcome.Applied, monitor.DeviceName);
                    (await guard.RestoreAsync(Token)).Should().OnlyContain(r => r.Outcome == RestoreOutcome.Restored);
                    (await channel.ReadAsync(0x10, Token))!.Value.Current.Should().Be(before.Current);
                    tested++;
                }
            }
        }

        Assert.SkipWhen(tested == 0, "No monitor reports its brightness over DDC/CI.");
        journal.Pending().Should().BeEmpty();
    }
}
