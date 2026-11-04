using AwesomeAssertions;
using SoftCal.Core.Instruments;
using SoftCal.Instruments.Webcam.Controls;
using Xunit;

namespace SoftCal.Instruments.Webcam.Tests;

public sealed class CameraLockTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A C920e-like camera: the ranges and defaults this machine's camera reports.</summary>
    private static FakeBackend C920e() => new(new Dictionary<UvcControlId, (UvcControlRange Range, UvcControlValue Value)>
    {
        [UvcControlCatalog.WhiteBalance] = (new(2000, 6500, 1, 4000, true, true), new(4000, true)),
        [UvcControlCatalog.Exposure] = (new(-11, -2, 1, -5, true, true), new(-5, true)),
        [UvcControlCatalog.Gain] = (new(0, 255, 1, 0, false, true), new(0, false)),
        [UvcControlCatalog.BacklightCompensation] = (new(0, 1, 1, 0, false, true), new(1, false)),
        [UvcControlCatalog.Sharpness] = (new(0, 255, 1, 128, false, true), new(128, false)),
        [UvcControlCatalog.Focus] = (new(0, 250, 5, 0, true, true), new(0, true)),
        [UvcControlCatalog.Brightness] = (new(0, 255, 1, 128, false, true), new(140, false)),
        [UvcControlCatalog.Contrast] = (new(0, 255, 1, 128, false, true), new(128, false)),
        [UvcControlCatalog.Saturation] = (new(0, 255, 1, 128, false, true), new(150, false)),
        [UvcControlCatalog.Zoom] = (new(100, 500, 1, 100, false, true), new(100, false)),
        [UvcControlCatalog.PowerLineFrequency] = (new(0, 2, 1, 2, false, true), new(2, false)),
        [UvcControlCatalog.AutoExposurePriority] = (new(0, 1, 1, 1, false, true), new(1, false)),
    });

    [Fact]
    public void The_plan_fixes_every_automatic_control_and_writes_defaults_explicitly()
    {
        var backend = C920e();
        var snapshot = ControlSnapshot.Capture(backend, "usb-046d-08b6");

        var plan = CameraLock.Plan(snapshot, new LockRequest(), new WebcamLockSettings { InitialExposureLog2 = -7 });
        UvcControlValue? Planned(UvcControlId id) => plan.FirstOrDefault(p => p.Id == id)?.Value;

        Planned(UvcControlCatalog.WhiteBalance).Should().Be(new UvcControlValue(6500, false));
        Planned(UvcControlCatalog.Exposure).Should().Be(new UvcControlValue(-7, false));
        Planned(UvcControlCatalog.Gain).Should().Be(new UvcControlValue(0, false));
        Planned(UvcControlCatalog.BacklightCompensation).Should().Be(new UvcControlValue(0, false));
        Planned(UvcControlCatalog.Sharpness).Should().Be(new UvcControlValue(0, false));
        Planned(UvcControlCatalog.Focus).Should().Be(new UvcControlValue(0, false));
        Planned(UvcControlCatalog.Brightness).Should().Be(new UvcControlValue(128, false), "a user-changed brightness goes back to the default");
        Planned(UvcControlCatalog.Saturation).Should().Be(new UvcControlValue(128, false));
        Planned(UvcControlCatalog.PowerLineFrequency).Should().Be(new UvcControlValue(2, false), "IKsControl extras are pinned at their current value");
        plan.Should().OnlyContain(p => !p.Value.IsAuto);
        plan.Should().NotContain(p => p.Id == UvcControlCatalog.Privacy);
        plan.Should().NotContain(p => p.Id == UvcControlCatalog.Hue, "an unsupported control is not written");
        plan[0].Id.Should().Be(UvcControlCatalog.WhiteBalance);
    }

    [Fact]
    public void Restoring_does_not_pin_the_gain_that_auto_exposure_happened_to_choose()
    {
        var backend = C920e();
        backend.Set(UvcControlCatalog.Gain, new UvcControlValue(255, false)); // momentary auto-exposure gain
        var snapshot = ControlSnapshot.Capture(backend, "cam");

        var restore = CameraLock.PlanRestore(snapshot);

        restore.Single(p => p.Id == UvcControlCatalog.Gain).Value.Should().Be(new UvcControlValue(0, false));
        restore.Single(p => p.Id == UvcControlCatalog.WhiteBalance).Value.Should().Be(new UvcControlValue(4000, true));
        restore.Single(p => p.Id == UvcControlCatalog.Brightness).Value.Should().Be(new UvcControlValue(140, false), "user settings are restored as they were");
    }

    [Fact]
    public void A_requested_focus_and_white_balance_are_clamped_to_the_range_and_step()
    {
        var snapshot = ControlSnapshot.Capture(C920e(), "cam");

        var plan = CameraLock.Plan(snapshot, new LockRequest { FocusValue = 123, WhiteBalanceKelvin = 9000 }, new WebcamLockSettings());

        plan.Single(p => p.Id == UvcControlCatalog.Focus).Value.Value.Should().Be(125);
        plan.Single(p => p.Id == UvcControlCatalog.WhiteBalance).Value.Value.Should().Be(6500);
    }

    [Fact]
    public void Verification_tells_locked_mismatched_failed_and_unsupported_controls_apart()
    {
        var backend = C920e();
        var snapshot = ControlSnapshot.Capture(backend, "cam");
        var plan = CameraLock.Plan(snapshot, new LockRequest(), new WebcamLockSettings());
        backend.Ignore.Add(UvcControlCatalog.Sharpness);
        backend.Reject.Add(UvcControlCatalog.Saturation);
        var errors = new Dictionary<UvcControlId, string>();
        foreach (var write in plan)
        {
            try
            {
                backend.Set(write.Id, write.Value);
            }
            catch (InvalidOperationException ex)
            {
                errors[write.Id] = ex.Message;
            }
        }

        var verification = CameraLock.Verify(snapshot, plan, errors, backend.Get);
        LockOutcome Outcome(UvcControlId id) => verification.Single(v => v.Id == id).Outcome;

        Outcome(UvcControlCatalog.WhiteBalance).Should().Be(LockOutcome.Locked);
        Outcome(UvcControlCatalog.Sharpness).Should().Be(LockOutcome.VerifiedMismatch);
        Outcome(UvcControlCatalog.Saturation).Should().Be(LockOutcome.Failed);
        Outcome(UvcControlCatalog.Hue).Should().Be(LockOutcome.NotSupported);
        var report = CameraLock.ToLockReport(verification);
        report.CriticalControlsLocked.Should().BeTrue();
        report.Controls.Should().Contain(c => c.Control == CameraControl.Sharpness && c.Outcome == LockOutcome.VerifiedMismatch);
    }

    [Fact]
    public void A_white_balance_left_in_auto_is_not_a_lock()
    {
        var backend = C920e();
        var snapshot = ControlSnapshot.Capture(backend, "cam");
        var plan = CameraLock.Plan(snapshot, new LockRequest(), new WebcamLockSettings());
        foreach (var write in plan)
        {
            backend.Set(write.Id, write.Value);
        }

        backend.ForceAuto(UvcControlCatalog.WhiteBalance);
        var report = CameraLock.ToLockReport(CameraLock.Verify(snapshot, plan, new Dictionary<UvcControlId, string>(), backend.Get));

        report.CriticalControlsLocked.Should().BeFalse();
    }

    [Fact]
    public void Restoring_puts_back_values_and_auto_modes()
    {
        var backend = C920e();
        var snapshot = ControlSnapshot.Capture(backend, "cam");
        foreach (var write in CameraLock.Plan(snapshot, new LockRequest(), new WebcamLockSettings()))
        {
            backend.Set(write.Id, write.Value);
        }

        foreach (var write in CameraLock.PlanRestore(snapshot))
        {
            backend.Set(write.Id, write.Value);
        }

        foreach (var entry in snapshot.Entries.Where(e => e.Value is not null))
        {
            backend.Get(entry.Id).Should().Be(entry.Value, entry.Name);
        }

        backend.Get(UvcControlCatalog.WhiteBalance)!.Value.IsAuto.Should().BeTrue();
    }

    [Fact]
    public async Task The_restore_writes_every_value_by_hand_first_then_switches_the_automatic_ones_back_on()
    {
        // A C920e switched back to automatic while idle kept the locked white balance (its picture stayed at 6500 K):
        // the restore runs while it streams, the values first, the automatic modes after.
        var backend = C920e();
        var snapshot = ControlSnapshot.Capture(backend, "cam");
        foreach (var write in CameraLock.Plan(snapshot, new LockRequest(), new WebcamLockSettings()))
        {
            backend.Set(write.Id, write.Value);
        }

        backend.Log.Clear();
        var failed = await CameraLock.ApplyRestoreAsync(backend, snapshot, TimeSpan.Zero, Token);

        failed.Should().BeEmpty();
        var firstAuto = backend.Log.FindIndex(w => w.Value.IsAuto);
        firstAuto.Should().BePositive();
        backend.Log.Take(firstAuto).Should().OnlyContain(w => !w.Value.IsAuto, "every control is written by hand first");
        backend.Log.Take(firstAuto).Select(w => w.Id).Should().Contain([UvcControlCatalog.WhiteBalance, UvcControlCatalog.Exposure, UvcControlCatalog.Focus]);
        backend.Log.Skip(firstAuto).Should().OnlyContain(w => w.Value.IsAuto, "then only the automatic ones are switched back on");
        backend.Log.Skip(firstAuto).Select(w => w.Id).Should().BeEquivalentTo([UvcControlCatalog.WhiteBalance, UvcControlCatalog.Exposure, UvcControlCatalog.Focus]);
        foreach (var entry in snapshot.Entries.Where(e => e.Value is not null))
        {
            backend.Get(entry.Id).Should().Be(entry.Value, entry.Name);
        }
    }

    [Fact]
    public async Task A_control_the_restore_could_not_put_back_is_named()
    {
        var backend = C920e();
        var snapshot = ControlSnapshot.Capture(backend, "cam");
        backend.Set(UvcControlCatalog.WhiteBalance, new UvcControlValue(6500, false));
        backend.Set(UvcControlCatalog.Sharpness, new UvcControlValue(0, false));
        backend.Ignore.Add(UvcControlCatalog.WhiteBalance);
        backend.Reject.Add(UvcControlCatalog.Sharpness);

        var failed = await CameraLock.ApplyRestoreAsync(backend, snapshot, TimeSpan.Zero, Token);

        failed.Should().BeEquivalentTo(["WhiteBalance", "Sharpness"]);
        backend.Get(UvcControlCatalog.Brightness).Should().Be(new UvcControlValue(140, false), "the others are still put back");
    }

    [Fact]
    public void The_snapshot_round_trips_through_json_for_the_journal()
    {
        var snapshot = ControlSnapshot.Capture(C920e(), "usb-046d-08b6");

        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        var back = System.Text.Json.JsonSerializer.Deserialize<ControlSnapshot>(json)!;

        back.DeviceId.Should().Be("usb-046d-08b6");
        back.Entries.Should().BeEquivalentTo(snapshot.Entries);
    }

    [Theory]
    [InlineData(@"\\?\usb#vid_046d&pid_08b6&mi_00#8&12999464&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global", true, 0x046D, 0x08B6)]
    [InlineData(@"\\?\swd#vcamdevapi#{e5323777-f976-4f5b-9b55-b94699c46e44}\global", false, null, null)]
    [InlineData(@"\\?\root#image#0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global", false, null, null)]
    public void Only_usb_hardware_cameras_are_accepted(string link, bool usb, int? vid, int? pid)
    {
        var device = new WebcamDevice("Camera", link);

        device.IsUsbHardware.Should().Be(usb);
        device.VendorId.Should().Be(vid);
        device.ProductId.Should().Be(pid);
        if (usb)
        {
            device.StableId.Should().Be("usb-046d-08b6", "the instance path (which identifies the port) is not part of the id");
        }
    }

    [Fact]
    public void A_range_clamps_and_snaps_to_its_step()
    {
        var focus = new UvcControlRange(0, 250, 5, 0, true, true);

        focus.Clamp(-3).Should().Be(0);
        focus.Clamp(7).Should().Be(5);
        focus.Clamp(8).Should().Be(10);
        focus.Clamp(999).Should().Be(250);
    }

    /// <summary>A UVC backend in memory. Controls in <see cref="Ignore"/> accept writes but keep their value;
    /// controls in <see cref="Reject"/> throw. <see cref="Log"/> lists the writes accepted, in order.</summary>
    private sealed class FakeBackend(Dictionary<UvcControlId, (UvcControlRange Range, UvcControlValue Value)> controls) : IUvcControlBackend
    {
        public HashSet<UvcControlId> Ignore { get; } = [];

        public HashSet<UvcControlId> Reject { get; } = [];

        public List<(UvcControlId Id, UvcControlValue Value)> Log { get; } = [];

        public UvcControlRange? GetRange(UvcControlId id) => controls.TryGetValue(id, out var c) ? c.Range : null;

        public UvcControlValue? Get(UvcControlId id) => controls.TryGetValue(id, out var c) ? c.Value : null;

        public void Set(UvcControlId id, UvcControlValue value)
        {
            if (!controls.TryGetValue(id, out var c))
            {
                throw new InvalidOperationException("not supported");
            }

            if (Reject.Contains(id))
            {
                throw new InvalidOperationException("rejected");
            }

            Log.Add((id, value));
            if (!Ignore.Contains(id))
            {
                controls[id] = (c.Range, value.IsAuto ? new UvcControlValue(value.Value, true) : value);
            }
        }

        public void ForceAuto(UvcControlId id) => controls[id] = (controls[id].Range, controls[id].Value with { IsAuto = true });
    }
}
