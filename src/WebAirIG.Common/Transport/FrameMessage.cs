using System.Buffers.Binary;

namespace WebAirIG.Common.Transport;

public enum VideoCodec : byte
{
    H264 = 1,
    Hevc = 2,
}

[Flags]
public enum FrameFlags : byte
{
    None = 0,
    Keyframe = 1,
}

/// <summary>
/// One encoded camera frame plus its truth sidecar. Wire layout (little-endian):
/// <code>
///  off size field
///   0   4   magic "WAIG"
///   4   1   version (1)
///   5   1   codec (1 = H.264, 2 = HEVC)
///   6   1   camera id
///   7   1   flags (bit0 = keyframe)
///   8   8   frame number (IG frame counter, shared by all cameras)
///  16   8   sim time, seconds (double)
///  24   8   UTC capture time, .NET ticks
///  32   2   width
///  34   2   height
///  36   4   payload length (Annex-B access unit)
///  40   4   truth length (UTF-8 JSON, may be 0)
///  44   4   session id (random per IG run; frame numbers restart when it changes)
///  48   4   per-camera sequence number (increments by 1 per sent message; gaps = lost frames)
///  52  12   reserved
///  64   .   payload, then truth
/// </code>
/// </summary>
public sealed class FrameMessage
{
    public const int HeaderSize = 64;
    public const uint Magic = 0x47494157; // "WAIG" little-endian
    public const byte CurrentVersion = 1;

    public VideoCodec Codec { get; init; }
    public byte CameraId { get; init; }
    public FrameFlags Flags { get; init; }
    public ulong FrameNumber { get; init; }
    public double SimTime { get; init; }
    public long UtcTicks { get; init; }
    public ushort Width { get; init; }
    public ushort Height { get; init; }
    public uint SessionId { get; init; }
    public uint Sequence { get; init; }
    public byte[] Payload { get; init; } = [];
    public byte[] Truth { get; init; } = [];

    public bool IsKeyframe => (Flags & FrameFlags.Keyframe) != 0;
    public int SerializedLength => HeaderSize + Payload.Length + Truth.Length;

    public byte[] Serialize()
    {
        var buf = new byte[SerializedLength];
        WriteTo(buf);
        return buf;
    }

    public void WriteTo(Span<byte> dst)
    {
        if (dst.Length < SerializedLength) throw new ArgumentException("Destination too small", nameof(dst));
        dst[..HeaderSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(dst, Magic);
        dst[4] = CurrentVersion;
        dst[5] = (byte)Codec;
        dst[6] = CameraId;
        dst[7] = (byte)Flags;
        BinaryPrimitives.WriteUInt64LittleEndian(dst[8..], FrameNumber);
        BinaryPrimitives.WriteDoubleLittleEndian(dst[16..], SimTime);
        BinaryPrimitives.WriteInt64LittleEndian(dst[24..], UtcTicks);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[32..], Width);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[34..], Height);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[36..], (uint)Payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[40..], (uint)Truth.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[44..], SessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[48..], Sequence);
        Payload.CopyTo(dst[HeaderSize..]);
        Truth.CopyTo(dst[(HeaderSize + Payload.Length)..]);
    }

    public static bool TryParse(ReadOnlySpan<byte> src, out FrameMessage? message)
    {
        message = null;
        if (src.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(src) != Magic) return false;
        if (src[4] != CurrentVersion) return false;
        uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(src[36..]);
        uint truthLen = BinaryPrimitives.ReadUInt32LittleEndian(src[40..]);
        long total = HeaderSize + (long)payloadLen + truthLen;
        if (total > src.Length) return false;

        message = new FrameMessage
        {
            Codec = (VideoCodec)src[5],
            CameraId = src[6],
            Flags = (FrameFlags)src[7],
            FrameNumber = BinaryPrimitives.ReadUInt64LittleEndian(src[8..]),
            SimTime = BinaryPrimitives.ReadDoubleLittleEndian(src[16..]),
            UtcTicks = BinaryPrimitives.ReadInt64LittleEndian(src[24..]),
            Width = BinaryPrimitives.ReadUInt16LittleEndian(src[32..]),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(src[34..]),
            SessionId = BinaryPrimitives.ReadUInt32LittleEndian(src[44..]),
            Sequence = BinaryPrimitives.ReadUInt32LittleEndian(src[48..]),
            Payload = src.Slice(HeaderSize, (int)payloadLen).ToArray(),
            Truth = src.Slice(HeaderSize + (int)payloadLen, (int)truthLen).ToArray(),
        };
        return true;
    }
}
