using SoftCal.Core.Measurement;
using SoftCal.FeasibilityHarness.Infrastructure;
using SoftCal.Windows.Gamma;
using SoftCal.Windows.NightLight;

namespace SoftCal.FeasibilityHarness.Steps;

/// <summary>
/// Gamma ramp write and camera check: a test ramp (×0.875) must make code 128 look exactly like code 112 to the camera;
/// the original ramp is journaled first and always restored (on success, error, Ctrl+C, or by the guardian after a
/// crash), then verified by read-back and by camera.
/// </summary>
internal static class GammaStep
{
    public const double Factor = 0.875;

    public static async Task RunAsync(StepContext c)
    {
        Ui.Title("Gamma ramp write and camera check");
        if (c.Rig.Monitor is not { } monitor)
        {
            c.Skip("gamma", "needs a real monitor");
            return;
        }

        var nightLight = NightLightReader.Read();
        if (nightLight.Active != false)
        {
            Ui.Warn(nightLight.Summary);
            c.Session.Result("gamma", new { skipped = true, reason = "Night light is on or unknown", nightLight = nightLight.Summary });
            return;
        }

        var device = new GdiGammaRampDevice(monitor.DeviceName);
        Safety.EnsureGuardian();
        using var guard = GammaRampGuard.Begin(device, Safety.Journal);
        var original = guard.Original;
        Ui.Info($"original ramp of {monitor.DeviceName}: {(original.IsIdentity ? "identity" : $"NOT identity (max deviation {original.MaxDeviationFromIdentity()} / 65535)")}; journaled");

        var options = ReadPolicy.Default with { Frames = 20 };
        var s128 = new SolidPatch(DeviceRgb.Gray(128));
        var s112 = new SolidPatch(DeviceRgb.Gray(112));
        await c.Measure.AutoExposeAsync(s128, 140, c.Token);

        Reading? baseline112 = null, baseline128 = null, test128 = null, restored128 = null;
        bool written, verifiedWrite, restoredOk;
        try
        {
            baseline112 = await c.Measure.ReadAsync(s112, options, c.Token);
            baseline128 = await c.Measure.ReadAsync(s128, options, c.Token);

            var test = GammaRamp.Scaled(Factor);
            written = guard.Apply(test);
            verifiedWrite = device.Read().SameAs(test);
            await c.Measure.WaitUntilAsync(c.Measure.LastFrame!.TimestampSeconds + 0.5, c.Token);
            test128 = await c.Measure.ReadAsync(s128, options, c.Token);
        }
        finally
        {
            restoredOk = guard.Restore();
        }

        await c.Measure.WaitUntilAsync(c.Measure.LastFrame!.TimestampSeconds + 0.5, c.Token);
        restored128 = await c.Measure.ReadAsync(s128, options, c.Token);

        // Express the camera differences in display codes, using the 112 → 128 slope.
        var slope = (baseline128.Channel(3) - baseline112.Channel(3)) / 16;
        var effectCode = 128 - ((baseline128.Channel(3) - test128.Channel(3)) / slope);
        var restoreCode = (restored128.Channel(3) - baseline128.Channel(3)) / slope;
        var tookEffect = Math.Abs(effectCode - 112) <= 1;
        var restoredByCamera = Math.Abs(restoreCode) <= 0.5;
        (tookEffect && restoredOk && restoredByCamera ? (Action<FormattableString>)Ui.Good : Ui.Warn)(
            $"SetDeviceGammaRamp {(written ? "accepted" : "REJECTED")}, read back {(verifiedWrite ? "identical" : "different")}; camera sees code 128 as {effectCode:0.00} (expected 112) → {(tookEffect ? "the ramp takes effect" : "NO EFFECT")}; restore {(restoredOk ? "verified" : "FAILED")}, camera {restoreCode:+0.00;-0.00} code");
        c.Session.Result("gamma", new
        {
            factor = Factor,
            originalIsIdentity = original.IsIdentity,
            originalMaxDeviation = original.MaxDeviationFromIdentity(),
            written,
            verifiedWrite,
            tookEffect,
            seenAsCode = effectCode,
            restoredOk,
            restoredByCamera,
            restoreErrorCode = restoreCode,
            baseline112,
            baseline128,
            test128,
            restored128,
        });
    }
}
