namespace WebAirIG.Cigi;

/// <summary>Builds one CIGI message (one UDP datagram): IG Control or SOF first, then any number of packets.</summary>
public sealed class CigiMessageBuilder
{
    private byte[] _buf = new byte[8192];
    private int _len;

    /// <summary>CIGI 4.0 allows either byte order; receivers detect it from the first packet. Default: little-endian.</summary>
    public bool BigEndian { get; set; }
    public int Length => _len;
    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, _len);

    public void Reset() => _len = 0;

    private Span<byte> Reserve(int size)
    {
        if (_len + size > 65000) throw new InvalidOperationException("CIGI message would exceed a UDP datagram");
        if (_len + size > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + size));
        var s = _buf.AsSpan(_len, size);
        _len += size;
        return s;
    }

    public CigiMessageBuilder Add(in IgControlPacket p) { p.Write(Reserve(IgControlPacket.Size), BigEndian); return this; }
    public CigiMessageBuilder Add(in StartOfFramePacket p) { p.Write(Reserve(StartOfFramePacket.Size), BigEndian); return this; }
    public CigiMessageBuilder Add(in EntityPositionPacket p) { p.Write(Reserve(EntityPositionPacket.Size), BigEndian); return this; }
    public CigiMessageBuilder Add(in EntityControlPacket p) { p.Write(Reserve(EntityControlPacket.Size), BigEndian); return this; }
    public CigiMessageBuilder Add(in ArticulatedPartControlPacket p) { p.Write(Reserve(ArticulatedPartControlPacket.Size), BigEndian); return this; }
    public CigiMessageBuilder Add(in ViewControlPacket p) { p.Write(Reserve(ViewControlPacket.Size), BigEndian); return this; }
    public CigiMessageBuilder Add(in ViewDefinitionPacket p) { p.Write(Reserve(ViewDefinitionPacket.Size), BigEndian); return this; }
    public CigiMessageBuilder Add(in SensorControlPacket p) { p.Write(Reserve(SensorControlPacket.Size), BigEndian); return this; }

    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
}

/// <summary>Visitor for incoming CIGI messages. Unknown packets are reported via <see cref="OnUnknown"/>.</summary>
public abstract class CigiPacketHandler
{
    public virtual void OnIgControl(in IgControlPacket p) { }
    public virtual void OnStartOfFrame(in StartOfFramePacket p) { }
    public virtual void OnEntityPosition(in EntityPositionPacket p) { }
    public virtual void OnEntityControl(in EntityControlPacket p) { }
    public virtual void OnArticulatedPartControl(in ArticulatedPartControlPacket p) { }
    public virtual void OnViewControl(in ViewControlPacket p) { }
    public virtual void OnViewDefinition(in ViewDefinitionPacket p) { }
    public virtual void OnSensorControl(in SensorControlPacket p) { }
    public virtual void OnUnknown(ushort packetId, ReadOnlySpan<byte> packet) { }
}

public static class CigiMessageParser
{
    /// <summary>
    /// CIGI 4.0 byte order detection: the first packet is IG Control or SOF (size 24). If the first byte is 0 the
    /// message is big-endian.
    /// </summary>
    public static bool IsBigEndian(ReadOnlySpan<byte> message) => message.Length >= 2 && message[0] == 0;

    /// <summary>Major version of the first packet (byte 4 of IG Control / SOF in CIGI 4).</summary>
    public static int MajorVersion(ReadOnlySpan<byte> message) => message.Length >= 5 ? message[4] : -1;

    /// <summary>Dispatches every packet in <paramref name="message"/>. Returns the number of packets parsed.</summary>
    public static int Parse(ReadOnlySpan<byte> message, CigiPacketHandler handler)
    {
        bool be = IsBigEndian(message);
        int off = 0, count = 0;
        while (off + 4 <= message.Length)
        {
            int size = E.U16(message[off..], be);
            ushort id = E.U16(message[(off + 2)..], be);
            if (size < 4 || off + size > message.Length) break;
            var p = message.Slice(off, size);
            switch (id)
            {
                case CigiPacketId.IgControl when size >= IgControlPacket.Size: handler.OnIgControl(IgControlPacket.Read(p, be)); break;
                case CigiPacketId.StartOfFrame when size >= StartOfFramePacket.Size: handler.OnStartOfFrame(StartOfFramePacket.Read(p, be)); break;
                case CigiPacketId.EntityPosition when size >= EntityPositionPacket.Size: handler.OnEntityPosition(EntityPositionPacket.Read(p, be)); break;
                case CigiPacketId.EntityControl when size >= EntityControlPacket.Size: handler.OnEntityControl(EntityControlPacket.Read(p, be)); break;
                case CigiPacketId.ArticulatedPartControl when size >= ArticulatedPartControlPacket.Size: handler.OnArticulatedPartControl(ArticulatedPartControlPacket.Read(p, be)); break;
                case CigiPacketId.ViewControl when size >= ViewControlPacket.Size: handler.OnViewControl(ViewControlPacket.Read(p, be)); break;
                case CigiPacketId.ViewDefinition when size >= ViewDefinitionPacket.Size: handler.OnViewDefinition(ViewDefinitionPacket.Read(p, be)); break;
                case CigiPacketId.SensorControl when size >= SensorControlPacket.Size: handler.OnSensorControl(SensorControlPacket.Read(p, be)); break;
                default: handler.OnUnknown(id, p); break;
            }
            off += size;
            count++;
        }
        return count;
    }

    /// <summary>Converts seconds to the CIGI 10 microsecond timestamp unit (wraps at 2^32).</summary>
    public static uint ToTimestamp(double seconds) => unchecked((uint)(long)Math.Round(seconds * 100_000.0));
    public static double FromTimestamp(uint ticks) => ticks / 100_000.0;
}
