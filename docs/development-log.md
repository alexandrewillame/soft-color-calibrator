# Soft Color Calibrator: development log

How the app was built and verified, milestone by milestone (this was the README until the first public release; the
user-facing one is [../README.md](../README.md)).

A Windows 11 app that uses a camera — a USB webcam, or an Android phone on the same Wi-Fi — to measure a monitor and
then either guide its on-screen (OSD) settings or produce a Windows calibration file (ICC profile with optional `vcgt`
curves).

The approach and the milestone plan are in [docs/implementation-plan.md](implementation-plan.md).

## Status

**M0 – Foundation** is done:

- Color math, in `SoftCal.Core/Color`: xy/u′v′/XYZ, RGB↔XYZ from primaries, Bradford adaptation, Lab, CIEDE2000, and CCT/Δuv.
- Tone curves, in `SoftCal.Core/ToneCurves`: sRGB, power, BT.1886, measured curves with a monotone cubic spline, and gamma statistics.
- EDID parsing and the stable `DisplayKey`, in `SoftCal.Core/Displays`.
- The instrument and patch interfaces that the webcam and the future phone both implement, in `SoftCal.Core/Instruments` and `SoftCal.Core/Measurement`.
- A crash-tolerant session format: a JSON header plus an append-only JSONL event log, in `SoftCal.Core/Sessions`.
- `SimulatedRig`, a simulated display and camera with known ground truth, in `SoftCal.Instruments.Simulated`.

**M1 – Instrument feasibility** (the webcam decision gate) is done: **go with restrictions** (camera placement, horizontal-line
halftones, a 5-minute time budget, deep shadows). The gate numbers and the recommendation are in
[docs/m1-feasibility-report.md](m1-feasibility-report.md).

- `SoftCal.Instruments.Webcam`: Media Foundation capture in controlling mode on the camera's native YUY2 (NV12
  fallback, converters disabled); UVC lock written before the stream starts, read back and verified, watched with
  `IMFCameraControlMonitor` / `IMFSensorActivityMonitor`, and restored; per-frame ROI statistics straight from Y′CbCr.
- **The restore runs while the camera still streams** (2026-10-20). The C920e, switched back to automatic while idle,
  reads "automatic" but keeps the locked white balance and exposure. Its picture stayed at 6500 K (an orange cast in
  warm room light: red 1.5 and blue 0.28 times green) with its gain at the maximum, in every app, until a white balance
  change while streaming. So every restore (end of a run, the guardian's after a crash, the next start's, the
  harness's reset) keeps or starts a stream and writes in two passes: every control at its value by hand, then, half a
  second later, the automatic ones switched back on, then 1.5 s of automatic before the stream stops (about 2 s in
  all). Checked on the C920e with the harness (`camera --lock-test --watch`): the picture follows the room's light
  again (white balance 2701 K automatic, red 1.14 and blue 0.85 times green, as at 2800 K set by hand).
- `SoftCal.Windows`: the D3D11 flip-model patch window (pixel-exact, checked by screen read-back, scan-out timestamps
  from DXGI frame statistics), display enumeration, gamma ramps with a crash-safe journal, the DDC/CI VCP probe (never
  the capabilities request), the read-only Night light reader (CloudStore Bond blobs) and the NVAPI link-format probe.
- `SoftCal.Core`: the measurement core (`MeasurementRig`: settling, own auto-exposure; `NullMatcher`: A-B-B-A nulls and
  the midpoint tree) and the analyses (optical sync, rolling-shutter flicker, halftone ripple and null bias,
  Robertson-style bracketing with the exposure ratios as unknowns, trust test, the M1 gate).
- `tools/SoftCal.FeasibilityHarness`: runs and records every M1 test.

**M2 – Windows platform and safety** is built and tested in software. The checks that write to real displays are
pending; see "Pending" below.

- `SoftCal.Windows/Safety`:
  - One state journal for every reversible change.
  - Entries are tied to their owner's process id *and* start time, so a reused id never hides a dead run.
  - A restorer per kind of state.
  - The shared `Guardian`, which restores whatever a killed process left behind.
  - A test kills a helper process at random moments and checks that every original value comes back.
- Restorers for every kind of state SoftCal changes:
  - gamma ramps;
  - camera controls;
  - HDR/ACM switches (`SoftCal.Windows/AdvancedColor`, no admin needed);
  - DDC/CI values (`SoftCal.Windows/Ddc`): serialized, time-limited, verified by reading back, write-capped, and never
    the capabilities request.
- Read-only pre-flight (`SoftCal.Windows/OsState`) checks:
  - Night light, plus a watcher that pauses measurement if it switches on;
  - color filters, HDR/ACM and link range;
  - Energy saver and automatic brightness;
  - the Windows calibration loader.

  It reports each problem as a blocker, warning or info, with a fix.
- `DisplayKeepAwake`: a named power request that keeps the screen on.
- The elevated helper (`SoftCal.Windows/Elevation`): an allowlist of tasks behind one UAC prompt. Today it only has a
  ping.

**M3 – Measurement engine and first report** is built and verified on the simulator. Two real runs on the BenQ
XL2720 agree to about 0.1 code, in 2.4 minutes each.

- `QuickPlan` (`SoftCal.Core/Measurement`) measures, in about 5 minutes:
  - a horizontal-line midpoint tree of grays down to the shadows;
  - a 50 % null per channel, giving each color's own gamma;
  - a checkerboard-vs-lines pattern check;
  - near-black and near-white steps;
  - white/black readings for contrast.
- `DisplayAnalysis` and `ReportWriter` (`SoftCal.Core/Analysis`) turn the readings into a report:
  - the tone curve and three gamma figures;
  - gray balance at every level of the gray tree, and each color's gamma;
  - black crush, white clipping and the Full/Limited range signature;
  - contrast (a rough figure, or "≥ N:1");
  - the EDID white point and gamut.

  Every number carries a confidence label. The report is written as HTML (with an SVG tone curve) and JSON. The raw
  readings are saved next to it (`*.measurement.json`), so a run can be re-analysed when the analysis improves.
- **Gray balance is measured on the grays themselves.** Each camera channel finds its own match point for each gray.
  A gray with white's tint matches at the same code in every channel, whatever the camera's response, white balance
  or color matrix. A channel that matches lower is brighter at that gray. The tint's direction is exact; its size
  depends a little on the camera's color matrix. A tint is named only above 2 % and above twice the noise.
  - Each channel is matched inside its own pair of codes. Single-code steps are uneven on 6-bit+FRC panels, so a
    channel is never extrapolated from the luma's pair: on the BenQ that inflated the code-50 blue deficit by
    about 3 %.
  - Known limit: going down the tree, each gray is carried to the nearest whole code along a smooth curve. Uneven
    steps make this slightly wrong, so tints below about 3 % of white are good to roughly ±2 % on such panels.
  - Why not the single-color nulls: on the BenQ (TN), red, green and blue shown alone reach half their light about
    18 codes below gray does, every run. Its channels interact, so colors shown alone do not predict grays. The
    report says so as a "Panel" note.
- `LoopbackRemoteInstrument` gives the same answers as the in-process instrument despite a clock offset, timestamp
  jitter and dropped frames. This proves the phone-ready interface before the network transport exists.
- `src/SoftCal.App`, the first WPF app, has six steps: display, pre-flight, monitor settings (with a DDC/CI read),
  camera placement, measure, report. Its measurement uses every safety guard: guardian, journal, HDR/ACM and
  calibration curve set aside and restored, camera lock journaled and restored, Night light watch, keep-awake.
  Tick "Use the built-in simulator" to try it without a camera.

**M4 – Monitor settings (OSD) advisor** is built and verified on the simulator. A real walkthrough is pending.

- `OsdAdvisor` (`SoftCal.Core/Advisor`) asks for one change at a time and judges each one by measurement, never by the
  value the user reports. So a setting that was mis-set or mistyped is caught by the next reading. The order:
  1. RGB range and picture enhancements.
  2. Contrast: lowered until no channel clips, then raised back toward the edge.
  3. Gamma preset: every preset is measured. The closest to 2.2 wins, and ties go to the most neutral mid-gray. A
     preset whose readings disagree, or that reads the same as another, is measured again.
  4. Shadows. A brightness change is tested first: a backlight leaves the null tone unchanged, so brightness is put
     back. An offset moves the tone, so brightness is searched for the lowest value that keeps code 4 apart from
     black. Otherwise a black boost is raised a step at a time until code 6 stands apart.
  5. A final check of everything.

  The white comes after: it is matched to another screen's with the RGB gains (see "The white match" below).
- `AdvisorProbe` measures only what a change affects:
  - shadow steps, about 5 s;
  - highlight steps judged per channel, about 5 s;
  - the top of the gray tree, about 17 s.
- `SimulatedDisplay` models OSD controls: brightness as backlight or offset, contrast gain with clipping, RGB gains,
  gamma presets and a black boost. On it, TN-like and offset-brightness monitors reach the targets, including with a
  simulated user who sets or reports wrong values.
- The app's step 7 keeps the camera, patch window and every guard open while you change the monitor's menu. The
  advised value is pre-filled, and a meter shows the latest readings. Brightness and Contrast can be written over
  DDC/CI, either for every change (opt-in box) or for one change ("Set it for me"). Writes are journaled: kept when
  the run finishes, put back if it stops or crashes.

**Profiles (2026-10-24).** SoftCal keeps each monitor's settings in profiles, which the user makes, changes and
removes at will. Most monitors need one: every monitor starts with "Default". More serve different uses (day and
night, games…). Profiles take the place of the monitor's own picture modes: SoftCal does not ask which one the monitor
is in.

- **What a profile holds.** A saved set of monitor settings, and its own calibration (Windows calibration file).
  Measurements are labelled with the profile chosen.
- **Step 1 shows the profile beside the monitor** (reworked the same day: the user could not tell what a profile held
  before loading it, nor what Save would overwrite). Like a preset manager with its "modified" mark, but value by value:
  - one card in two sides, each with its title, icon and buttons: on the left **Profile** (the selector, New…,
    Rename…, Delete, and "Load to the monitor →"), the profile's saved values below; on the right **On the monitor**
    (Read, "← Save to profile", when it was read), the monitor's values below, read over DDC/CI, typed from its menu
    or loaded. The row titles sit at the far left, a divider between the sides. Values that differ are amber, and a
    short status names them ("Contrast and calibration differ.", "✓ 1-DESK is on the monitor."), and which other
    profile the monitor matches, if one does (the BenQ's user modes switched from its own buttons are recognized this
    way);
  - the calibration row compares the profile's own calibration with the one Windows uses (not on the simulator);
  - a profile sets only what it has a value for (one saved before a value was known leaves it alone); Red, Green and
    Blue count only in the User mode; the color temperature by kind (User or a preset), as the box shows it;
  - choosing a profile changes only the profile's side. Measuring waits while the profile sets values the monitor does
    not have ("1-DESK differs from the monitor: load it or save to it first"), so a run always adjusts the profile
    that is on the monitor.
- **Every overwrite asks first** (the user asked, so settings are never erased by mistake): Save, Load and Delete open
  SoftCal's own dialog with what changes, one line each ("Brightness: 31 → 28"), the action's own button, and Cancel
  as the default (Enter alone changes nothing).
- **Load to the monitor** (offered when it would change something):
  - sets what differs of Brightness and Contrast over DDC/CI, and of Red, Green and Blue in the monitor's User mode
    (journaled while written, then kept), then reads the monitor back;
  - says what only the menu can set, when it differs: the color temperature, the gamma setting, a shadow boost;
  - makes Windows use the profile's calibration, or removes the one installed when it belongs to another profile
    (another profile's curves do not fit this one); the dialog lists that switch too;
  - warns, in the dialog, when the monitor's settings are in no profile (they would be lost).
- **Save to profile** stores the monitor's settings in the profile chosen, with Undo until the next change; a profile
  calibrated with other settings is said to need measuring again. New… makes a profile from the monitor's settings; a
  measurement's new values go into the profile measured. Rename… and Delete (a monitor keeps one at least).
- **More settings** (in the card, under the buttons): whether the menu has a gamma setting (and its choices) and a
  shadow boost (its name, highest value). Each adds its row to the table.
- **Research behind it** (2026-10-24): PowerToys Power Display keeps DDC/CI profiles that set only the settings they
  include; NirSoft ControlMyMonitor saves and loads whole configurations without a comparison; audio and amp preset
  managers mark an edited preset (a "modified" suffix, a lit Save) and offer Save, Save as new and Revert; GeForce
  Experience showed current and suggested game settings side by side before applying them.
- **Calibrations** belong to the profile they were made for: installing one stores it in the profile, and Remove
  forgets it there. Windows' files are called color profiles on screen, to keep them apart.
- **From before profiles.** The settings kept per picture mode became profiles of those names (the BenQ's 1-DESK,
  2-DIM and 3-GAME; an unnamed mode is "Default"); the last mode used is the profile chosen, and its settings the
  monitor's. The calibration installed then is linked to the profile its picture mode names. Older reports'
  picture modes read as their profiles.
- **Panel type.** "Look it up", beside the label (the check box lines up with the boxes above and below), opens a web
  search for the monitor's maker, model and EDID ID (e.g. "BenQ ZOWIE XL LCD BNQ7F2B monitor panel type TN IPS VA":
  several BenQ models call themselves ZOWIE XL LCD, the ID tells them apart). The (i) beside "Panel type" describes a
  quick check. The answer is remembered for the monitor.
- **Less text on screen** (2026-10-24, the user's review): leads, steps, statuses and pre-flight messages say only what
  to do; the why went into tooltips and (i) icons (Panel type, Match its white to, Profile). Tooltips wrap.
- Tests: the profiles (making, renaming, deleting, names, the files from before, current settings apart from them),
  the comparison (either direction, User mode only for the gains, empty profiles, the settings once loaded), what Load
  does (only what differs over DDC/CI or for the menu, the calibration switch), older records and reports, and the
  panel search.

**The app, after the first walkthroughs:**

- **Steps.** The side list is clickable for every step already reached, and Next stays greyed out until the current
  step is complete (with a tooltip saying why).
- **Pre-flight.** Each finding has a button that opens where to fix it: Night light, Color filters, Display or Energy
  saver settings, the NVIDIA Control Panel, or the NVIDIA App. SoftCal changes none of these itself; HDR and ACM are
  the exception, switched off for the measurement and restored. A running NVIDIA overlay is flagged: its pop-ups
  appear when the patch window opens.
- **Monitor settings.**
  - Kept in profiles (see "Profiles" below): each monitor has one or more, each with its own settings and calibration.
  - Numeric fields accept digits only and are range-checked; the three RGB gains have their own boxes. Their boxes
    have no clear button (Fluent's, shown while typing, hid the digits in a box this narrow).
  - The fields are read over DDC/CI automatically the first time they are empty, except the color temperature: what
    monitors report for it differs by brand (the BenQ reports "6500 K" in its User mode, the ViewSonic the same for
    "Native"), so the user says whether it is the User mode or a preset.
  - Every report records the profile it measured.
- **Camera.** A live camera view (the camera's own settings, nothing written) of the aiming area shown on the
  measured display: the middle of the patch square, inset so the camera's reading region stays on the patch wherever
  it rests, filled with a fine, even grid (squares of about 5 mm on a 27" monitor, so the few centimetres the camera
  sees through its hood hold several; lines that survive the measurement's defocus). There is no center mark: the
  camera may rest anywhere inside, and a mark to aim at invites tilting the camera toward it.
  - Straightness (`PlacementCheck`): a tilted camera sees the squares as trapezoids, larger on the side nearer the
    screen. The check measures the squares' size in the picture's left and right halves and its top and bottom halves
    (each from the spacing of many lines: the autocorrelation of the line profile, the hood's shading taken out), only
    over the lit part of the picture, and compares them. Equal within 3 % counts as straight: through a 4 cm hood that
    catches a tilt of about 7° or more, without one about 2°. A strongly tilted camera's halves are compared from the
    middle of the picture, so a 15° tilt still reads as a tilt, not as the area missing.
  - A level over the camera's view (step 2 and every camera move) shows which way to straighten it: a circle at the
    picture's middle and a dot toward the side whose squares look smaller, the side farther from the screen, which is
    where the camera points. Pressing that side of the camera toward the screen brings the dot back; it is green and
    inside the circle exactly when the check says straight (the larger of the two tilts sets its distance, both
    together its direction). The picture is shown as the camera sends it, not mirrored: with the camera held upright
    against the screen, its left is the user's left.
  - `AimingAreaTests` checks it on the real rendering as a C920e at 9 cm sees it (640 × 480, a 2 mm defocus, with and
    without a hood), resting anywhere on the area, straight or tilted.
  - The instructions say to rest the hood tube against the screen: on a TN panel the angle matters most. They also
    say the room light can stay on or off, as long as it doesn't change during a run.
  - The aiming area stays on the measured screen from step 2 through step 3 (2026-10-24, the user found the desktop
    coming back in between odd): its window is kept when step 2 is left for step 3, and closes once the run's own
    window covers the screen, or when another step is shown.
  - The camera's picture is shown stretched from its darkest to its brightest hundredth, so a dim one still shows the
    grid; only what is shown changes, never what is measured.
- **Measurement.**
  - The patch window stays opaque when you hover over the taskbar (excluded from Aero Peek).
  - A palindrome whose reference moved by more than 1 % between its first and last reading is read again, up to 3
    times. That covers a hand-held camera wobbling.
  - The Quick plan starts with a 10-second content check: the same gray is read after a white screen and after a
    black one. Dynamic contrast, ECO and light-sensor features show up as a difference and a drift, and are reported
    as a Problem.
- **The patch** stays a square of 60 % of the screen height on black, about 20 % of the screen. A full-screen patch
  would swing the whole screen between black and white, which is exactly what makes content-dependent features
  change the light.
- **Report.** Before/after: pick an earlier report of the same monitor (the newest of the same profile is
  preselected) to compare gamma, half-light code, gray tint at mid-gray and in the shadows, crush, clipping and
  contrast. The earlier tone curve is drawn in gray.

**After a full review (2026-10-19):**

- **Nothing on screen may cover the patch.** `PatchCoverWatch` looks at the measured screen about 30 times a second
  through DXGI desktop duplication, comparing the square where the camera rests with the pixels the patch window drew.
  It catches another window or a notification over the patch, a taskbar preview or "peek" that hides it, a visible
  pointer over it, and the patch gone altogether (Show desktop, a UAC prompt, the lock screen, a display change).
  `MeasurementRig` asks it about every reading's frames (`IPatchWatch`): a disturbed reading is taken again once the
  patch has been clear for half a second, and the measure step says why in an amber line. Six disturbed readings in a
  row, or a patch covered for a minute, stop the run with the reason. Real-screen test:
  `HardwareTests.The_patch_watch_sees_a_window_over_the_patch`.
- **Night light and color filters block every screen that measures**: step 2, step 3 and both Advanced screens, not
  just step 1. A banner above the screen lists what to turn off, with the button to its Settings page, and the
  screen's measuring buttons wait. It is checked on entering the screen, when the window comes back to the front, and
  every two seconds. The Result step is exempt (measuring is over). A run also refuses to start with either on, and
  stops if a color filter switches on during it (Night light already did).
- **SoftCal's window leaves the measured screen** whenever a patch window covers it: in step 2 (the aiming area) and
  for the Advanced runs, not only during the advice. It comes back when the measuring is over. With a single screen,
  step 2 says a second one is needed (the patch window would hide SoftCal's prompts and buttons).
- **Small windows.** Measure, Full display report and Windows calibration file scroll, and a prompt scrolls itself
  into view, so Done and Stop are never out of reach. The result table's columns share the width (the tags stay
  visible), lists and combo boxes shrink with the window, and option rows wrap. The camera view keeps the picture's
  shape. The tone chart says what it shows and has a legend; the advanced screen open now is marked in the menu.
- **Navigation.** Another display, or switching the simulator, starts the later steps again (the camera must be
  placed again, and no result of the other display or world is shown); a real run can no longer start from the demo's
  values or save them as the real monitor's. Jumping ahead in the menu needs every step before to be complete. Step 2
  remembers the chosen camera. Coming back to step 1 keeps unsaved edits.
- **Messages.** How a run ended ("Stopped: …", "Cancelled …") stays on the measure step and goes into the log instead
  of being replaced by "Ready when you are". Cancel during a prompt cancels the run (everything is put back); Stop keeps
  what is done. Closing the window while busy asks, then stops the run and closes once it has put things back.
  Unexpected errors are shown and logged instead of closing the app.
- **Safety.**
  - Before a run, whatever this process could not put back earlier, or a dead run left, is put back first; what still
    cannot be is said.
  - A camera whose journal entry is still there (an earlier restore failed) is not journaled over: that entry is the
    only record of its true settings.
  - A camera that sends no frame for 10 s ends the run with a reason instead of hanging it; the run's token now also
    ends the frame stream. A patch the graphics card stops presenting (driver reset) fails instead of waiting.
  - A DDC/CI call that has started runs to its end (or its timeout) even when the run is cancelled, so a restore never
    overlaps it on the same handle.
  - Each clean-up step runs on its own and says when it could not put something back (DDC/CI values, the GPU curve,
    HDR/ACM, the camera), instead of the run claiming everything was restored.
  - Install writes its record as soon as Windows uses the profile, and removes a recalibration's previous profile only
    once the new one is in place. Records and step 1's settings are written whole (temp file, then move); a damaged
    settings file is kept aside. The calibration loader stops only when no record file is left.
- **Advisor.**
  - A search that cannot fix what it is for (lowering Contrast does not stop the clipping, raising the boost or an
    offset Brightness does not lift the shadows) puts the control back where it was and is not run again by the final
    check (it used to push Contrast to 0 or the boost to its maximum).
  - The gamma preset changes only when another is clearly closer (beyond what two readings of one preset differ by);
    presets that all measure the same are left as they are, with a note, without rechecking each.
  - When the RGB range has been checked twice, Brightness and Contrast are tried instead of giving up.
  - The result's reasons follow the final readings (no "keeps every step apart" while steps still merge); a white
    match stopped by its change limit says so; switching to the User color mode counts as a change.
  - The white match uses the gains' real maximum (VCP 0x16's), not 100; gains that were put back after a stopped
    match are reported as put back.
  - A contrast ratio is no longer shown as "≥ N:1" from an assumed camera curve (it came out at millions to one): only
    that black is below what the camera sees.

**M5 – Windows calibration file**: the profile, the curves, and the app step that installs them with Windows' own
loader. Built and verified on the simulator; the first real install on the BenQ is next.

- `SoftCal.Icc` writes and reads ICC v2.4 display profiles. Each one has:
  - a header and the description;
  - the display's white in `wtpt`, and the Bradford adaptation to D50 in `chad`;
  - the colorants, adapted to D50;
  - 1024-entry tone curves;
  - the calibration curves in `vcgt` (3 × 256 × 16 bits).

  The reader also reads v4 text, gamma-only and parametric curves, and formula `vcgt`, and it reads Windows' own sRGB
  profile correctly. `python tools/validate_icc.py PROFILE.icc` checks a profile with LittleCMS (Pillow's ImageCms).
  LittleCMS accepts SoftCal's profiles, finds their `vcgt`, and converts sRGB colors into them as expected.
- `CurveSolver` (`SoftCal.Core/Calibration`) turns a Quick measurement into GPU curves:
  - The gray tree gives each channel's own response, interpolated in log-log space. For every frame-buffer code, the
    solver finds the input where each channel gives the target's light: gamma 2.2 (or another), sRGB, or the
    display's own tone with neutral grays.
  - Native black and white are kept, and so are the codes up to the crush.
  - Measuring again with the curves loaded and solving again refines them. The refine keeps the display's own black
    crush, which travels with the curves, rather than the trial's near-black step test. That test runs through the
    curves and is coarse: on 2026-10-18 it reported codes 0–20 as black, and the refined curve sent inputs 1–12 to
    codes 10.8–11.7, two levels on the 8-bit link, so the darkest twelve steps merged. A crush is also never trusted
    at or above the deepest gray the tree measured light at. Regression test: `CurveSolverShadowTests`, on the BenQ's
    real measurements.
- **Closed loop on a BenQ-like simulated display** (γ 2.43, dark grays 21 % off neutral, interacting channels):
  - Two passes reach γ 2.20, with every level within 0.6 code of gamma 2.2. 237–248 of the 256 levels are kept.
  - Grays end neutral to 0.9 % on a 10-bit link with even steps. That is the solver's own precision.
  - On an 8-bit link without dithering, like the BenQ's, about 2–3 % is the floor. Each channel's output is rounded
    to a whole code, and uneven 6-bit+FRC steps add code-to-code wiggles that no smooth curve follows.
  - The profile made from the result holds the curves exactly in `vcgt`. Its tone curves, measured with the curves
    loaded, follow gamma 2.2 within 6 %.
- **App step 8, "Windows calibration file":**
  - **Create.** Pick the tone (gamma 2.2, the sRGB curve, or the display's own tone), whether to make grays neutral,
    and whether to refine with a second pass. SoftCal then:
    1. starts from the step 5 measurement when it is under 30 minutes old and of the same display, profile and
       camera (otherwise it measures first);
    2. solves the curves;
    3. loads them in the graphics card as a trial, through the journaled gamma guard, and measures with them.

    A before/after table shows the gain and the levels kept. The card's curves are put back afterwards, and nothing is
    installed.
  - **Install** (after a confirmation that lists every change; no administrator prompt):
    1. Writes the profile (EDID primaries and white, tone curves measured with the curves, `vcgt`) to
       `%LOCALAPPDATA%\SoftColorCalibrator\profiles`.
    2. Adds it to Windows' color profiles.
    3. Makes it the display's default for the current user, and turns on "Use my settings for this device" for the
       display (`WcsSetUsePerUserProfiles`). Without that switch, color-managed apps keep getting the system-wide
       profile (on the BenQ, `XL2720.icm`). The switch goes on *before* the association: turning it on replaces the
       user's list for the display with a copy of the system's, which drops an association made just before.
    4. Adds **SoftCal's loader** to the user's startup apps and starts it (see below).
    5. Loads the curves now.

    A record of what was there before is kept per display: the previous default profile, the per-user switch and the
    state of Windows' loader.
  - **SoftCal's loader** keeps every installed calibration's curves in the graphics card.
    - It is the app started with `--load-calibrations`: no window, about every 2 seconds it compares each calibrated
      display's GPU ramp with its profile's `vcgt`, and writes the curves back when they changed. Windows resets them
      at sign-in, at Switch user, on display and profile changes and after sleep.
    - It polls rather than listening for display events: listening needs a window, and a window on a thread that
      does not pump messages stalls Windows' broadcasts.
    - It starts at sign-in from `HKCU\…\CurrentVersion\Run` ("SoftColorCalibrator.Loader", shown in Task Manager ›
      Startup apps). It pauses while SoftCal measures or installs (a named event held by the session), backs off for
      10 minutes when something keeps undoing the curves (more than 3 times a minute: another program owns them), and
      stops by itself once no calibration is installed. It logs to `%LOCALAPPDATA%\SoftColorCalibrator\logs\loader.log`.
    - **Why not only Windows' own loader:** it rounds the curves to 8 bits (the GPU ramp then equals the `vcgt` rounded
      to 8 bits, entry for entry) and loads only at sign-in and when a display's default profile changes. SoftCal's
      loader keeps the full 16-bit curves and puts them back after any reset. `ColorProfileGetDeviceCapabilities`
      reports no `vcgt` support (and MHC2 support) for both monitors of the development PC (NVIDIA RTX 4000 Ada, driver
      32.0.15.9716), yet Windows' loader did load SoftCal's curves there once the association was real, so that flag is
      reported, not trusted. The sign-in tests of 2026-10-17 that seemed to show Windows unable to load curves were
      invalid: see below. A profile with an `MHC2` tag would be applied by Windows' newer pipeline (a possible later
      alternative).
    - **Start SoftCal from Explorer or the Start menu, not from inside another app.** Windows silently redirects the
      registry and AppData writes of processes started from inside an MSIX app (Claude's desktop app, for one) into
      that app's storage, even when they show no package identity. A calibration installed from there exists only
      for that app: Windows keeps using the old profile, and SoftCal's own record, loader log and startup entry go
      astray. SoftCal now detects this (it writes a probe file and looks for it in each package's `LocalCache`),
      says so in pre-flight, and refuses to install, fix or remove from such a copy.
    - `build.bat` asks a running loader to stop before building (it keeps the Release build's files in use); the app
      starts it again on its next run, but only when the startup entry names that same exe, so development builds
      never start one.
  - **Check** compares the graphics card's curves with the installed profile's `vcgt`, and says whether apps get
    SoftCal's profile and whether SoftCal's loader is in the startup apps and running. It is instant and needs no
    camera. It lists what it can fix and offers to: the profile for apps (per user), SoftCal's loader, the curves now,
    and, for a calibration installed before SoftCal had its own loader, switching to it and turning "Use Windows
    display calibration" back off as it was (one administrator prompt).
  - **Verify** measures the display as Windows shows it, with the installed curves left on (about 1.5 minutes). It
    compares the result with the calibration run's measurement before the curves, and the half-light code with the
    trial's. A difference of more than 1.5 codes from the trial means the monitor or the camera's position changed.
  - Step 8 opens from the step list at any time once a display is chosen, without unlocking the steps in between.
    Check and Remove work from there; Create and Verify need the camera placed in step 4.
  - **Remove** removes the profile, makes the previous default the default again, puts the per-user switch back,
    sets the card's curves back to linear, and takes SoftCal's loader out of the startup apps once no display is
    calibrated. For a calibration that had turned Windows' loader on, it can put that back too (administrator prompt).
  - In the simulator, Install only writes the profile file.

**The app now: four steps for the monitor's own settings.** Your monitor → Camera → Measure → Result. The full display
report and the Windows calibration file moved to the menu's Advanced section. The one measurement (about 2–3 minutes)
checks whether the brightness follows the picture, reads the shadows, highlights and mid-grays, advises Brightness and
Contrast (set by hand or over DDC/CI), then matches the white. The result is a before → after table of Brightness,
Contrast and Color temperature (R / G / B), each with why. SoftCal's window moves off the measured screen for the run
(its patch window covers it) and comes back where it was afterwards.

**The white match** (`WhiteMatcher`, `SoftCal.Core/Advisor/WhiteMatch.cs`). A webcam's own idea of white is not
reliable: the C920e locked at 6500 K read both monitors of the development PC as about 5500 K. And a factory preset may
not hold its factory values: a firmware update reset the BenQ's 6500 K preset (gains 84/85/84) to a clearly green
white, 14 % less red and 18 % less blue than the ViewSonic's for the same green (Night light off, same camera). So
SoftCal matches the monitor's white to another screen chosen in step 1:

1. The user rests the camera on the reference screen. Before that, the reference shows the aiming area, the measured
   screen's patch window steps aside and SoftCal's window moves onto that screen, and the prompt shows the camera's
   live view (on the area, straight). Then SoftCal shows white on the reference, full screen for a few seconds, and
   reads it at two exposures: the second gives the camera's local slope, which turns encoded ratios into light ratios.
   For the way back, SoftCal's window returns to the reference screen and the measured screen shows the aiming area.
   The live view has its own exposure (2026-10-24): it starts at 1/64 s, right for the C920e, and moves a ×2 step at
   a time until the area's lines show, then keeps it for the next move. The user's phone (raw sensor data) had shown a
   nearly black view at 1/64 s: in those runs the white itself read at 1/8 s, at 43–50 % of the camera's range in
   green (about 20 % in red: raw data is not white-balanced), unclipped, both screens at the same exposure; the
   comparison takes ratios of linear values, so that level serves. The run's log now says each white's level and
   exposure.
2. The same on the monitor. The difference, red and blue light each relative to green, is the screens' own: one camera
   reads both, so its tone curve and its white balance cancel out. A camera left on the wrong screen is caught (its
   white does not read far above black) and the user is asked again.
3. Within 1.2 %, nothing changes. Otherwise an automatic search scales the monitor's red and blue drive against green
   until the camera sees the reference's white (Newton steps with a 2 × 2 matrix learned as it goes, Broyden; about
   ten readings, nothing for the user to do). It also measures how the white follows the drive.
4. That turns each difference into whole-step gains. The exact answer has its highest gain at the highest gain the
   monitor had, so no channel can start to clip, and the whole steps around it are compared by their predicted
   result. Each change is measured, and how the white really moved updates the matrix (gains move it more gently
   than the signal: about 0.7 times on the BenQ, 0.45 when they scale the light). The match ends within 1.2 %, or
   at the best whole steps when they are within 2.5 % (one step moves the white about 2–3 %).

Without DDC/CI the gains are set in the User color mode (most monitors allow them only there), at most four changes.
With DDC/CI and "Let SoftCal set…" ticked, SoftCal sets them itself in the monitor's current mode, up to six changes
of a few seconds each; when the first one does not move the white (a preset that keeps its own gains), it puts the
values back and asks for the User mode. Writes are journaled: kept when the match finishes, put back if it stops.
Every white reading is saved in the run's `.advice.json`. Without a reference screen the color row says "Not
measured". The reference's own setting matters: the match makes the monitor's white equal to the reference's, so a
reference in a calibrated mode (sRGB or 6500 K, when its factory values hold) gives a standard white, and one in
"Native" gives its panel's own white.

The first real match (BenQ against the ViewSonic, 2026-10-18) started 16 % low in red and 20 % low in blue for the
same green, and moved the white as the simulator does. It ended 2 % short, at its then three-change limit, because
the first change assumed signal-like gains; the steps above came from it. On the simulator, a green panel against a
neutral one matches within 2 % of the true light with gains of every kind, also with a biased camera white balance
and an S-shaped camera curve, and with BenQ-like gentle gains set over DDC/CI in the preset's own mode.

**Measuring with a phone (M6, 2026-10-19).** An Android phone can measure instead of a USB webcam. It runs **SoftCal
Camera**, a small app that lends its camera over the local network; on the PC only step 2 changes (choose **Phone**,
type the code the phone shows, Connect). Setup, the protocol and the build are in
[docs/phone-link.md](phone-link.md).

- **Wi-Fi, not Bluetooth.** The phone listens and announces itself over DNS-SD (`_softcal._tcp`); SoftCal finds it by
  name and connects out to it, so Windows Firewall never asks. The address can also be typed.
- **Pairing** by the 6-digit code the phone shows: each side proves it knows the code with an HMAC over both sides'
  nonces, so the code never travels. After 5 wrong codes the phone shows a new one. One PC at a time.
- **The same measurement code.** `PhoneInstrument` puts the link behind `IMeasurementInstrument`. The phone computes
  each frame's region statistics from its raw sensor frames when its camera offers them (linear, before any
  processing), otherwise from its YUV frames (full-range BT.601, as the webcam's). It times each frame on its own
  clock: when its last row finished exposing. SoftCal maps that time to its own clock (pings every 2 s; the
  fastest of the last 16 gives the offset, NTP-like), so network delay never decides which frames saw a patch. Each
  capture request carries its exposure epoch as its tag, so every frame says exactly which exposure it was taken with.
- **The lock** on the phone: automatic exposure off with SoftCal's ×2 exposure steps up to 1/4 s, the lowest ISO, the
  daylight white balance preset (≈ D65), focus at the farthest (sent to the nearest and back when it does not move),
  and edge enhancement, noise reduction and stabilization off. Without raw data, a fixed sRGB tone curve is also set
  where the phone allows it. Each control is read back from
  the capture results. The phone puts its camera back in automatic at the end, and by itself whenever the link drops,
  so nothing is journaled on the PC.
- **Step 2** remembers the camera kind and the phone's last address. The phone's live view is turned upright the way
  the phone stands, and shows the same level dot as a webcam's.
- **The phone app** is C# on .NET for Android (Camera2, Android 9+), and shares `SoftCal.Remote` with the PC.
  `build-phone.bat` builds it and leaves `SoftCalCamera.apk` here, to copy to the phone.
- **Tests:** pairing (right and wrong codes, a new code after five, a busy phone, another protocol version), the clock
  mapping, the YUV statistics (both chroma layouts, clipping, row bands, the four sensor rotations, picture packing),
  the raw statistics (the four Bayer layouts, a black level per position, clipping near white, row bands), a linear
  camera through the whole Quick measurement, the exposure control, and the blur and brightness checks,
  and the measurement core reading patches from a simulated phone over a real socket. Further tests cover a camera
  lost mid-stream, the phone going away, and the phone putting its camera back.
  `NetworkTests` (`SOFTCAL_NETWORK=1`) checked the DNS-SD browsing against devices on the development network.
- **First tries on a real phone (Galaxy S23 Ultra).** Discovery, pairing, the clock mapping (within 3–4 ms) and the
  camera view work. Two fixes came from them:
  - Each camera frame is now given back to the camera with `close()`. Dispose only lets go of .NET's handle, so the
    camera stopped after its four buffers.
  - The Samsung kept its lens on the screen when asked for the farthest focus, then reported the farthest while still
    seeing the screen sharply. So SoftCal checks the blur in the picture before measuring (`DefocusCheck`: 1-pixel
    lines against a solid gray of their light, at most 15 % ripple). When the blur is not enough, it searches the
    phone's focus settings, judging each by the picture (`DefocusSearch`, through the link's focus command: protocol
    version 2). A short tube (about 4 cm, closer than the lens can focus) blurs the screen whatever the lens does.
  - The phone shows its camera's preview, outlines the area SoftCal measures, and says what it sends to the PC and how
    often. Its camera runs only while SoftCal needs it (it closes 10 s after the last request, unless it is locked
    for a measurement).
  - The brightness check took the phone's own adaptation (its tone curve follows what it sees) for a monitor whose
    brightness follows the picture. It now keeps a large gray patch under the camera and switches only the rest of
    the screen between white and black. The monitor still sees its picture swing; the camera only ever sees the gray.
  - The result page says how it was measured: the camera (USB webcam, phone camera with its model, or simulator), the
    profile, the display and the time. The run's saved record has the camera and profile too.
  - The Samsung's tone curve follows what its camera sees, and it lets no app fix it, so its processed readings
    depended on the scene. The phone now measures from the sensor's raw data when the camera offers it (protocol
    version 3): linear, before any tone curve, white balance, noise reduction or sharpening. Exposure steps, the blur
    check and the brightness check judge the light the same way for linear readings, and the contrast ratio is shown
    for them (no camera curve to assume). The lock's reply says whether the phone really reads raw data; the log and
    the result page say so too. Details in [docs/phone-link.md](phone-link.md#raw-sensor-data).

  A full measurement with a phone is still to be done.

**Pending:** the hardware write checks on real displays. These are the `SOFTCAL_HARDWARE_WRITE` tests and the
harness's `safety … --crash` check. They briefly flip ACM, nudge DDC/CI brightness and darken the gamma ramp, then
restore them.

## Build and test

Requires the .NET 10 SDK (pinned in `global.json`). No Visual Studio is needed.

```
dotnet build SoftColorCalibrator.slnx
dotnet test
```

The batch files at the root handle the app alone:

- `build.bat` builds it (Release). Close the app first: a running copy locks its files.
- `run.bat` starts the last build.
- `build-run.bat` does both.
- `build-phone.bat` builds the phone app, SoftCal Camera (needs the .NET Android workload, a JDK 17 and the Android
  SDK; see [docs/phone-link.md](phone-link.md)), and leaves `SoftCalCamera.apk` here. It is not part of the
  solution.

Tests use xUnit v3 on Microsoft.Testing.Platform. The opt-in is in `global.json`. Tests marked
`[Trait("Category", "Hardware")]` run against the real camera and displays and skip unless `SOFTCAL_HARDWARE=1`. The
ones that change something also need `SOFTCAL_HARDWARE_WRITE=1`: camera controls, a gamma ramp, the Auto Color
Management switch, or DDC/CI brightness. Each change is always restored.

```
dotnet test -- --filter-trait "Category=Hardware"
```

## The M1 harness

```
dotnet run --project tools/SoftCal.FeasibilityHarness -- <command>
```

| Command | What it does |
|---|---|
| `list` | Cameras and monitors (read-only) |
| `probe` | Night light, link format (DisplayConfig + NVAPI), DDC/CI VCP codes and gamma ramps — read-only |
| `nightlight --wait` | Night light state; waits until it reads off |
| `window --monitor N` | Patch-window pixel exactness by screen read-back |
| `lock --monitor N --allow-camera` | Camera lock + read-back report, then restore |
| `full --monitor N --allow-camera` | Trust test, optical sync, flicker, defocus, 50 % null, pattern shapes, midpoint tree ×3, bracketed 17-step ramp ×3 |
| `run --monitor N --allow-camera <steps>` | Any steps; also `move --phase base\|plus\|minus`, `hold --minutes 30`, `dark` |
| `gamma --monitor N --allow-camera --allow-gamma` | Gamma-ramp write and camera check (journaled, always restored) |
| `report` | Gate numbers from every recorded session (`gate-report.md` next to the sessions) |
| `reanalyze <session>` | Re-fits a session's recorded bracketing ramps (analysis is a pure function of the recording) |
| `camera [--auto] [--watch] [--wb-test] [--lock-test]` | The webcam's controls (read-only). `--auto` puts each to its default, automatic where it can be (while streaming); `--wb-test` compares the picture's colour at 2800 K, 6500 K and automatic, then resets; `--lock-test` locks it as a measurement does and restores it as at a run's end; `--watch` then streams 5 s, changing nothing, and shows white balance, exposure, gain and the picture's mean colour (numbers only, no picture kept) |
| `recover` | Restores anything a crashed run left journaled |
| `state` | Pre-flight: Night light, color filters, HDR/ACM, link range, power, calibration loader, with findings (read-only) |
| `safety --monitor N [--allow-gamma] [--allow-acm] [--allow-ddc] [--crash]` | Changes each allowed kind of state under the journal, then restores it. With `--crash` the harness kills itself and the guardian must restore everything |
| `elevate` | UAC round trip of the elevated helper (ping) |

Add `--simulated` to run any measurement against the simulated rig instead of the camera. Every run is recorded under
`%LOCALAPPDATA%\SoftColorCalibrator\sessions\m1`; changed state is journaled in `%LOCALAPPDATA%\SoftColorCalibrator\journal`
and restored by a guardian process if the harness dies.

## Layout

```
src/SoftCal.Core                   platform-neutral math, models, measurement core and analyses
src/SoftCal.Instruments.Simulated  simulated display + camera for tests
src/SoftCal.Instruments.Webcam     Media Foundation webcam instrument (UVC lock, ROI statistics)
src/SoftCal.Instruments.Phone      a phone running SoftCal Camera as an instrument; finding phones (DNS-SD)
src/SoftCal.Remote                 the phone link, shared by the PC and the phone: messages, pairing, clock sync,
                                   the phone's server, the PC's client, the phone's frame statistics
src/SoftCal.Phone                  SoftCal Camera, the Android app (Camera2); built by build-phone.bat
src/SoftCal.Windows                patch window, displays, gamma ramps, DDC/CI, HDR/ACM, Night light, NVAPI,
                                   OS pre-flight, safety journal + guardian, power request, elevated helper
tools/SoftCal.FeasibilityHarness   M1 feasibility harness
tests/                             xUnit v3 test projects (EDID fixtures are anonymized)
tests/SoftCal.SafetyCrashHost      helper process the guardian crash tests kill at random moments
docs/                              implementation plan, M1 feasibility report, the phone link
```
