using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SoftCal.Core.Displays;

/// <summary>
/// A stable, privacy-preserving identity for a physical monitor, derived from its EDID.
/// It survives reboots and reconnection (unlike adapter LUIDs) and does not expose the serial number:
/// the readable prefix is manufacturer + product code, followed by a short hash of the identifying fields.
/// </summary>
public readonly record struct DisplayKey
{
    private DisplayKey(string value) => Value = value;

    public string Value { get; }

    /// <summary>Creates the key. Pass a <paramref name="disambiguator"/> (e.g. the connector) only when two identical
    /// monitors report the same EDID identity.</summary>
    public static DisplayKey FromEdid(Edid edid, string? disambiguator = null)
    {
        ArgumentNullException.ThrowIfNull(edid);
        var identity = string.Join(
            '|',
            edid.ManufacturerId,
            edid.ProductCode.ToString("X4", CultureInfo.InvariantCulture),
            edid.SerialNumber.ToString(CultureInfo.InvariantCulture),
            edid.SerialText ?? string.Empty,
            edid.MonitorName ?? string.Empty,
            (edid.YearOfManufacture ?? edid.ModelYear ?? 0).ToString(CultureInfo.InvariantCulture),
            (edid.WeekOfManufacture ?? 0).ToString(CultureInfo.InvariantCulture),
            disambiguator ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var prefix = edid.ManufacturerId + edid.ProductCode.ToString("X4", CultureInfo.InvariantCulture);
        return new DisplayKey($"{prefix}-{Convert.ToHexStringLower(hash.AsSpan(0, 5))}");
    }

    public static DisplayKey Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new DisplayKey(value);
    }

    public override string ToString() => Value;
}
