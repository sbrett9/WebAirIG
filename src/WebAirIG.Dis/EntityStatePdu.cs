using System.Buffers.Binary;
using System.Text;

namespace WebAirIG.Dis;

public readonly record struct DisEntityId(ushort Site, ushort Application, ushort Entity);

public readonly record struct DisEntityType(byte Kind, byte Domain, ushort Country, byte Category, byte Subcategory, byte Specific, byte Extra);

public readonly record struct DisVec3f(float X, float Y, float Z);

/// <summary>
/// DIS (IEEE 1278.1-2012, protocol version 7) Entity State PDU with no variable parameter records (144 bytes).
/// All fields big-endian. Location is ECEF meters; orientation is psi/theta/phi (rad) of body relative to ECEF.
/// </summary>
public sealed class EntityStatePdu
{
    public const int Length = 144;
    public const byte ProtocolVersion = 7;
    public const byte PduType = 1;
    public const byte ProtocolFamily = 1;

    public byte ExerciseId { get; set; } = 1;
    public uint Timestamp { get; set; }
    public DisEntityId EntityId { get; set; }
    public byte ForceId { get; set; }
    public DisEntityType EntityType { get; set; }
    public DisEntityType AlternativeEntityType { get; set; }
    public DisVec3f LinearVelocity { get; set; }
    public double LocationX { get; set; }
    public double LocationY { get; set; }
    public double LocationZ { get; set; }
    public float Psi { get; set; }
    public float Theta { get; set; }
    public float Phi { get; set; }
    public uint Appearance { get; set; }
    /// <summary>2 = DRM(F,P,W): constant velocity, fixed orientation, world coordinates.</summary>
    public byte DeadReckoningAlgorithm { get; set; } = 2;
    public DisVec3f LinearAcceleration { get; set; }
    public DisVec3f AngularVelocity { get; set; }
    public string Marking { get; set; } = "";
    public uint Capabilities { get; set; }

    public byte[] ToBytes()
    {
        var b = new byte[Length];
        WriteTo(b);
        return b;
    }

    public void WriteTo(Span<byte> d)
    {
        if (d.Length < Length) throw new ArgumentException("Buffer too small", nameof(d));
        d[..Length].Clear();
        d[0] = ProtocolVersion;
        d[1] = ExerciseId;
        d[2] = PduType;
        d[3] = ProtocolFamily;
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], Timestamp);
        BinaryPrimitives.WriteUInt16BigEndian(d[8..], Length);
        // d[10] PDU status, d[11] padding = 0
        BinaryPrimitives.WriteUInt16BigEndian(d[12..], EntityId.Site);
        BinaryPrimitives.WriteUInt16BigEndian(d[14..], EntityId.Application);
        BinaryPrimitives.WriteUInt16BigEndian(d[16..], EntityId.Entity);
        d[18] = ForceId;
        d[19] = 0; // number of variable parameter records
        WriteType(d[20..], EntityType);
        WriteType(d[28..], AlternativeEntityType);
        WriteVec(d[36..], LinearVelocity);
        BinaryPrimitives.WriteDoubleBigEndian(d[48..], LocationX);
        BinaryPrimitives.WriteDoubleBigEndian(d[56..], LocationY);
        BinaryPrimitives.WriteDoubleBigEndian(d[64..], LocationZ);
        BinaryPrimitives.WriteSingleBigEndian(d[72..], Psi);
        BinaryPrimitives.WriteSingleBigEndian(d[76..], Theta);
        BinaryPrimitives.WriteSingleBigEndian(d[80..], Phi);
        BinaryPrimitives.WriteUInt32BigEndian(d[84..], Appearance);
        d[88] = DeadReckoningAlgorithm;
        // d[89..104) other DR parameters = 0
        WriteVec(d[104..], LinearAcceleration);
        WriteVec(d[116..], AngularVelocity);
        d[128] = 1; // character set ASCII
        var mark = Encoding.ASCII.GetBytes(Marking.Length > 11 ? Marking[..11] : Marking);
        mark.CopyTo(d[129..]);
        BinaryPrimitives.WriteUInt32BigEndian(d[140..], Capabilities);
    }

    public static bool TryParse(ReadOnlySpan<byte> s, out EntityStatePdu? pdu)
    {
        pdu = null;
        if (s.Length < Length || s[2] != PduType) return false;
        int len = BinaryPrimitives.ReadUInt16BigEndian(s[8..]);
        if (len < Length || len > s.Length) return false;
        int markLen = s.Slice(129, 11).IndexOf((byte)0);
        if (markLen < 0) markLen = 11;
        pdu = new EntityStatePdu
        {
            ExerciseId = s[1],
            Timestamp = BinaryPrimitives.ReadUInt32BigEndian(s[4..]),
            EntityId = new DisEntityId(
                BinaryPrimitives.ReadUInt16BigEndian(s[12..]),
                BinaryPrimitives.ReadUInt16BigEndian(s[14..]),
                BinaryPrimitives.ReadUInt16BigEndian(s[16..])),
            ForceId = s[18],
            EntityType = ReadType(s[20..]),
            AlternativeEntityType = ReadType(s[28..]),
            LinearVelocity = ReadVec(s[36..]),
            LocationX = BinaryPrimitives.ReadDoubleBigEndian(s[48..]),
            LocationY = BinaryPrimitives.ReadDoubleBigEndian(s[56..]),
            LocationZ = BinaryPrimitives.ReadDoubleBigEndian(s[64..]),
            Psi = BinaryPrimitives.ReadSingleBigEndian(s[72..]),
            Theta = BinaryPrimitives.ReadSingleBigEndian(s[76..]),
            Phi = BinaryPrimitives.ReadSingleBigEndian(s[80..]),
            Appearance = BinaryPrimitives.ReadUInt32BigEndian(s[84..]),
            DeadReckoningAlgorithm = s[88],
            LinearAcceleration = ReadVec(s[104..]),
            AngularVelocity = ReadVec(s[116..]),
            Marking = Encoding.ASCII.GetString(s.Slice(129, markLen)),
            Capabilities = BinaryPrimitives.ReadUInt32BigEndian(s[140..]),
        };
        return true;
    }

    private static void WriteType(Span<byte> d, DisEntityType t)
    {
        d[0] = t.Kind; d[1] = t.Domain;
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], t.Country);
        d[4] = t.Category; d[5] = t.Subcategory; d[6] = t.Specific; d[7] = t.Extra;
    }

    private static DisEntityType ReadType(ReadOnlySpan<byte> s) =>
        new(s[0], s[1], BinaryPrimitives.ReadUInt16BigEndian(s[2..]), s[4], s[5], s[6], s[7]);

    private static void WriteVec(Span<byte> d, DisVec3f v)
    {
        BinaryPrimitives.WriteSingleBigEndian(d, v.X);
        BinaryPrimitives.WriteSingleBigEndian(d[4..], v.Y);
        BinaryPrimitives.WriteSingleBigEndian(d[8..], v.Z);
    }

    private static DisVec3f ReadVec(ReadOnlySpan<byte> s) =>
        new(BinaryPrimitives.ReadSingleBigEndian(s), BinaryPrimitives.ReadSingleBigEndian(s[4..]), BinaryPrimitives.ReadSingleBigEndian(s[8..]));
}
