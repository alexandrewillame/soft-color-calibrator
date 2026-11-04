# Changelog

What changes in each version of Soft Color Calibrator (Windows) and SoftCal Camera (Android, iPhone and iPad). All the
apps share one version number. The newest version comes first.

## Unreleased

### Soft Color Calibrator (Windows 11)

- Simulator: its demo monitor answers over DDC/CI like a real one, so **Let SoftCal change the monitor's settings**
  can be tried without hardware: SoftCal sets the demo monitor's Brightness, Contrast and Red, Green, Blue itself.

## 0.1.0: first public preview

### Soft Color Calibrator (Windows 11)

- Four guided steps: your monitor, camera, measure (about 2 to 3 minutes), result.
- Monitor settings advice: brightness, contrast and RGB gains for the monitor's own menu, one change at a time, each
  judged by a new measurement; set over DDC/CI when the monitor allows it.
- White matching to another screen on the same PC.
- Full display report: tone curve and gamma, gray balance, black crush, white clipping, contrast, Full/Limited range,
  before/after comparisons (HTML and JSON).
- Windows calibration file: an ICC v2.4 profile with `vcgt` curves; create, try, install, check, verify and remove,
  with SoftCal's own loader keeping the curves in place.
- Profiles per monitor, each with its own settings and calibration.
- Measuring with a USB webcam (Media Foundation, locked and restored) or with a phone or tablet running SoftCal Camera
  (Android, iPhone, iPad).
- Safety: every change (graphics card curves, camera controls, DDC/CI values, HDR/ACM) is journaled and put back,
  by a guardian process if the app crashes.

### SoftCal Camera (Android 9 or later)

- Lends the phone's camera to Soft Color Calibrator over the local Wi-Fi network: found automatically, paired with a
  6-digit code.
- Measures from the camera's raw sensor data when the phone offers it; locks exposure, ISO, white balance, focus and
  processing while measuring, then puts the camera back to automatic.
- Shows what it sends to the PC and how often; the camera runs only while SoftCal needs it.

### SoftCal Camera (iPhone and iPad, iOS 16 or later)

- The same app for iPhone and iPad: lends the device's back camera to Soft Color Calibrator over the local Wi-Fi
  network, found automatically, paired with a 6-digit code.
- Locks exposure, ISO, white balance (gains for D65), focus and video HDR while measuring, with one tone curve for the
  whole picture, then puts the camera back to automatic. iOS offers no raw frames to stream, so it measures the
  processed picture.
- Shows what it sends to the PC and how often; the camera runs only while SoftCal needs it.
