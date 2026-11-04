using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SoftCal.App.Services;
using SoftCal.Instruments.Phone;
using SoftCal.Instruments.Webcam;
using SoftCal.Remote;

namespace SoftCal.App;

/// <summary>
/// Step 2 with a phone: SoftCal Camera open on a phone on the same network is found by name (or its address typed),
/// and paired with the code it shows. The link stays open from step to step until it is disconnected, the phone goes
/// away, or SoftCal closes; the phone puts its camera back in automatic whenever it drops.
/// </summary>
public partial class MainWindow
{
    private PhoneFinder? _phoneFinder;
    private IReadOnlyList<FoundPhone> _foundPhones = [];
    private PhoneClient? _phone;
    private bool _connectingPhone;

    /// <summary>What the last attempt said (a wrong code, the phone gone…), until the next one.</summary>
    private string? _phoneNote;

    /// <summary>The address the phone was reached at last time, put in the phone box until a phone is found.</summary>
    private string? _lastPhoneAddress;

    private bool UsePhone => !Simulated && UsePhoneRadio.IsChecked == true;

    /// <summary>The phone to measure with: connected, and able to (manual exposure).</summary>
    private PhoneClient? MeasuringPhone =>
        UsePhone && _phone is { IsConnected: true } phone && PhoneInstrument.CannotMeasureReason(phone.Capabilities) is null ? phone : null;

    /// <summary>The webcam to measure with, unless a phone is chosen.</summary>
    private WebcamDevice? MeasuringWebcam => UsePhone ? null : SelectedCamera;

    /// <summary>The chosen kind of camera is there: the webcam found, or the phone connected and able to measure.</summary>
    private bool HasCamera => UsePhone ? MeasuringPhone is not null : SelectedCamera is not null;

    private void RestoreCameraChoice()
    {
        var choice = CameraChoiceStore.Load();
        _lastPhoneAddress = choice.PhoneAddress;
        PhoneList.Text = _lastPhoneAddress ?? string.Empty;
        (choice.Phone ? UsePhoneRadio : UseWebcamRadio).IsChecked = true;
    }

    private void SaveCameraChoice(Uri? phoneAddress = null, string? phoneName = null)
    {
        if (phoneAddress is not null)
        {
            _lastPhoneAddress = phoneAddress.Authority;
        }

        var last = CameraChoiceStore.Load();
        CameraChoiceStore.Save(new CameraChoice(UsePhoneRadio.IsChecked == true, _lastPhoneAddress, phoneName ?? last.PhoneName));
    }

    private async void CameraKind_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        SaveCameraChoice();

        // Another kind of camera: it must be placed, and ticked, again.
        CameraReadyCheck.IsChecked = false;
        ShowCameraKind();
        if (_step == CameraIndex)
        {
            await StopPreviewAsync();
            StartPreview();
        }
    }

    /// <summary>Shows the webcam's or the phone's controls and placing steps, and looks for phones while one is to be
    /// connected.</summary>
    private void ShowCameraKind()
    {
        var phone = UsePhone;
        CameraKindPanel.Visibility = Simulated ? Visibility.Collapsed : Visibility.Visible;
        CameraList.Visibility = !Simulated && !phone && _cameras.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PhonePanel.Visibility = phone ? Visibility.Visible : Visibility.Collapsed;
        PhoneHowTo.Visibility = phone ? Visibility.Visible : Visibility.Collapsed;
        WebcamHowTo.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        if (phone && _step == CameraIndex && _phone is null)
        {
            StartFindingPhones();
        }
        else
        {
            StopFindingPhones();
        }

        ShowPhoneState();
        UpdateNavigation();
    }

    private void StartFindingPhones()
    {
        if (_phoneFinder is not null)
        {
            return;
        }

        var finder = new PhoneFinder();
        finder.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_phoneFinder, finder))
            {
                ListPhones(finder.Phones);
            }
        });
        _phoneFinder = finder;
        try
        {
            finder.Start();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _phoneNote = "This PC cannot look for phones on the network (" + ex.Message + "): type the address the phone shows.";
        }

        ShowPhoneState();
    }

    private void StopFindingPhones()
    {
        _phoneFinder?.Dispose();
        _phoneFinder = null;
    }

    /// <summary>Lists the phones found; the one chosen stays chosen, an address being typed is left alone, and the
    /// first phone is chosen when nothing is.</summary>
    private void ListPhones(IReadOnlyList<FoundPhone> phones)
    {
        var chosen = PhoneList.SelectedIndex >= 0 && PhoneList.SelectedIndex < _foundPhones.Count ? _foundPhones[PhoneList.SelectedIndex].Id : null;

        // An address the user typed stays; last time's address gives way to a phone found.
        var typed = PhoneList.SelectedIndex < 0 && PhoneList.Text != _lastPhoneAddress ? PhoneList.Text : null;
        _foundPhones = phones;
        PhoneList.ItemsSource = phones.Select(PhoneLabel).ToArray();
        var index = chosen is null ? -1 : phones.ToList().FindIndex(p => p.Id == chosen);
        if (index >= 0)
        {
            PhoneList.SelectedIndex = index;
        }
        else if (!string.IsNullOrWhiteSpace(typed))
        {
            PhoneList.SelectedIndex = -1;
            PhoneList.Text = typed;
        }
        else if (phones.Count > 0)
        {
            PhoneList.SelectedIndex = 0;
        }

        ShowPhoneState();
    }

    private static string PhoneLabel(FoundPhone phone)
    {
        var address = phone.Addresses.FirstOrDefault(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            ?? phone.Addresses.FirstOrDefault();
        return address is null ? phone.Name : $"{phone.Name}  ·  {address}";
    }

    /// <summary>Where to reach the phone chosen or typed: a found phone's addresses (IPv4 first), or the address typed.</summary>
    private List<Uri> PhoneEndpoints()
    {
        var index = PhoneList.SelectedIndex;
        if (index >= 0 && index < _foundPhones.Count && PhoneList.Text == PhoneLabel(_foundPhones[index]))
        {
            return [.. _foundPhones[index].Endpoints];
        }

        return PhoneAddress.Parse(PhoneList.Text) is { } typed ? [typed] : [];
    }

    private void PhoneCodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            PhoneConnect_Click(sender, e);
        }
    }

    private async void PhoneConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_connectingPhone || _phone is not null)
        {
            return;
        }

        var code = Pairing.Normalize(PhoneCodeBox.Text);
        if (code.Length != 6)
        {
            _phoneNote = "Type the 6-digit code SoftCal Camera shows on the phone.";
            ShowPhoneState();
            PhoneCodeBox.Focus();
            return;
        }

        var endpoints = PhoneEndpoints();
        if (endpoints.Count == 0)
        {
            _phoneNote = "Choose the phone, or type the address it shows.";
            ShowPhoneState();
            PhoneList.Focus();
            return;
        }

        _connectingPhone = true;
        _phoneNote = null;
        PhoneConnectButton.IsEnabled = false;
        ShowPhoneState();
        string? failure = null;
        try
        {
            foreach (var endpoint in endpoints)
            {
                try
                {
                    var client = await PhoneClient.ConnectAsync(endpoint, code, Environment.MachineName, null, CancellationToken.None);
                    SaveCameraChoice(endpoint, PhoneInstrument.Name(client.Phone));
                    OnPhoneConnected(client);
                    return;
                }
                catch (PhoneLinkException ex)
                {
                    failure = ex.Message;

                    // A phone that answered (a wrong code, another PC on it) answers the same at its other addresses.
                    if (ex.InnerException is null)
                    {
                        break;
                    }
                }
            }

            _phoneNote = failure ?? "The phone did not answer.";
        }
        finally
        {
            _connectingPhone = false;
            PhoneConnectButton.IsEnabled = true;
            ShowPhoneState();
        }
    }

    private async void OnPhoneConnected(PhoneClient client)
    {
        client.Disconnected += (_, reason) => Dispatcher.BeginInvoke(() => OnPhoneLost(client, reason));
        _phone = client;
        _phoneNote = null;
        PhoneCodeBox.Clear();
        StopFindingPhones();
        CameraReadyCheck.IsChecked = false;
        if (!client.IsConnected)
        {
            OnPhoneLost(client, "The phone closed the link.");
            return;
        }

        ShowPhoneState();
        UpdateNavigation();
        if (_step == CameraIndex)
        {
            await StopPreviewAsync();
            StartPreview();
        }
    }

    /// <summary>The link dropped (the phone app closed, the phone left the network…): the camera must be connected and
    /// placed again.</summary>
    private void OnPhoneLost(PhoneClient client, string reason)
    {
        if (!ReferenceEquals(_phone, client))
        {
            return;
        }

        _phone = null;
        _phoneNote = "The phone disconnected: " + reason;
        CameraReadyCheck.IsChecked = false;
        _ = client.DisposeAsync();
        if (_step == CameraIndex)
        {
            ShowCameraKind();
        }

        UpdateNavigation();
    }

    private async void PhoneDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_phone is not { } phone || Busy)
        {
            return;
        }

        _phone = null;
        _phoneNote = "Disconnected.";
        CameraReadyCheck.IsChecked = false;
        await StopPreviewAsync();
        await phone.DisposeAsync();
        ShowCameraKind();
        StartPreview();
    }

    private void ShowPhoneState()
    {
        var connected = _phone is { IsConnected: true };
        PhonePairing.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        PhoneDisconnectButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        PhoneDisconnectButton.IsEnabled = !Busy;
        Brush color;
        string text;
        if (_phone is { IsConnected: true } phone)
        {
            PhoneTubeRun.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"Tape a black paper tube (about {MeasurementSession.PhoneTubeCentimetres(phone.Capabilities):0} cm) around the phone's main camera.");
            var cannot = PhoneInstrument.CannotMeasureReason(phone.Capabilities);
            text = cannot is null
                ? $"Connected to {PhoneInstrument.Name(phone.Phone)}."
                : $"Connected to {PhoneInstrument.Name(phone.Phone)}, but it cannot measure: {cannot}";
            color = (Brush)FindResource(cannot is null ? "GoodBrush" : "WarnBrush");
        }
        else if (_connectingPhone)
        {
            text = "Connecting…";
            color = (Brush)FindResource("TextDimBrush");
        }
        else if (_phoneNote is { } note)
        {
            text = note;
            color = (Brush)FindResource("WarnBrush");
        }
        else
        {
            text = _foundPhones.Count == 0
                ? "Open SoftCal Camera on the phone (same Wi-Fi): it appears here, or type its address."
                : "Type the code the phone shows, then Connect.";
            color = (Brush)FindResource("TextDimBrush");
        }

        PhoneStatus.Text = text;
        PhoneStatus.Foreground = color;
    }

    /// <summary>SoftCal is closing: the phone is told (it puts its camera back in automatic and shows a new code).</summary>
    private void ClosePhone()
    {
        StopFindingPhones();
        if (_phone is not { } phone)
        {
            return;
        }

        _phone = null;
        try
        {
            // Every await inside runs off this thread: waiting here cannot block it.
            phone.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The phone notices the link dropping within seconds anyway.
        }
    }
}
