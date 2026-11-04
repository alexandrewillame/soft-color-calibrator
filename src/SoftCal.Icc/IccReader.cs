using System.Buffers.Binary;
using System.Text;
using SoftCal.Core.Color;

namespace SoftCal.Icc;

/// <summary>A file that is not an RGB display profile SoftCal can read.</summary>
public sealed class IccFormatException : Exception
{
    public IccFormatException()
    {
    }

    public IccFormatException(string message)
        : base(message)
    {
    }

    public IccFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads RGB matrix/TRC display profiles, v2 or v4: the tags SoftCal writes, plus the variants others use (v4 'mluc'
/// text, gamma-only and parametric curves, the formula form of 'vcgt'). Used to check SoftCal's own files and to read a
/// display's current profile before replacing it.
/// </summary>
public static class IccReader
{
    public static IccProfile Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 132 || Signature(bytes[36..]) != "acsp")
        {
            throw new IccFormatException("Not an ICC profile (no 'acsp' signature).");
        }

        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (size > bytes.Length || size < 132)
        {
            throw new IccFormatException($"The header says {size} bytes but the file has {bytes.Length}.");
        }

        if (Signature(bytes[16..]) != "RGB ")
        {
            throw new IccFormatException($"Not an RGB profile ({Signature(bytes[16..]).Trim()}).");
        }

        var tags = new Dictionary<string, (int Offset, int Size)>(StringComparer.Ordinal);
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes[128..]);
        for (var i = 0; i < count; i++)
        {
            var entry = bytes[(132 + (12 * i))..];
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (offset < 0 || length < 0 || offset + length > size)
            {
                throw new IccFormatException($"Tag {Signature(entry)} lies outside the profile.");
            }

            tags[Signature(entry)] = (offset, length);
        }

        ReadOnlySpan<byte> Tag(string signature, ReadOnlySpan<byte> all) =>
            tags.TryGetValue(signature, out var t) ? all.Slice(t.Offset, t.Size) : throw new IccFormatException($"The profile has no '{signature}' tag.");

        var created = new DateTime(
            BinaryPrimitives.ReadUInt16BigEndian(bytes[24..]),
            Math.Max(1, (int)BinaryPrimitives.ReadUInt16BigEndian(bytes[26..])),
            Math.Max(1, (int)BinaryPrimitives.ReadUInt16BigEndian(bytes[28..])),
            BinaryPrimitives.ReadUInt16BigEndian(bytes[30..]),
            BinaryPrimitives.ReadUInt16BigEndian(bytes[32..]),
            BinaryPrimitives.ReadUInt16BigEndian(bytes[34..]),
            DateTimeKind.Utc);
        var chad = tags.ContainsKey("chad") ? ReadSf32(Tag("chad", bytes)) : new Matrix3(1, 0, 0, 0, 1, 0, 0, 0, 1);
        return new IccProfile(
            tags.ContainsKey("desc") ? ReadText(Tag("desc", bytes)) : string.Empty,
            tags.ContainsKey("cprt") ? ReadText(Tag("cprt", bytes)) : string.Empty,
            ReadXyz(Tag("wtpt", bytes)),
            chad,
            ReadXyz(Tag("rXYZ", bytes)),
            ReadXyz(Tag("gXYZ", bytes)),
            ReadXyz(Tag("bXYZ", bytes)),
            [ReadCurve(Tag("rTRC", bytes)), ReadCurve(Tag("gTRC", bytes)), ReadCurve(Tag("bTRC", bytes))],
            tags.ContainsKey("vcgt") ? ReadVcgt(Tag("vcgt", bytes)) : null,
            created);
    }

    private static string Signature(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes[..4]);

    private static double S15Fixed16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadInt32BigEndian(bytes) / 65536.0;

    private static Xyz ReadXyz(ReadOnlySpan<byte> tag) => Signature(tag) == "XYZ "
        ? new Xyz(S15Fixed16(tag[8..]), S15Fixed16(tag[12..]), S15Fixed16(tag[16..]))
        : throw new IccFormatException($"Expected an XYZ tag, found '{Signature(tag)}'.");

    private static Matrix3 ReadSf32(ReadOnlySpan<byte> tag)
    {
        if (Signature(tag) != "sf32" || tag.Length < 44)
        {
            throw new IccFormatException("Malformed 'chad' tag.");
        }

        var v = new double[9];
        for (var i = 0; i < 9; i++)
        {
            v[i] = S15Fixed16(tag[(8 + (4 * i))..]);
        }

        return new Matrix3(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);
    }

    private static string ReadText(ReadOnlySpan<byte> tag)
    {
        switch (Signature(tag))
        {
            case "desc":
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                return Encoding.ASCII.GetString(tag.Slice(12, Math.Max(0, length - 1)));
            case "text":
                return Encoding.ASCII.GetString(tag[8..]).TrimEnd('\0');
            case "mluc":
                var records = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                if (records == 0)
                {
                    return string.Empty;
                }

                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[20..]);
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[24..]);
                return Encoding.BigEndianUnicode.GetString(tag.Slice(offset, size)).TrimEnd('\0');
            default:
                throw new IccFormatException($"Unknown text type '{Signature(tag)}'.");
        }
    }

    /// <summary>A tone curve as linear light, sampled at <see cref="IccWriter.CurveEntries"/> even steps (tables as stored).</summary>
    private static double[] ReadCurve(ReadOnlySpan<byte> tag)
    {
        switch (Signature(tag))
        {
            case "curv":
                var count = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                if (count == 0)
                {
                    return Sample(x => x);
                }

                if (count == 1)
                {
                    var gamma = BinaryPrimitives.ReadUInt16BigEndian(tag[12..]) / 256.0;
                    return Sample(x => Math.Pow(x, gamma));
                }

                var table = new double[count];
                for (var i = 0; i < count; i++)
                {
                    table[i] = BinaryPrimitives.ReadUInt16BigEndian(tag[(12 + (2 * i))..]) / 65535.0;
                }

                return table;
            case "para":
                var function = BinaryPrimitives.ReadUInt16BigEndian(tag[8..]);
                var parameters = new double[(tag.Length - 12) / 4];
                for (var i = 0; i < parameters.Length; i++)
                {
                    parameters[i] = S15Fixed16(tag[(12 + (4 * i))..]);
                }

                return function switch
                {
                    0 => Sample(x => Math.Pow(x, parameters[0])),
                    3 when parameters.Length >= 5 => Sample(x => x >= parameters[4] ? Math.Pow((parameters[1] * x) + parameters[2], parameters[0]) : parameters[3] * x),
                    _ => throw new IccFormatException($"Parametric curve type {function} is not supported."),
                };
            default:
                throw new IccFormatException($"Unknown curve type '{Signature(tag)}'.");
        }
    }

    private static double[] Sample(Func<double, double> curve) =>
        [.. Enumerable.Range(0, IccWriter.CurveEntries).Select(i => curve((double)i / (IccWriter.CurveEntries - 1)))];

    private static VideoCardGamma ReadVcgt(ReadOnlySpan<byte> tag)
    {
        var type = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
        if (type == 0)
        {
            var channels = BinaryPrimitives.ReadUInt16BigEndian(tag[12..]);
            var entries = BinaryPrimitives.ReadUInt16BigEndian(tag[14..]);
            var entrySize = BinaryPrimitives.ReadUInt16BigEndian(tag[16..]);
            if (channels is not (1 or 3) || entries < 2 || entrySize is not (1 or 2))
            {
                throw new IccFormatException("Malformed 'vcgt' table.");
            }

            var data = tag[18..].ToArray();
            ushort[] Ramp(int channel)
            {
                var ramp = new ushort[VideoCardGamma.Entries];
                for (var i = 0; i < VideoCardGamma.Entries; i++)
                {
                    // Resample tables that do not have 256 entries.
                    var position = (double)i / (VideoCardGamma.Entries - 1) * (entries - 1);
                    var lower = Math.Min((int)position, entries - 2);
                    double At(int index) => entrySize == 2
                        ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2 * ((channel * entries) + index)))
                        : data[(channel * entries) + index] * 257.0;
                    var value = At(lower) + ((At(lower + 1) - At(lower)) * (position - lower));
                    ramp[i] = (ushort)Math.Round(Math.Clamp(value, 0, 65535));
                }

                return ramp;
            }

            return channels == 3 ? new VideoCardGamma(Ramp(0), Ramp(1), Ramp(2)) : new VideoCardGamma(Ramp(0), Ramp(0), Ramp(0));
        }

        if (type == 1)
        {
            // Formula: per channel gamma, min and max (s15Fixed16).
            var ramps = new ushort[3][];
            for (var channel = 0; channel < 3; channel++)
            {
                var gamma = S15Fixed16(tag[(12 + (12 * channel))..]);
                var min = S15Fixed16(tag[(16 + (12 * channel))..]);
                var max = S15Fixed16(tag[(20 + (12 * channel))..]);
                ramps[channel] = new ushort[VideoCardGamma.Entries];
                for (var i = 0; i < VideoCardGamma.Entries; i++)
                {
                    ramps[channel][i] = (ushort)Math.Round(Math.Clamp(min + ((max - min) * Math.Pow(i / 255.0, gamma)), 0, 1) * 65535);
                }
            }

            return new VideoCardGamma(ramps[0], ramps[1], ramps[2]);
        }

        throw new IccFormatException($"Unknown 'vcgt' type {type}.");
    }
}
