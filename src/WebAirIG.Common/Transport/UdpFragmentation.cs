using System.Buffers.Binary;

namespace WebAirIG.Common.Transport;

/// <summary>
/// Splits serialized <see cref="FrameMessage"/>s into datagrams. Fragment header (little-endian, 32 bytes):
/// <code>
///   0  4  magic "WAIF"
///   4  1  version (1)
///   5  1  camera id
///   6  2  fragment index
///   8  2  fragment count
///  10  2  reserved
///  12  4  session id
///  16  8  frame number
///  24  4  total message length
///  28  4  byte offset of this fragment within the message
/// </code>
/// </summary>
public static class UdpFragmentation
{
    public const int HeaderSize = 32;
    public const uint Magic = 0x46494157; // "WAIF"
    public const int MaxFragments = ushort.MaxValue;

    /// <summary>Calls <paramref name="send"/> once per datagram. The span is only valid during the callback.</summary>
    public static int Fragment(
        ReadOnlySpan<byte> message, byte cameraId, uint sessionId, ulong frameNumber, int maxDatagramBytes,
        Span<byte> scratch, SendDatagram send)
    {
        int maxPayload = maxDatagramBytes - HeaderSize;
        if (maxPayload <= 0) throw new ArgumentOutOfRangeException(nameof(maxDatagramBytes));
        int count = (message.Length + maxPayload - 1) / maxPayload;
        if (count == 0) count = 1;
        if (count > MaxFragments) throw new InvalidOperationException($"Message too large ({message.Length} bytes) for datagram size {maxDatagramBytes}");

        for (int i = 0; i < count; i++)
        {
            int offset = i * maxPayload;
            int len = Math.Min(maxPayload, message.Length - offset);
            var dg = scratch[..(HeaderSize + len)];
            BinaryPrimitives.WriteUInt32LittleEndian(dg, Magic);
            dg[4] = 1;
            dg[5] = cameraId;
            BinaryPrimitives.WriteUInt16LittleEndian(dg[6..], (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(dg[8..], (ushort)count);
            BinaryPrimitives.WriteUInt16LittleEndian(dg[10..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(dg[12..], sessionId);
            BinaryPrimitives.WriteUInt64LittleEndian(dg[16..], frameNumber);
            BinaryPrimitives.WriteUInt32LittleEndian(dg[24..], (uint)message.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(dg[28..], (uint)offset);
            message.Slice(offset, len).CopyTo(dg[HeaderSize..]);
            send(dg);
        }
        return count;
    }

    public delegate void SendDatagram(ReadOnlySpan<byte> datagram);
}

/// <summary>Reassembles fragments into complete messages; incomplete messages are dropped after a timeout.</summary>
public sealed class UdpReassembler
{
    private sealed class Partial
    {
        public required byte[] Data;
        public required bool[] Have;
        public int Received;
        public long CreatedTicks;
    }

    private readonly Dictionary<(uint Session, byte Camera, ulong Frame), Partial> _partials = new();
    private readonly long _timeoutTicks;
    private long _lastSweep;

    public long Completed { get; private set; }
    public long Dropped { get; private set; }
    public long BadDatagrams { get; private set; }
    public int MaxMessageBytes { get; }

    public UdpReassembler(TimeSpan timeout, int maxMessageBytes = 64 * 1024 * 1024)
    {
        _timeoutTicks = (long)(timeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        MaxMessageBytes = maxMessageBytes;
    }

    /// <summary>Adds a datagram; returns the complete message when its last fragment arrives, otherwise null.</summary>
    public byte[]? Add(ReadOnlySpan<byte> dg)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _lastSweep > _timeoutTicks / 4) Sweep(now);

        if (dg.Length < UdpFragmentation.HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(dg) != UdpFragmentation.Magic || dg[4] != 1)
        {
            BadDatagrams++;
            return null;
        }
        byte cam = dg[5];
        int index = BinaryPrimitives.ReadUInt16LittleEndian(dg[6..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(dg[8..]);
        uint session = BinaryPrimitives.ReadUInt32LittleEndian(dg[12..]);
        ulong frame = BinaryPrimitives.ReadUInt64LittleEndian(dg[16..]);
        int total = (int)BinaryPrimitives.ReadUInt32LittleEndian(dg[24..]);
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(dg[28..]);
        var payload = dg[UdpFragmentation.HeaderSize..];

        if (count == 0 || index >= count || total <= 0 || total > MaxMessageBytes || offset < 0 || offset + payload.Length > total)
        {
            BadDatagrams++;
            return null;
        }

        if (count == 1)
        {
            Completed++;
            return payload.ToArray();
        }

        var key = (session, cam, frame);
        if (!_partials.TryGetValue(key, out var p))
        {
            p = new Partial { Data = new byte[total], Have = new bool[count], CreatedTicks = now };
            _partials[key] = p;
        }
        else if (p.Data.Length != total || p.Have.Length != count)
        {
            BadDatagrams++;
            return null;
        }

        if (!p.Have[index])
        {
            payload.CopyTo(p.Data.AsSpan(offset));
            p.Have[index] = true;
            p.Received++;
        }

        if (p.Received == count)
        {
            _partials.Remove(key);
            Completed++;
            return p.Data;
        }
        return null;
    }

    private void Sweep(long now)
    {
        _lastSweep = now;
        List<(uint, byte, ulong)>? stale = null;
        foreach (var (key, p) in _partials)
        {
            if (now - p.CreatedTicks > _timeoutTicks)
                (stale ??= []).Add(key);
        }
        if (stale is null) return;
        foreach (var key in stale)
        {
            _partials.Remove(key);
            Dropped++;
        }
    }
}
