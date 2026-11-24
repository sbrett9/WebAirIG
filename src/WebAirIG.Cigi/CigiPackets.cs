namespace WebAirIG.Cigi;

/// <summary>
/// CIGI 4.0 (SISO-STD-013-2014) packet IDs used by WebAirIG. Layouts were verified against the Wireshark CIGI
/// dissector and the CIGI Class Library 4.0 Pack/Unpack code. Every packet starts with
/// [u16 Packet Size][u16 Packet ID]; offsets below are from the start of the packet.
/// </summary>
public static class CigiPacketId
{
    public const ushort IgControl = 0x0000;
    public const ushort EntityPosition = 0x0001;
    public const ushort ArticulatedPartControl = 0x0005;
    public const ushort ViewControl = 0x000F;
    public const ushort SensorControl = 0x0010;
    public const ushort ViewDefinition = 0x0014;
    public const ushort EntityControl = 0x0025;
    public const ushort StartOfFrame = 0xFFFF;
}

public enum IgMode : byte { ResetStandby = 0, Operate = 1, Debug = 2 }

public enum EntityState : byte { Standby = 0, Active = 1, Remove = 2 }

/// <summary>IG Control (Host to IG), ID 0x0000, 24 bytes. Must be the first packet of every host message.</summary>
public record struct IgControlPacket
{
    public const int Size = 24;
    public byte MajorVersion = 4;
    public sbyte DatabaseNumber;
    public bool EntityTypeSubstitutionEnable;
    public IgMode Mode;
    public bool TimestampValid;
    public bool SmoothingEnable;
    public byte MinorVersion;
    public uint HostFrameNumber;
    public uint LastIgFrameNumber;
    /// <summary>10 microsecond ticks.</summary>
    public uint Timestamp;

    public IgControlPacket() { }

    public readonly void Write(Span<byte> d, bool be)
    {
        E.Header(d, Size, CigiPacketId.IgControl, be);
        d[4] = MajorVersion;
        d[5] = unchecked((byte)DatabaseNumber);
        d[6] = E.Set(EntityTypeSubstitutionEnable, 0);
        d[7] = (byte)(((byte)Mode & 0x03) | E.Set(TimestampValid, 2) | E.Set(SmoothingEnable, 3) | ((MinorVersion & 0x0F) << 4));
        E.U32(d[8..], HostFrameNumber, be);
        E.U32(d[12..], LastIgFrameNumber, be);
        E.U32(d[16..], Timestamp, be);
    }

    public static IgControlPacket Read(ReadOnlySpan<byte> s, bool be) => new()
    {
        MajorVersion = s[4],
        DatabaseNumber = unchecked((sbyte)s[5]),
        EntityTypeSubstitutionEnable = E.Bit(s[6], 0),
        Mode = (IgMode)(s[7] & 0x03),
        TimestampValid = E.Bit(s[7], 2),
        SmoothingEnable = E.Bit(s[7], 3),
        MinorVersion = (byte)(s[7] >> 4),
        HostFrameNumber = E.U32(s[8..], be),
        LastIgFrameNumber = E.U32(s[12..], be),
        Timestamp = E.U32(s[16..], be),
    };
}

/// <summary>Start of Frame (IG to Host), ID 0xFFFF, 24 bytes. Must be the first packet of every IG message.</summary>
public record struct StartOfFramePacket
{
    public const int Size = 24;
    public byte MajorVersion = 4;
    public sbyte DatabaseNumber;
    public IgMode Mode;
    public bool TimestampValid;
    /// <summary>false = WGS-84, true = host-defined.</summary>
    public bool EarthReferenceModelHostDefined;
    public byte MinorVersion;
    public uint IgFrameNumber;
    public uint Timestamp;
    public uint LastHostFrameNumber;
    public bool Overframing;
    public bool Paging;
    public bool ExcessiveVariableLengthData;

    public StartOfFramePacket() { }

    public readonly void Write(Span<byte> d, bool be)
    {
        E.Header(d, Size, CigiPacketId.StartOfFrame, be);
        d[4] = MajorVersion;
        d[5] = unchecked((byte)DatabaseNumber);
        d[7] = (byte)(((byte)Mode & 0x03) | E.Set(TimestampValid, 2) | E.Set(EarthReferenceModelHostDefined, 3) | ((MinorVersion & 0x0F) << 4));
        E.U32(d[8..], IgFrameNumber, be);
        E.U32(d[12..], Timestamp, be);
        E.U32(d[16..], LastHostFrameNumber, be);
        d[20] = (byte)(E.Set(Overframing, 0) | E.Set(Paging, 1) | E.Set(ExcessiveVariableLengthData, 2));
    }

    public static StartOfFramePacket Read(ReadOnlySpan<byte> s, bool be)
    {
        // CCL packs the condition flags as a native u16 at offset 20, which lands in byte 21 when little-endian.
        byte flags = (byte)((s[20] | s[21]) & 0x07);
        return new StartOfFramePacket
        {
            MajorVersion = s[4],
            DatabaseNumber = unchecked((sbyte)s[5]),
            Mode = (IgMode)(s[7] & 0x03),
            TimestampValid = E.Bit(s[7], 2),
            EarthReferenceModelHostDefined = E.Bit(s[7], 3),
            MinorVersion = (byte)(s[7] >> 4),
            IgFrameNumber = E.U32(s[8..], be),
            Timestamp = E.U32(s[12..], be),
            LastHostFrameNumber = E.U32(s[16..], be),
            Overframing = E.Bit(flags, 0),
            Paging = E.Bit(flags, 1),
            ExcessiveVariableLengthData = E.Bit(flags, 2),
        };
    }
}

/// <summary>Entity Position (Host to IG), ID 0x0001, 48 bytes. Top-level entities use geodetic lat/lon/alt.</summary>
public record struct EntityPositionPacket
{
    public const int Size = 48;
    public bool Attached;
    /// <summary>0 none, 1 non-conformal, 2 conformal.</summary>
    public byte GroundClamp;
    public ushort EntityId;
    public ushort ParentId;
    public float RollDeg;
    public float PitchDeg;
    public float YawDeg;
    /// <summary>Latitude (deg) or X offset (m) when attached.</summary>
    public double LatOrX;
    public double LonOrY;
    public double AltOrZ;

    public readonly void Write(Span<byte> d, bool be)
    {
        E.Header(d, Size, CigiPacketId.EntityPosition, be);
        d[4] = (byte)(E.Set(Attached, 0) | ((GroundClamp & 0x03) << 1));
        E.U16(d[6..], EntityId, be);
        E.U16(d[8..], ParentId, be);
        E.F32(d[12..], RollDeg, be);
        E.F32(d[16..], PitchDeg, be);
        E.F32(d[20..], YawDeg, be);
        E.F64(d[24..], LatOrX, be);
        E.F64(d[32..], LonOrY, be);
        E.F64(d[40..], AltOrZ, be);
    }

    public static EntityPositionPacket Read(ReadOnlySpan<byte> s, bool be) => new()
    {
        Attached = E.Bit(s[4], 0),
        GroundClamp = (byte)((s[4] >> 1) & 0x03),
        EntityId = E.U16(s[6..], be),
        ParentId = E.U16(s[8..], be),
        RollDeg = E.F32(s[12..], be),
        PitchDeg = E.F32(s[16..], be),
        YawDeg = E.F32(s[20..], be),
        LatOrX = E.F64(s[24..], be),
        LonOrY = E.F64(s[32..], be),
        AltOrZ = E.F64(s[40..], be),
    };
}

/// <summary>Entity Control (Host to IG), ID 0x0025, 16 bytes. Creates/removes entities and sets their type.</summary>
public record struct EntityControlPacket
{
    public const int Size = 16;
    public EntityState State;
    public bool CollisionDetect;
    public bool InheritAlpha;
    public bool Smoothing;
    /// <summary>false = short entity type (u16 at offset 10); true = DIS-style extended type.</summary>
    public bool ExtendedEntityType;
    public byte Alpha;
    public ushort EntityId;
    public byte Kind;
    public byte Domain;
    /// <summary>Country (extended) or short entity type (short).</summary>
    public ushort CountryOrShortType;
    public byte Category;
    public byte Subcategory;
    public byte Specific;
    public byte Extra;

    public readonly void Write(Span<byte> d, bool be)
    {
        E.Header(d, Size, CigiPacketId.EntityControl, be);
        d[4] = (byte)(((byte)State & 0x03) | E.Set(CollisionDetect, 2) | E.Set(InheritAlpha, 3) | E.Set(Smoothing, 4) | E.Set(ExtendedEntityType, 5));
        d[5] = Alpha;
        E.U16(d[6..], EntityId, be);
        d[8] = Kind;
        d[9] = Domain;
        E.U16(d[10..], CountryOrShortType, be);
        d[12] = Category;
        d[13] = Subcategory;
        d[14] = Specific;
        d[15] = Extra;
    }

    public static EntityControlPacket Read(ReadOnlySpan<byte> s, bool be) => new()
    {
        State = (EntityState)(s[4] & 0x03),
        CollisionDetect = E.Bit(s[4], 2),
        InheritAlpha = E.Bit(s[4], 3),
        Smoothing = E.Bit(s[4], 4),
        ExtendedEntityType = E.Bit(s[4], 5),
        Alpha = s[5],
        EntityId = E.U16(s[6..], be),
        Kind = s[8],
        Domain = s[9],
        CountryOrShortType = E.U16(s[10..], be),
        Category = s[12],
        Subcategory = s[13],
        Specific = s[14],
        Extra = s[15],
    };
}

/// <summary>Articulated Part Control (Host to IG), ID 0x0005, 32 bytes. WebAirIG drives camera gimbals with it.</summary>
public record struct ArticulatedPartControlPacket
{
    public const int Size = 32;
    public ushort EntityId;
    public byte PartId;
    public bool PartEnable;
    public bool XEnable, YEnable, ZEnable, RollEnable, PitchEnable, YawEnable;
    public float X, Y, Z;
    public float RollDeg, PitchDeg, YawDeg;

    public readonly void Write(Span<byte> d, bool be)
    {
        E.Header(d, Size, CigiPacketId.ArticulatedPartControl, be);
        E.U16(d[4..], EntityId, be);
        d[6] = PartId;
        d[7] = (byte)(E.Set(PartEnable, 0) | E.Set(XEnable, 1) | E.Set(YEnable, 2) | E.Set(ZEnable, 3) |
                      E.Set(RollEnable, 4) | E.Set(PitchEnable, 5) | E.Set(YawEnable, 6));
        E.F32(d[8..], X, be);
        E.F32(d[12..], Y, be);
        E.F32(d[16..], Z, be);
        E.F32(d[20..], RollDeg, be);
        E.F32(d[24..], PitchDeg, be);
        E.F32(d[28..], YawDeg, be);
    }

    public static ArticulatedPartControlPacket Read(ReadOnlySpan<byte> s, bool be) => new()
    {
        EntityId = E.U16(s[4..], be),
        PartId = s[6],
        PartEnable = E.Bit(s[7], 0),
        XEnable = E.Bit(s[7], 1),
        YEnable = E.Bit(s[7], 2),
        ZEnable = E.Bit(s[7], 3),
        RollEnable = E.Bit(s[7], 4),
        PitchEnable = E.Bit(s[7], 5),
        YawEnable = E.Bit(s[7], 6),
        X = E.F32(s[8..], be),
        Y = E.F32(s[12..], be),
        Z = E.F32(s[16..], be),
        RollDeg = E.F32(s[20..], be),
        PitchDeg = E.F32(s[24..], be),
        YawDeg = E.F32(s[28..], be),
    };
}

/// <summary>View Control (Host to IG), ID 0x000F, 40 bytes. Attaches a view to an entity with an eyepoint offset.</summary>
public record struct ViewControlPacket
{
    public const int Size = 40;
    public byte GroupId;
    public bool XEnable, YEnable, ZEnable, RollEnable, PitchEnable, YawEnable;
    public ushort ViewId;
    public ushort EntityId;
    public float X, Y, Z;
    public float RollDeg, PitchDeg, YawDeg;

    public readonly void Write(Span<byte> d, bool be)
    {
        E.Header(d, Size, CigiPacketId.ViewControl, be);
        d[4] = GroupId;
        d[5] = (byte)(E.Set(XEnable, 0) | E.Set(YEnable, 1) | E.Set(ZEnable, 2) | E.Set(RollEnable, 3) | E.Set(PitchEnable, 4) | E.Set(YawEnable, 5));
        E.U16(d[6..], ViewId, be);
        E.U16(d[8..], EntityId, be);
        E.F32(d[12..], X, be);
        E.F32(d[16..], Y, be);
        E.F32(d[20..], Z, be);
        E.F32(d[24..], RollDeg, be);
        E.F32(d[28..], PitchDeg, be);
        E.F32(d[32..], YawDeg, be);
    }

    public static ViewControlPacket Read(ReadOnlySpan<byte> s, bool be) => new()
    {
        GroupId = s[4],
        XEnable = E.Bit(s[5], 0),
        YEnable = E.Bit(s[5], 1),
        ZEnable = E.Bit(s[5], 2),
        RollEnable = E.Bit(s[5], 3),
        PitchEnable = E.Bit(s[5], 4),
        YawEnable = E.Bit(s[5], 5),
        ViewId = E.U16(s[6..], be),
        EntityId = E.U16(s[8..], be),
        X = E.F32(s[12..], be),
        Y = E.F32(s[16..], be),
        Z = E.F32(s[20..], be),
        RollDeg = E.F32(s[24..], be),
        PitchDeg = E.F32(s[28..], be),
        YawDeg = E.F32(s[32..], be),
    };
}

/// <summary>View Definition (Host to IG), ID 0x0014, 40 bytes. Field of view half-angles in degrees.</summary>
public record struct ViewDefinitionPacket
{
    public const int Size = 40;
    public ushort ViewId;
    public byte GroupId;
    public bool NearEnable, FarEnable, LeftEnable, RightEnable, TopEnable, BottomEnable;
    public byte MirrorMode;
    public byte PixelReplication;
    public bool Orthographic;
    public bool Reorder;
    public byte ViewType;
    public float Near, Far;
    public float LeftDeg, RightDeg, TopDeg, BottomDeg;

    public readonly void Write(Span<byte> d, bool be)
    {
        E.Header(d, Size, CigiPacketId.ViewDefinition, be);
        E.U16(d[4..], ViewId, be);
        d[6] = GroupId;
        d[7] = (byte)(E.Set(NearEnable, 0) | E.Set(FarEnable, 1) | E.Set(LeftEnable, 2) | E.Set(RightEnable, 3) |
                      E.Set(TopEnable, 4) | E.Set(BottomEnable, 5) | ((MirrorMode & 0x03) << 6));
        d[8] = (byte)((PixelReplication & 0x07) | E.Set(Orthographic, 3) | E.Set(Reorder, 4) | ((ViewType & 0x07) << 5));
        E.F32(d[12..], Near, be);
        E.F32(d[16..], Far, be);
        E.F32(d[20..], LeftDeg, be);
        E.F32(d[24..], RightDeg, be);
        E.F32(d[28..], TopDeg, be);
        E.F32(d[32..], BottomDeg, be);
    }

    public static ViewDefinitionPacket Read(ReadOnlySpan<byte> s, bool be) => new()
    {
        ViewId = E.U16(s[4..], be),
        GroupId = s[6],
        NearEnable = E.Bit(s[7], 0),
        FarEnable = E.Bit(s[7], 1),
        LeftEnable = E.Bit(s[7], 2),
        RightEnable = E.Bit(s[7], 3),
        TopEnable = E.Bit(s[7], 4),
        BottomEnable = E.Bit(s[7], 5),
        MirrorMode = (byte)(s[7] >> 6),
        PixelReplication = (byte)(s[8] & 0x07),
        Orthographic = E.Bit(s[8], 3),
        Reorder = E.Bit(s[8], 4),
        ViewType = (byte)(s[8] >> 5),
        Near = E.F32(s[12..], be),
        Far = E.F32(s[16..], be),
        LeftDeg = E.F32(s[20..], be),
        RightDeg = E.F32(s[24..], be),
        TopDeg = E.F32(s[28..], be),
        BottomDeg = E.F32(s[32..], be),
    };
}

/// <summary>Sensor Control (Host to IG), ID 0x0010, 32 bytes. Drives the pseudo-IR post-process.</summary>
public record struct SensorControlPacket
{
    public const int Size = 32;
    public byte SensorId;
    public bool SensorOn;
    /// <summary>false = white-hot, true = black-hot.</summary>
    public bool BlackHot;
    public bool LineDropout;
    public bool AutoGain;
    public bool TrackBlack;
    public byte TrackMode;
    public bool ExtendedResponse;
    public ushort ViewId;
    public float Gain;
    public float Level;
    public float AcCouplingUs;
    public float Noise;

    public readonly void Write(Span<byte> d, bool be)
    {
        E.Header(d, Size, CigiPacketId.SensorControl, be);
        d[4] = SensorId;
        d[5] = (byte)(E.Set(SensorOn, 0) | E.Set(BlackHot, 1) | E.Set(LineDropout, 2) | E.Set(AutoGain, 3) | E.Set(TrackBlack, 4) | ((TrackMode & 0x07) << 5));
        d[6] = E.Set(ExtendedResponse, 0);
        E.U16(d[8..], ViewId, be);
        E.F32(d[12..], Gain, be);
        E.F32(d[16..], Level, be);
        E.F32(d[20..], AcCouplingUs, be);
        E.F32(d[24..], Noise, be);
    }

    public static SensorControlPacket Read(ReadOnlySpan<byte> s, bool be) => new()
    {
        SensorId = s[4],
        SensorOn = E.Bit(s[5], 0),
        BlackHot = E.Bit(s[5], 1),
        LineDropout = E.Bit(s[5], 2),
        AutoGain = E.Bit(s[5], 3),
        TrackBlack = E.Bit(s[5], 4),
        TrackMode = (byte)(s[5] >> 5),
        ExtendedResponse = E.Bit(s[6], 0),
        ViewId = E.U16(s[8..], be),
        Gain = E.F32(s[12..], be),
        Level = E.F32(s[16..], be),
        AcCouplingUs = E.F32(s[20..], be),
        Noise = E.F32(s[24..], be),
    };
}
