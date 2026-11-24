using WebAirIG.Common.Truth;
using WebAirIG.Common.Video;

namespace WebAirIG.Consumer.Services;

/// <summary>
/// A decoded BGRA frame plus its truth sidecar. Reference counted so the preview, detector and recorder can read it
/// concurrently while the decoder keeps producing; the buffer returns to the pool when the last reference is released.
/// </summary>
public sealed class DecodedFrame
{
    private readonly FrameBufferPool _pool;
    private int _refs = 1;

    public DecodedFrame(FrameBufferPool pool, byte[] bgra, int width, int height, int cameraId, ulong frameNumber,
        double simTime, long utcTicks, uint sessionId, FrameTruth? truth)
    {
        _pool = pool;
        Bgra = bgra;
        Width = width;
        Height = height;
        CameraId = cameraId;
        FrameNumber = frameNumber;
        SimTime = simTime;
        UtcTicks = utcTicks;
        SessionId = sessionId;
        Truth = truth;
    }

    public byte[] Bgra { get; }
    public int Width { get; }
    public int Height { get; }
    public int CameraId { get; }
    public ulong FrameNumber { get; }
    public double SimTime { get; }
    public long UtcTicks { get; }
    public uint SessionId { get; }
    public FrameTruth? Truth { get; }
    public DateTime DecodedUtc { get; } = DateTime.UtcNow;

    public DecodedFrame AddRef()
    {
        Interlocked.Increment(ref _refs);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0) _pool.Return(Bgra);
    }
}
