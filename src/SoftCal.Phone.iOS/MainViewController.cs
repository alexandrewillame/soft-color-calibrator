using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AVFoundation;
using CoreMotion;
using SoftCal.Remote;

namespace SoftCal.Phone.iOS;

/// <summary>
/// SoftCal Camera's one screen, as on Android: what the camera sees (with the area SoftCal measures outlined), what is
/// being sent to the PC and when, the code to type on the PC, whether SoftCal is connected, and the device's address for
/// typing it by hand. Behind it the camera, the link's server and the network announcement run while the app is in front
/// (iOS lends the camera only to the app on screen, so the screen stays on, dimmed while measuring). When the app
/// leaves the screen they stop, and start again, with a new code, when it comes back.
/// </summary>
public sealed class MainViewController : UIViewController
{
    /// <summary>docs/privacy.html, published by GitHub Pages: the App Store requires a privacy policy at a public address.</summary>
    private const string PrivacyPolicyUrl = "https://alexandrewillame.github.io/soft-color-calibrator/privacy.html";

    /// <summary>The project on GitHub, where SoftCal for Windows is downloaded: this app does nothing without it.</summary>
    private const string ProjectUrl = "https://github.com/alexandrewillame/soft-color-calibrator";

    private static readonly UIColor Background = UIColor.FromRGB(16, 18, 22);
    private static readonly UIColor Dim = UIColor.FromRGB(150, 160, 175);
    private static readonly UIColor Bright = UIColor.FromRGB(240, 243, 247);
    private static readonly UIColor Good = UIColor.FromRGB(110, 210, 140);
    private static readonly UIColor Warn = UIColor.FromRGB(240, 190, 90);

    /// <summary>"phone" or "iPad", in the screen's sentences.</summary>
    private static readonly string Device = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad ? "iPad" : "phone";

    private readonly CancellationTokenSource _serving = new();
    private readonly Advertiser _advertiser = new();
    private PhoneCamera? _camera;
    private PhoneDescription? _phone;
    private PhoneServer? _server;
    private CancellationTokenSource? _run;
    private TurnWatcher? _turns;
    private NSTimer? _refresh;
    private PreviewBox _previewBox = null!;
    private MeasuredOverlay _overlay = null!;
    private UILabel _sending = null!;
    private UILabel _code = null!;
    private UILabel _status = null!;
    private UILabel _note = null!;
    private UILabel _hint = null!;
    private UILabel _getSoftCal = null!;
    private UILabel _address = null!;
    private UILabel _frames = null!;
    private UILabel _problem = null!;
    private UIButton _settings = null!;
    private PhoneServerStatus? _shown;
    private bool _measuring;
    private bool _started;
    private bool _active;
    private NFloat? _brightness;
    private (long Frames, long Statistics, long Pictures, long At) _before;

    public override UIStatusBarStyle PreferredStatusBarStyle() => UIStatusBarStyle.LightContent;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        BuildLayout();
        switch (AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video))
        {
            case AVAuthorizationStatus.Authorized:
                StartAll();
                break;
            case AVAuthorizationStatus.NotDetermined:
                _ = AskForCameraAsync();
                break;
            default:
                ShowCameraRefused();
                break;
        }
    }

    /// <summary>In front and active: the screen stays on (and dims again if a measurement runs).</summary>
    public void Activated()
    {
        _active = true;
        UIApplication.SharedApplication.IdleTimerDisabled = true;
        DimScreen(_measuring);
    }

    /// <summary>Covered or leaving (a call, the app switcher): the user's brightness back, the screen may sleep.</summary>
    public void Deactivated()
    {
        _active = false;
        DimScreen(false);
        UIApplication.SharedApplication.IdleTimerDisabled = false;
    }

    /// <summary>Back on screen: a new server and announcement (iOS took the old ones' sockets away meanwhile).</summary>
    public void EnteringForeground()
    {
        if (_started)
        {
            _turns?.Enable();
            StartServing();
        }
    }

    /// <summary>Off screen: iOS stops the camera, and soon the app; the PC is let go of now, cleanly.</summary>
    public void EnteredBackground()
    {
        StopServing();
        _turns?.Disable();
        _camera?.Suspend();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh?.Invalidate();
            _serving.Cancel();
            StopServing();
            _turns?.Dispose();
            _camera?.Dispose();
            _serving.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string Spaced(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;

    /// <summary>The device as its owner knows it ("Apple iPhone 15 Pro"): the name the PC lists, as on Android.</summary>
    private static string PhoneName(PhoneDescription phone) => $"{phone.Manufacturer} {phone.Model}";

    private async Task AskForCameraAsync()
    {
        var granted = await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            if (granted)
            {
                StartAll();
            }
            else
            {
                ShowCameraRefused();
            }
        });
    }

    private void ShowCameraRefused()
    {
        ShowProblem("SoftCal Camera needs the camera to measure: allow it in Settings, then open the app again.");
        _settings.Hidden = false;
    }

    /// <summary>Starts the camera object, the link and the announcement. The camera does not run yet: it starts when
    /// SoftCal needs a picture or a measurement, and stops when it has not for a while (see
    /// <see cref="PhoneCamera.IdleAfter"/>).</summary>
    private void StartAll()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            var camera = _camera = PhoneCamera.Create();
            camera.Faulted += (_, fault) => BeginInvokeOnMainThread(() => ShowProblem(fault.Message));
            camera.ModeChanged += (_, manual) => BeginInvokeOnMainThread(() => SetMeasuring(manual));
            _turns = new TurnWatcher(camera);
            _turns.Enable();
            AttachPreview(camera);

            var version = NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleShortVersionString")?.ToString() ?? "?";
            _phone = new PhoneDescription("Apple", DeviceName.Model(), "SoftCal Camera " + version);
            _advertiser.Failed += (_, line) => BeginInvokeOnMainThread(() => ShowProblem(line));
            StartServing();
            ShowCameraState(camera);
            _refresh = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromSeconds(1), _ => Refresh());
            Refresh();
        }
#pragma warning disable CA1031 // Shown on the screen: there is nothing else this app can do.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowProblem("SoftCal Camera could not start: " + ex.Message);
        }
    }

    /// <summary>The link's server and its announcement. The link runs even while the camera cannot start (another app
    /// has it): SoftCal then says so.</summary>
    private void StartServing()
    {
        if (_camera is not { } camera || _phone is not { } phone || _run is not null)
        {
            return;
        }

        PhoneServer server;
        try
        {
            server = new PhoneServer(camera, phone);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            ShowProblem("The link to the PC could not start: " + ex.Message);
            return;
        }

        var run = _run = CancellationTokenSource.CreateLinkedTokenSource(_serving.Token);
        _server = server;
        server.StatusChanged += (_, status) => BeginInvokeOnMainThread(() =>
        {
            // A server stopped meanwhile says it lost its PC after the new one showed its code.
            if (ReferenceEquals(_server, server))
            {
                Show(status);
            }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(run.Token).ConfigureAwait(false);
            }
            catch (Exception) when (run.IsCancellationRequested)
            {
                // Stopped while it listened.
            }
        });
        _advertiser.Register(PhoneName(phone), server.Port);
        _before = default;
        Show(new PhoneServerStatus(PhoneServerState.Waiting, server.Code, null, null));
    }

    private void StopServing()
    {
        if (_run is not { } run)
        {
            return;
        }

        _run = null;
        run.Cancel();
        _advertiser.Unregister();
        _server?.Dispose();
    }

    /// <summary>What the camera sees, over the whole preview box (turned by the sensor's mounting only, as on Android).</summary>
    private void AttachPreview(PhoneCamera camera)
    {
        var preview = _previewBox.Preview;
        preview.VideoGravity = AVLayerVideoGravity.ResizeAspect;
        preview.Session = camera.Session;
        if (preview.Connection is { } connection)
        {
            if (OperatingSystem.IsIOSVersionAtLeast(17))
            {
                if (connection.IsVideoRotationAngleSupported(90))
                {
                    connection.VideoRotationAngle = 90;
                }
            }
            else if (connection.SupportsVideoOrientation)
            {
                connection.VideoOrientation = AVCaptureVideoOrientation.Portrait;
            }
        }
    }

    /// <summary>
    /// Every second: where the PC can reach the device (Wi-Fi may come later), whether the camera delivers frames
    /// (another app may take it), what is being sent to SoftCal, and the outline of the area it measures.
    /// </summary>
    private void Refresh()
    {
        if (_server is not { } server)
        {
            return;
        }

        var addresses = Network.LocalAddresses();
        _address.Text = addresses.Count == 0
            ? $"Not on Wi-Fi: connect the {Device} to the same network as the PC."
            : string.Create(CultureInfo.InvariantCulture, $"This {Device}: {string.Join(", ", addresses)}  ·  port {server.Port}");
        _address.TextColor = addresses.Count == 0 ? Warn : Dim;

        var now = (Frames: _camera?.FramesSeen ?? 0, Statistics: server.FramesSent, Pictures: server.PicturesSent, At: Environment.TickCount64);
        if (_before.At > 0 && now.Statistics >= _before.Statistics)
        {
            var seconds = Math.Max(0.001, (now.At - _before.At) / 1000.0);
            ShowRates((now.Frames - _before.Frames) / seconds, (now.Statistics - _before.Statistics) / seconds, (now.Pictures - _before.Pictures) / seconds);
        }

        _before = now;
        _overlay.SetNeedsDisplay();
    }

    /// <summary>The camera's frame rate, and what goes to SoftCal: the measured area's average color (one small message
    /// per frame, while it measures) and gray pictures (while it shows the camera's view). Nothing else leaves the
    /// device.</summary>
    private void ShowRates(double frames, double statistics, double pictures)
    {
        if (_camera is { } camera)
        {
            var flowing = camera.IsOpen && frames >= 0.5;
            _frames.Text = !camera.IsOpen ? "Camera: off (it starts when SoftCal needs it)"
                : flowing ? string.Create(CultureInfo.InvariantCulture, $"Camera: on, {frames:0} frames a second")
                : "Camera: no frames";
            _frames.TextColor = !camera.IsOpen || flowing ? Dim : Warn;
        }

        string text;
        if (_shown is not { State: PhoneServerState.Connected })
        {
            text = "Sending nothing: SoftCal is not connected.";
        }
        else if (statistics >= 0.5)
        {
            text = string.Create(CultureInfo.InvariantCulture, $"Sending to SoftCal: the average color of the outlined area, {statistics:0} times a second (no pictures).");
            if (pictures >= 0.2)
            {
                text += " And the camera's view, in gray.";
            }
        }
        else if (pictures >= 0.2)
        {
            text = string.Create(CultureInfo.InvariantCulture, $"Sending to SoftCal: the camera's view, in gray, {pictures:0.#} pictures a second.");
        }
        else
        {
            text = "Connected: sending nothing right now.";
        }

        _sending.Text = text;
        _sending.TextColor = statistics >= 0.5 || pictures >= 0.2 ? Good : Dim;
    }

    private void Show(PhoneServerStatus status)
    {
        _shown = status;
        _code.AttributedText = new NSAttributedString(Spaced(status.Code), new UIStringAttributes { Font = _code.Font, ForegroundColor = Bright, KerningAdjustment = 2.9f });
        if (status.State == PhoneServerState.Connected)
        {
            _status.Text = _measuring ? $"Measuring: keep the {Device} still." : $"Connected to {status.Peer}";
            _status.TextColor = Good;
            _hint.Text = $"Place the {Device} as SoftCal shows: its back camera, in its black tube, against the outlined area.";
        }
        else
        {
            SetMeasuring(false);
            _status.Text = "Waiting for SoftCal on the PC…";
            _status.TextColor = Bright;
            _hint.Text = "In SoftCal on the PC, choose Phone in the camera step, then type this code.";
        }

        // Where to get SoftCal, until a PC connects (then it is there).
        _getSoftCal.Hidden = status.State == PhoneServerState.Connected;

        // What just happened (SoftCal disconnected, a new code after wrong ones), under the state.
        _note.Text = status.Message ?? string.Empty;
        _note.Hidden = status.Message is null;
    }

    /// <summary>The screen dims while the camera is locked for measuring (less heat), the preview still visible.</summary>
    private void SetMeasuring(bool measuring)
    {
        if (_measuring == measuring)
        {
            return;
        }

        _measuring = measuring;
        DimScreen(measuring && _active);
        if (_shown is { State: PhoneServerState.Connected } shown)
        {
            Show(shown);
        }
    }

    /// <summary>Dims the screen, or gives the user's brightness back (iOS keeps an app's brightness after it leaves).</summary>
    private void DimScreen(bool dim)
    {
        if (View?.Window?.WindowScene?.Screen is not { } screen)
        {
            return;
        }

        if (dim)
        {
            _brightness ??= screen.Brightness;
            screen.Brightness = 0.15f;
        }
        else if (_brightness is { } brightness)
        {
            screen.Brightness = brightness;
            _brightness = null;
        }
    }

    /// <summary>The camera is ready: nothing to say, unless it cannot measure.</summary>
    private void ShowCameraState(PhoneCamera camera) => ShowProblem(camera.ManualSensor
        ? null
        : $"This {Device}'s camera does not let apps set its exposure: SoftCal can see through it but cannot measure with it.");

    private void ShowProblem(string? line)
    {
        _problem.Text = line ?? string.Empty;
        _problem.Hidden = line is null;
    }

    /// <summary>The privacy policy, in the browser.</summary>
    private void OpenPrivacyPolicy() => Open(PrivacyPolicyUrl);

    /// <summary>This app's page in Settings, where its camera is allowed.</summary>
    private void OpenSettings() => Open(UIApplication.OpenSettingsUrlString);

    private static void Open(string url)
    {
        if (NSUrl.FromString(url) is { } link)
        {
            UIApplication.SharedApplication.OpenUrl(link, new UIApplicationOpenUrlOptions(), null);
        }
    }

    private void BuildLayout()
    {
        var root = View!;
        root.BackgroundColor = Background;
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false };
        var page = new UIStackView { Axis = UILayoutConstraintAxis.Vertical, Alignment = UIStackViewAlignment.Fill, TranslatesAutoresizingMaskIntoConstraints = false };
        root.AddSubview(scroll);
        scroll.AddSubview(page);

        // The system's bars and the home indicator keep their room (the safe area); the page scrolls on small screens.
        var safe = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            scroll.TopAnchor.ConstraintEqualTo(safe.TopAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(safe.BottomAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(safe.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(safe.TrailingAnchor),
            page.TopAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TopAnchor, 20),
            page.BottomAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.BottomAnchor, -20),
            page.LeadingAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.LeadingAnchor, 24),
            page.TrailingAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.TrailingAnchor, -24),
        ]);

        // The app's name, and its privacy policy (the App Store asks for a link to it in the app as well).
        var title = new UIStackView { Axis = UILayoutConstraintAxis.Horizontal, Alignment = UIStackViewAlignment.Center };
        title.AddArrangedSubview(Label("SoftCal Camera", 18, Dim));
        var privacy = new UIButton(UIButtonType.System);
        privacy.SetAttributedTitle(new NSAttributedString("Privacy policy", new UIStringAttributes { Font = UIFont.SystemFontOfSize(14), ForegroundColor = Dim, UnderlineStyle = NSUnderlineStyle.Single }), UIControlState.Normal);
        privacy.SetContentHuggingPriority((float)UILayoutPriority.Required, UILayoutConstraintAxis.Horizontal);
        privacy.TouchUpInside += (_, _) => OpenPrivacyPolicy();
        title.AddArrangedSubview(privacy);
        page.AddArrangedSubview(title);
        page.SetCustomSpacing(4, title);

        // What the camera sees, 3:4 as the device stands, about a third of the screen tall; the area SoftCal measures is
        // outlined over it. In the page before its constraints are activated: the height follows the screen's, and a
        // constraint between views not yet in one hierarchy throws.
        var previewRow = new UIView();
        _previewBox = new PreviewBox { BackgroundColor = UIColor.Black, ClipsToBounds = true, TranslatesAutoresizingMaskIntoConstraints = false };
        previewRow.AddSubview(_previewBox);
        _overlay = new MeasuredOverlay(() => _camera) { TranslatesAutoresizingMaskIntoConstraints = false };
        _previewBox.AddSubview(_overlay);
        page.AddArrangedSubview(previewRow);
        var tall = _previewBox.HeightAnchor.ConstraintEqualTo(root.HeightAnchor, 0.34f);
        tall.Priority = (float)UILayoutPriority.DefaultHigh;
        NSLayoutConstraint.ActivateConstraints(
        [
            _previewBox.TopAnchor.ConstraintEqualTo(previewRow.TopAnchor),
            _previewBox.BottomAnchor.ConstraintEqualTo(previewRow.BottomAnchor),
            _previewBox.CenterXAnchor.ConstraintEqualTo(previewRow.CenterXAnchor),
            _previewBox.WidthAnchor.ConstraintEqualTo(_previewBox.HeightAnchor, 0.75f),
            _previewBox.WidthAnchor.ConstraintLessThanOrEqualTo(previewRow.WidthAnchor),
            tall,
            _overlay.TopAnchor.ConstraintEqualTo(_previewBox.TopAnchor),
            _overlay.BottomAnchor.ConstraintEqualTo(_previewBox.BottomAnchor),
            _overlay.LeadingAnchor.ConstraintEqualTo(_previewBox.LeadingAnchor),
            _overlay.TrailingAnchor.ConstraintEqualTo(_previewBox.TrailingAnchor),
        ]);
        page.SetCustomSpacing(8, previewRow);
        _sending = Label("Sending nothing: SoftCal is not connected.", 15, Dim);
        page.AddArrangedSubview(_sending);
        page.SetCustomSpacing(18, _sending);

        var codeTitle = Label("Code", 14, Dim);
        page.AddArrangedSubview(codeTitle);
        _code = new UILabel { Font = UIFont.GetMonospacedSystemFont(48, UIFontWeight.Regular)!, TextColor = Bright, Text = "––– –––", AdjustsFontSizeToFitWidth = true };
        page.AddArrangedSubview(_code);
        _hint = Label(string.Empty, 16, Dim);

        // Whoever installs this app first learns where SoftCal for the PC is: the address to type on the PC, which also
        // opens in the browser. Until a PC connects; the two lines are grouped so the space below stays the same.
        _getSoftCal = Label(string.Empty, 15, Dim);
        var address = ProjectUrl["https://".Length..];
        var line = new NSMutableAttributedString($"Get SoftCal for Windows, free: {address}", new UIStringAttributes { Font = _getSoftCal.Font, ForegroundColor = Dim });
        line.AddAttributes(new UIStringAttributes { ForegroundColor = Bright, UnderlineStyle = NSUnderlineStyle.Single }, new NSRange(line.Length - address.Length, address.Length));
        _getSoftCal.AttributedText = line;
        _getSoftCal.UserInteractionEnabled = true;
        _getSoftCal.AccessibilityTraits = UIAccessibilityTrait.Link;
        _getSoftCal.AddGestureRecognizer(new UITapGestureRecognizer(() => Open(ProjectUrl)));
        var help = new UIStackView([_hint, _getSoftCal]) { Axis = UILayoutConstraintAxis.Vertical, Spacing = 6 };
        page.AddArrangedSubview(help);
        page.SetCustomSpacing(16, help);
        _status = Label("Starting the camera…", 20, Bright);
        page.AddArrangedSubview(_status);
        _note = Label(string.Empty, 15, Dim);
        _note.Hidden = true;
        page.AddArrangedSubview(_note);
        page.SetCustomSpacing(10, _note);
        page.SetCustomSpacing(10, _status);
        _address = Label(string.Empty, 14, Dim);
        page.AddArrangedSubview(_address);
        _frames = Label(string.Empty, 14, Dim);
        page.AddArrangedSubview(_frames);
        page.SetCustomSpacing(10, _frames);
        _problem = Label(string.Empty, 15, Warn);
        _problem.Hidden = true;
        page.AddArrangedSubview(_problem);

        // Beside the line that needs it: only when the camera was refused.
        _settings = new UIButton(UIButtonType.System) { Hidden = true, HorizontalAlignment = UIControlContentHorizontalAlignment.Leading };
        _settings.SetTitle("Open Settings", UIControlState.Normal);
        _settings.SetTitleColor(Warn, UIControlState.Normal);
        _settings.TouchUpInside += (_, _) => OpenSettings();
        page.AddArrangedSubview(_settings);
    }

    /// <summary>A label of <paramref name="points"/> at the default text size, larger with the user's larger text.</summary>
    private static UILabel Label(string text, float points, UIColor color) => new()
    {
        Text = text,
        TextColor = color,
        Font = UIFontMetrics.DefaultMetrics.GetScaledFont(UIFont.SystemFontOfSize(points)!),
        AdjustsFontForContentSizeCategory = true,
        Lines = 0,
    };

    /// <summary>The preview box: its own layer is the camera's preview, so it always fills the box (a layer added inside a
    /// view keeps the frame it was given), as in Apple's camera samples.</summary>
    private sealed class PreviewBox : UIView
    {
        [Export("layerClass")]
        public static ObjCRuntime.Class LayerClass() => new(typeof(AVCaptureVideoPreviewLayer));

        public AVCaptureVideoPreviewLayer Preview => (AVCaptureVideoPreviewLayer)Layer;
    }

    /// <summary>Outlines, over the preview, the area SoftCal measures while it measures.</summary>
    private sealed class MeasuredOverlay : UIView
    {
        private readonly Func<PhoneCamera?> _camera;

        public MeasuredOverlay(Func<PhoneCamera?> camera)
        {
            _camera = camera;
            BackgroundColor = UIColor.Clear;
            Opaque = false;
            ContentMode = UIViewContentMode.Redraw;
            UserInteractionEnabled = false;
        }

        public override void Draw(CGRect rect)
        {
            base.Draw(rect);
            double w = Bounds.Width, h = Bounds.Height;
            if (_camera() is { IsOpen: false })
            {
                using var centered = new NSMutableParagraphStyle { Alignment = UITextAlignment.Center };
                var off = new UIStringAttributes { Font = UIFont.SystemFontOfSize(14), ForegroundColor = Dim, ParagraphStyle = centered };
                using var text = new NSString("Camera off\nit starts when SoftCal needs it");
                text.DrawString(new CGRect(0, (h / 2) - 20, w, 44), off);
                return;
            }

            if (_camera() is not { Measured: { } measured } phone)
            {
                return;
            }

            // The region is given on the picture SoftCal sees (turned the way the device stands); the preview is turned
            // by the sensor's mounting only.
            var region = Yuv420Statistics.Turn(measured.Region, measured.Rotation, phone.SensorOrientation);
            var left = (region.CenterX - (region.Width / 2)) * w;
            var top = (region.CenterY - (region.Height / 2)) * h;
            using var outline = UIBezierPath.FromRect(new CGRect(left, top, region.Width * w, region.Height * h));
            outline.LineWidth = 3;
            Warn.SetStroke();
            outline.Stroke();
            using var label = new NSString("measured");
            label.DrawString(new CGPoint(left, Math.Max(0, top - 18)), new UIStringAttributes { Font = UIFont.SystemFontOfSize(13), ForegroundColor = Warn });
        }
    }

    /// <summary>Follows how the device stands (its accelerometer), so pictures are turned with up as up.</summary>
    private sealed class TurnWatcher(PhoneCamera camera) : IDisposable
    {
        private readonly CMMotionManager _motion = new() { AccelerometerUpdateInterval = 0.2 };

        public void Enable()
        {
            if (!_motion.AccelerometerAvailable || _motion.AccelerometerActive)
            {
                return;
            }

            _motion.StartAccelerometerUpdates(NSOperationQueue.MainQueue, (data, _) =>
            {
                if (data is not null)
                {
                    OnGravity(data.Acceleration.X, data.Acceleration.Y, data.Acceleration.Z);
                }
            });
        }

        public void Disable() => _motion.StopAccelerometerUpdates();

        public void Dispose()
        {
            Disable();
            _motion.Dispose();
        }

        /// <summary>At rest the accelerometer reads gravity, in the device's own axes (x to the right, y up).</summary>
        private void OnGravity(double x, double y, double z)
        {
            // Too flat to say which way is up, as Android's orientation listener (within about 27° of lying flat).
            if (4 * ((x * x) + (y * y)) < z * z)
            {
                return;
            }

            // Degrees clockwise from upright, as Android's: 90 with the left side up.
            var orientation = ((int)Math.Round(Math.Atan2(x, -y) * 180 / Math.PI) + 360) % 360;
            var nearest = (orientation + 45) / 90 * 90 % 360;
            if (nearest == camera.DeviceRotation)
            {
                return;
            }

            // Only well into the new quarter turn, so a device standing near 45° does not flip back and forth.
            var away = Math.Abs(((orientation - nearest + 540) % 360) - 180);
            if (away <= 30)
            {
                camera.DeviceRotation = nearest;
            }
        }
    }
}
