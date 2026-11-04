# Measuring with a phone: SoftCal Camera and the phone link

SoftCal can measure with a phone's or a tablet's camera instead of a USB webcam: an Android phone or tablet, or an
iPhone or iPad. The device runs **SoftCal Camera**, a small app that lends its camera to SoftCal over the local
network. On the PC nothing else changes: the same steps, the same measurement code, the same reports, whichever kind of
device it is. The phone only does what a webcam's driver would do: it locks its camera when told to, and sends the
statistics of each frame's measured region, timed on its own clock.

## Why Wi-Fi rather than Bluetooth

- **Throughput.** Placing the camera needs its live picture (about 300 KB, several times a second). Measuring sends
  about 30 small messages a second. Wi-Fi carries both easily. Bluetooth Low Energy does not carry the pictures, and
  classic Bluetooth needs pairing in Windows Settings first.
- **No firewall prompt.** The phone listens and SoftCal connects *out* to it, so Windows Firewall has nothing to ask.
  If you cancel a firewall prompt, Windows silently creates a block rule; this design never shows one.
- **No setup.** The phone announces itself on the network (DNS-SD, the way printers and speakers do), so SoftCal lists
  it by name. Its address can also be typed.
- **Timing is not the network's problem.** Every frame carries the time it was taken, and SoftCal maps the phone's clock
  to its own. The network's delay and jitter never decide which frames saw a patch.

## Using it

1. **Install SoftCal Camera.**
   - **Android:** from Google Play (coming soon), or with the `SoftCalCamera-<version>.apk` of the
     [latest release](https://github.com/alexandrewillame/soft-color-calibrator/releases/latest): open it on the phone,
     and allow installing apps from that source when Android asks. To build it yourself, run `build-phone.bat` (see
     "Building the phone app" below): it leaves `SoftCalCamera.apk` in the repository folder, to copy to the phone.
   - **iPhone and iPad:** from the App Store (coming soon), or a TestFlight invitation. iOS installs apps only from
     there: see "Building the iOS app" below.
2. **Open SoftCal Camera** on the phone, on the same Wi-Fi as the PC. Allow the camera when it asks (on an iPhone or
   iPad, allow the local network too: SoftCal finds the device through it). It shows a 6-digit code and the phone's
   address.
   - Until a PC connects, it also says where to get SoftCal for Windows: this project's GitHub page, written out to type
     on the PC, and a link that opens it in the phone's browser. Someone who installed the phone app first learns there
     that it is the camera of a PC app.
   - It also shows what its camera sees, and, while SoftCal measures, outlines the area SoftCal reads.
   - A line under the preview says what is being sent to the PC, and how often: gray pictures of the camera's view
     (while SoftCal shows it in step 2), or the average color of the outlined area (one small message per frame, while
     it measures). Nothing else leaves the phone.
   - The camera runs only while SoftCal needs it. It opens when SoftCal asks for a picture or a measurement, and closes
     again 10 seconds after the last request, unless SoftCal holds it locked for a measurement. Meanwhile the preview
     says "Camera off".
3. **In SoftCal on the PC, step 2 (Place the camera):** choose **Phone**. The phone appears in the list after a few
   seconds. Type the code it shows and click **Connect**. The camera's view appears on the left, as with a webcam.
4. **Place the phone:**
   - Tape a black paper tube, about 4 cm long, around the phone's main back camera. Once the phone is connected,
     step 2 gives the length for that phone: half its nearest focus, 3–5 cm. So close, the phone's lens cannot focus
     on the screen, which reaches the camera blurred whatever the lens does, as the measurement needs.
   - Rest the tube flat against the screen, anywhere inside the outlined area, with the phone propped up (a stack of
     books works).
   - The camera must look straight at the screen: the dot on its view inside the circle. The exact spot does not
     matter.
5. **Keep SoftCal Camera open, in front,** until the end. Android and iOS lend the camera only to the app on screen.
   The phone's screen stays on, and dims while measuring. On an iPhone or iPad, leaving the app ends the link: iOS
   stops the camera and soon the app. Back in front, the app shows a new code; connect again in step 2.
6. **Keep the room light as it is** until the end, as with a webcam.

The link stays open from step to step. **Disconnect** in step 2 ends it. When the link drops for any reason (SoftCal
closes, the phone leaves the Wi-Fi, the app is closed), the phone puts its camera back in automatic and shows a new
code.

### What a phone needs

**Android phones and tablets:**

- Android 9 or later.
- A back camera that lets apps set its exposure. In Camera2 terms, this is the `MANUAL_SENSOR` capability, which
  most phones from the last several years have. SoftCal says so when a phone lacks it: such a phone can show its view
  but cannot measure.
- Best, raw sensor data (the `RAW` capability, with Bayer `RAW_SENSOR` frames), which most recent phones offer: every
  number SoftCal measures then comes from it, before any of the phone's processing (see
  [Raw sensor data](#raw-sensor-data)). Without it, SoftCal measures the processed picture: with a fixed tone curve
  where the phone allows one (`MANUAL_POST_PROCESSING`), otherwise with the phone's own, which may follow the scene.
  The lock report says which.

**iPhones and iPads:**

- iOS or iPadOS 16 or later: an iPhone 8 or later, and the iPads that run it. Every one lets apps set the exposure of
  its back camera.
- No raw data: iOS gives apps raw frames only as single photos, not while the camera streams. SoftCal measures the
  processed picture, as on an Android phone without raw data, through iOS's own tone curve, which may follow the
  scene; iOS applies it evenly over the whole picture while measuring (see
  [On an iPhone or iPad](#on-an-iphone-or-ipad)).

### When it does not connect

- **The phone is not listed.** Some networks keep devices from seeing each other: guest Wi-Fi, "AP isolation", or some
  mesh systems. Type the address the phone shows (for example `192.168.1.23`) in the phone box instead. If it still
  does not answer, the network blocks devices from reaching each other; use another network or the phone's hotspot.
  On an iPhone or iPad, check that SoftCal Camera may use the local network: Settings › Privacy & Security › Local
  Network.
- **"Wrong code".** Type the 6 digits the phone shows now. After 5 wrong codes the phone shows a new one.
- **"Already connected to another PC".** Disconnect it there, or close and reopen SoftCal Camera.
- **"The phone's camera sees the screen's finest lines sharply".** The phone is farther from the screen than its lens
  can focus. Shorten its tube to the length SoftCal gives, place it again in step 2, and measure again.
- **"The phone's camera stopped", or "sends no frames".** Another app took the camera, or SoftCal Camera left the
  screen. Bring it back to the front: the camera view comes back by itself; start a measurement that stopped again.
  The phone's screen shows the camera's frame rate ("Camera: 30 frames a second") under its address.

## What the phone does to its camera

| Phase | Settings |
|---|---|
| Nothing asked for 10 s, and not locked | Camera off |
| Waiting, connected, placing | Automatic: exposure, white balance, continuous autofocus |
| Locked for measuring | See below |
| End of the measurement, or link dropped | Automatic again |

When locked for measuring, an Android phone sets (an iPhone or iPad: see
[On an iPhone or iPad](#on-an-iphone-or-ipad)):

- **Exposure:** automatic exposure off. SoftCal sets the exposure time in ×2 steps, as with a webcam, up to 1/4 s.
- **Sensitivity:** the lowest ISO.
- **White balance:** the daylight preset, which Camera2 defines as approximately CIE D65.
- **Focus:** the farthest the lens goes, so that the screen a few centimetres away is blurred (the short tube makes
  sure of it when the lens does not follow).
- **Processing off:** edge enhancement, noise reduction, optical and video stabilization, scene modes and effects.
- **Black level:** locked.
- **Tone curve:** none for the measurement when it reads raw data. Otherwise a fixed sRGB curve, where the phone allows
  it.

Each control is read back from the camera's capture results. The lock report in SoftCal's log shows what was asked and
what the camera reports, as for a webcam. When the lens does not reach the farthest focus, the phone sends it to its
nearest focus and back: a Galaxy S23 Ultra only moved it when the distance asked for changed.

**The blur is checked in the picture.** A phone's report of its lens cannot always be trusted. The Galaxy S23 Ultra
reported its focus at the farthest and still saw the screen sharply. So before measuring, SoftCal shows 1-pixel lines
beside a solid gray of the same light, at one exposure.

- **The test.** The lines' pixels may spread at most 15 % beyond the camera's own noise (relative RMS), and clip at most
  0.2 %. Every halftone reading counts on the light of its 1-pixel elements reaching the camera already mixed. A
  camera that sees them reads them wrong, and its dark lines clip. A ripple r biases a halftone reading by about
  −0.27 r² of its light on a γ 1/2.2 camera: 0.6 % at 15 %, nearly the same at every level and in every channel, so
  gamma and gray balance barely move. Raw (linear) readings show the same blur's ripple about 2.2 times larger; it is
  divided by 2.2, so the same blur passes as with a processed picture. (Linear readings take no such bias; the limit
  still keeps the lines well mixed over the region.)
- **When it fails, the phone's focus settings are tried.** The lines stay on screen while the phone's focus moves from
  the farthest to its nearest, and the picture's ripple is read at each. The search stops at webcam-like blur (2.5 %)
  and otherwise keeps the least ripple. The log lists every setting tried. "The farthest" is asked as 0.05 diopters
  (20 m): some phones seem to take exactly 0 as "not set".
- **When no setting blurs enough,** the measurement stops, saying so. Bring the phone closer than it can focus, or
  measure with a webcam.

Every capture request carries its exposure epoch as its tag, so each frame says exactly which exposure it was taken
with. A webcam only gives the epoch approximately. The statistics are computed on the phone, from its raw frames when
the camera offers them (below), otherwise from its YUV frames, which use full-range BT.601 (JFIF), as the webcam's
statistics do. Each frame reports:

- the mean, the standard deviation and the clipped fraction of red, green and blue over the measured region (R′, G′
  and B′ for processed frames);
- the mean green (luma, for processed frames) of horizontal bands. These follow the sensor's rows (the rolling
  shutter's), whichever way the phone is turned.

Pictures for placing the phone are turned upright the way the phone stands, using its orientation sensor. They come
from the processed frames: they only show where things are.

### Raw sensor data

A phone's processed picture is not a measuring instrument. Its tone curve may follow what the camera sees: the Galaxy
S23 Ultra's does, and it lets no app fix it. Noise reduction and sharpening also change with the scene. So when the
camera offers raw frames, the phone measures those instead.

- **What it reads.** Camera2's `RAW_SENSOR` frames, at the smallest size offered: one 16-bit value per photosite
  behind the Bayer filter, as the sensor gave it. No tone curve, white balance, colour matrix, noise reduction or
  sharpening has touched them.
- **How it reads them.** Each 2×2 block of the measured region gives a red, a blue and the mean of its two greens. The
  black level is taken off: the frame's own level when the camera reports one, otherwise the sensor's fixed pattern.
  The white level then scales the values to 0–255, so they are proportional to the light. A photosite within 2 % of
  the white level counts as clipped (some sensors saturate a little below it). The region is placed on the raw frame
  through the sensor's active area, the part the processed picture shows.
- **When.** The raw output is part of the camera session from the start. The phone asks for raw frames only while
  SoftCal measures, because they are large. The statistics then come from raw frames only, never mixed with processed
  ones.
- **The colours are the sensor's own.** Raw values are not white-balanced, so a white reads greenish. Nothing SoftCal
  computes needs them balanced: it compares readings with each other (a gray against white, a pattern against a
  solid, one screen against another).
- **What changes on the PC.** Readings proportional to the light double with each exposure step, and the exposure
  control expects that. The blur and brightness checks judge the same change of light as with a processed picture: a
  ripple or a change shows about 2.2 times larger in linear values. The contrast ratio is shown, since no camera curve
  has to be assumed.
- **If the raw output fails.** A camera that will not stream raw frames beside the others measures the processed
  picture instead. Its lock reply tells SoftCal, which then treats the readings as encoded.
- **In the log.** The lock line's Gamma entry says "measured from the sensor's raw data" with the size, filter layout,
  black and white levels. The next line says which data the phone reads. The result page says "raw sensor data" after
  the phone's name.

### On an iPhone or iPad

The iOS app does the same through AVFoundation, with the controls iOS gives apps:

- **Camera:** the main back camera (the wide one), never a virtual camera that switches lenses by itself (as for
  close-ups).
- **Frames:** full-range 4:2:0, about 640×480 in 4:3 (the whole sensor), 30 a second, as on Android. The statistics are
  the processed frames', decoded with the Y′CbCr matrix each frame says it uses (BT.601 as JFIF, or BT.709).
- **Exposure and ISO:** custom exposure, at the time SoftCal sets and the lowest ISO. Frames last as long as their
  exposure needs.
- **White balance:** the device's own gains for the color temperature SoftCal asks for: 6500 K, tint 0 (D65), exactly
  rather than a preset.
- **Focus:** locked at the farthest the lens goes. iOS places the lens from 0 (its nearest focus) to 1 (the farthest),
  not in diopters: SoftCal's diopters are converted taking the lens's travel as even in diopters, from the nearest
  focus iOS reports (`minimumFocusDistance`). The blur check judges the picture anyway.
- **Processing:** video HDR, low-light boost and stabilization off, and the low-light noise reduction of iOS 27. iOS
  lets apps turn off neither its sharpening and noise reduction nor fix its tone curve. While measuring, the tone curve
  is one for the whole picture (global tone mapping) where the camera allows it, but it may still follow the scene.
  The lock report says so; SoftCal's readings compare patches through that same curve, as with a webcam.
- **Epochs:** every change of settings reports the first frame it holds for (AVFoundation's sync time). Frames take a
  change's epoch from that frame on; frames before it keep the previous epoch, which SoftCal then skips.
- **Leaving the screen:** iOS stops the camera, and soon the app and its network. The app ends the link, stops its
  announcement, and starts both again, with a new code, when it is back in front.
- **In the log.** The lock line shows the exposure, ISO, white-balance gains (and the temperature iOS reads back from
  them), the lens position, VideoHdr, and Gamma's "the phone's own tone curve stays".

## Timing

- **When a frame was taken.** A frame's time is when its last row finished exposing. It is on the phone's
  elapsed-real-time clock: the sensor timestamp, plus the rolling shutter's skew, plus the exposure. A phone whose
  sensor timestamps are not on that clock uses the frame's arrival time instead, which is later and so never early.
- **On an iPhone or iPad,** the clock is the host clock, the capture session's. AVFoundation does not say which moment
  of a frame its timestamp is: the app takes it as the start of the first row, and adds the exposure and the shortest
  frame time the format allows (no row reads out later than that). Were the timestamp later than the first row's
  start, the frame's time would only be later, never early. The rolling shutter's time per row is not known
  (`rowTime` is null).
- **Matching the clocks.** SoftCal pings the phone every 2 seconds, and six times right after connecting. Like NTP, it
  keeps the offset from the ping that came back fastest among the last 16, to within half that round trip: a few
  milliseconds on a home network.
- **Measuring.** SoftCal maps each frame's time to its own clock (the one the patch window's display times are on).
  The measurement code then judges whether a frame saw a patch settled exactly as it does for a USB camera.

## The protocol

The protocol's version is 3: version 2 added the focus command, version 3 the raw statistics (`raw` in the
capabilities and in the lock's reply). Each side refuses another version, so an older phone app or SoftCal says to
update.

**Transport**

- WebSocket (RFC 6455) over TCP, path `/softcal`.
- Port 47630, or another one when that one is taken; the announcement gives it.
- Text frames carry JSON. Binary frames carry picture pixels.
- The phone's server is a plain socket plus the runtime's WebSocket; there is no web server.

**Discovery**

- DNS-SD service `_softcal._tcp.local`, with TXT `v=1`.
- The instance name is the phone's name, for example "Google Pixel 7".
- On Windows, SoftCal browses with `DeviceInformation.CreateWatcher` (association endpoint services, the DNS-SD
  protocol).
- Android announces with `NsdManager`; iOS with Bonjour (`NSNetService`), which needs the service type in the app's
  `NSBonjourServices` and the user's permission for the local network.

**Pairing**

- The phone shows a 6-digit code. Neither side ever sends it.
- `K = SHA-256("softcal-pair|" + digits)`.
- The PC proves it knows the code with `HMAC-SHA256(K, "pc|" + pcNonce + "|" + phoneNonce)`.
- The phone proves it in turn with `HMAC-SHA256(K, "phone|" + pcNonce + "|" + phoneNonce)`. Both are Base64 and
  compared in constant time.
- After 5 wrong proofs the phone picks a new code, so guessing it on the network gets nowhere.
- One PC at a time; a second PC is told the phone is busy.
- The link is not encrypted. It carries frame statistics and grey pictures of the area under the tube, which shows
  SoftCal's own aiming pattern and patches.

**Keeping the link alive**

- WebSocket pings run every 5 s each way. A peer silent for 10 s is dropped.
- SoftCal's own pings run every 2 s. A phone that answers none for 8 s is dropped, and SoftCal says so.

### Messages

Every message is a JSON object with its kind in `type`; property names are camelCase. Commands from the PC carry an
`id`, and the phone's reply repeats it.

| Direction | `type` | Fields | Meaning |
|---|---|---|---|
| PC → phone | `hello` | `protocol`, `client`, `nonce` | First message: the PC's protocol version, its name, its half of the challenge |
| phone → PC | `challenge` | `protocol`, `nonce`, `phone` {`manufacturer`, `model`, `app`} | The phone's half of the challenge and what it is |
| PC → phone | `auth` | `mac` | The PC's proof |
| phone → PC | `welcome` | `mac`, `capabilities` | The phone's proof; the session is open |
| PC → phone | `ping` | `id`, `pcTime` | Clock probe |
| phone → PC | `pong` | `id`, `pcTime`, `phoneTime` | The phone's time when it answered |
| PC → phone | `prepare` | `id`, `lock` {`whiteBalanceKelvin`, `focusDiopters`} | Lock the camera (focus null = the farthest) |
| phone → PC | `prepared` | `id`, `controls` [{`control`, `outcome`, `requested`, `actual`, `note`}], `raw` | The lock as read back; `raw`: while it holds, the statistics come from the sensor's raw data |
| PC → phone | `exposure` | `id`, `seconds` | Set the exposure time |
| phone → PC | `exposureSet` | `id`, `nominal`, `actual`, `epoch` | The exposure asked for, as reported by the sensor, and its epoch |
| PC → phone | `startStream` | `id`, `region` {`centerX`, `centerY`, `width`, `height`, `rowBands`} | One `frame` per camera frame from now on (region on the upright picture, 0–1) |
| phone → PC | `frame` | `seq`, `time`, `epoch`, `exposure`, `red`/`green`/`blue` {`mean`, `stdDev`, `clipped`}, `pixels`, `bands` | One frame's region statistics |
| PC → phone | `stopStream` | `id` | Stop the frames |
| PC → phone | `captureImage` | `id`, `region`, `frames`, `skip`, `maxWidth` | A luma picture of a region, averaged over frames |
| phone → PC | `image` | `id`, `width`, `height`, `bits`, `frames`, `firstTime`, `lastTime` | The picture's header; its pixels follow in the next binary frame (8 bits, or 16-bit little-endian mean × 256 when averaged) |
| PC → phone | `restore` | `id` | Back to automatic |
| PC → phone | `focus` | `id`, `diopters` | Move the locked camera's focus (0 the farthest), the exposure unchanged |
| phone → PC | `focusSet` | `id`, `requested`, `reported`, `moving`, `phoneTime` | The focus as the camera reports it (not always where the lens is), whether the lens still moved, and from when frames show it |
| phone → PC | `ok` | `id` | Done (for commands without data) |
| phone → PC | `error` | `id`, `message` | A command failed (`id` 0: no command in particular, e.g. a refused pairing) |
| phone → PC | `fault` | `kind`, `message` | Something went wrong outside a command (`kind` is an `InstrumentFaultKind`: `Disconnected`, `Thermal`…) |
| either | `bye` | `reason` | Closing |

`capabilities` holds:

- `minExposure` and `maxExposure` (seconds), `minIso` and `maxIso`;
- `manualSensor`, `manualPostProcessing` and `realtimeTimestamps`;
- `frameWidth`, `frameHeight` (upright) and `frameRate`;
- `rowTime` (the rolling shutter's time per row, when known);
- `whiteBalancePresets` and `minimumFocusDiopters`;
- `raw`: the phone hopes to measure from the sensor's raw data (its camera offers it); the lock's reply confirms it.

Times are in seconds. `time`, `phoneTime`, `firstTime` and `lastTime` are on the phone's clock.

## Building the phone app

The Android app is C# on .NET for Android (the iOS app: [below](#building-the-ios-app)). It shares the protocol code
(`SoftCal.Remote`) with the PC, so both sides always speak the same version. It is built apart from the PC solution,
and needs:

- the .NET Android workload: `dotnet workload install android`;
- a JDK 17. This repository expects Microsoft's build of OpenJDK in `%USERPROFILE%\Android\openjdk`;
- the Android SDK (platform 36, build tools, platform tools) in `%USERPROFILE%\Android\Sdk`.

Both live outside AppData on purpose. Other paths work by passing `-p:JavaSdkDirectory=…` and
`-p:AndroidSdkDirectory=…`.

`build-phone.bat` builds the Release app:

- It writes the build output under `%USERPROFILE%\Android\build`. An Android build creates and deletes thousands of
  files, and a synced folder's client locks them.
- It signs the app with its own key, made on the first build. The key and its password are kept in
  `%USERPROFILE%\Android` and never in the repository, so each new build installs over the last one. **Back the key
  up** if you want to keep updating the installed app: an app signed with another key has to be uninstalled first. On
  Google Play this key is the *upload key*; Google signs the app people download with its own key.
- It copies the results to `SoftCalCamera.apk` (to install directly) and `SoftCalCamera.aab` (the app bundle for
  Google Play).
- The version is the repository's (`<Version>` in `Directory.Build.props`), shared with the PC app; Google Play's
  version code is made from it (0.1.0 is 100).

## Building the iOS app

The iOS app is C# on .NET for iOS, and shares the protocol code (`SoftCal.Remote`) with the PC and the Android app.
iOS apps are built and signed on a Mac, with Xcode: Windows cannot build them.

- **Without a Mac:** GitHub Actions builds it on one of GitHub's Macs (`.github/workflows/ios.yml`). On every change
  to the iOS app or the protocol, a simulator build checks that it compiles. Run by hand, it makes the signed app and
  uploads it to App Store Connect, for TestFlight and the App Store (the workflow's comments list the secrets it
  needs).
- **On a Mac:** `bash build-ios.sh check` (the simulator build), `bash build-ios.sh ipa` (the signed app in
  `artifacts/release/<version>/app-store/`), `bash build-ios.sh upload` (the same, then uploaded). The script installs
  the .NET iOS workload and picks the Xcode version that the workload asks for.
- The version is the repository's, as for the Android app; the App Store's build number is made from it the same way
  as Google Play's version code (0.1.0 is 100).
- iOS installs apps only from the App Store, TestFlight, or Xcode on a developer's own devices: there is no file to
  download from GitHub as there is for Android.

## Code

| Project | What |
|---|---|
| `src/SoftCal.Remote` | The protocol, shared by both sides: messages, pairing, clock sync, the phone's server (`PhoneServer`), the PC's client (`PhoneClient`), and the phone's YUV frame statistics and pictures |
| `src/SoftCal.Instruments.Phone` | The PC side: `PhoneInstrument` (the link behind `IMeasurementInstrument`) and `PhoneFinder` (DNS-SD browsing) |
| `src/SoftCal.Phone` | The Android app: Camera2 (`PhoneCamera`), the screen (`MainActivity`), the network announcement and Wi-Fi locks |
| `src/SoftCal.Phone.iOS` | The iOS app (iPhone and iPad): AVFoundation (`PhoneCamera`), the screen (`MainViewController`), the Bonjour announcement and addresses (`Network`), the model's name (`DeviceName`) |
| `tests/SoftCal.Instruments.Phone.Tests` | Pairing, clock sync, statistics, addresses, and the measurement core reading a simulated phone over a real socket |

The tests run the phone's server around a simulated camera on the loopback. The measurement core then reads patches
through the link exactly as it reads a USB camera. `NetworkTests` (`SOFTCAL_NETWORK=1`) checks that the finder lists
DNS-SD services announced on the real network.
