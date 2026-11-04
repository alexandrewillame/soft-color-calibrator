using System.Globalization;
using Android.Content;
using Android.Net;
using Android.Net.Nsd;
using Android.Net.Wifi;
using SoftCal.Remote;

namespace SoftCal.Phone;

/// <summary>The phone on the local network: its addresses, and keeping Wi-Fi awake and quick while SoftCal Camera is open.</summary>
internal sealed class Network : IDisposable
{
    private readonly ConnectivityManager _connectivity;
    private readonly WifiManager.WifiLock? _wifi;
    private readonly WifiManager.MulticastLock? _multicast;

    public Network(Context context)
    {
        _connectivity = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService)!;
        if (context.ApplicationContext!.GetSystemService(Context.WifiService) is WifiManager wifi)
        {
            // Wi-Fi power saving would hold frames back by up to a few hundred milliseconds.
#pragma warning disable CA1422 // Low-latency mode is the API 29+ name of the same lock.
            _wifi = wifi.CreateWifiLock(OperatingSystem.IsAndroidVersionAtLeast(29) ? WifiMode.FullLowLatency : WifiMode.FullHighPerf, "SoftCal Camera");
#pragma warning restore CA1422
            _wifi?.Acquire();

            // Some phones' Wi-Fi drops multicast packets (the PC's search for the phone) unless an app asks for them.
            _multicast = wifi.CreateMulticastLock("SoftCal Camera");
            _multicast?.Acquire();
        }
    }

    /// <summary>The phone's IPv4 addresses on Wi-Fi or Ethernet (where a PC can reach it).</summary>
    public IReadOnlyList<string> LocalAddresses()
    {
        var addresses = new List<string>();
#pragma warning disable CA1422 // GetAllNetworks is deprecated from API 31 on, without a replacement that lists them all.
        foreach (var network in _connectivity.GetAllNetworks())
#pragma warning restore CA1422
        {
            var capabilities = _connectivity.GetNetworkCapabilities(network);
            if (capabilities is null || !(capabilities.HasTransport(TransportType.Wifi) || capabilities.HasTransport(TransportType.Ethernet)))
            {
                continue;
            }

            foreach (var link in _connectivity.GetLinkProperties(network)?.LinkAddresses ?? [])
            {
                if (link.Address is Java.Net.Inet4Address v4 && !v4.IsLoopbackAddress && v4.HostAddress is { } text)
                {
                    addresses.Add(text);
                }
            }
        }

        return addresses;
    }

    public void Dispose()
    {
        if (_wifi?.IsHeld == true)
        {
            _wifi.Release();
        }

        if (_multicast?.IsHeld == true)
        {
            _multicast.Release();
        }
    }
}

/// <summary>Advertises the phone's link as <c>_softcal._tcp</c> over DNS-SD, so SoftCal on the PC lists it by name.</summary>
internal sealed class Advertiser : Java.Lang.Object, NsdManager.IRegistrationListener
{
    private readonly NsdManager _nsd;
    private bool _registered;

    public Advertiser(Context context) => _nsd = (NsdManager)context.GetSystemService(Context.NsdService)!;

    /// <summary>Raised with a line for the screen when the advertisement fails (the PC can still use the address).</summary>
    public event EventHandler<string>? Failed;

    public void Register(string name, int port)
    {
        if (_registered)
        {
            return;
        }

        var info = new NsdServiceInfo { ServiceName = name, ServiceType = PhoneLink.ServiceType, Port = port };
        info.SetAttribute("v", PhoneLink.Protocol.ToString(CultureInfo.InvariantCulture));
        _nsd.RegisterService(info, NsdProtocol.DnsSd, this);
        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        try
        {
            _nsd.UnregisterService(this);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // Never registered (it failed).
        }
    }

    public void OnRegistrationFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode)
    {
        _registered = false;
        Failed?.Invoke(this, $"The phone could not announce itself on the network ({errorCode}): type its address in SoftCal instead.");
    }

    public void OnServiceRegistered(NsdServiceInfo? serviceInfo)
    {
    }

    public void OnServiceUnregistered(NsdServiceInfo? serviceInfo)
    {
    }

    public void OnUnregistrationFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode)
    {
    }
}
