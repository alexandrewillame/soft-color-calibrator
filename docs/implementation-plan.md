# Soft Color Calibrator: Implementation Plan

## 1. Context

You want a Windows app that uses a camera to calibrate a screen's **color, brightness, contrast and gamma**. v1
uses a USB webcam. A future version adds an Android phone on the same network. The app gives you:

- **A status report on the display:** tone curve and gamma, gray balance, an estimated white point, contrast and
  black level, clipping, RGB-range problems, Windows' color state and the monitor's identity.
- **Two ways to fix it:**
  1. **A calibration file for Windows:** an ICC profile, plus optional GPU curves (`vcgt`).
  2. **Manual monitor (OSD) settings:** the app recommends new values from the current OSD values. You enter those,
     or the app reads them over DDC/CI.
- **Gamma correction is optional at every level.**
- **Night light is detected,** and you must turn it off before measuring.
- **The camera must not alter colors.**
- **Everything sits behind an instrument interface,** so the phone can be added later.

**Your decisions:**
- Close-up camera placement for v1.
- No colorimeter: the absolute white point is shown as an estimate.
- English UI, with strings in resource files (resx) so it can be translated later.
- The repo stays in this OneDrive folder as-is.

The folder is empty apart from `Notes.txt`, which stays unread and untracked (it goes in `.gitignore`).

**Dev machine (checked read-only):**

| Item | Detail |
|---|---|
| OS | Windows 11 Pro, build 26300 ("26H2") |
| Tooling | .NET SDK 10.0.401, git, Python 3.14. **No Visual Studio**, so everything builds from the `dotnet` CLI |
| Camera | Logitech C920e on the inbox UVC driver (white balance 2000–6500 K, manual exposure and focus) |
| GPU | NVIDIA RTX 4000 Ada |
| Monitor 1 | BenQ ZOWIE XL2720: TN panel; OSD gamma presets 1–5; User RGB that can only go down; Black eQualizer |
| Monitor 2 | ViewSonic VX2770: probably no gamma control |
| Both monitors | 1080p, 2013, EDID 1.3/1.4 with near-sRGB primaries and gamma 2.2; Auto Color Management (ACM) supported but off |
| Color state | Night light currently **ON** (2631 K, manual). "Use Windows display calibration" **OFF**, and the Calibration Loader task disabled |

## 2. Recommended stack: C# on .NET 10 LTS, WPF, Windows 11 (22H2+)

- **Native API access.** Every needed API is reachable from C# through **CsWin32**-generated bindings and the WinRT
  projection, with no C++ toolchain (none is installed here):
  - Media Foundation and UVC camera controls
  - DDC/CI through `dxva2`
  - GPU gamma ramps
  - `mscms` color-profile APIs
  - DisplayConfig, for HDR and ACM state
  - `DisplayMonitor`, for the raw EDID
- **Support window.** .NET 10 is supported until Nov 2028. .NET 11, due Nov 2026, is a short-term release.
- **Why WPF.** It is mature for wizards, charts and multi-window apps, and builds from the CLI. WinUI (Windows App
  SDK 2.x) is the credible alternative, but deployment and charting take more work. The Fluent `ThemeMode` is still
  experimental, so we keep a plain style as a fallback.
- **Why Windows 11 only.** Windows 10 consumer extended support has ended. The modern profile-association APIs
  need build 20348 or later.
- **Target framework:** `net10.0-windows10.0.22621.0`. The math library, `SoftCal.Core`, is plain `net10.0` and
  headless-testable.

| Library (MIT/Apache, active in 2026) | Use |
|---|---|
| CommunityToolkit.Mvvm 8.4, Microsoft.Extensions.Hosting, Serilog | MVVM, dependency injection and config, rolling logs |
| Microsoft.Windows.CsWin32 | Win32 and COM bindings (Media Foundation, UVC, DXGI, mscms, dxva2, gdi32, user32) |
| Vortice.Direct3D11 / DXGI | Pixel-exact flip-model patch window with present timestamps |
| Wacton.Unicolour 8 | CCT/Δuv, Bradford adaptation, ΔE2000, color conversions |
| MathNet.Numerics 5 | Least squares, splines |
| ScottPlot.WPF 5 | Tone-curve, balance and CIE charts |
| xUnit v3, AwesomeAssertions | Tests (FluentAssertions 8 is no longer free) |
| QRCoder, ASP.NET Core Kestrel, Win32 `DnsServiceRegister` | Phone link (M6) |

- **ICC:** we write our own small ICC writer and reader. No maintained .NET library writes `vcgt` or MHC2.
- **Validation:** during development, a Python Pillow `ImageCms` script (LittleCMS) checks the profiles.
- **Ideas only** (GPL/AGPL licenses): ArgyllCMS/DisplayCAL (method), ColorControl.
- **Code we may borrow** (MIT/Unlicense): PowerToys PowerDisplay and Monitorian (DDC/CI), dantmnf/MHC2 (MHC2).
- **Prior art:** "Webcam based display calibration" (Thomas & Gerhardt, CIC 2012) uses the same null method.

## 3. What a webcam can measure (sets the design and the UI's confidence labels)

A webcam is not a colorimeter: it fails the Luther condition and it processes images. The method therefore uses
**null comparisons**: two patches on the same display are adjusted until the camera reads them the same. The camera's
own response, white balance and color matrix then cancel out.

| Quantity | Method | Confidence |
|---|---|---|
| Tone curve and gamma, per channel | Halftone null + midpoint tree; exposure bracketing as a cross-check | High (down to about code 20) |
| Gray balance relative to the display's own white and black | 3-channel null against dithered white/black | High |
| Clipping, black crush, Full/Limited mismatch | Near-black and near-white step tests | High |
| Contrast ratio, black level | Long-exposure readings with dark frames; floor-limited ("≥ N:1") | Medium |
| White point (CCT, Δuv) | Per-primary decomposition anchored to EDID, plus the camera-model estimate | **Estimate, with ± range** |
| Primaries and gamut | EDID chromaticities | Manufacturer data |
| Absolute luminance (cd/m²) | Not in v1 | — |

**Consequences:**
- The default white target is **"Keep native white"**. D65 is available, shown with its ± range.
- The advisor never recommends a change smaller than the measurement uncertainty.

## 4. Architecture

```
SoftColorCalibrator.slnx
├─ src/SoftCal.Core                    net10.0, no Windows deps
│   Color/ ToneCurves/ Instruments/ (seams) Measurement/ (ReadingService, null+tree, plans)
│   Analysis/ Calibration/ (vcgt solver) Advisor/ (OSD rules) Sessions/ (document+checkpoints)
│   Displays/ (EDID parser, DisplayKey) Characterization/ (per camera, per camera+display)
├─ src/SoftCal.Icc                     ICC v2.4/v4.3 writer+reader, vcgt (MHC2-ready)
├─ src/SoftCal.Windows                 display enum, D3D patch presenter, gamma ramps, mscms,
│                                      DDC/CI, OS probes, StateJournal + Guardian, power requests
├─ src/SoftCal.Instruments.Webcam      Media Foundation capture, UVC lock/monitor/restore, frame stats
├─ src/SoftCal.Instruments.Simulated   SimulatedRig (display + camera), loopback "remote" instrument
├─ src/SoftCal.Instruments.Remote      protocol DTOs, Kestrel WebSocket host, pairing (M6)
├─ src/SoftCal.App                     WPF UI + narrow elevated-helper entry point (--elevated <task>)
├─ tools/SoftCal.PhoneSimulator        console "fake phone" speaking the protocol
├─ tools/validate_icc.py               LittleCMS check of generated profiles (dev only)
├─ tests/                              Core, Icc, EndToEnd (simulated + replay), Windows (Hardware trait)
└─ docs/                               this plan, method, accuracy notes, protocol spec
```

**Key interfaces.** The instrument is a timestamped stream plus control commands. All reading logic runs once, on
the PC, for every instrument.

```csharp
interface IMeasurementInstrument : IAsyncDisposable {
  InstrumentInfo Info { get; } InstrumentCapabilities Caps { get; }        // exposure model, lock support, linear?, cam→XYZ…
  Task<LockReport> PrepareAsync(LockRequest r, CancellationToken ct);       // lock + read-back verification
  Task<ExposureState> SetExposureAsync(ExposureRequest r, CancellationToken ct); // actual value + epoch
  IAsyncEnumerable<FrameStats> StreamAsync(RoiSpec roi, CancellationToken ct);   // seq, timestamp, epoch, channel stats, row bands
  Task RestoreAsync(CancellationToken ct);
  event EventHandler<InstrumentFault>? Faulted; }
interface IPatchPresenter { Task<PresentedPatch> ShowAsync(PatchSpec p, CancellationToken ct); } // solid/halftone; scan-out QPC
interface IReadingService {   // instrument-agnostic: settle, own AE, ABBA, drift, overlay guard
  Task<Reading> ReadAsync(PatchSpec p, ReadPolicy pol, CancellationToken ct);
  Task<NullResult> NullAsync(PatchSpec reference, Func<int, PatchSpec> candidate, CancellationToken ct); }
interface IMonitorControls { Task<int?> ReadAsync(ControlId c, CancellationToken ct);   // DDC/CI or "ask the user"
                             Task<int?> ApplyAsync(ControlId c, int v, CancellationToken ct); }
interface IStateGuard { string Kind { get; } JsonNode Capture(); Task ApplyAsync(); Task RestoreAsync(JsonNode original); }
```

- **Session document.** A versioned JSON file plus append-only JSONL checkpoints. It holds the raw statistics of every
  reading, a snapshot of the OS, OSD, display and instrument state, and the patch timeline. Analysis is a pure
  function of the document, so old sessions can be re-analyzed and recordings replayed in tests. Stored in
  `%LOCALAPPDATA%\SoftColorCalibrator\` and culture-invariant.
- **Characterization store.**
  - Per camera (VID/PID, serial, firmware, mode): response curve and exposure ratios.
  - Per camera + display pair: `M_cam`, the white reference, flicker.
  - Each record has a quick re-check.
- **DisplayKey.** Built from EDID IDs, serial and a hash, with the connector used only to tell identical monitors
  apart. It is kept separate from the per-session handle (LUIDs change at each boot).
- **State journal + guardian process.** Every reversible change is journaled before it is applied:
  - gamma ramp
  - ICC association and the HKLM loader flag
  - HDR/ACM
  - DDC/CI values
  - UVC controls, which persist in the camera

  A small guardian process restores everything if the app dies. Recovery at next launch is the backup.

## 5. User workflow (wizard)

1. **Display and goal.**
   - Pick a display (EDID name; "Identify" flashes a number).
   - Goals: *Status report*, *Guide my monitor settings*, *Create a Windows calibration file*.
   - Targets:
     - White: **Keep native** (default) / D65 / custom K.
     - Tone: **Keep native — no gamma change** / sRGB / 2.2 / 2.4 / BT.1886.
     - GPU curves (`vcgt`): on/off.
2. **Pre-flight checks.** Each check shows the state, a fix and a re-check. Every change is journaled and restored.
   - **Night light: detect, ask, guide, verify. We never write the registry.**
     - **Detect:** parse the CloudStore Bond blobs (global and the new per-device keys) to get on/off, the schedule
       and the K value. "Unknown" means we ask you.
     - **If it is on, or will start during the session:** explain, ask for confirmation, open
       `ms-settings:nightlight`, and poll until it reads off.
     - **During measurement:** a registry watch pauses the run if it turns on.
     - **At the end:** offer to reopen Settings if it was on before.
   - **HDR / ACM.** Read with ADVANCED_COLOR_INFO_2 (`activeColorMode`) on 24H2+, or INFO plus `wideColorEnforced` on
     22H2/23H2. After you confirm, set them off with `SET_HDR_STATE` / `SET_WCG_STATE` (no admin), then re-query.
   - **Other modifiers:**
     - Color filters (`HKCU\…\ColorFiltering\Active`).
     - Adaptive brightness (power setting `ADAPTBRIGHT`).
     - Energy saver (`SYSTEM_POWER_STATUS`).
     - Laptops only: "change brightness based on content" and Adaptive color have no API, so we ask.
   - **GPU gamma ramp and profile.** Back them up and set a linear ramp for native measurement. Check
     `ColorProfileGetDeviceCapabilities` (`SupportsVcgt`, probed at runtime). After measuring, confirm by camera that
     a test ramp actually takes effect (`SetDeviceGammaRamp` can return TRUE and do nothing).
     **If no ramp works → GPU curves are disabled (profile-only + OSD advice).**
   - **Display link.** Check native resolution and refresh (DisplayConfig + EDID). Have you confirm the GPU sends RGB
     4:4:4 at full range (NVIDIA Control Panel guidance; querying NVAPI is an M1 probe).
   - **Session hygiene.**
     - Ask for Do Not Disturb; keep the display awake (`SetThreadExecutionState`); warm-up timer (≥ 30 min).
     - Photosensitivity warning: flashes stay at 3 Hz or slower.
     - Warn about color-altering apps (f.lux, Iris, DisplayCAL loader, GPU color overrides).
3. **Monitor settings (OSD) questionnaire.** Fields: picture mode, brightness, contrast, color preset, RGB gains,
   **gamma setting (only if the monitor has one)**, sharpness, and the features that must be off (dynamic contrast,
   ECO / light sensor, low blue light, Black eQualizer).
   - Saved as a **monitor control profile** per DisplayKey, with each control's kind: numeric, preset list, or
     "can only decrease".
   - **DDC/CI pre-fill:**
     - We probe known VCP codes one by one with timeouts: 0x10 brightness, 0x12 contrast, 0x14 preset, 0x16/18/1A
       gains, 0x6C/6E/70 black levels, 0x72 gamma, 0xDC mode.
     - We **never send the capabilities request by default**: a malformed reply can crash Windows (BSOD).
     - Traffic is serialized per monitor, about 40 ms per read and 50 ms per write, with retries.
     - Laptop panels use WMI brightness.
4. **Camera setup (close-up).**
   1. Pick a camera. Only hardware sources with a `USB\VID_` ID are accepted; virtual cameras are excluded. Check
      camera privacy access (`ms-settings:privacy-webcam`) and whether another app is using the camera.
   2. Placement: **8–10 cm** from the screen center, square to it. Use a dark hood (a black paper tube), tape over the
      activity LED, room dimmed.
   3. Lock and verify (see §6).
   4. Find the camera's footprint (≤ 3 Hz flashes), then size the patch to 1.5× it.
   5. Calibrate latency and clock offset optically.
   6. Measure flicker.
   7. Defocus to a measured ripple limit.
   8. Run the trust test.
5. **Measure.**
   - The D3D patch window covers the target display.
   - Progress shows on the other display, or for a single monitor, as Space/Esc control with sound cues.
   - Pause and resume (re-checks lock, clock and position).
   - Checkpoints.
6. **Report.** Charts, plain-language findings with confidence labels, and the Windows/monitor state. Export to HTML
   and JSON.
7. **Fix.**
   - **OSD path:** the advisor (§8) with a live meter.
   - **Windows file path:** solve and refine the `vcgt` (§7), run a profiling pass, write the ICC, install and
     associate it, set up loading, verify.
8. **Verify and revert.** Before/after report. One-click revert from the state journal.

## 6. Measurement method (close-up)

### 6.1 Camera access and lock

**Capture**
- Media Foundation `MFCreateDeviceSource` in **controlling mode**.
- Source reader on native **YUY2** (NV12 as fallback), with `MF_READWRITE_DISABLE_CONVERTERS`. Low resolution at
  30 fps; lower the frame rate when long exposures need it.

**Controls**
- `IAMVideoProcAmp` / `IAMCameraControl` (taken from the MF source): set and read back.
- `IKsControl`: extra UVC properties, e.g. power line frequency.
- `IMFExtendedCameraController`: Video HDR, background blur, eye contact and auto-framing **off**.
- Studio Effects detected through `DEVPKEY_DeviceInterface_IsWindowsCameraEffectAvailable`, and turned off.

**Lock order:**
1. Snapshot all controls (journaled).
2. Write the manual values **before starting the stream**, so the per-camera defaults set in Windows 11 Settings are
   not applied:
   - white balance 6500 K
   - exposure manual
   - gain at minimum
   - backlight compensation 0
   - sharpness at minimum
   - focus manual
   - every other control at its default, written explicitly
3. Start the stream and discard the first frames.
4. Read back and verify.
5. `IMFCameraControlMonitor` re-applies any value another app changes, and the affected reading is redone.
   `IMFSensorActivityMonitor` warns when another app is streaming.
6. Restore on exit (or through the guardian).

### 6.2 Reading service (shared by all instruments)

- **Optical clock sync.** A coded flash sequence (≤ 3 Hz) gives one combined offset (clock + display + pipeline). It
  is measured against D3D present/scan-out timestamps, so each frame can be assigned to a patch over USB or LAN.
- **Which frames count.** A frame is accepted when its timestamp is after settling, its exposure epoch is current,
  and the horizontal bands agree. "The reading stopped changing" is never used, because matched pairs are equal by
  design.
- **Flicker.** Measure backlight PWM from rolling-shutter banding (a short exposure plus an FFT). Make the ROI height a
  whole number of flicker periods where possible. Read matched pairs in **A-B-B-A** order at the **same exposure**.
- **Decoding.** Average Y, Cb and Cr as doubles, with no clipping. Range (full/limited) is detected from the clip
  levels. Frames with clipped ROI pixels are rejected.
- **Overlay guard.** Screen read-back of the patch area rejects readings with toasts or the cursor.
- **Drift.** White references are interleaved through the run.
- **Own auto-exposure.** Exposure steps are ×2 only (log2 seconds), and the true ratios are fitted (Robertson).

### 6.3 Tone and balance measurement

- **Halftone null.**
  - Show a pattern with a fraction *f* of pixels on. The defocused camera integrates it.
  - Find the solid level *v\** with the same reading. That gives EOTF(*v\**) = black + *f*·(white − black).
  - Interpolate between the two neighboring codes.
- **Midpoint tree for the shadows.** A 1-px checkerboard of two already-matched levels gives their mean light.
  Halving reaches about 1/256 (≈ code 21 at γ 2.2) in 8 steps. Without the tree, plain 0/255 dithers only reach about
  code 99.
- **Pattern-spread check.** Compare the checkerboard, horizontal lines, vertical lines and Bayer 4×4. Any
  disagreement points to panel inversion, crosstalk, FRC, 4:2:2 or scaling, and is flagged in the report.
- **Defocus.** With focus at infinity, the blur on the screen is about the lens aperture (~2 mm ≈ 6 px) at any
  distance. We pick focus by measured ripple: residual ripple ≤ 2.5 %, which keeps the null bias ≤ 0.1 %. A ±1 cm move
  test confirms it.
- **Exposure bracketing (cross-check and black range).** Robertson-style response fit with exposure ratios as
  unknowns, plus lens-capped dark frames at each exposure and detection of the camera's black clamp.
- **Contrast and black.**
  - Black is measured with a black surround and must be ≥ 5σ above the dark frame; otherwise the report shows
    "≥ N:1".
  - An APL test (center patch on a black vs a white surround) catches dynamic contrast, OLED ABL and local dimming.
- **White point.**
  1. Decompose white into per-primary factors, k = `M_cam`⁻¹·c_W. These are camera-independent for an additive
     display.
  2. Anchor them once: the EDID white and primaries are assumed to describe the monitor's factory 6500 K / "Normal"
     preset. You select it, or we switch to it via DDC/CI 0x14.
  3. Report this estimate next to the camera-model estimate (linearized RGB taken as BT.709). Their spread plus the
     EDID tolerance gives the ± range.

## 7. Calibration (Windows file path)

### 7.1 Solving the curves

- **Target per level:** `black + E_t(v)·(white − black)`. This keeps the black's tint, so the solver never crushes a
  channel.
  - *Keep native tone:* E_t is the measured native luminance fraction; only the chromaticity is corrected.
  - *White target ≠ native:* the white itself is scaled in the `vcgt` (one channel stays at 1.0).
- **Solver:**
  1. Initial `vcgt` from the null-measured per-channel EOTFs.
  2. **3-channel dithered-white nulls, top-down:** a solid gray at v is matched against a checkerboard of calibrated
     white/black, then of grays already balanced. The R, G and B `vcgt` entries are adjusted until all three camera
     channels match.
  3. `M_cam` supplies only the step direction, so the converged result needs no camera linearity or colorimetry.
  4. Stop when the change is < ½ output code or < 2σ, with at most 3 passes. Keep the best pass.
  5. Smooth and keep the curves monotone between the matched nodes; report the 8-bit levels lost.

### 7.2 The profile

**Format:** ICC **v2.4** by default (v4.3 optional).

| Tag(s) | Content |
|---|---|
| Header | `mntr` / RGB / XYZ / D50 |
| `desc`, `cprt` | Text (`mluc` in v4) |
| `wtpt`, `chad` | White, plus Bradford adaptation to D50 |
| `rXYZ`/`gXYZ`/`bXYZ` | EDID primaries, scaled to the achieved white |
| `rTRC`/`gTRC`/`bTRC` | `curv`, 1024 entries, measured with the curves applied |
| `vcgt` | 3 × 256 × 16-bit table |
| `meta` | Instrument, method, targets, white estimate ± range |

The writer is ready for MHC2.

**Install and associate (no admin):**
- `InstallColorProfileW` (standard users can add files to the COLOR folder; an elevated fallback exists).
- `ColorProfileAddDisplayAssociation` / `SetDisplayDefaultAssociation`: current-user scope, adapter LUID + **source**
  id, on the standard (SDR) list. Exports are probed at runtime.
- Fallback: `WcsAssociateColorProfileWithDevice` + `WcsSetUsePerUserProfiles`.

**Loading at sign-in:**
- Default: Windows' loader, turned on with `WcsSetCalibrationManagementState(TRUE)` (one UAC prompt). It is off here,
  so M5 checks that the loader task really runs.
- Option: a SoftCal mini-loader that runs at sign-in and on display changes and applies full 16-bit ramps. Windows'
  loader truncates curves to 8 bits.

**Limits:**
- **ACM:** with ACM on, 24H2+ ignores `vcgt` and TRCs. If you re-enable ACM, the app warns you. MHC2 output (a
  documented format) comes later.
- **Profile-only mode:** used when the `vcgt` is off or the GPU rejects ramps. The profile then describes the display
  as measured.

## 8. OSD advisor

**Probe set (~15 s):**
- white, black, 50 % gray
- codes 4 and 8
- codes 247, 251 and 255, per channel

These give the per-primary factors k, the black offset, the per-channel clip points and the gamma.

**Learning each control:** two-point probing (x and x ± 5–10 % of its range).
- The change must exceed 5σ.
- Add a third (secant) point if a prediction misses by more than 30 %.
- No effect means the control is locked in this picture mode.
- The learned slopes are saved per monitor.

**Rules, in order:**
1. **Clean-up.** Turn off dynamic contrast, ECO, low blue light and Black eQualizer, and set sharpness to neutral
   (halftones need it). For RGB range:
   - 0–16 and 235–255 both flat → the GPU sends full range but the monitor expects limited;
   - raised black with distinct steps → the reverse.

   Fix range in the GPU or OSD settings, never with brightness or contrast.
2. **Brightness.**
   - If it scales every level, it is the backlight: set it last, to your preference.
   - If it lifts black more than the rest, it is an offset: use the highest value that keeps code 0 at the floor and
     codes 2–6 distinct.
3. **Contrast.** The highest value where codes 247/251/255 stay distinct in every channel and gray 250 balances like
   white, minus 1–2 steps.
4. **Color preset** (only if the white target isn't native). Pick the smallest Δu′v′. Prefer "User" if lowering at
   most two gains reaches the target with ≤ 15 % luminance loss.
5. **RGB gains.** A Newton step on k with the learned slopes.
   - Never raise a gain past its clip-free value (the BenQ's sliders can only go down).
   - Round to whole OSD steps.
   - Re-check contrast (rule 3) and the balance at 25/50/75 %.
6. **Gamma preset** (only if the monitor has one). A 9-point match per preset.
   - Pick the smallest RMS log-slope error against the target over 10–90 %.
   - Break ties on gray balance, then on 8-bit levels lost in the `vcgt`.

**Stop when any of these holds:**
- Within tolerance: white within 0.002 Δu′v′ (relative), a clip margin of at least 1 code, gamma within ±0.05.
- The best next step predicts less than 2σ improvement.
- The error rises: go back to the best state seen.
- The step limit is reached.

Whatever is left goes to the `vcgt`.

**User experience:**
- The live meter shows the remaining error in OSD steps ("Red: 3 down").
- DDC/CI auto-apply is opt-in, rate-limited, capped (to spare the monitor's EEPROM) and journaled.

## 9. Ready for the Android phone

- **Instrument interface.** The phone implements the same `IMeasurementInstrument`. It streams ROI `FrameStats`
  (about 30 small messages per second) and accepts lock and exposure commands. Optical sync makes network jitter
  irrelevant.
- **Capabilities.** `hello` negotiates:
  - protocol version
  - exposure model (continuous, or a list with or without actual values) and ISO
  - linear vs encoded output, and lock support per control
  - row line time
  - camera→XYZ matrix (DNG ForwardMatrix, which makes phones *more* accurate than webcams)
  - thermal and battery events
- **Protocol v1** (`docs/protocol/`): versioned JSON over a TLS WebSocket.
  - The PC hosts on Kestrel. Pairing is by QR code: host, port, one-time token, certificate fingerprint.
  - An elevated helper adds a Private-profile firewall rule after asking. If you click Cancel on the Windows firewall
    prompt, Windows silently creates block rules.
- **When it gets built.**
  - M3 proves the interface with an in-process **loopback remote instrument** that injects latency, jitter, clock
    offset and dropouts.
  - M6 adds transport, pairing and `tools/SoftCal.PhoneSimulator`.
  - The future Android app is plain Kotlin Camera2 (FULL level, MANUAL_SENSOR and MANUAL_POST_PROCESSING):
    - AE/AWB off, with a fixed color-correction transform and gains
    - a linear tone map, or RAW plus DNG matrices
  - Browser capture was rejected: Chrome on Android snaps white balance to presets and keeps the nonlinear tone map.

## 10. Milestones (risk first)

| # | Size | Deliverables | Exit criteria |
|---|---|---|---|
| **M0** Foundation | M | See below | `dotnet build` and `dotnet test` green; color math matches reference values (D65 CCT, sRGB matrix, Sharma ΔE2000 set) |
| **M1** Instrument feasibility (C920e + both monitors) | L | See below | See below |
| **M2** Windows platform + safety | L | See below | Killing the app at random points restores every kind of state; Night light state matches Settings; DDC/CI values match the OSD or are cleanly unsupported |
| **M3** Engine + status report | L | See below | See below |
| **M4** OSD advisor + live meter | M | Monitor control profiles; questionnaire + DDC/CI pre-fill; probing; rules; live meter; opt-in auto-apply | Simulated monitors converge to target with a simulated user who makes typos; real walkthroughs on BenQ (gamma presets) and ViewSonic (no gamma) |
| **M5** Windows calibration file | L | `vcgt` solver; ICC writer/reader; install and associate; loader options; verification; before/after; revert; profile-only mode | LittleCMS accepts the profile; Windows lists it; after sign-out the GPU ramp equals the `vcgt` (checked by camera); verification shows the gain |
| **M6** Phone link | S | Protocol spec + schema; Kestrel WSS host; QR pairing; firewall helper; PhoneSimulator | PhoneSimulator over the LAN matches the in-process results |
| **M7** Hardening + release | M | Error categories with recovery actions; diagnostics export; accessibility (keyboard, UIA names, high contrast, numeric meters); resx completeness; user guide + accuracy notes; Velopack per-user installer built from the CLI (signing optional) | Clean-machine install; full flow on the dev machine |

**M0 deliverables:**
- `git init`, `.gitignore` (including `Notes.txt`).
- `SoftColorCalibrator.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json` (10.0.401),
  `.editorconfig`.
- Copy this plan to `docs/`.
- Core color and tone math, the EDID parser (fixtures from both monitors, serials anonymized) and DisplayKey.
- `TimeProvider` virtual clock and the session/recording format.
- First version of the SimulatedRig.

**M1 deliverables:**
- MF capture, UVC lock/verify/restore, ROI statistics.
- D3D patch window (solid + halftones, pixel-exact check by screen read-back).
- Optical sync and flicker measurement.
- Null + tree + bracketing; trust test; every run recorded.
- Platform probes:
  - gamma ramp write and camera check
  - DDC/CI VCP probe per monitor
  - Night light Bond parsing
  - link/RGB format, including NVAPI feasibility

**M1 exit criteria (decision gate):**
- Matched-reading σ ≤ 0.3 code.
- Pattern shapes agree within 1 code.
- Tree points at codes ~21/38/72 repeat within 1 code.
- A ±1 cm camera move changes results by ≤ 1 code.
- Ripple bias < 0.1 %.
- Gamma spread ≤ 0.03 over 3 runs.
- Locks hold for 30 min.
- Outcome: **go / go with restrictions / bracketing-only fallback**.

**M2 deliverables:**
- Display enumeration (DisplayConfig ↔ GDI ↔ HMONITOR ↔ EDID), per-monitor DPI v2.
- StateJournal + guardian.
- Gamma-ramp manager with watchdog.
- OS probes and toggles (Night light read-only, HDR/ACM, filters, power settings).
- DDC/CI layer (probe, read, write, revert).
- Narrow elevated helper; power requests.

**M3 deliverables:**
- ReadingService (settle, own auto-exposure, ABBA, drift, overlay guard).
- Loopback remote instrument.
- Quick (≤ 5 min) and Standard (≤ 15 min) plans.
- Analysis; report UI + HTML/JSON export; wizard steps 1–6; characterization store.

**M3 exit criteria:**
- The simulated end-to-end run recovers the ground truth within the Monte Carlo p95 error budget.
- Real reports repeat across 2 distances and a camera remount.
- The loopback instrument with jitter matches in-process results.

**Later:** the Android app; a viewing-distance mode (markers, uniformity map); MHC2 for ACM/HDR; matching two
monitors to each other (a relative and highly accurate webcam use case); camera spectral models with
display-technology spectra (public-domain CCSS) for a better absolute white; a colorimeter bridge; absolute
luminance.

## 11. Verification

- **Build and run.**
  - `dotnet build SoftColorCalibrator.slnx -c Release`; `dotnet test` (unit, simulated end-to-end, replay of recorded
    sessions).
  - Hardware-trait tests run on the dev machine only.
  - `dotnet run --project src/SoftCal.App`.
- **SimulatedRig.** Every effect the method claims to cancel or detect can be toggled, with ground truth:
  - **Display:** spectral primaries with a slight shift by level; arbitrary tone curves; tinted black; clipping and
    range mismatch; realistic OSD controls (gain before vs after the panel LUT, backlight vs offset brightness,
    presets, whole steps, DDC/CI quirks); 6-bit+FRC; crosstalk; a 4:2:2 link; a viewing-angle tone curve; ABL; PWM;
    drift; latency jitter; a GPU LUT with 8-bit output, dithering or clamping.
  - **Camera:** random spectral sensitivities, white balance, color matrix and response curve; optional local tone
    mapping and sharpening; defocus blur; YCbCr variants; noise; pixel gain variation; black clamp; heat drift;
    vignetting; rolling shutter; clock ppm; exposure that applies late and differs from nominal; ambient light and LED
    glare; auto features re-enabling mid-run.
- **Key tests.**
  - **Invariance (the method's core claim):** changing the camera's curve, matrix, white balance or exposure must not
    move the matched-reading results.
  - **Monte Carlo p50/p95 errors** per quantity. These drive the UI's ± ranges and the accuracy notes.
  - **Fault injection** (dropout, overlay, Night light switching on, hot-plug, kill): the run pauses and every state
    is fully restored.
  - **Advisor closed loop** with a simulated user.
- **ICC.**
  - `python tools/validate_icc.py <profile>` (Pillow `ImageCms`/LittleCMS) and a round-trip through our reader.
  - Windows lists and associates the profile.
  - After sign-in, `GetDeviceGammaRamp` equals the `vcgt` and the camera confirms the effect.
- **Hardware checklist (C920e, BenQ XL2720, ViewSonic VX2770, RTX 4000).**
  - Camera: lock report, trust test, M1 gate numbers, locks hold with Logitech software running.
  - Windows state: Night light detection while ON (as now) and OFF; HDR/ACM toggle and restore; crash restore of every
    state kind.
  - Workflows: OSD flow on both monitors; profile + loader after sign-out; one-click revert.
  - Eye checks: a 1-px blend test and near-black/near-white step charts look right.
- **Phone path.** PhoneSimulator completes a Standard measurement over the LAN.

## 12. Risks and mitigations

- **Near-black accuracy** (most visible to you, weakest for webcams). Midpoint tree, dark frames, black-clamp
  detection, and explicit exit criteria at codes ~21/38/72.
- **Processing after the frame buffer breaks halftones** (4:2:2 link, scaling, monitor sharpness, inversion, FRC —
  the BenQ is TN 6-bit+FRC). Link check, pattern-spread check, sharpness neutral; fall back to bracketing.
- **Flicker and clock beats with ×2 exposures.** Banding analysis, whole-period ROI, ABBA, optical sync.
- **The camera keeps hidden processing, or another app or Settings rewrites controls.** Write before the stream
  starts, control monitor, trust test, sensor-activity warning, restore.
- **White estimate errors.** Default to native white, show the ± range, never advise below the uncertainty.
- **Leftover system state after a crash.** Journal + guardian in M2, plus recovery at next launch.
- **Gamma ramps rejected or silently ignored.** Capability probe, check by camera, profile-only mode.
- **ACM ignores `vcgt`.** Warn the user; MHC2 later.
- **Night light format undocumented.** Read-only parsing; the user turns it off in Settings.
- **DDC/CI crashes or stalls.** No capabilities request by default, serialize, time out; DDC/CI is always optional.
- **EDID feedback loop** (our MHC2 would override the values Windows reports). Factory data comes from the raw EDID
  only.
- **No maintained open-source equivalent, and phone-based commercial tools review poorly.** The trust tests and
  confidence labels are what set this app apart.
- **Patent.** Dolby US8836796B2 (camera-based display calibration) is listed as active until about 2033. Personal use
  is fine; get a freedom-to-operate check before commercial distribution.
- **Repo in OneDrive** (your choice). Pause syncing if builds hit "file in use"; app data lives in `%LOCALAPPDATA%`.
