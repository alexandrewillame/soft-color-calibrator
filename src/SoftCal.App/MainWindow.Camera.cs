using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoftCal.App.Services;
using SoftCal.Core.Analysis;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.Instruments.Phone;
using SoftCal.Instruments.Webcam;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Patches;

namespace SoftCal.App;

/// <summary>Step 2: the camera's live picture of the aiming area (a webcam's, or a connected phone's), shown as soon as
/// the step opens.</summary>
public partial class MainWindow
{
    /// <summary>SoftCal's window cannot share the screen the camera rests on: the outlined area's window covers all of
    /// it, and the camera's view, the prompts and Next would be out of reach.</summary>
    private const string SecondScreenNeeded = "Connect a second screen: SoftCal's window moves there while the camera rests on this one.";

    private CancellationTokenSource? _preview;
    private Task _previewTask = Task.CompletedTask;

    /// <summary>The aiming area on the measured screen, from the camera step until a measurement covers the screen with
    /// its own window (or a step other than the camera's and the measurement's is shown): the screen stays dark between
    /// the two steps instead of showing the desktop.</summary>
    private D3DPatchWindow? _aimWindow;

    /// <summary>The camera chosen in step 2 (its stable id): chosen again when the step opens again.</summary>
    private string? _cameraId;
    private bool _listingCameras;

    private async void CameraList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_listingCameras || _step != CameraIndex)
        {
            return;
        }

        // Another camera: it must be placed, and ticked, again.
        if (SelectedCamera?.StableId != _cameraId)
        {
            _cameraId = SelectedCamera?.StableId;
            CameraReadyCheck.IsChecked = false;
        }

        await StopPreviewAsync();
        StartPreview();
    }

    /// <summary>Lists the USB cameras with the one chosen before still chosen; when it is gone, the first is chosen and
    /// must be placed again.</summary>
    private void ListCameras()
    {
        try
        {
            _cameras = Simulated ? [] : [.. WebcamDevice.Enumerate().Where(d => d.IsUsbHardware)];
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _cameras = [];
            PreviewStatus.Text = "The cameras could not be listed: " + ex.Message;
        }

        var index = 0;
        for (var i = 0; i < _cameras.Count; i++)
        {
            if (_cameras[i].StableId == _cameraId)
            {
                index = i;
                break;
            }
        }

        _listingCameras = true;
        try
        {
            CameraList.ItemsSource = Simulated ? new[] { "Simulator" } : _cameras.Select(c => c.FriendlyName).ToArray();
            CameraList.SelectedIndex = index;
        }
        finally
        {
            _listingCameras = false;
        }

        if (!Simulated && !UsePhone && SelectedCamera?.StableId != _cameraId)
        {
            _cameraId = SelectedCamera?.StableId;
            CameraReadyCheck.IsChecked = false;
        }

        ShowCameraKind();
    }

    /// <summary>Starts the live view (once; the step's whole time). A view still closing (the step was left a moment ago)
    /// is let finish first. Any failure is shown in the step, never thrown.</summary>
    private async void StartPreview()
    {
        if (_preview is { } current)
        {
            if (!current.IsCancellationRequested)
            {
                return;
            }

            await StopPreviewAsync();
            if (_preview is not null || _step != CameraIndex)
            {
                return;
            }
        }

        if (Simulated)
        {
            PreviewImage.Source = null;
            PreviewLevel.Clear();
            PreviewStatus.Text = "Simulator: there is no camera to show.";
            return;
        }

        // What shows the view: a webcam opened for it (closed after), or the connected phone (its link stays).
        Func<(ILumaImageSource Camera, IAsyncDisposable Owner)> open;
        string opening;
        if (UsePhone)
        {
            if (_phone is not { IsConnected: true } phone)
            {
                PreviewImage.Source = null;
                PreviewLevel.Clear();
                PreviewStatus.Text = "Connect the phone (on the right) to see its camera.";
                return;
            }

            open = () =>
            {
                var instrument = new PhoneInstrument(phone);
                return (instrument, instrument);
            };
            opening = "Waiting for the phone's picture…";
        }
        else
        {
            if (SelectedCamera is not { } camera)
            {
                PreviewImage.Source = null;
                PreviewLevel.Clear();
                PreviewStatus.Text = _cameras.Count == 0 ? "No USB camera found: plug one in, or choose Phone." : "Choose a camera.";
                return;
            }

            open = () =>
            {
                var webcam = WebcamInstrument.Open(camera);
                return (webcam, webcam);
            };
            opening = "Opening the camera…";
        }

        if (SelectedMonitor is not { } monitor)
        {
            PreviewStatus.Text = "Choose a display first.";
            return;
        }

        if (_monitors.Count < 2)
        {
            PreviewImage.Source = null;
            PreviewLevel.Clear();
            PreviewStatus.Text = SecondScreenNeeded;
            return;
        }

        // The outlined area covers the measured screen: SoftCal's window moves to the other one first.
        StepAside(null);
        var cts = new CancellationTokenSource();
        _preview = cts;
        PreviewStatus.Text = opening;
        var task = _previewTask = PreviewAsync(open, monitor, cts.Token);
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_preview, cts))
            {
                PreviewStatus.Text = "Camera view closed.";
            }
        }
#pragma warning disable CA1031 // An async void handler must never let an error crash the app; it is shown (and logged when unexpected).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (ex is not (COMException or InvalidOperationException or MeasurementException or TimeoutException))
            {
                AppPaths.WriteErrorLog(ex);
            }

            if (ReferenceEquals(_preview, cts))
            {
                PreviewStatus.Text = "The camera view stopped: " + ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_preview, cts))
            {
                _preview = null;
                PreviewImage.Source = null;
                PreviewLevel.Clear();
            }

            cts.Dispose();
        }
    }

    /// <summary>Stops the preview without waiting (leaving the step, closing the window).</summary>
    private void StopPreview() => _ = StopPreviewAsync();

    /// <summary>Stops the preview and waits until the camera and the target are released (before a measurement opens
    /// the camera again). Never throws: the preview's own loop reports what went wrong.</summary>
    private async Task StopPreviewAsync()
    {
        _preview?.Cancel();
        try
        {
            await _previewTask;
        }
#pragma warning disable CA1031 // Already reported by the preview loop; stopping must never throw.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>The aiming area on the measured screen (kept after the step), then the camera's view of it.</summary>
    private async Task PreviewAsync(Func<(ILumaImageSource Camera, IAsyncDisposable Owner)> open, DisplayMonitor monitor, CancellationToken cancellationToken)
    {
        await ShowAimAsync(monitor);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => RunPreviewAsync(open, cancellationToken), cancellationToken);
    }

    /// <summary>Shows the aiming area on <paramref name="monitor"/>, in the window kept for it (opened when there is
    /// none); it stays until <see cref="CloseAim"/>.</summary>
    private async Task ShowAimAsync(DisplayMonitor monitor)
    {
        if (_aimWindow is not { } window || window.DeviceName != monitor.DeviceName)
        {
            CloseAim();
            var opened = await Task.Run(() => D3DPatchWindow.Open(monitor.DeviceName));

            // Another step or display was chosen meanwhile, or another call opened one first: this one is not wanted.
            if (_aimWindow is not null || _step is not (CameraIndex or MeasureIndex) || SelectedMonitor?.DeviceName != monitor.DeviceName)
            {
                opened.Dispose();
                return;
            }

            window = _aimWindow = opened;
        }

        await window.ShowAsync(new PlacementTargetPatch(), CancellationToken.None);
    }

    /// <summary>Closes the aiming area's window (the measured screen shows the desktop again, or the window a
    /// measurement opened over it).</summary>
    private void CloseAim()
    {
        _aimWindow?.Dispose();
        _aimWindow = null;
    }

    /// <summary>Runs on a thread-pool (MTA) thread: a webcam's COM objects need it.</summary>
    private async Task RunPreviewAsync(Func<(ILumaImageSource Camera, IAsyncDisposable Owner)> open, CancellationToken cancellationToken)
    {
        var (camera, owner) = open();
        await using (owner.ConfigureAwait(false))
        {
            await camera.StartPreviewAsync(cancellationToken).ConfigureAwait(false);
            var whole = new RoiSpec(0.5, 0.5, 1, 1);
            while (!cancellationToken.IsCancellationRequested)
            {
                RoiImage image;
                try
                {
                    image = await camera.CaptureLumaImageAsync(whole, 1, 0, cancellationToken).ConfigureAwait(false);
                }
                catch (MeasurementException ex) when (camera is PhoneInstrument { Client.IsConnected: true } && !cancellationToken.IsCancellationRequested)
                {
                    // The phone's camera can come back (SoftCal Camera brought to the front again): the view waits for it.
                    await Dispatcher.InvokeAsync(() => ShowPreviewProblem(ex.Message));
                    await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var view = PlacementCheck.Analyze(image.Width, image.Height, image.Pixels);
                var bitmap = ToBitmap(image);
                await Dispatcher.InvokeAsync(() => ShowPreview(bitmap, view));
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The camera's picture for the eye: its levels stretched from the darkest to the brightest hundredth, so a
    /// dim one (a camera locked for measuring) still shows the grid. Only what is shown changes, never what is
    /// measured.</summary>
    private static BitmapSource ToBitmap(RoiImage image)
    {
        var histogram = new int[256];
        foreach (var value in image.Pixels)
        {
            histogram[(int)Math.Clamp(value, 0, 255)]++;
        }

        int Level(double fraction)
        {
            var count = 0;
            for (var level = 0; level < 256; level++)
            {
                count += histogram[level];
                if (count > fraction * image.Pixels.Count)
                {
                    return level;
                }
            }

            return 255;
        }

        var (low, high) = (Level(0.01), Level(0.99));
        var stretch = high - low >= 8;
        var pixels = new byte[image.Width * image.Height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)Math.Clamp(stretch ? (image.Pixels[i] - low) * 230.0 / (high - low) : image.Pixels[i], 0, 255);
        }

        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Gray8, null, pixels, image.Width);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The phone's picture did not come: the reason, in the view's place, while the view keeps trying.</summary>
    private void ShowPreviewProblem(string message)
    {
        if (_preview is null)
        {
            return;
        }

        PreviewImage.Source = null;
        PreviewLevel.Clear();
        PreviewStatus.Text = message + " Trying again…";
    }

    /// <summary>Shows the camera's picture and says whether it rests on the aiming area and looks straight at the screen
    /// (what matters), with the level's dot showing which way it is tilted; where on the area it rests does not
    /// matter.</summary>
    private void ShowPreview(BitmapSource bitmap, PlacementView view)
    {
        if (_preview is null)
        {
            return;
        }

        PreviewImage.Source = bitmap;
        PreviewLevel.Show(bitmap.PixelWidth, bitmap.PixelHeight, view);
        PreviewStatus.Text = PlacementText(view);
    }

    /// <summary>The camera's placement in words, for the camera step and the camera moves of a measurement (the level
    /// over the picture shows the same).</summary>
    private static string PlacementText(PlacementView view) => !view.Found
        ? "The outlined area is not in view: rest the camera on it."
        : view.Square switch
        {
            true => "On the area ✓  ·  Straight ✓",
            false => "On the area ✓  ·  Tilted: press the side nearest the dot toward the screen.",
            null => "On the area ✓  ·  Move it a little to check it is straight.",
        };
}
