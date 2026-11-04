# Contributing

Thanks for helping. Problem reports are the most useful contribution: open an
[issue](https://github.com/alexandrewillame/soft-color-calibrator/issues) with the run's log (the issue form says
where it is). For a larger change, open an issue first so we can agree on the approach.

## Build and run

You need Windows 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download) (the version is pinned in
`global.json`). No Visual Studio needed.

```bash
dotnet build SoftColorCalibrator.slnx
```

```bash
dotnet test
```

`run.bat` starts the app (Release); `build-run.bat` builds it first. **Advanced › Simulator** runs everything with a
pretend monitor and camera: nothing on the PC changes, and no camera is needed.

Tests that use real hardware skip themselves unless asked for:

| Variable | Runs |
|---|---|
| `SOFTCAL_HARDWARE=1` | Reading the displays and the webcam |
| `SOFTCAL_HARDWARE_WRITE=1` (with the one above) | Changing graphics card curves and camera controls, always put back |
| `SOFTCAL_NETWORK=1` | The phone link over the local network |

Only those: `dotnet test -- --filter-trait "Category=Hardware"`.

The phone apps are built apart: `build-phone.bat` for Android, `build-ios.sh` (on a Mac, or GitHub's Macs through
`.github/workflows/ios.yml`) for iPhone and iPad. [docs/phone-link.md](docs/phone-link.md) explains both.

## How the code is written

- **Warnings are errors**, with .NET's analyzers on (`Directory.Build.props`, `.editorconfig`). Measurement data never
  depends on the user's culture: format and parse with the invariant culture.
- **Everything SoftCal changes is put back.** A change to the system (graphics card curves, monitor values over
  DDC/CI, camera controls, HDR) goes through the safety journal, so the guardian process restores it even after a
  crash. Look at how `AppSafety` and the existing restorers do it.
- **On-screen text is short.** A label says what is true in every state; explanations go in a tooltip or an (i) icon;
  a button sits next to what it acts on.
- **Comments say why**, in plain sentences.
- Tests use xUnit v3 and AwesomeAssertions; the simulator (`SoftCal.Instruments.Simulated`) stands in for a display
  and a camera.

## Pictures and the privacy policy

The README's pictures are in `images/`. `docs/privacy.html` is the phone apps' privacy policy: Google Play and the App
Store require one at a public address, so GitHub Pages publishes it from `docs/`.

## License

By contributing, you agree that your contribution is released under the project's
[GNU General Public License v3](LICENSE).
