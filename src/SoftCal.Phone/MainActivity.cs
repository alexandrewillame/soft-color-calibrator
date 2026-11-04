using System.Globalization;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Text.Style;
using Android.Util;
using Android.Views;
using Android.Widget;
using SoftCal.Remote;

namespace SoftCal.Phone;

/// <summary>
/// SoftCal Camera's one screen: what the camera sees (with the area SoftCal measures outlined), what is being sent to
/// the PC and when, the code to type on the PC, whether SoftCal is connected, and the phone's address for typing it by
/// hand. Behind it the camera, the link's server and the network announcement run while the app is open (in front:
/// Android lends the camera only to the app on screen, so the screen stays on, dimmed while measuring).
/// </summary>
[Activity(
    Label = "SoftCal Camera",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.Portrait,
    Theme = "@android:style/Theme.Material.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode)]
public sealed class MainActivity : Activity
{
    private const int CameraRequest = 1;

    /// <summary>docs/privacy.html, published by GitHub Pages: Google Play requires a privacy policy at a public address.</summary>
    private const string PrivacyPolicyUrl = "https://alexandrewillame.github.io/soft-color-calibrator/privacy.html";

    /// <summary>The project on GitHub, where SoftCal for Windows is downloaded: this app does nothing without it.</summary>
    private const string ProjectUrl = "https://github.com/alexandrewillame/soft-color-calibrator";

    private static readonly Color Background = Color.Rgb(16, 18, 22);
    private static readonly Color Dim = Color.Rgb(150, 160, 175);
    private static readonly Color Bright = Color.Rgb(240, 243, 247);
    private static readonly Color Good = Color.Rgb(110, 210, 140);
    private static readonly Color Warn = Color.Rgb(240, 190, 90);

    private readonly CancellationTokenSource _serving = new();
    private PhoneCamera? _camera;
    private PhoneServer? _server;
    private Network? _network;
    private Advertiser? _advertiser;
    private TurnWatcher? _turns;
    private Handler? _ui;
    private ISurfaceHolder? _holder;
    private MeasuredOverlay _overlay = null!;
    private TextView _sending = null!;
    private TextView _code = null!;
    private TextView _status = null!;
    private TextView _note = null!;
    private TextView _hint = null!;
    private TextView _getSoftCal = null!;
    private TextView _address = null!;
    private TextView _frames = null!;
    private TextView _problem = null!;
    private PhoneServerStatus? _shown;
    private bool _measuring;
    private bool _started;
    private (long Frames, long Statistics, long Pictures, long At) _before;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        SetContentView(BuildLayout());
        _ui = new Handler(Looper.MainLooper!);
        if (CheckSelfPermission(Android.Manifest.Permission.Camera) == Permission.Granted)
        {
            StartAll();
        }
        else
        {
            RequestPermissions([Android.Manifest.Permission.Camera], CameraRequest);
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != CameraRequest)
        {
            return;
        }

        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            StartAll();
        }
        else
        {
            ShowProblem("SoftCal Camera needs the camera to measure. Allow it in the phone's Settings › Apps › SoftCal Camera › Permissions, then open the app again.");
        }
    }

    protected override void OnDestroy()
    {
        _serving.Cancel();
        _advertiser?.Unregister();
        _server?.Dispose();
        _turns?.Disable();
        _camera?.Dispose();
        _network?.Dispose();
        _serving.Dispose();
        base.OnDestroy();
    }

    private static string Spaced(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;

    /// <summary>The phone as its owner knows it ("Google Pixel 7"): the name the PC lists.</summary>
    private static string PhoneName()
    {
        var maker = Build.Manufacturer ?? string.Empty;
        var model = Build.Model ?? "Android phone";
        return model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) || maker.Length == 0
            ? model
            : char.ToUpperInvariant(maker[0]) + maker[1..] + " " + model;
    }

    /// <summary>Starts the link and the announcement. The camera is not opened here: it opens when SoftCal needs a
    /// picture or a measurement, and closes again when it has not for a while (see <see cref="PhoneCamera.IdleAfter"/>).</summary>
    private void StartAll()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            var camera = _camera = PhoneCamera.Create(this);
            camera.Faulted += (_, fault) => RunOnUiThread(() => ShowProblem(fault.Message));
            camera.ModeChanged += (_, manual) => RunOnUiThread(() => SetMeasuring(manual));
            _turns = new TurnWatcher(this, camera);
            _turns.Enable();
            SizePreview();

            // The link runs even while the camera cannot open yet (another app has it): SoftCal then says so.
            _network = new Network(this);
            var version = PackageManager?.GetPackageInfo(PackageName!, 0)?.VersionName ?? "?";
            var server = _server = new PhoneServer(camera, new PhoneDescription(Build.Manufacturer ?? string.Empty, Build.Model ?? string.Empty, "SoftCal Camera " + version));
            server.StatusChanged += (_, status) => RunOnUiThread(() => Show(status));
            _ = Task.Run(() => server.RunAsync(_serving.Token));
            _advertiser = new Advertiser(this);
            _advertiser.Failed += (_, line) => RunOnUiThread(() => ShowProblem(line));
            _advertiser.Register(PhoneName(), server.Port);
            Show(new PhoneServerStatus(PhoneServerState.Waiting, server.Code, null, null));
            ShowCameraState(camera);
            Refresh();
        }
#pragma warning disable CA1031 // Shown on the screen: there is nothing else this app can do.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowProblem("SoftCal Camera could not start: " + ex.Message);
        }
    }

    /// <summary>The preview's surface takes the camera's preview size; once it has it, the camera draws on it.</summary>
    private void SizePreview()
    {
        if (_camera is not { } camera || _holder is not { } holder)
        {
            return;
        }

        holder.SetFixedSize(camera.PreviewSize.Width, camera.PreviewSize.Height);
        var frame = holder.SurfaceFrame;
        if (frame is not null && frame.Width() == camera.PreviewSize.Width && frame.Height() == camera.PreviewSize.Height && holder.Surface is { IsValid: true } surface)
        {
            camera.AttachPreview(surface);
        }
    }

    private void OnPreviewChanged(ISurfaceHolder holder, int width, int height)
    {
        _holder = holder;
        if (_camera is { } camera && width == camera.PreviewSize.Width && height == camera.PreviewSize.Height && holder.Surface is { IsValid: true } surface)
        {
            camera.AttachPreview(surface);
        }
    }

    /// <summary>
    /// Every second: where the PC can reach the phone (Wi-Fi may come later), whether the camera delivers frames
    /// (another app may take it), what is being sent to SoftCal, and the outline of the area it measures.
    /// </summary>
    private void Refresh()
    {
        if (_network is null || _server is not { } server || IsDestroyed)
        {
            return;
        }

        var addresses = _network.LocalAddresses();
        _address.Text = addresses.Count == 0
            ? "Not on Wi-Fi: connect the phone to the same network as the PC."
            : string.Create(CultureInfo.InvariantCulture, $"This phone: {string.Join(", ", addresses)}  ·  port {server.Port}");
        _address.SetTextColor(addresses.Count == 0 ? Warn : Dim);

        var now = (Frames: _camera?.FramesSeen ?? 0, Statistics: server.FramesSent, Pictures: server.PicturesSent, At: SystemClock.ElapsedRealtime());
        if (_before.At > 0)
        {
            var seconds = Math.Max(0.001, (now.At - _before.At) / 1000.0);
            ShowRates((now.Frames - _before.Frames) / seconds, (now.Statistics - _before.Statistics) / seconds, (now.Pictures - _before.Pictures) / seconds);
        }

        _before = now;
        _overlay.Invalidate();
        _ui?.PostDelayed(Refresh, 1000);
    }

    /// <summary>The camera's frame rate, and what goes to SoftCal: the measured area's average color (one small message
    /// per frame, while it measures) and gray pictures (while it shows the camera's view). Nothing else leaves the
    /// phone.</summary>
    private void ShowRates(double frames, double statistics, double pictures)
    {
        if (_camera is { } camera)
        {
            var flowing = camera.IsOpen && frames >= 0.5;
            _frames.Text = !camera.IsOpen ? "Camera: off (it starts when SoftCal needs it)"
                : flowing ? string.Create(CultureInfo.InvariantCulture, $"Camera: on, {frames:0} frames a second")
                : "Camera: no frames";
            _frames.SetTextColor(!camera.IsOpen || flowing ? Dim : Warn);
        }

        string text;
        if (_shown is not { State: PhoneServerState.Connected })
        {
            text = "Sending nothing: SoftCal is not connected.";
        }
        else if (statistics >= 0.5)
        {
            var source = _camera is { MeasuresRaw: true } ? " (from the sensor's raw data, before any processing)" : string.Empty;
            text = string.Create(CultureInfo.InvariantCulture, $"Sending to SoftCal: the average color of the outlined area{source}, {statistics:0} times a second (no pictures).");
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
        _sending.SetTextColor(statistics >= 0.5 || pictures >= 0.2 ? Good : Dim);
    }

    private void Show(PhoneServerStatus status)
    {
        _shown = status;
        _code.Text = Spaced(status.Code);
        if (status.State == PhoneServerState.Connected)
        {
            _status.Text = _measuring ? "Measuring: keep the phone still." : $"Connected to {status.Peer}";
            _status.SetTextColor(Good);
            _hint.Text = "Place the phone as SoftCal shows: its back camera, in its black tube, against the outlined area.";
        }
        else
        {
            SetMeasuring(false);
            _status.Text = "Waiting for SoftCal on the PC…";
            _status.SetTextColor(Bright);
            _hint.Text = "In SoftCal on the PC, choose Phone in the camera step, then type this code.";
        }

        // Where to get SoftCal, until a PC connects (then it is there).
        _getSoftCal.Visibility = status.State == PhoneServerState.Connected ? ViewStates.Gone : ViewStates.Visible;

        // What just happened (SoftCal disconnected, a new code after wrong ones), under the state.
        _note.Text = status.Message ?? string.Empty;
        _note.Visibility = status.Message is null ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>The screen dims while the camera is locked for measuring (less heat), the preview still visible.</summary>
    private void SetMeasuring(bool measuring)
    {
        if (_measuring == measuring)
        {
            return;
        }

        _measuring = measuring;
        var attributes = Window!.Attributes!;
        attributes.ScreenBrightness = measuring ? 0.15f : WindowManagerLayoutParams.BrightnessOverrideNone;
        Window.Attributes = attributes;
        if (_shown is { State: PhoneServerState.Connected } shown)
        {
            Show(shown);
        }
    }

    /// <summary>The camera is open: nothing to say, unless it cannot measure.</summary>
    private void ShowCameraState(PhoneCamera camera) => ShowProblem(camera.ManualSensor
        ? null
        : "This phone's camera does not let apps set its exposure: SoftCal can see through it but cannot measure with it.");

    private void ShowProblem(string? line)
    {
        _problem.Text = line ?? string.Empty;
        _problem.Visibility = line is null ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>A page in the phone's browser: the privacy policy, or the project where SoftCal is downloaded.</summary>
    private void Open(string url)
    {
        try
        {
            StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url)));
        }
        catch (ActivityNotFoundException)
        {
            ShowProblem("No browser to open " + url);
        }
    }

    private View BuildLayout()
    {
        var page = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        page.SetPadding(Dp(24), Dp(20), Dp(24), Dp(20));

        // The app's name, and its privacy policy (Google Play asks for a link to it in the app as well).
        var title = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        title.SetGravity(GravityFlags.CenterVertical);
        title.AddView(Label("SoftCal Camera", 18, Dim), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var privacy = Label("Privacy policy", 14, Dim);
        privacy.PaintFlags |= PaintFlags.UnderlineText;
        privacy.SetPadding(Dp(12), Dp(10), 0, Dp(10));
        privacy.Click += (_, _) => Open(PrivacyPolicyUrl);
        title.AddView(privacy);
        page.AddView(title);
        page.AddView(Gap(2));

        // What the camera sees, 3:4 as the phone stands, about a third of the screen tall; the area SoftCal measures is
        // outlined over it.
        var height = (int)((Resources?.DisplayMetrics?.HeightPixels ?? 2000) * 0.34);
        var preview = new FrameLayout(this);
        preview.SetBackgroundColor(Color.Black);
        preview.LayoutParameters = new LinearLayout.LayoutParams(height * 3 / 4, height) { Gravity = GravityFlags.CenterHorizontal };
        var surface = new SurfaceView(this);
        surface.Holder!.AddCallback(new PreviewCallbacks(this));
        preview.AddView(surface, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _overlay = new MeasuredOverlay(this, () => _camera);
        preview.AddView(_overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        page.AddView(preview);
        page.AddView(Gap(8));
        _sending = Label("Sending nothing: SoftCal is not connected.", 15, Dim);
        page.AddView(_sending);
        page.AddView(Gap(18));

        page.AddView(Label("Code", 14, Dim));
        _code = Label("––– –––", 48, Bright);
        _code.Typeface = Typeface.Monospace;
        _code.LetterSpacing = 0.06f;
        page.AddView(_code);
        _hint = Label(string.Empty, 16, Dim);
        page.AddView(_hint);

        // Whoever installs this app first learns where SoftCal for the PC is: the address to type on the PC, which also
        // opens in the phone's browser. Until a PC connects.
        var address = ProjectUrl["https://".Length..];
        var line = new SpannableString($"Get SoftCal for Windows, free: {address}");
        line.SetSpan(new UnderlineSpan(), line.Length() - address.Length, line.Length(), SpanTypes.ExclusiveExclusive);
        line.SetSpan(new ForegroundColorSpan(Bright), line.Length() - address.Length, line.Length(), SpanTypes.ExclusiveExclusive);
        _getSoftCal = Label(string.Empty, 15, Dim);
        _getSoftCal.TextFormatted = line;
        _getSoftCal.SetPadding(0, Dp(6), 0, 0);
        _getSoftCal.Click += (_, _) => Open(ProjectUrl);
        page.AddView(_getSoftCal);
        page.AddView(Gap(16));
        _status = Label("Starting the camera…", 20, Bright);
        page.AddView(_status);
        _note = Label(string.Empty, 15, Dim);
        _note.Visibility = ViewStates.Gone;
        page.AddView(_note);
        page.AddView(Gap(10));
        _address = Label(string.Empty, 14, Dim);
        page.AddView(_address);
        _frames = Label(string.Empty, 14, Dim);
        page.AddView(_frames);
        page.AddView(Gap(10));
        _problem = Label(string.Empty, 15, Warn);
        _problem.Visibility = ViewStates.Gone;
        page.AddView(_problem);

        // Edge to edge (Android 15 and later): the system bars' room is kept free around the page.
        var root = new FrameLayout(this);
        root.SetBackgroundColor(Background);
        root.SetFitsSystemWindows(true);
        root.AddView(page);
        return root;
    }

    private TextView Label(string text, float sp, Color color)
    {
        var label = new TextView(this) { Text = text };
        label.SetTextSize(ComplexUnitType.Sp, sp);
        label.SetTextColor(color);
        return label;
    }

    private Space Gap(int dp)
    {
        var space = new Space(this);
        space.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(dp));
        return space;
    }

    private int Dp(float dp) => (int)Math.Round(dp * (Resources?.DisplayMetrics?.Density ?? 2));

    /// <summary>The preview's surface coming and going (the app leaving the screen and coming back).</summary>
    private sealed class PreviewCallbacks(MainActivity owner) : Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder)
        {
            owner._holder = holder;
            owner.SizePreview();
        }

        public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) => owner.OnPreviewChanged(holder, width, height);

        public void SurfaceDestroyed(ISurfaceHolder holder)
        {
            owner._holder = null;
            owner._camera?.DetachPreview();
        }
    }

    /// <summary>Outlines, over the preview, the area SoftCal measures while it measures.</summary>
    private sealed class MeasuredOverlay(Context context, Func<PhoneCamera?> camera) : View(context)
    {
        private readonly Paint _line = new() { Color = Warn, StrokeWidth = 3 * (context.Resources?.DisplayMetrics?.Density ?? 2) };
        private readonly Paint _text = new() { Color = Warn, TextSize = TypedValue.ApplyDimension(ComplexUnitType.Sp, 13, context.Resources?.DisplayMetrics), AntiAlias = true };

        private readonly Paint _off = new() { Color = Dim, TextSize = TypedValue.ApplyDimension(ComplexUnitType.Sp, 14, context.Resources?.DisplayMetrics), AntiAlias = true, TextAlign = Paint.Align.Center };

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            if (camera() is { IsOpen: false })
            {
                canvas.DrawText("Camera off", Width / 2f, (Height / 2f) - _off.TextSize, _off);
                canvas.DrawText("it starts when SoftCal needs it", Width / 2f, (Height / 2f) + (_off.TextSize * 0.6f), _off);
                return;
            }

            if (camera() is not { Measured: { } measured } phone)
            {
                return;
            }

            // The region is given on the picture SoftCal sees (turned the way the phone stands); the preview is turned
            // by the sensor's mounting only.
            var region = Yuv420Statistics.Turn(measured.Region, measured.Rotation, phone.SensorOrientation);
            var (w, h) = (Width, Height);
            var left = (float)((region.CenterX - (region.Width / 2)) * w);
            var top = (float)((region.CenterY - (region.Height / 2)) * h);
            var right = (float)((region.CenterX + (region.Width / 2)) * w);
            var bottom = (float)((region.CenterY + (region.Height / 2)) * h);
            _line.SetStyle(Paint.Style.Stroke);
            canvas.DrawRect(left, top, right, bottom, _line);
            canvas.DrawText("measured", left, Math.Max(_text.TextSize, top - (_text.TextSize / 3)), _text);
        }
    }

    /// <summary>Follows how the phone stands (its orientation sensor), so pictures are turned with up as up.</summary>
    private sealed class TurnWatcher(Context context, PhoneCamera camera) : OrientationEventListener(context)
    {
        public override void OnOrientationChanged(int orientation)
        {
            if (orientation == OrientationUnknown)
            {
                return;
            }

            var nearest = (orientation + 45) / 90 * 90 % 360;
            if (nearest == camera.DeviceRotation)
            {
                return;
            }

            // Only well into the new quarter turn, so a phone standing near 45° does not flip back and forth.
            var away = Math.Abs(((orientation - nearest + 540) % 360) - 180);
            if (away <= 30)
            {
                camera.DeviceRotation = nearest;
            }
        }
    }
}
