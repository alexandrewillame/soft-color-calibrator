using System.Text;

namespace SoftCal.Windows.NightLight;

/// <summary>Bond CompactBinary field types.</summary>
public enum BondType : byte
{
    Stop = 0,
    StopBase = 1,
    Bool = 2,
    UInt8 = 3,
    UInt16 = 4,
    UInt32 = 5,
    UInt64 = 6,
    Float = 7,
    Double = 8,
    String = 9,
    Struct = 10,
    List = 11,
    Set = 12,
    Map = 13,
    Int8 = 14,
    Int16 = 15,
    Int32 = 16,
    Int64 = 17,
    WString = 18,
}

/// <summary>A decoded field. <see cref="Value"/> is a long, ulong, bool, double, string, <see cref="BondStruct"/>,
/// byte[] (lists of 8-bit integers) or a list of values.</summary>
public sealed record BondField(ushort Id, BondType Type, object Value);

/// <summary>A decoded struct: its fields in wire order (base-class fields first).</summary>
public sealed record BondStruct(IReadOnlyList<BondField> Fields)
{
    public BondField? this[ushort id] => Fields.FirstOrDefault(f => f.Id == id);

    public long? Int(ushort id) => this[id]?.Value switch
    {
        long l => l,
        ulong u => (long)u,
        bool b => b ? 1 : 0,
        _ => null,
    };

    public ulong? UInt(ushort id) => this[id]?.Value switch
    {
        ulong u => u,
        long l => (ulong)l,
        _ => null,
    };

    public BondStruct? Struct(ushort id) => this[id]?.Value as BondStruct;

    public byte[]? Bytes(ushort id) => this[id]?.Value as byte[];
}

/// <summary>
/// A minimal, read-only Bond CompactBinary v1 reader, enough for the CloudStore blobs Windows uses for Night light
/// (marshaled form: "CB" + version 1, then a struct). Malformed input throws <see cref="FormatException"/>.
/// </summary>
public sealed class BondCompactReader
{
    private readonly byte[] _data;
    private int _position;

    private BondCompactReader(byte[] data, int offset)
    {
        _data = data;
        _position = offset;
    }

    /// <summary>True when the bytes start with the marshaled CompactBinary v1 header (43 42 01 00).</summary>
    public static bool HasHeader(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0x43 && data[1] == 0x42 && data[2] == 0x01 && data[3] == 0x00;

    /// <summary>Parses a marshaled blob into its top-level struct.</summary>
    public static BondStruct ParseMarshaled(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!HasHeader(data))
        {
            throw new FormatException("Not a Bond CompactBinary v1 blob (missing CB header).");
        }

        var reader = new BondCompactReader(data, 4);
        return reader.ReadStruct(0);
    }

    private BondStruct ReadStruct(int depth)
    {
        if (depth > 32)
        {
            throw new FormatException("Bond structs nest too deeply.");
        }

        var fields = new List<BondField>();
        while (true)
        {
            var header = ReadByte();
            var type = (BondType)(header & 0x1F);
            var idBits = header >> 5;
            if (type == BondType.Stop)
            {
                return new BondStruct(fields);
            }

            if (type == BondType.StopBase)
            {
                continue;
            }

            ushort id = idBits switch
            {
                <= 5 => (ushort)idBits,
                6 => ReadByte(),
                _ => (ushort)(ReadByte() | (ReadByte() << 8)),
            };
            fields.Add(new BondField(id, type, ReadValue(type, depth)));
        }
    }

    private object ReadValue(BondType type, int depth) => type switch
    {
        BondType.Bool => ReadByte() != 0,
        BondType.UInt8 => (ulong)ReadByte(),
        BondType.Int8 => (long)(sbyte)ReadByte(),
        BondType.UInt16 or BondType.UInt32 or BondType.UInt64 => ReadVarUInt(),
        BondType.Int16 or BondType.Int32 or BondType.Int64 => ZigZag(ReadVarUInt()),
        BondType.Float => (double)BitConverter.ToSingle(ReadBytes(4)),
        BondType.Double => BitConverter.ToDouble(ReadBytes(8)),
        BondType.String => Encoding.UTF8.GetString(ReadBytes(checked((int)ReadVarUInt()))),
        BondType.WString => Encoding.Unicode.GetString(ReadBytes(checked((int)ReadVarUInt() * 2))),
        BondType.Struct => ReadStruct(depth + 1),
        BondType.List or BondType.Set => ReadList(depth),
        BondType.Map => ReadMap(depth),
        _ => throw new FormatException($"Unknown Bond type {(int)type} at offset {_position}."),
    };

    private object ReadList(int depth)
    {
        var elementType = (BondType)(ReadByte() & 0x1F);
        var count = checked((int)ReadVarUInt());
        if (elementType is BondType.Int8 or BondType.UInt8)
        {
            return ReadBytes(count).ToArray();
        }

        var items = new List<object>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(ReadValue(elementType, depth + 1));
        }

        return items;
    }

    private object ReadMap(int depth)
    {
        var keyType = (BondType)(ReadByte() & 0x1F);
        var valueType = (BondType)(ReadByte() & 0x1F);
        var count = checked((int)ReadVarUInt());
        var items = new List<object>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(new KeyValuePair<object, object>(ReadValue(keyType, depth + 1), ReadValue(valueType, depth + 1)));
        }

        return items;
    }

    private static long ZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    private ulong ReadVarUInt()
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            var b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new FormatException("Bond varint is too long.");
    }

    private byte ReadByte()
    {
        if (_position >= _data.Length)
        {
            throw new FormatException("Unexpected end of the Bond blob.");
        }

        return _data[_position++];
    }

    private ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || _position + count > _data.Length)
        {
            throw new FormatException("Unexpected end of the Bond blob.");
        }

        var span = _data.AsSpan(_position, count);
        _position += count;
        return span;
    }
}
