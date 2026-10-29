namespace MyWahoo.Core.Protocol;

/// <summary>Minimal protobuf (proto2 wire format) writer: just what the Zwift protocols need.</summary>
public sealed class ProtoWriter
{
    private readonly List<byte> _buf = [];

    public ProtoWriter UInt(int field, ulong value)
    {
        Tag(field, 0);
        Varint(value);
        return this;
    }

    /// <summary>sint32/sint64: zigzag-encoded varint.</summary>
    public ProtoWriter SInt(int field, long value)
    {
        Tag(field, 0);
        Varint((ulong)((value << 1) ^ (value >> 63)));
        return this;
    }

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> value)
    {
        Tag(field, 2);
        Varint((ulong)value.Length);
        foreach (var b in value) _buf.Add(b);
        return this;
    }

    public ProtoWriter Message(int field, ProtoWriter inner) => Bytes(field, inner.ToArray());

    public byte[] ToArray() => [.. _buf];

    private void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

    private void Varint(ulong v)
    {
        while (v >= 0x80)
        {
            _buf.Add((byte)(v | 0x80));
            v >>= 7;
        }
        _buf.Add((byte)v);
    }
}

public readonly record struct ProtoField(int Number, int WireType, ulong Varint, ReadOnlyMemory<byte> Bytes)
{
    public long ZigZag => (long)(Varint >> 1) ^ -(long)(Varint & 1);
}

/// <summary>Minimal protobuf reader. Unknown fields are returned to the caller; malformed input stops iteration.</summary>
public static class ProtoReader
{
    public static IEnumerable<ProtoField> Read(ReadOnlyMemory<byte> data)
    {
        var pos = 0;
        while (pos < data.Length)
        {
            if (!TryVarint(data.Span, ref pos, out var key)) yield break;
            var number = (int)(key >> 3);
            var wire = (int)(key & 7);
            switch (wire)
            {
                case 0:
                    if (!TryVarint(data.Span, ref pos, out var v)) yield break;
                    yield return new ProtoField(number, wire, v, default);
                    break;
                case 1:
                    if (pos + 8 > data.Length) yield break;
                    yield return new ProtoField(number, wire, BitConverter.ToUInt64(data.Span.Slice(pos, 8)), data.Slice(pos, 8));
                    pos += 8;
                    break;
                case 2:
                    if (!TryVarint(data.Span, ref pos, out var len) || pos + (int)len > data.Length) yield break;
                    yield return new ProtoField(number, wire, 0, data.Slice(pos, (int)len));
                    pos += (int)len;
                    break;
                case 5:
                    if (pos + 4 > data.Length) yield break;
                    yield return new ProtoField(number, wire, BitConverter.ToUInt32(data.Span.Slice(pos, 4)), data.Slice(pos, 4));
                    pos += 4;
                    break;
                default:
                    yield break;
            }
        }
    }

    private static bool TryVarint(ReadOnlySpan<byte> s, ref int pos, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && pos < s.Length; shift += 7)
        {
            var b = s[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }
}
