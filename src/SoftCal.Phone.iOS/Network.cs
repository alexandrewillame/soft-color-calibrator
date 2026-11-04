using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SoftCal.Remote;

namespace SoftCal.Phone.iOS;

/// <summary>The device on the local network: its addresses (iOS keeps Wi-Fi awake for the app in front by itself).</summary>
internal static class Network
{
    /// <summary>The device's IPv4 addresses on Wi-Fi or Ethernet (<c>en…</c>) and on its own hotspot (<c>bridge…</c>):
    /// where a PC can reach it.</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var addresses = new List<string>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!adapter.Name.StartsWith("en", StringComparison.Ordinal) && !adapter.Name.StartsWith("bridge", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;

                // Not the self-assigned 169.254 addresses: a PC on the same Wi-Fi never has one.
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && address.GetAddressBytes() is not [169, 254, ..])
                {
                    addresses.Add(address.ToString());
                }
            }
        }

        return addresses;
    }
}

/// <summary>Advertises the phone's link as <c>_softcal._tcp</c> over DNS-SD (Bonjour), so SoftCal on the PC lists it by
/// name. iOS asks the user for the local network the first time.</summary>
internal sealed class Advertiser
{
    private NSNetService? _service;

    /// <summary>Raised with a line for the screen when the advertisement fails (the PC can still use the address).</summary>
    public event EventHandler<string>? Failed;

    /// <summary>Announces the link on <paramref name="port"/>; on the main thread (the announcement runs on its loop).</summary>
    public void Register(string name, int port)
    {
        if (_service is not null)
        {
            return;
        }

#pragma warning disable CA1422 // NSNetService is deprecated from iOS 15 on: Network.framework only announces its own listeners, and this one is the protocol's socket. It still announces any port.
        var service = _service = new NSNetService("local.", PhoneLink.ServiceType + ".", name, port);
        using var version = new NSString(PhoneLink.Protocol.ToString(CultureInfo.InvariantCulture));
        using var txt = NSDictionary.FromObjectAndKey(version.Encode(NSStringEncoding.UTF8), new NSString("v"));
        service.SetTxtRecordData(NSNetService.DataFromTxtRecord(txt));
        service.PublishFailure += (_, _) =>
        {
            Unregister();
            Failed?.Invoke(this, "This device could not announce itself on the network: allow SoftCal Camera in Settings › Privacy & Security › Local Network, or type its address in SoftCal.");
        };
        service.Publish();
#pragma warning restore CA1422
    }

    public void Unregister()
    {
        if (_service is not { } service)
        {
            return;
        }

        _service = null;
#pragma warning disable CA1422 // See Register.
        service.Stop();
#pragma warning restore CA1422
        service.Dispose();
    }
}
