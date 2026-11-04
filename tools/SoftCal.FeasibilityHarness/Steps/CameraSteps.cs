using SoftCal.Core.Analysis;
using SoftCal.Core.Color;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.FeasibilityHarness.Infrastructure;
using SoftCal.Instruments.Webcam;
using SoftCal.Instruments.Webcam.Controls;
using SoftCal.Instruments.Webcam.Frames;

namespace SoftCal.FeasibilityHarness.Steps;

/// <summary>Lock + read-back, trust test, optical sync, flicker, defocus and the long lock hold.</summary>
internal static class CameraSteps
{
    private static readonly SolidPatch White = new(DeviceRgb.White);

    // ---------------------------------------------------------------- lock

    public static async Task LockAsync(StepContext c)
    {
        Ui.Title("Lock and read back");
        var focus = c.Args.Get("focus") is { } f ? int.Parse(f, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
        var report = await c.Rig.LockAsync(new LockRequest { WhiteBalanceKelvin = 6500, FocusValue = focus }, c.Token);
        object? detail = null;
        if (c.Rig.Webcam is { } cam)
        {
            Ui.Info($"camera: {cam.Device.FriendlyName}, format {cam.Format.Subtype} {cam.Format.Width}×{cam.Format.Height} @ {cam.Format.FrameRate:0.##} fps, matrix {cam.Format.Matrix} range {cam.Format.Range}");
            Ui.Info($"{"control",-24} {"before",-14} {"requested",-12} {"read back",-14} result");
            foreach (var v in cam.LockVerification ?? [])
            {
                Ui.Info($"{v.Name,-24} {Show(v.Before),-14} {Show(v.Requested),-12} {Show(v.After),-14} {v.Outcome}{(v.Note is null ? "" : "  (" + v.Note + ")")}");
            }

            foreach (var e in cam.EffectsAfterLock)
            {
                Ui.Info($"{e.Control,-24} {(e.Supported ? $"flags {e.Flags}" : "not supported"),-40} {e.Note}");
            }

            Ui.Info($"studio effects available: {cam.StudioEffectsAvailable?.ToString() ?? "unknown"}; control monitor: {cam.ControlWatcherFailure ?? "watching"}; sensor activity monitor: {cam.ActivityWatcherFailure ?? "watching"}");
            var range = await DetectRangeAsync(c, cam);
            detail = new
            {
                format = cam.Format,
                nativeFormatCount = cam.NativeFormats.Count,
                snapshot = cam.Snapshot,
                plan = cam.LockPlan,
                verification = cam.LockVerification,
                effects = cam.EffectsAfterLock,
                studioEffectsAvailable = cam.StudioEffectsAvailable,
                controlWatcher = cam.ControlWatcherFailure ?? "watching",
                sensorActivityWatcher = cam.ActivityWatcherFailure ?? "watching",
                timestampSource = cam.TimestampSource?.ToString(),
                range,
                decoder = cam.Decoder,
                clip = cam.Clip,
            };
        }

        (report.CriticalControlsLocked ? (Action<FormattableString>)Ui.Good : Ui.Warn)($"critical controls (white balance, exposure, gain) locked: {report.CriticalControlsLocked}");
        c.Session.Result("lock", new { report, criticalLocked = report.CriticalControlsLocked, detail });
    }

    /// <summary>
    /// Finds the camera's real code range from its clip levels: saturated white at the longest exposure and display
    /// black at the shortest. The media type may claim limited range while the camera encodes full range (or vice
    /// versa); the null method does not care, but clipping detection does.
    /// </summary>
    private static async Task<object> DetectRangeAsync(StepContext c, WebcamInstrument cam)
    {
        var steps = c.Measure.ExposureSteps;
        async Task<(int Min, int Max, double MeanY)> Probe(PatchSpec patch, double exposure)
        {
            await c.Measure.SetExposureAsync(exposure, c.Token);
            var shown = await c.Measure.ShowAsync(patch, c.Token);
            await c.Measure.WaitUntilAsync(shown.PresentTimeSeconds + c.Measure.LatencySeconds + (2 * exposure) + 0.3, c.Token);
            int min = 255, max = 0, n = 0;
            double y = 0;
            await foreach (var frame in cam.StreamDetailedAsync(c.Roi, c.Token))
            {
                min = Math.Min(min, frame.MinLuma);
                max = Math.Max(max, frame.MaxLuma);
                y += frame.MeanY;
                if (++n == 6)
                {
                    break;
                }
            }

            return (min, max, y / n);
        }

        var bright = await Probe(White, steps[^1]);
        var dark = await Probe(new SolidPatch(DeviceRgb.Black), steps[0]);
        var full = bright.Max >= 250 || dark.Min <= 4;
        var decoder = new YcbcrDecoder(cam.Format.Matrix ?? YcbcrMatrix.Bt601, full ? YcbcrRange.Full : YcbcrRange.Limited);

        // Clip where this camera really saturates (the C920e tops out below the nominal 235), so clipped frames are
        // never mistaken for bright ones.
        var saturated = bright.MeanY >= bright.Max - 1.5;
        var nominal = ClipThresholds.For(decoder.Range);
        var clip = saturated
            ? new ClipThresholds(0.5, decoder.Luma(bright.Max) - 2, Math.Max(nominal.LumaLow, dark.Min), bright.Max - 1)
            : nominal;
        cam.SetDecoding(decoder, clip);
        Ui.Info($"code range: saturated white Y {bright.Max} (mean {bright.MeanY:0.0}), black at {steps[0] * 1000:0.##} ms Y {dark.Min}..; using {decoder.Range} range {decoder.Matrix}; clipped at Y ≥ {clip.LumaHigh} or channel ≥ {clip.ChannelHigh:0.0}");
        return new { saturatedMaxLuma = bright.Max, saturatedMeanLuma = bright.MeanY, blackMinLuma = dark.Min, blackMeanLuma = dark.MeanY, chosen = decoder.Range.ToString(), clip };
    }

    private static string Show(UvcControlValue? v) => v is { } x ? (x.IsAuto ? $"auto ({x.Value})" : x.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : "—";

    // ---------------------------------------------------------------- trust

    public static async Task TrustAsync(StepContext c)
    {
        Ui.Title("Camera trust test (W→R→W→B→W→K→W, 10 s white)");
        await c.Measure.AutoExposeAsync(White, 180, c.Token);
        PatchSpec[] sequence =
        [
            White, new SolidPatch(new DeviceRgb(255, 0, 0)), White, new SolidPatch(new DeviceRgb(0, 0, 255)),
            White, new SolidPatch(DeviceRgb.Black), White,
        ];
        var readings = new List<Reading>();
        foreach (var patch in sequence)
        {
            var policy = ReadPolicy.Default with { Frames = 20, SettleMarginSeconds = 0.6, MaxClippedFraction = 1 };
            readings.Add(await c.Measure.ReadAsync(patch, policy, c.Token));
        }

        var whites = readings.Where(r => r.Patch == White).Select(r => r.Mean).ToList();
        var shown = await c.Measure.ShowAsync(White, c.Token);
        await c.Measure.WaitUntilAsync(shown.PresentTimeSeconds + c.Measure.LatencySeconds + c.Measure.ExposureSeconds + 0.3, c.Token);
        var hold = await c.Measure.CollectAsync(10, c.Token);
        var stability = hold.Select(f => new Vec3(f.Red.Mean, f.Green.Mean, f.Blue.Mean)).ToList();
        var result = TrustTest.Evaluate(whites, stability);
        (result.Passed ? (Action<FormattableString>)Ui.Good : Ui.Warn)(
            $"white after colors: max change {result.WhiteSequenceDeviation:P2}; 10 s drift {result.StabilityDrift:P2}, frame noise {result.StabilityNoise:P2} → {(result.Passed ? "PASS" : "FAIL")} (limit 1 %)");
        c.Session.Result("trust", new { result, exposureSeconds = c.Measure.ExposureSeconds, readings, holdFrames = hold.Count });
    }

    // ---------------------------------------------------------------- sync

    public static async Task SyncAsync(StepContext c)
    {
        const double SymbolSeconds = 0.4;
        Ui.Title($"Optical latency / clock calibration (31-symbol code, {OpticalSync.MaxFlashRateHz(SymbolSeconds):0.##} Hz max)");
        var off = new SolidPatch(DeviceRgb.Gray(64));
        var on = new SolidPatch(DeviceRgb.Gray(192));
        await c.Measure.AutoExposeAsync(on, 170, c.Token);
        var code = OpticalSync.MSequence31();
        var transitions = new List<SyncTransition>();
        var start = await c.Measure.ShowAsync(off, c.Token);
        transitions.Add(new SyncTransition(start.PresentTimeSeconds, false));
        await c.Measure.WaitUntilAsync(start.PresentTimeSeconds + 1.0, c.Token);
        c.Measure.BeginCapture();
        var level = false;
        foreach (var bit in code)
        {
            var shown = bit == level ? null : await c.Measure.ShowAsync(bit ? on : off, c.Token);
            if (shown is not null)
            {
                transitions.Add(new SyncTransition(shown.PresentTimeSeconds, bit));
                level = bit;
            }

            var symbolStart = shown?.PresentTimeSeconds ?? transitions[^1].TimeSeconds;
            await c.Measure.WaitUntilAsync(Math.Max(symbolStart, c.Measure.LastFrame!.TimestampSeconds) + SymbolSeconds - 0.01, c.Token);
        }

        var last = await c.Measure.ShowAsync(off, c.Token);
        transitions.Add(new SyncTransition(last.PresentTimeSeconds, false));
        await c.Measure.WaitUntilAsync(last.PresentTimeSeconds + 1.0, c.Token);
        var frames = c.Measure.EndCapture();

        // Linearize the luma with a nominal camera gamma: the fit only needs the on/off proportion within a frame.
        var samples = frames.Select(f => new SyncSample(f.TimestampSeconds, Math.Pow(Math.Max(0, Luma(f)) / 255.0, 2.2))).ToList();
        var result = OpticalSync.Fit(transitions, samples, c.Measure.ExposureSeconds);
        c.Measure.LatencySeconds = Math.Max(0.0, result.OffsetSeconds);
        var good = result.RelativeResidual < 0.15;
        (good ? (Action<FormattableString>)Ui.Good : Ui.Warn)(
            $"latency (display → frame timestamp) {result.OffsetSeconds * 1000:0.0} ms; residual {result.RelativeResidual:P1} of contrast; halves {result.OffsetFirstHalfSeconds * 1000:0.0}/{result.OffsetSecondHalfSeconds * 1000:0.0} ms (drift {result.DriftPpm:0} ppm); {frames.Count} frames");
        c.Session.Result("sync", new
        {
            result,
            reliable = good,
            symbolSeconds = SymbolSeconds,
            maxFlashRateHz = OpticalSync.MaxFlashRateHz(SymbolSeconds),
            exposureSeconds = c.Measure.ExposureSeconds,
            transitions,
            presenterTiming = c.Rig.Window?.LastTiming,
            timestampSource = c.Rig.Webcam?.TimestampSource?.ToString(),
        });
    }

    // ---------------------------------------------------------------- flicker

    public static async Task FlickerAsync(StepContext c)
    {
        Ui.Title("PWM flicker from rolling-shutter banding");
        var tall = new RoiSpec(0.5, 0.5, 0.6, 0.9) { RowBands = 2000 };
        await using var rows = new MeasurementRig(c.Rig.Instrument, c.Rig.Presenter, tall, c.Session) { LatencySeconds = c.Measure.LatencySeconds };
        var steps = rows.ExposureSteps;

        // 1. Line time, from frames caught mid-transition (gentle 32 ↔ 224 steps, 1 per second).
        var lineExposure = steps.FirstOrDefault(s => s >= 1.0 / 300) is var le && le > 0 ? le : steps[0];
        await rows.SetExposureAsync(lineExposure, c.Token);
        var darkPatch = new SolidPatch(DeviceRgb.Gray(32));
        var brightPatch = new SolidPatch(DeviceRgb.Gray(224));
        var dark = await rows.ReadAsync(darkPatch, ReadPolicy.Default with { Frames = 8, MaxClippedFraction = 1 }, c.Token);
        var bright = await rows.ReadAsync(brightPatch, ReadPolicy.Default with { Frames = 8, MaxClippedFraction = 1 }, c.Token);
        var transitionFrames = new List<IReadOnlyList<double>>();
        for (var i = 0; i < (c.Quick ? 4 : 6); i++)
        {
            await rows.ShowAsync(darkPatch, c.Token);
            await rows.WaitUntilAsync(rows.LastFrame!.TimestampSeconds + 0.5, c.Token);
            rows.BeginCapture();
            var shown = await rows.ShowAsync(brightPatch, c.Token);
            await rows.WaitUntilAsync(shown.PresentTimeSeconds + rows.LatencySeconds + 0.3, c.Token);
            transitionFrames.AddRange(rows.EndCapture().Where(f => f.RowBandMeans is not null).Select(f => f.RowBandMeans!));
        }

        double DarkLuma(Reading r) => (0.299 * r.Mean.X) + (0.587 * r.Mean.Y) + (0.114 * r.Mean.Z);
        var lineTime = FlickerAnalysis.LineTimeFromTransition(transitionFrames, lineExposure, DarkLuma(dark), DarkLuma(bright));

        // 2. Flicker: white at the shortest exposure that gives a usable signal (short exposures integrate the least
        // PWM away, but the C920e's shortest ones leave white under its black clamp), 3 s of frames.
        FlickerResult? result = null;
        double usedExposure = 0;
        foreach (var exposure in steps)
        {
            await rows.SetExposureAsync(exposure, c.Token);
            var reading = await rows.ReadAsync(White, ReadPolicy.Default with { Frames = 4, MaxClippedFraction = 1 }, c.Token);
            var level = Math.Max(reading.Mean.X, Math.Max(reading.Mean.Y, reading.Mean.Z));
            if (level < 40)
            {
                continue;
            }

            if (level > 230)
            {
                break;
            }

            var frames = await rows.CollectAsync(c.Quick ? 2 : 3, c.Token);
            var bands = frames.Where(f => f.RowBandMeans is { Count: >= 16 }).Select(f => f.RowBandMeans!).ToList();
            result = FlickerAnalysis.Analyze(bands, lineTime);
            usedExposure = exposure;
            break;
        }

        if (result is null)
        {
            c.Skip("flicker", "no exposure gives white a usable, unclipped signal");
            return;
        }

        Ui.Info($"line time {(lineTime is null ? "unknown" : $"{lineTime * 1e6:0.0} µs")} (from {transitionFrames.Count} transition frames, ±15 % from the display's own scan)");
        (result.Detected ? (Action<FormattableString>)Ui.Warn : Ui.Good)(
            $"{(result.Detected ? "flicker DETECTED" : "no flicker")}: peak {result.PeakPeriodRows:0.0} rows/cycle, depth {result.ModulationDepth:P2} (floor {result.NoiseFloorDepth:P2}){(result.FrequencyHz is { } hz ? $", ≈ {hz:0} Hz" : "")} at {usedExposure * 1000:0.###} ms");
        c.Session.Result("flicker", new { result, lineTimeSeconds = lineTime, exposureSeconds = usedExposure, lineExposureSeconds = lineExposure, transitionFrames = transitionFrames.Count });
    }

    // ---------------------------------------------------------------- defocus

    public static async Task DefocusAsync(StepContext c)
    {
        Ui.Title("Defocus by measured ripple (limit 2.5 %)");
        if (c.Rig.Webcam is not { } cam)
        {
            c.Skip("defocus", "needs per-pixel images (webcam only)");
            return;
        }

        var range = cam.Snapshot.Find(UvcControlCatalog.Focus)?.Range;
        if (range is null)
        {
            c.Skip("defocus", "the camera has no focus control");
            return;
        }

        int[] candidates = c.Quick
            ? [range.Min, range.Min + 50, range.Min + 100]
            : [range.Min, range.Min + 25, range.Min + 50, range.Min + 100, range.Min + 150, range.Max];
        int[] focusValues = [.. candidates.Select(range.Clamp).Distinct()];
        var imageFrames = c.Quick ? 12 : 30;
        var half = new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard);
        var patterns = new (string Name, PatchSpec Patch)[]
        {
            ("checkerboard", half),
            ("horizontal lines", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.HorizontalLines)),
            ("vertical lines", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.VerticalLines)),
            ("bayer 4/16", new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 4)),
        };
        var solid50 = new SolidPatch(DeviceRgb.Gray(186));
        var solid25 = new SolidPatch(DeviceRgb.Gray(136));
        var imageRoi = new RoiSpec(0.5, 0.5, 0.25, 0.25) { RowBands = 0 };

        // Expose on the solid that matches the checkerboard's mean: an unblurred halftone has black and clipped pixels.
        await c.Measure.AutoExposeAsync(solid50, 140, c.Token);

        async Task<RippleMeasurement> Image(PatchSpec patch)
        {
            var shown = await c.Measure.ShowAsync(patch, c.Token);
            await c.Measure.WaitUntilAsync(shown.PresentTimeSeconds + c.Measure.LatencySeconds + c.Measure.ExposureSeconds + 0.4, c.Token);
            var image = await cam.CaptureLumaImageAsync(imageRoi, imageFrames, 1, c.Token);
            return RippleAnalysis.Measure(image.Pixels, image.Width, image.Height);
        }

        var sweep = new List<object>();
        int? chosen = null;
        double chosenWorst = double.PositiveInfinity;
        foreach (var focus in focusValues)
        {
            await cam.SetFocusAsync(focus, c.Token);
            await c.Measure.WaitUntilAsync(c.Measure.LastFrame!.TimestampSeconds + 1.0, c.Token);
            var reference50 = await Image(solid50);
            var reference25 = await Image(solid25);
            var rows = new List<object>();
            var worst = 0.0;
            var worstBias = 0.0;
            foreach (var (name, patch) in patterns)
            {
                var m = await Image(patch);
                var r = RippleAnalysis.Evaluate(m, name.StartsWith("bayer", StringComparison.Ordinal) ? reference25 : reference50, c.CameraGamma);
                worst = Math.Max(worst, r.ExcessRelativeRms);
                worstBias = Math.Max(worstBias, Math.Abs(r.NullBias));
                rows.Add(new { pattern = name, ripple = r });
            }

            Ui.Info($"focus {focus,3}: worst excess ripple {worst:P2}, worst null bias {worstBias:P3}");
            sweep.Add(new { focus, worstRipple = worst, worstBias, solid50 = reference50, solid25 = reference25, patterns = rows });

            // Keep the focus with the least ripple among those within the limit.
            if (worst <= RippleResult.RippleLimit && worst < chosenWorst)
            {
                chosen = focus;
                chosenWorst = worst;
            }
        }

        var final = chosen ?? focusValues.First();
        await cam.SetFocusAsync(final, c.Token);
        await c.Measure.WaitUntilAsync(c.Measure.LastFrame!.TimestampSeconds + 1.5, c.Token);

        // Re-measure at the chosen focus: this is the ripple the nulls will see.
        var s50 = await Image(solid50);
        var s25 = await Image(solid25);
        var finalRows = new List<RippleResult>();
        foreach (var (name, patch) in patterns)
        {
            finalRows.Add(RippleAnalysis.Evaluate(await Image(patch), name.StartsWith("bayer", StringComparison.Ordinal) ? s25 : s50, c.CameraGamma));
        }

        var worstFinal = finalRows.Max(r => r.ExcessRelativeRms);
        var biasFinal = finalRows.Max(r => Math.Abs(r.NullBias));
        (chosen is not null ? (Action<FormattableString>)Ui.Good : Ui.Warn)(
            $"focus {final}: residual ripple {worstFinal:P2} (limit 2.5 %), null bias {biasFinal:P3} (limit 0.1 %) at camera γ {c.CameraGamma:0.##}");
        c.Session.Result("defocus", new
        {
            focus = final,
            withinLimit = chosen is not null,
            worstRipple = worstFinal,
            worstBias = biasFinal,
            cameraGamma = c.CameraGamma,
            patterns = patterns.Select((p, i) => new { pattern = p.Name, ripple = finalRows[i] }),
            sweep,
        });
    }

    // ---------------------------------------------------------------- hold

    public static async Task HoldAsync(StepContext c)
    {
        var minutes = c.Args.GetDouble("minutes", 30);
        Ui.Title($"Lock hold ({minutes:0} min, white read every 30 s)");
        await c.Measure.AutoExposeAsync(White, 180, c.Token);
        var start = c.Session.Now;
        var samples = new List<object>();
        var changes = 0;
        Vec3? first = null;
        var maxDrift = 0.0;
        while (c.Session.Now - start < minutes * 60)
        {
            var reading = await c.Measure.ReadAsync(White, ReadPolicy.Default with { Frames = 10, MaxClippedFraction = 1 }, c.Token);
            first ??= reading.Mean;
            var drift = Math.Max(Math.Abs(reading.Mean.X / first.Value.X - 1), Math.Max(Math.Abs(reading.Mean.Y / first.Value.Y - 1), Math.Abs(reading.Mean.Z / first.Value.Z - 1)));
            maxDrift = Math.Max(maxDrift, drift);
            var check = c.Rig.Webcam?.CheckLock() ?? [];
            var mismatched = check.Where(x => x.Actual is not { } a || a.Value != x.Expected.Value || a.IsAuto != x.Expected.IsAuto).Select(x => x.Control.ToString()).ToList();
            changes += mismatched.Count;
            samples.Add(new { t = c.Session.Now - start, white = reading.Mean, drift, mismatched });
            Ui.Info($"t = {(c.Session.Now - start) / 60:0.0} min: white {reading.Mean.Y:0.00} (drift {drift:P2}){(mismatched.Count > 0 ? "  CHANGED: " + string.Join(", ", mismatched) : "")}");
            await c.Measure.WaitUntilAsync(c.Measure.LastFrame!.TimestampSeconds + 30, c.Token);
        }

        var external = c.Rig.Webcam?.ExternalChanges ?? [];
        var held = changes == 0 && external.Count == 0;
        (held ? (Action<FormattableString>)Ui.Good : Ui.Warn)($"locks {(held ? "held" : "did NOT hold")} for {(c.Session.Now - start) / 60:0.0} min; max white drift {maxDrift:P2}");
        c.Session.Result("hold", new { minutes = (c.Session.Now - start) / 60, held, readBackChanges = changes, externalChanges = external, maxWhiteDrift = maxDrift, samples });
    }

    private static double Luma(FrameStats f) => (0.299 * f.Red.Mean) + (0.587 * f.Green.Mean) + (0.114 * f.Blue.Mean);
}
