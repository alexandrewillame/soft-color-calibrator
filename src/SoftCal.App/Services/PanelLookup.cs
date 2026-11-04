using System.Globalization;
using SoftCal.Core.Displays;
using SoftCal.Windows.Displays;

namespace SoftCal.App.Services;

/// <summary>
/// A web search that tells a monitor's panel type (TN, IPS or VA): its maker, its model as its EDID names it, and its
/// EDID ID (maker code and product number, e.g. BNQ7F2B), which databases of monitors list when the name is shared by
/// several models (the BenQ XL2720 calls itself "ZOWIE XL LCD").
/// </summary>
internal static class PanelLookup
{
    /// <summary>Common makers' PNP codes (the EDID's three letters) and their names.</summary>
    private static readonly Dictionary<string, string> Makers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACI"] = "ASUS",
        ["ACR"] = "Acer",
        ["AOC"] = "AOC",
        ["APP"] = "Apple",
        ["AUS"] = "ASUS",
        ["BNQ"] = "BenQ",
        ["DEL"] = "Dell",
        ["ENC"] = "EIZO",
        ["GBT"] = "Gigabyte",
        ["GSM"] = "LG",
        ["HPN"] = "HP",
        ["HWP"] = "HP",
        ["IVM"] = "iiyama",
        ["LEN"] = "Lenovo",
        ["MSI"] = "MSI",
        ["NEC"] = "NEC",
        ["PHL"] = "Philips",
        ["SAM"] = "Samsung",
        ["SNY"] = "Sony",
        ["VSC"] = "ViewSonic",
        ["XMI"] = "Xiaomi",
    };

    /// <summary>"BenQ ZOWIE XL LCD BNQ7F2B monitor panel type TN IPS VA", or the display's Windows name when its EDID
    /// cannot be read.</summary>
    public static string Query(DisplayMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        Edid? edid = null;
        if (monitor.Edid is { } bytes)
        {
            try
            {
                edid = Edid.Parse(bytes);
            }
            catch (EdidFormatException)
            {
                // The Windows name is all there is.
            }
        }

        return Query(edid?.ManufacturerId, edid?.ProductCode, edid?.MonitorName ?? monitor.FriendlyName);
    }

    /// <summary>The search for a maker code, product number and model name (any may be missing).</summary>
    public static string Query(string? makerId, ushort? productCode, string? model)
    {
        var parts = new List<string>();
        var maker = makerId is { Length: > 0 } id ? Makers.GetValueOrDefault(id, id) : null;
        var name = model?.Trim();
        if (maker is not null && (name is null || !name.StartsWith(maker, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(maker);
        }

        if (name is { Length: > 0 })
        {
            parts.Add(name);
        }

        if (makerId is { Length: > 0 } code && productCode is { } product)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{code.ToUpperInvariant()}{product:X4}"));
        }

        parts.Add("monitor panel type TN IPS VA");
        return string.Join(' ', parts);
    }

    public static string SearchUrl(string query) => "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
}
