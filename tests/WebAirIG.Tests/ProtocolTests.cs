using WebAirIG.Cigi;
using WebAirIG.Common.Geo;
using WebAirIG.Common.Transport;
using WebAirIG.Common.Video;
using WebAirIG.Dis;

namespace WebAirIG.Tests;

public class CigiTests
{
    private sealed class Collector : CigiPacketHandler
    {
        public readonly List<object> Packets = [];
        public override void OnIgControl(in IgControlPacket p) => Packets.Add(p);
        public override void OnStartOfFrame(in StartOfFramePacket p) => Packets.Add(p);
        public override void OnEntityPosition(in EntityPositionPacket p) => Packets.Add(p);
        public override void OnEntityControl(in EntityControlPacket p) => Packets.Add(p);
        public override void OnArticulatedPartControl(in ArticulatedPartControlPacket p) => Packets.Add(p);
        public override void OnViewControl(in ViewControlPacket p) => Packets.Add(p);
        public override void OnViewDefinition(in ViewDefinitionPacket p) => Packets.Add(p);
        public override void OnSensorControl(in SensorControlPacket p) => Packets.Add(p);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostMessage_RoundTrips_InBothByteOrders(bool bigEndian)
    {
        var b = new CigiMessageBuilder { BigEndian = bigEndian };
        var ig = new IgControlPacket { Mode = IgMode.Operate, HostFrameNumber = 77, LastIgFrameNumber = 76, Timestamp = 123456, TimestampValid = true, DatabaseNumber = -3 };
        var ec = new EntityControlPacket { EntityId = 4, State = EntityState.Active, CountryOrShortType = 12, Alpha = 255 };
        var ep = new EntityPositionPacket { EntityId = 4, LatOrX = 35.123456789, LonOrY = -117.5, AltOrZ = 5000.25, YawDeg = 271, PitchDeg = -2, RollDeg = 25 };
        var ap = new ArticulatedPartControlPacket { EntityId = 0, PartId = 101, PartEnable = true, YawEnable = true, PitchEnable = true, YawDeg = 12.5f, PitchDeg = -7f };
        var vc = new ViewControlPacket { ViewId = 2, EntityId = 0, XEnable = true, YEnable = true, ZEnable = true, PitchEnable = true, X = 20, Y = -2, Z = 3, PitchDeg = -90 };
        var vd = new ViewDefinitionPacket { ViewId = 2, LeftEnable = true, RightEnable = true, TopEnable = true, BottomEnable = true, LeftDeg = -5, RightDeg = 5, TopDeg = 5, BottomDeg = -5, Near = 0.5f, Far = 90000, NearEnable = true, FarEnable = true };
        var sc = new SensorControlPacket { ViewId = 2, SensorId = 2, SensorOn = true, BlackHot = true, Gain = 1.5f, Level = 0.45f, Noise = 0.01f };
        b.Add(ig).Add(ec).Add(ep).Add(ap).Add(vc).Add(vd).Add(sc);
        var bytes = b.ToArray();

        Assert.Equal(24 + 16 + 48 + 32 + 40 + 40 + 32, bytes.Length);
        Assert.Equal(0, bytes.Length % 8);
        Assert.Equal(bigEndian, CigiMessageParser.IsBigEndian(bytes));
        Assert.Equal(4, CigiMessageParser.MajorVersion(bytes));

        var c = new Collector();
        Assert.Equal(7, CigiMessageParser.Parse(bytes, c));
        Assert.Equal(ig, c.Packets[0]);
        Assert.Equal(ec, c.Packets[1]);
        Assert.Equal(ep, c.Packets[2]);
        Assert.Equal(ap, c.Packets[3]);
        Assert.Equal(vc, c.Packets[4]);
        Assert.Equal(vd, c.Packets[5]);
        Assert.Equal(sc, c.Packets[6]);
    }

    [Fact]
    public void IgControl_Header_Is_Size_Then_Id()
    {
        var b = new CigiMessageBuilder { BigEndian = true };
        b.Add(new IgControlPacket { Mode = IgMode.Operate });
        var s = b.Span;
        Assert.Equal(new byte[] { 0x00, 0x18, 0x00, 0x00, 0x04 }, s[..5].ToArray());
        Assert.Equal(1, s[7] & 0x03);
    }

    [Fact]
    public void StartOfFrame_RoundTrip()
    {
        var b = new CigiMessageBuilder();
        var sof = new StartOfFramePacket { Mode = IgMode.Operate, IgFrameNumber = 1000, Timestamp = 3_333_300, LastHostFrameNumber = 999, TimestampValid = true, Overframing = true };
        b.Add(sof);
        var bytes = b.ToArray();
        Assert.Equal(0xFF, bytes[2]);
        Assert.Equal(0xFF, bytes[3]);
        var c = new Collector();
        CigiMessageParser.Parse(bytes, c);
        Assert.Equal(sof, c.Packets[0]);
    }
}

public class DisTests
{
    [Fact]
    public void EntityStatePdu_Is144Bytes_And_RoundTrips()
    {
        var pdu = new EntityStatePdu
        {
            ExerciseId = 3,
            Timestamp = DisConversions.AbsoluteTimestamp(new DateTime(2026, 11, 20, 10, 30, 0, DateTimeKind.Utc)),
            EntityId = new DisEntityId(1, 3001, 7),
            ForceId = 1,
            EntityType = new DisEntityType(1, 2, 225, 1, 2, 3, 0),
            LinearVelocity = new DisVec3f(1, 2, 3),
            LocationX = -2_500_000.5, LocationY = -4_700_000.25, LocationZ = 3_600_000.125,
            Psi = 1, Theta = -0.5f, Phi = 0.25f,
            Marking = "OWNSHIP777XYZ",
        };
        var bytes = pdu.ToBytes();
        Assert.Equal(144, bytes.Length);
        Assert.Equal(7, bytes[0]);
        Assert.Equal(1, bytes[2]);
        Assert.Equal(144, (bytes[8] << 8) | bytes[9]);
        Assert.True(EntityStatePdu.TryParse(bytes, out var back));
        Assert.Equal(pdu.EntityId, back!.EntityId);
        Assert.Equal(pdu.EntityType, back.EntityType);
        Assert.Equal(pdu.LocationX, back.LocationX);
        Assert.Equal("OWNSHIP777X", back.Marking);
        Assert.Equal(1u, back.Timestamp & 1u);
    }

    [Fact]
    public void Orientation_RoundTrips_Through_Ecef()
    {
        var pos = new GeoPoint(35, -117, 5000);
        var nedFromBody = Mat3d.FromYawPitchRoll(0.8, 0.1, -0.2);
        var (psi, theta, phi) = DisConversions.OrientationFromLocalNed(pos, nedFromBody);
        var back = DisConversions.LocalNedFromOrientation(pos, psi, theta, phi);
        var (y, p, r) = back.ToYawPitchRoll();
        Assert.Equal(0.8, y, 5);
        Assert.Equal(0.1, p, 5);
        Assert.Equal(-0.2, r, 5);
    }
}

public class TransportTests
{
    [Fact]
    public void FrameMessage_RoundTrip()
    {
        var m = new FrameMessage
        {
            Codec = VideoCodec.Hevc, CameraId = 3, Flags = FrameFlags.Keyframe, FrameNumber = 123456789, SimTime = 42.5,
            UtcTicks = 99, Width = 2048, Height = 2048, SessionId = 0xDEADBEEF, Sequence = 4242,
            Payload = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray(),
            Truth = "{\"a\":1}"u8.ToArray(),
        };
        Assert.True(FrameMessage.TryParse(m.Serialize(), out var back));
        Assert.Equal(m.FrameNumber, back!.FrameNumber);
        Assert.Equal(m.SessionId, back.SessionId);
        Assert.Equal(4242u, back.Sequence);
        Assert.True(back.IsKeyframe);
        Assert.Equal(m.Payload, back.Payload);
        Assert.Equal(m.Truth, back.Truth);
    }

    [Fact]
    public void Udp_Fragment_Reassemble_OutOfOrder_WithDuplicates()
    {
        var msg = new byte[250_000];
        new Random(1).NextBytes(msg);
        var datagrams = new List<byte[]>();
        var scratch = new byte[1400];
        int n = UdpFragmentation.Fragment(msg, 2, 7, 55, 1400, scratch, dg => datagrams.Add(dg.ToArray()));
        Assert.Equal(datagrams.Count, n);
        Assert.True(n > 100);

        var r = new UdpReassembler(TimeSpan.FromSeconds(1));
        var shuffled = datagrams.OrderBy(_ => Random.Shared.Next()).ToList();
        shuffled.Insert(10, shuffled[3]); // duplicate
        byte[]? result = null;
        foreach (var dg in shuffled) result ??= r.Add(dg);
        Assert.NotNull(result);
        Assert.Equal(msg, result);
    }

    [Fact]
    public void Udp_Reassembler_Incomplete_Returns_Null()
    {
        var msg = new byte[10_000];
        var datagrams = new List<byte[]>();
        UdpFragmentation.Fragment(msg, 0, 1, 1, 1400, new byte[1400], dg => datagrams.Add(dg.ToArray()));
        var r = new UdpReassembler(TimeSpan.FromSeconds(1));
        foreach (var dg in datagrams.Skip(1)) Assert.Null(r.Add(dg));
    }
}

public class AnnexBTests
{
    private static byte[] Nal(params byte[] body) => [0, 0, 0, 1, .. body];

    [Fact]
    public void H264_Splits_On_Aud_And_Detects_Idr()
    {
        // AU0: AUD, SPS, PPS, IDR slice (first_mb=0). AU1: AUD, P slice. AU2: AUD, P slice.
        var stream = new List<byte>();
        stream.AddRange(Nal(0x09, 0xF0));
        stream.AddRange(Nal(0x67, 0x42, 0x00, 0x1F));
        stream.AddRange(Nal(0x68, 0xCE, 0x3C, 0x80));
        stream.AddRange(Nal(0x65, 0x88, 0x84, 0x00, 0x33));
        stream.AddRange(Nal(0x09, 0xF0));
        stream.AddRange(Nal(0x41, 0x9A, 0x02, 0x03));
        stream.AddRange(Nal(0x09, 0xF0));
        stream.AddRange(Nal(0x41, 0x9A, 0x04, 0x05));

        var aus = new List<(byte[] Au, bool Key)>();
        var splitter = new AnnexBAccessUnitSplitter(VideoCodec.H264);
        // Feed in awkward chunk sizes to exercise boundary handling.
        var data = stream.ToArray();
        for (int i = 0; i < data.Length; i += 3)
            splitter.Push(data.AsSpan(i, Math.Min(3, data.Length - i)), (au, key) => aus.Add((au, key)));
        splitter.Flush((au, key) => aus.Add((au, key)));

        Assert.Equal(3, aus.Count);
        Assert.True(aus[0].Key);
        Assert.False(aus[1].Key);
        Assert.False(aus[2].Key);
        Assert.Equal(data.Length, aus.Sum(a => a.Au.Length));
    }

    [Fact]
    public void H264_Splits_Without_Aud_Using_FirstSlice()
    {
        var stream = new List<byte>();
        stream.AddRange(Nal(0x65, 0x88, 0x11));   // IDR, first_mb = 0
        stream.AddRange(Nal(0x65, 0x40, 0x11));   // IDR, first_mb != 0 (second slice, same picture)
        stream.AddRange(Nal(0x41, 0x9A, 0x22));   // P, first_mb = 0 -> new AU
        stream.AddRange(Nal(0x41, 0x9A, 0x33));   // P, first_mb = 0 -> new AU
        var aus = new List<bool>();
        var s = new AnnexBAccessUnitSplitter(VideoCodec.H264);
        s.Push(stream.ToArray(), (_, k) => aus.Add(k));
        s.Flush((_, k) => aus.Add(k));
        Assert.Equal(new[] { true, false, false }, aus);
    }

    [Fact]
    public void Hevc_Splits_On_Aud()
    {
        var stream = new List<byte>();
        stream.AddRange(Nal(0x46, 0x01, 0x50));             // AUD (type 35)
        stream.AddRange(Nal(0x40, 0x01, 0x0C));             // VPS (32)
        stream.AddRange(Nal(0x26, 0x01, 0xAF, 0x11));       // IDR_W_RADL (19), first slice
        stream.AddRange(Nal(0x46, 0x01, 0x50));             // AUD
        stream.AddRange(Nal(0x02, 0x01, 0xD0, 0x22));       // TRAIL_R (1), first slice
        var aus = new List<bool>();
        var s = new AnnexBAccessUnitSplitter(VideoCodec.Hevc);
        s.Push(stream.ToArray(), (_, k) => aus.Add(k));
        s.Flush((_, k) => aus.Add(k));
        Assert.Equal(new[] { true, false }, aus);
    }
}
