using System.Buffers.Binary;

namespace WebAirIG.Cigi;

/// <summary>Byte-order aware primitive access. CIGI 4.0 messages may be sent in either byte order.</summary>
internal static class E
{
    public static ushort U16(ReadOnlySpan<byte> s, bool be) => be ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
    public static uint U32(ReadOnlySpan<byte> s, bool be) => be ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
    public static float F32(ReadOnlySpan<byte> s, bool be) => be ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s);
    public static double F64(ReadOnlySpan<byte> s, bool be) => be ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s);

    public static void U16(Span<byte> d, ushort v, bool be) { if (be) BinaryPrimitives.WriteUInt16BigEndian(d, v); else BinaryPrimitives.WriteUInt16LittleEndian(d, v); }
    public static void U32(Span<byte> d, uint v, bool be) { if (be) BinaryPrimitives.WriteUInt32BigEndian(d, v); else BinaryPrimitives.WriteUInt32LittleEndian(d, v); }
    public static void F32(Span<byte> d, float v, bool be) { if (be) BinaryPrimitives.WriteSingleBigEndian(d, v); else BinaryPrimitives.WriteSingleLittleEndian(d, v); }
    public static void F64(Span<byte> d, double v, bool be) { if (be) BinaryPrimitives.WriteDoubleBigEndian(d, v); else BinaryPrimitives.WriteDoubleLittleEndian(d, v); }

    public static void Header(Span<byte> d, int size, ushort id, bool be)
    {
        d[..size].Clear();
        U16(d, (ushort)size, be);
        U16(d[2..], id, be);
    }

    public static bool Bit(byte b, int bit) => (b & (1 << bit)) != 0;
    public static byte Set(bool v, int bit) => v ? (byte)(1 << bit) : (byte)0;
}
