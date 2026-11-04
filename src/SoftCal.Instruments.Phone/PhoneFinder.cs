using System.Globalization;
using System.Net;
using System.Net.Sockets;
using SoftCal.Remote;
using Windows.Devices.Enumeration;

namespace SoftCal.Instruments.Phone;

/// <summary>A phone found on the local network: its name as it advertises itself, and where its link listens.</summary>
public sealed record FoundPhone(string Id, string Name, string? HostName, int Port, IReadOnlyList<string> Addresses)
{
    /// <summary>The addresses to try, IPv4 first (a phone's IPv6 link-local address needs a scope this PC may not
    /// pick right).</summary>
    public IEnumerable<Uri> Endpoints =>
        Addresses
            .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
            .OfType<IPAddress>()
            .OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Select(ip => PhoneAddress.ToUri(ip.ToString(), Port));
}

/// <summary>
/// Finds phones running SoftCal Camera: they advertise <c>_softcal._tcp</c> over DNS-SD (multicast DNS), which
/// Windows browses for its own device lists. Nothing is opened on this PC: SoftCal then connects out to the phone, so
/// Windows Firewall has nothing to ask.
/// </summary>
public sealed class PhoneFinder : IDisposable
{
    /// <summary>DNS-SD services of one type in the local domain (the protocol id is DNS-SD's).</summary>
    private readonly string _query;

    private static readonly string[] Properties =
    [
        "System.Devices.Dnssd.HostName",
        "System.Devices.Dnssd.InstanceName",
        "System.Devices.Dnssd.PortNumber",
        "System.Devices.IpAddress",
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceInformation> _seen = new(StringComparer.Ordinal);
    private DeviceWatcher? _watcher;

    public PhoneFinder()
        : this(PhoneLink.ServiceType)
    {
    }

    /// <summary>Looks for another service type (tests use one that is surely on the network).</summary>
    internal PhoneFinder(string serviceType) =>
        _query = "System.Devices.AepService.ProtocolId:=\"{4526e8c1-8aac-4153-9b16-55e86ada0e54}\" AND "
            + "System.Devices.Dnssd.Domain:=\"local\" AND "
            + $"System.Devices.Dnssd.ServiceName:=\"{serviceType}\"";

    /// <summary>The list changed (raised on a background thread).</summary>
    public event EventHandler? Changed;

    /// <summary>The phones seen so far that say where they listen, by name.</summary>
    public IReadOnlyList<FoundPhone> Phones
    {
        get
        {
            lock (_gate)
            {
                return [.. _seen.Values.Select(ToPhone).OfType<FoundPhone>().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    /// <summary>Starts looking (again); phones keep being added and removed until <see cref="Stop"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_watcher is not null)
            {
                return;
            }

            _seen.Clear();
            var watcher = DeviceInformation.CreateWatcher(_query, Properties, DeviceInformationKind.AssociationEndpointService);
            watcher.Added += OnAdded;
            watcher.Updated += OnUpdated;
            watcher.Removed += OnRemoved;
            _watcher = watcher;
            watcher.Start();
        }
    }

    public void Stop()
    {
        DeviceWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is null)
        {
            return;
        }

        watcher.Added -= OnAdded;
        watcher.Updated -= OnUpdated;
        watcher.Removed -= OnRemoved;
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            watcher.Stop();
        }
    }

    public void Dispose() => Stop();

    private static FoundPhone? ToPhone(DeviceInformation info)
    {
        var port = info.Properties.TryGetValue("System.Devices.Dnssd.PortNumber", out var p) && p is not null
            ? Convert.ToInt32(p, CultureInfo.InvariantCulture)
            : 0;
        var addresses = info.Properties.TryGetValue("System.Devices.IpAddress", out var a) && a is string[] list ? list : [];
        if (port <= 0 || addresses.Length == 0)
        {
            return null;
        }

        var instance = info.Properties.TryGetValue("System.Devices.Dnssd.InstanceName", out var n) ? n as string : null;
        var host = info.Properties.TryGetValue("System.Devices.Dnssd.HostName", out var h) ? h as string : null;
        var name = !string.IsNullOrWhiteSpace(instance) ? instance : !string.IsNullOrWhiteSpace(info.Name) ? info.Name : host ?? addresses[0];
        return new FoundPhone(info.Id, name, host, port, addresses);
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        lock (_gate)
        {
            _seen[info.Id] = info;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_gate)
        {
            if (!_seen.TryGetValue(update.Id, out var info))
            {
                return;
            }

            info.Update(update);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_gate)
        {
            if (!_seen.Remove(update.Id))
            {
                return;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A phone's address as typed ("192.168.1.23", "192.168.1.23:47630", "[fe80::1]:47630", "pixel.local").</summary>
public static class PhoneAddress
{
    public static Uri ToUri(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return new UriBuilder("ws", host, port, PhoneLink.Path).Uri;
    }

    /// <summary>The link's address from what the user typed; null when it is no address.</summary>
    public static Uri? Parse(string? typed)
    {
        var text = typed?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..];
        }

        text = text.TrimEnd('/');
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            text = text[..slash];
        }

        var port = PhoneLink.DefaultPort;
        string host;
        if (text.StartsWith('['))
        {
            // [IPv6] or [IPv6]:port
            var close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }

            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port)))
            {
                return null;
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port))
            {
                return null;
            }
        }
        else
        {
            // A bare IPv6 address or a host name.
            host = text;
        }

        if (port is <= 0 or > 65535 || host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return null;
        }

        return ToUri(host, port);
    }
}
