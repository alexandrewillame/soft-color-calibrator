using System.Buffers.Binary;
using System.Text;
using SoftCal.Core.Color;

namespace SoftCal.Icc;

/// <summary>
/// Writes an <see cref="IccProfile"/> as an ICC.1:2001-04 (v2.4) display profile: big-endian, a 128-byte header, the
/// tag table, then each tag's data on a 4-byte boundary. v2.4 because every Windows component and every viewer reads it;
/// the 'chad' tag (new in v2.4) records the white's adaptation to D50.
/// </summary>
public static class IccWriter
{
    /// <summary>Tone curves are written with this many entries (enough for 16-bit smoothness, as ArgyllCMS does).</summary>
    public const int CurveEntries = 1024;

    public static byte[] Write(IccProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", TextDescription(profile.Description)),
            ("cprt", Text(profile.Copyright)),
            ("wtpt", XyzTag(profile.MediaWhite)),
            ("chad", Sf32(profile.Adaptation)),
            ("rXYZ", XyzTag(profile.RedColorant)),
            ("gXYZ", XyzTag(profile.GreenColorant)),
            ("bXYZ", XyzTag(profile.BlueColorant)),
            ("rTRC", Curve(profile.ToneCurves[0])),
            ("gTRC", Curve(profile.ToneCurves[1])),
            ("bTRC", Curve(profile.ToneCurves[2])),
        };
        if (profile.Vcgt is { } vcgt)
        {
            tags.Add(("vcgt", VideoCardGammaTag(vcgt)));
        }

        var tableSize = 4 + (12 * tags.Count);
        var offset = Align(128 + tableSize);
        var placed = new List<(string Signature, int Offset, byte[] Data)>();
        foreach (var (signature, data) in tags)
        {
            placed.Add((signature, offset, data));
            offset = Align(offset + data.Length);
        }

        var bytes = new byte[offset];
        WriteHeader(bytes, offset, profile.CreatedUtc);
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span[128..], (uint)tags.Count);
        for (var i = 0; i < placed.Count; i++)
        {
            var entry = span[(132 + (12 * i))..];
            WriteSignature(entry, placed[i].Signature);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)placed[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)placed[i].Data.Length);
            placed[i].Data.CopyTo(span[placed[i].Offset..]);
        }

        return bytes;
    }

    /// <summary>The s15Fixed16Number encoding: a signed 32-bit value with 16 fraction bits.</summary>
    internal static int S15Fixed16(double value) => (int)Math.Round(value * 65536, MidpointRounding.AwayFromZero);

    private static int Align(int offset) => (offset + 3) & ~3;

    private static void WriteSignature(Span<byte> destination, string signature) => Encoding.ASCII.GetBytes(signature.AsSpan(0, 4), destination);

    private static void WriteHeader(byte[] bytes, int size, DateTime createdUtc)
    {
        var h = bytes.AsSpan(0, 128);
        BinaryPrimitives.WriteUInt32BigEndian(h, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], 0x02400000);
        WriteSignature(h[12..], "mntr");
        WriteSignature(h[16..], "RGB ");
        WriteSignature(h[20..], "XYZ ");
        var t = createdUtc.ToUniversalTime();
        ushort[] date = [(ushort)t.Year, (ushort)t.Month, (ushort)t.Day, (ushort)t.Hour, (ushort)t.Minute, (ushort)t.Second];
        for (var i = 0; i < date.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(h[(24 + (2 * i))..], date[i]);
        }

        WriteSignature(h[36..], "acsp");
        WriteSignature(h[40..], "MSFT");

        // Rendering intent 0 (perceptual) at 64; the PCS illuminant, D50 exactly as ICC.1 writes it.
        BinaryPrimitives.WriteInt32BigEndian(h[68..], 0x0000F6D6);
        BinaryPrimitives.WriteInt32BigEndian(h[72..], 0x00010000);
        BinaryPrimitives.WriteInt32BigEndian(h[76..], 0x0000D32D);
        WriteSignature(h[80..], "SCAL");
    }

    /// <summary>textDescriptionType (v2): ASCII with its terminating NUL, then empty Unicode and ScriptCode parts.</summary>
    private static byte[] TextDescription(string text)
    {
        var ascii = Ascii(text);
        var data = new byte[8 + 4 + ascii.Length + 1 + 4 + 4 + 2 + 1 + 67];
        WriteSignature(data, "desc");
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)(ascii.Length + 1));
        ascii.CopyTo(data, 12);
        return data;
    }

    private static byte[] Text(string text)
    {
        var ascii = Ascii(text);
        var data = new byte[8 + ascii.Length + 1];
        WriteSignature(data, "text");
        ascii.CopyTo(data, 8);
        return data;
    }

    /// <summary>7-bit ASCII only (the v2 text types): anything else becomes '?'.</summary>
    private static byte[] Ascii(string text) => [.. text.Select(c => c is >= ' ' and <= '~' ? (byte)c : (byte)'?')];

    private static byte[] XyzTag(Xyz xyz)
    {
        var data = new byte[20];
        WriteSignature(data, "XYZ ");
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), S15Fixed16(xyz.X));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), S15Fixed16(xyz.Y));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), S15Fixed16(xyz.Z));
        return data;
    }

    private static byte[] Sf32(Matrix3 m)
    {
        var data = new byte[8 + 36];
        WriteSignature(data, "sf32");
        double[] values = [m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8 + (4 * i)), S15Fixed16(values[i]));
        }

        return data;
    }

    /// <summary>curveType with <see cref="CurveEntries"/> 16-bit samples, resampled linearly from the given curve.</summary>
    private static byte[] Curve(IReadOnlyList<double> curve)
    {
        if (curve.Count < 2)
        {
            throw new ArgumentException("A tone curve needs at least two samples.", nameof(curve));
        }

        var data = new byte[12 + (2 * CurveEntries)];
        WriteSignature(data, "curv");
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), CurveEntries);
        for (var i = 0; i < CurveEntries; i++)
        {
            var position = (double)i / (CurveEntries - 1) * (curve.Count - 1);
            var lower = Math.Min((int)position, curve.Count - 2);
            var value = curve[lower] + ((curve[lower + 1] - curve[lower]) * (position - lower));
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12 + (2 * i)), (ushort)Math.Round(Math.Clamp(value, 0, 1) * 65535));
        }

        return data;
    }

    /// <summary>vcgt as a table: type 0, 3 channels × 256 entries × 2 bytes.</summary>
    private static byte[] VideoCardGammaTag(VideoCardGamma vcgt)
    {
        var data = new byte[8 + 4 + 6 + (3 * VideoCardGamma.Entries * 2)];
        WriteSignature(data, "vcgt");
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(14), VideoCardGamma.Entries);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), 2);
        for (var c = 0; c < 3; c++)
        {
            var ramp = vcgt[c];
            if (ramp.Count != VideoCardGamma.Entries)
            {
                throw new ArgumentException("Each vcgt ramp has 256 entries.", nameof(vcgt));
            }

            for (var i = 0; i < VideoCardGamma.Entries; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(18 + (2 * ((c * VideoCardGamma.Entries) + i))), ramp[i]);
            }
        }

        return data;
    }
}
