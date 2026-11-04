using System.Collections.Immutable;
using System.Text;
using SoftCal.Core.Color;

namespace SoftCal.Core.Displays;

/// <summary>Thrown when bytes are not a valid EDID structure.</summary>
public sealed class EdidFormatException : FormatException
{
    public EdidFormatException()
    {
    }

    public EdidFormatException(string message)
        : base(message)
    {
    }

    public EdidFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A parsed VESA E-EDID (1.3/1.4) base block plus the parts of a CTA-861 extension that matter for calibration.
/// Factory colorimetry always comes from here (raw EDID), never from Windows APIs that may reflect an installed profile.
/// </summary>
public sealed record Edid
{
    private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    public required ImmutableArray<byte> Raw { get; init; }

    /// <summary>Three-letter PNP manufacturer ID, e.g. "BNQ".</summary>
    public required string ManufacturerId { get; init; }

    public required ushort ProductCode { get; init; }

    /// <summary>Numeric serial number from bytes 12–15 (0 when not provided).</summary>
    public required uint SerialNumber { get; init; }

    public int? WeekOfManufacture { get; init; }

    public int? YearOfManufacture { get; init; }

    public int? ModelYear { get; init; }

    public required int VersionMajor { get; init; }

    public required int VersionMinor { get; init; }

    public required bool IsDigital { get; init; }

    /// <summary>Bits per color channel (EDID 1.4 digital displays only).</summary>
    public int? BitsPerColor { get; init; }

    /// <summary>Digital interface named by EDID 1.4 (e.g. "DisplayPort", "HDMI-a").</summary>
    public string? DigitalInterface { get; init; }

    public int? WidthCm { get; init; }

    public int? HeightCm { get; init; }

    /// <summary>Display transfer characteristic (gamma) declared by the manufacturer.</summary>
    public double? Gamma { get; init; }

    public bool SrgbIsDefaultColorSpace { get; init; }

    public bool PreferredTimingIsNative { get; init; }

    /// <summary>Primaries and white point, from the 10-bit chromaticity fields.</summary>
    public required RgbPrimaries Chromaticities { get; init; }

    public string? MonitorName { get; init; }

    public string? SerialText { get; init; }

    public ImmutableArray<string> UnspecifiedText { get; init; } = [];

    public DetailedTiming? PreferredTiming { get; init; }

    public RangeLimits? RangeLimits { get; init; }

    public int ExtensionCount { get; init; }

    public CtaExtension? Cta { get; init; }

    /// <summary>Parses an EDID (128 bytes or more), validating the header and every block checksum present.</summary>
    public static Edid Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 128)
        {
            throw new EdidFormatException($"EDID must be at least 128 bytes, got {data.Length}.");
        }

        if (!data[..8].SequenceEqual(Header))
        {
            throw new EdidFormatException("Missing EDID header (00 FF FF FF FF FF FF 00).");
        }

        var blocks = data.Length / 128;
        for (var b = 0; b < blocks; b++)
        {
            var sum = 0;
            foreach (var value in data.Slice(b * 128, 128))
            {
                sum += value;
            }

            if ((sum & 0xFF) != 0)
            {
                throw new EdidFormatException($"Checksum error in EDID block {b}.");
            }
        }

        var b0 = data[..128];
        var versionMajor = b0[18];
        var versionMinor = b0[19];
        var isDigital = (b0[20] & 0x80) != 0;
        var is14 = versionMajor > 1 || (versionMajor == 1 && versionMinor >= 4);

        int? bitsPerColor = null;
        string? digitalInterface = null;
        if (isDigital && is14)
        {
            bitsPerColor = ((b0[20] >> 4) & 0x07) switch
            {
                1 => 6,
                2 => 8,
                3 => 10,
                4 => 12,
                5 => 14,
                6 => 16,
                _ => null,
            };
            digitalInterface = (b0[20] & 0x0F) switch
            {
                1 => "DVI",
                2 => "HDMI-a",
                3 => "HDMI-b",
                4 => "MDDI",
                5 => "DisplayPort",
                _ => null,
            };
        }

        int? week = null, year = null, modelYear = null;
        if (b0[16] == 0xFF)
        {
            modelYear = 1990 + b0[17];
        }
        else
        {
            week = b0[16] is >= 1 and <= 54 ? b0[16] : null;
            year = 1990 + b0[17];
        }

        string? monitorName = null, serialText = null;
        var unspecified = ImmutableArray.CreateBuilder<string>();
        DetailedTiming? preferred = null;
        RangeLimits? rangeLimits = null;
        ReadOnlySpan<int> descriptorOffsets = [54, 72, 90, 108];
        foreach (var offset in descriptorOffsets)
        {
            var d = b0.Slice(offset, 18);
            if (d[0] == 0 && d[1] == 0)
            {
                switch (d[3])
                {
                    case 0xFC:
                        monitorName = DecodeText(d[5..]);
                        break;
                    case 0xFF:
                        serialText = DecodeText(d[5..]);
                        break;
                    case 0xFE:
                        unspecified.Add(DecodeText(d[5..]));
                        break;
                    case 0xFD:
                        rangeLimits = ParseRangeLimits(d);
                        break;
                }
            }
            else
            {
                preferred ??= ParseDetailedTiming(d);
            }
        }

        var cta = blocks > 1 && data[128] == 0x02 ? CtaExtension.Parse(data.Slice(128, 128)) : null;

        return new Edid
        {
            Raw = [.. data],
            ManufacturerId = DecodeManufacturer(b0[8], b0[9]),
            ProductCode = (ushort)(b0[10] | (b0[11] << 8)),
            SerialNumber = (uint)(b0[12] | (b0[13] << 8) | (b0[14] << 16) | (b0[15] << 24)),
            WeekOfManufacture = week,
            YearOfManufacture = year,
            ModelYear = modelYear,
            VersionMajor = versionMajor,
            VersionMinor = versionMinor,
            IsDigital = isDigital,
            BitsPerColor = bitsPerColor,
            DigitalInterface = digitalInterface,
            WidthCm = b0[21] == 0 ? null : b0[21],
            HeightCm = b0[22] == 0 ? null : b0[22],
            Gamma = b0[23] == 0xFF ? null : (b0[23] + 100) / 100.0,
            SrgbIsDefaultColorSpace = (b0[24] & 0x04) != 0,
            PreferredTimingIsNative = (b0[24] & 0x02) != 0,
            Chromaticities = DecodeChromaticities(b0),
            MonitorName = monitorName,
            SerialText = serialText,
            UnspecifiedText = unspecified.ToImmutable(),
            PreferredTiming = preferred,
            RangeLimits = rangeLimits,
            ExtensionCount = b0[126],
            Cta = cta,
        };
    }

    /// <summary>Parses EDID bytes written as hexadecimal text (whitespace separated; lines starting with '#' are comments).</summary>
    public static Edid ParseHex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new List<byte>(256);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            foreach (var token in trimmed.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                bytes.Add(byte.Parse(token, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return Parse(bytes.ToArray());
    }

    private static string DecodeManufacturer(byte high, byte low)
    {
        var value = (high << 8) | low;
        Span<char> letters = stackalloc char[3];
        letters[0] = (char)('A' - 1 + ((value >> 10) & 0x1F));
        letters[1] = (char)('A' - 1 + ((value >> 5) & 0x1F));
        letters[2] = (char)('A' - 1 + (value & 0x1F));
        return new string(letters);
    }

    private static RgbPrimaries DecodeChromaticities(ReadOnlySpan<byte> b0)
    {
        int lo1 = b0[25], lo2 = b0[26];
        double C(int high, int low) => ((high << 2) | low) / 1024.0;
        return new RgbPrimaries(
            new(C(b0[27], (lo1 >> 6) & 3), C(b0[28], (lo1 >> 4) & 3)),
            new(C(b0[29], (lo1 >> 2) & 3), C(b0[30], lo1 & 3)),
            new(C(b0[31], (lo2 >> 6) & 3), C(b0[32], (lo2 >> 4) & 3)),
            new(C(b0[33], (lo2 >> 2) & 3), C(b0[34], lo2 & 3)));
    }

    private static string DecodeText(ReadOnlySpan<byte> payload)
    {
        var text = new StringBuilder(13);
        foreach (var c in payload[..Math.Min(13, payload.Length)])
        {
            if (c == 0x0A)
            {
                break;
            }

            text.Append(c is >= 0x20 and < 0x7F ? (char)c : '?');
        }

        return text.ToString().TrimEnd();
    }

    private static DetailedTiming? ParseDetailedTiming(ReadOnlySpan<byte> d)
    {
        var pixelClock10kHz = d[0] | (d[1] << 8);
        if (pixelClock10kHz == 0)
        {
            return null;
        }

        return new DetailedTiming(
            PixelClockMHz: pixelClock10kHz / 100.0,
            HorizontalActive: d[2] | ((d[4] >> 4) << 8),
            HorizontalBlanking: d[3] | ((d[4] & 0x0F) << 8),
            VerticalActive: d[5] | ((d[7] >> 4) << 8),
            VerticalBlanking: d[6] | ((d[7] & 0x0F) << 8),
            Interlaced: (d[17] & 0x80) != 0);
    }

    private static RangeLimits ParseRangeLimits(ReadOnlySpan<byte> d)
    {
        var offsets = d[4];
        var minV = d[5] + ((offsets & 0x01) != 0 ? 255 : 0);
        var maxV = d[6] + ((offsets & 0x02) != 0 ? 255 : 0);
        var minH = d[7] + ((offsets & 0x04) != 0 ? 255 : 0);
        var maxH = d[8] + ((offsets & 0x08) != 0 ? 255 : 0);
        int? maxClock = d[9] == 0 ? null : d[9] * 10;
        return new RangeLimits(minV, maxV, minH, maxH, maxClock);
    }
}

/// <summary>A detailed timing descriptor; the first one is the preferred (usually native) mode.</summary>
public sealed record DetailedTiming(
    double PixelClockMHz,
    int HorizontalActive,
    int HorizontalBlanking,
    int VerticalActive,
    int VerticalBlanking,
    bool Interlaced)
{
    public double RefreshHz =>
        PixelClockMHz * 1e6 / ((HorizontalActive + HorizontalBlanking) * (double)(VerticalActive + VerticalBlanking));
}

/// <summary>Display range limits (vertical Hz, horizontal kHz, maximum pixel clock MHz).</summary>
public sealed record RangeLimits(int MinVerticalHz, int MaxVerticalHz, int MinHorizontalKHz, int MaxHorizontalKHz, int? MaxPixelClockMHz);

/// <summary>The calibration-relevant parts of a CTA-861 extension block.</summary>
public sealed record CtaExtension(
    int Revision,
    bool SupportsYCbCr444,
    bool SupportsYCbCr422,
    bool HasHdmiVendorBlock,
    bool HasColorimetryBlock,
    bool HasHdrStaticMetadata,
    ImmutableArray<int> DataBlockTags)
{
    public static CtaExtension Parse(ReadOnlySpan<byte> block)
    {
        if (block.Length < 128 || block[0] != 0x02)
        {
            throw new EdidFormatException("Not a CTA-861 extension block.");
        }

        var revision = block[1];
        var dtdOffset = block[2];
        var flags = revision >= 2 ? block[3] : 0;
        var tags = ImmutableArray.CreateBuilder<int>();
        bool hdmi = false, colorimetry = false, hdr = false;

        // Data blocks occupy bytes 4 .. dtdOffset-1; offset 0 means "no data blocks and no DTDs".
        var end = dtdOffset is >= 4 and <= 127 ? dtdOffset : 4;
        var i = 4;
        while (i < end)
        {
            var tag = block[i] >> 5;
            var length = block[i] & 0x1F;
            if (i + 1 + length > end)
            {
                break;
            }

            tags.Add(tag);
            if (tag == 3 && length >= 3)
            {
                var oui = block[i + 1] | (block[i + 2] << 8) | (block[i + 3] << 16);
                hdmi |= oui == 0x000C03;
            }
            else if (tag == 7 && length >= 1)
            {
                var extended = block[i + 1];
                colorimetry |= extended == 5;
                hdr |= extended == 6;
            }

            i += 1 + length;
        }

        return new CtaExtension(
            revision,
            SupportsYCbCr444: (flags & 0x20) != 0,
            SupportsYCbCr422: (flags & 0x10) != 0,
            HasHdmiVendorBlock: hdmi,
            HasColorimetryBlock: colorimetry,
            HasHdrStaticMetadata: hdr,
            DataBlockTags: tags.ToImmutable());
    }
}
