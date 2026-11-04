# M1 feasibility report: webcam as a display instrument

Measured on 2026-10-15 with `tools/SoftCal.FeasibilityHarness` (every run recorded under
`%LOCALAPPDATA%\SoftColorCalibrator\sessions\m1`). Setup: Logitech C920e on its inbox UVC driver, 8–10 cm from the
screen centre inside a black paper tube, LED taped; NVIDIA RTX 4000 Ada; Windows 11 build 26300, Night light switched off
by the user for the runs, GPU gamma ramps identity.

## Recommendation: **go with restrictions**

The halftone null works with this webcam. It is precise (matched-reading σ 0.007–0.06 code), it repeats (tree points
within 0.13 code, gamma within 0.018 over 3 runs), and it agrees with camera-linearized exposure bracketing to about
0.1 code at mid-tones. The camera lock and the trust test are solid. Bracketing-only is not needed. The restrictions:

1. **Camera placement.** Results depend on where the camera sits. Re-seating it by hand moved the matched codes by
   0.7–1.6 code on the ViewSonic and by **17–32 codes on the TN BenQ**: a TN panel's brightness and gamma change
   with viewing angle, and a hand placement changes the angle. A room light switched on and off changed nothing
   (≤ 0.2 code), so the hood works and ambient light is not the cause. M3 needs a rigid spacer (the hood tube
   resting flat on the screen), a placement self-check, and an "angle-dependent" confidence label on TN panels.
2. **Halftone geometry.** On the TN BenQ, 1-px checkerboards, vertical lines and Bayer dithers read 1.1–1.6 codes
   low: horizontal subpixel crosstalk, with R and B pulled 8–12 codes below G. Horizontal lines agree with bracketing
   to 0.1 code on both monitors. Make horizontal lines the default halftone and keep the pattern-shape check on
   every monitor.
3. **Measurement time.** The gate campaign took ~31 min on the BenQ; the user's limit for the product is about
   5 min in total and 1–2 min per phase. The quick reading profile (frames capped by integrated exposure, one
   palindrome per null) measures four nulls in ~1 min and ran the whole gate sequence on the ViewSonic, with three
   trees, bracketing and the gamma check, in 6.9 min. M3's Quick plan should be one horizontal-line tree plus the
   per-channel nulls it needs, without bracketing.
4. **Deep shadows.** The C920e tops out at a ¼ s exposure and clamps black. The tree reaches 1/512 of white (code ~19
   on the BenQ) with σ 0.39 code at the bottom. The ViewSonic's black stays below the camera's floor even at ¼ s,
   so black level and contrast can only be reported as "≥ N:1" (the plan's fallback). A raised camera gain for the
   darkest levels is worth trying in M3.

## Gate

| Criterion | Limit | BenQ XL2720 (TN, DP 144 Hz) | ViewSonic VX2770 (60 Hz, limited range) |
|---|---|---|---|
| Matched-reading σ | ≤ 0.3 code | **0.007** ✅ (50 % null, 5 palindromes) | **0.060** ✅ |
| Pattern shapes agree | ≤ 1 code | **1.57** ❌ (50 %: 1.14; 25 %: 1.57; crosstalk) | **0.45** ✅ (25 %: 0.43) |
| Tree points ~72 / ~38 / ~21 repeat | ≤ 1 code | **0.13 / 0.02 / 0.08** ✅ (codes 71 / 39 / 19) | **0.04 / 0.01 / 0.01** ✅ (codes 84 / 38 / 24) |
| ±1 cm camera move | ≤ 1 code | **1.2–2.8** ❌ at a fixed pose; 17–32 after re-placing | **0.7–1.6** ❌ (hand re-seat) |
| Ripple bias | < 0.1 % | **0.061 %** ✅ (worst pattern; checkerboard 0.003 %) | **0.063 %** ✅ |
| Gamma spread, 3 runs | ≤ 0.03 | **0.018** ✅ (2.614 / 2.619 / 2.632) | **0.000** ✅ (1.840 ×3) |
| Locks hold 30 min | ≥ 30 min | **30.6 min** ✅; 13 runs, no control changed | 6.9 min in its run ✅ (same camera lock) |
| **Outcome** | | **go with restrictions** | **go with restrictions** |

Tree points are the halvings nearest codes 72/38/21. A display's gamma decides where they fall: on the BenQ the 9th
halving (1/512) landed at code 19; on the ViewSonic the 6th (1/64) at code 24 and the 7th already at 15.

## What was measured

- **Lock and trust.** Every writable control was written before the stream started and read back unchanged in all 16
  runs (white balance 6500 K, manual exposure, gain 0, sharpness 0, backlight compensation 0, manual focus, defaults
  written explicitly; power-line frequency and AE priority pinned through IKsControl). The C920e has no Windows camera
  effects. `IMFCameraControlMonitor` and `IMFSensorActivityMonitor` ran throughout and saw no change. Controls were
  restored after every run; the journal and the guardian process were armed. Trust test: white moved 0.06 % (BenQ) and
  0.20 % (ViewSonic) after red, blue and black, and drifted 0.03 % / 0.06 % over 10 s (limit 1 %).
- **Camera facts.** Native YUY2 640×480 at 30 fps, BT.601 limited range; it saturates at Y = 232 (not 235) and clamps
  black at Y = 16, so clip detection uses the measured levels. Exposure steps are ×2 within ±3 % (fitted). White at the
  shortest step (0.49 ms) is only ~4 codes above black.
- **Patch window.** D3D11 flip model, pixel-exact on both monitors for all 11 test patterns (desktop-duplication
  read-back, 0 of 2 073 600 pixels different). Each patch is stamped with the QPC time of its scan-out vblank from DXGI
  frame statistics. This needs continuous presentation: a single present returns empty statistics.
- **Optical sync.** The camera's device timestamps and the scan-out times share the QPC clock. The combined offset is
  −0.7 to −0.2 ms on the BenQ and 12.7 ms on the slower 60 Hz ViewSonic (residual 2.3–2.9 % of the flash contrast).
- **Flicker.** BenQ: none (0.08 % modulation). ViewSonic: PWM banding with a period of 60 rows and 6 % modulation at
  3.9 ms. It did not affect the nulls, which run at 15–250 ms exposures with A-B-B-A ordering. The rolling-shutter line
  time could not be calibrated reliably from transitions (16–56 µs), so the PWM frequency is only known as
  ~250–550 Hz.
- **Defocus.** At 8–10 cm, focus 150 is sharp; focus 50 gives the least residual ripple on both monitors: checkerboard
  0.3–0.5 %, lines 1.0–1.5 %, Bayer 4/16 2.2 %. The worst-case null bias is 0.06 %.
- **Bracketing.** A Robertson-style fit with the exposure ratios as unknowns, anchored by a horizontal-line halftone. It
  needed two fixes found on the hardware: gauges only over exposures that have data, and black below the camera's
  floor treated as zero. The bracketed solid-patch curves match the nulls: BenQ 50 % at code 227.3 vs 227.4, 1/16 at
  126.1 vs 126.1; ViewSonic effective gamma 1.893 vs 1.889. The same fit measures each halftone's additivity: on the
  BenQ the checkerboard emits 1.9 % less light than horizontal lines, which is the 1.2-code null difference.
- **Display curves.** BenQ (brightness 18, contrast 50, DDC gamma code 120): crushed shadows; 50 % light at code 210
  and γ 2.6 (10–90 %) at the first camera pose, code 227 and γ 3.6 at the second — the TN angle effect again.
  ViewSonic: γ 1.84 (log-log) / 1.89 (at 50 %).
- **Gamma ramp.** `SetDeviceGammaRamp` was accepted and read back on both monitors. With a ×0.875 ramp the camera saw
  code 128 as 112.00 (BenQ) and 112.05 (ViewSonic), expected 112. The original was restored and checked by read-back
  and by camera (within 0.01 code). The `vcgt` path is viable.

## Platform probes

- **Night light** (read-only CloudStore Bond parsing): read ON at 2631 K, manual, then OFF after the user switched it in
  Settings. Windows rewrote only the *global* key; the per-device key kept its old "on" state. The reader therefore
  takes the newest blob, and M2's watch must do the same.
- **DDC/CI** (codes 0x10, 0x12, 0x14, 0x16/18/1A, 0x6C/6E/70, 0x72, 0xDC one by one; never the capabilities request):
  both monitors answer within ~60 ms per code, no stalls. BenQ: brightness 18, contrast 50, preset 6500 K, gains
  84/85/84, gamma code 120; no black levels. ViewSonic: brightness 45, contrast 60, preset 6500 K, gains 50, black
  levels 80; no gamma, no 0xDC.
- **Link** (DisplayConfig + NVAPI GET): both RGB 8 bpc, SDR, ACM off. **The ViewSonic receives limited-range RGB
  (16–235)**, the BenQ full range. The status report must flag this; the fix is NVIDIA Control Panel → Output dynamic
  range: Full, with the monitor's input range matched.

## Next

- M2: StateJournal + guardian for every state kind (built here for camera controls and gamma ramps); the Night light
  watch must take the newest blob.
- M3: a Quick plan of 5 minutes or less with horizontal-line halftones, the placement self-check and spacer guidance,
  the pattern-shape check, and limited-range detection. Also: line-time calibration for PWM frequency, and gain for
  the deepest tree levels.
