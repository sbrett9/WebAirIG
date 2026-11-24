using System.Collections.Concurrent;
using System.Diagnostics;
using WebAirIG.Common.Transport;

namespace WebAirIG.Common.Video;

public sealed record DecoderSettings
{
    public required string FfmpegPath { get; init; }
    public required VideoCodec Codec { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>"cuda" (NVDEC), "d3d11va", "auto" or "none".</summary>
    public string HwAccel { get; init; } = "cuda";
    public string OutputPixelFormat { get; init; } = "bgra";
    public int QueueCapacity { get; init; } = 8;
}

/// <summary>Fixed-size frame buffers recycled between the decoder and its consumers.</summary>
public sealed class FrameBufferPool(int bufferSize, int maxPooled = 16)
{
    private readonly ConcurrentBag<byte[]> _bag = new();
    public int BufferSize { get; } = bufferSize;

    public byte[] Rent() => _bag.TryTake(out var b) ? b : new byte[BufferSize];

    public void Return(byte[] buffer)
    {
        if (buffer.Length == BufferSize && _bag.Count < maxPooled) _bag.Add(buffer);
    }
}

/// <summary>
/// Decodes an Annex-B stream with an ffmpeg child process (stdin = access units, stdout = raw frames).
/// Each submitted access unit produces one decoded frame in order, so metadata is paired via a FIFO.
/// Callers must feed complete AUs starting at a keyframe; after any loss, wait for the next keyframe.
/// </summary>
public sealed class FfmpegDecoder<TMeta> : IDisposable
{
    private readonly DecoderSettings _s;
    private readonly BlockingCollection<(byte[] Au, TMeta Meta)> _queue;
    private readonly ConcurrentQueue<TMeta> _inFlight = new();
    private Process? _proc;
    private Thread? _writer, _reader, _stderr;
    private volatile bool _failed;

    public FrameBufferPool Pool { get; }
    public int FrameBytes { get; }

    /// <summary>Raised on the decoder thread. The handler owns the buffer and should return it to <see cref="Pool"/>.</summary>
    public event Action<TMeta, byte[]>? FrameDecoded;
    public event Action<string>? Log;

    public long AccessUnitsSubmitted;
    public long FramesDecoded;
    public long Dropped;
    public bool IsRunning => _proc is { HasExited: false } && !_failed;
    public int Pending => _inFlight.Count + _queue.Count;

    public FfmpegDecoder(DecoderSettings settings)
    {
        _s = settings;
        FrameBytes = settings.Width * settings.Height * 4;
        Pool = new FrameBufferPool(FrameBytes);
        _queue = new BlockingCollection<(byte[], TMeta)>(Math.Max(1, settings.QueueCapacity));
    }

    public static string BuildArguments(DecoderSettings s)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats" };
        if (!string.IsNullOrWhiteSpace(s.HwAccel) && s.HwAccel != "none") args.AddRange(["-hwaccel", s.HwAccel]);
        args.AddRange([
            // No "-fflags nobuffer": it discards the packets read while probing (losing the first GOP), which would
            // break the 1 access unit -> 1 frame pairing.
            "-probesize", "2M", "-analyzeduration", "0", "-flags", "low_delay",
            "-f", s.Codec == VideoCodec.Hevc ? "hevc" : "h264", "-i", "pipe:0",
            "-an", "-fps_mode", "passthrough",
            "-vf", $"scale={s.Width}:{s.Height}",
            "-f", "rawvideo", "-pix_fmt", s.OutputPixelFormat, "pipe:1",
        ]);
        return string.Join(' ', args);
    }

    public void Start()
    {
        var psi = new ProcessStartInfo(_s.FfmpegPath, BuildArguments(_s))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg decoder");
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "ffmpeg dec writer" };
        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "ffmpeg dec reader" };
        _stderr = new Thread(StderrLoop) { IsBackground = true, Name = "ffmpeg dec stderr" };
        _writer.Start();
        _reader.Start();
        _stderr.Start();
    }

    public bool TrySubmit(byte[] accessUnit, TMeta meta)
    {
        if (_failed || _queue.IsAddingCompleted || !_queue.TryAdd((accessUnit, meta)))
        {
            Interlocked.Increment(ref Dropped);
            return false;
        }
        Interlocked.Increment(ref AccessUnitsSubmitted);
        return true;
    }

    private void WriterLoop()
    {
        try
        {
            var stdin = _proc!.StandardInput.BaseStream;
            foreach (var (au, meta) in _queue.GetConsumingEnumerable())
            {
                _inFlight.Enqueue(meta);
                stdin.Write(au, 0, au.Length);
                stdin.Flush();
            }
            stdin.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _failed = true;
            Log?.Invoke($"ffmpeg decoder input closed: {ex.Message}");
        }
    }

    private void ReaderLoop()
    {
        try
        {
            var stdout = _proc!.StandardOutput.BaseStream;
            while (true)
            {
                var frame = Pool.Rent();
                int read = 0;
                while (read < FrameBytes)
                {
                    int n = stdout.Read(frame, read, FrameBytes - read);
                    if (n <= 0) { Pool.Return(frame); _failed = true; return; }
                    read += n;
                }
                if (!_inFlight.TryDequeue(out var meta))
                {
                    Log?.Invoke("Decoder produced a frame with no pending metadata (dropped)");
                    Pool.Return(frame);
                    continue;
                }
                Interlocked.Increment(ref FramesDecoded);
                if (FrameDecoded is { } handler)
                {
                    try { handler(meta, frame); }
                    catch (Exception ex) { Log?.Invoke($"FrameDecoded handler error: {ex.Message}"); }
                }
                else Pool.Return(frame);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _failed = true;
            Log?.Invoke($"ffmpeg decoder output closed: {ex.Message}");
        }
    }

    private void StderrLoop()
    {
        try
        {
            string? line;
            while ((line = _proc!.StandardError.ReadLine()) is not null)
                if (!string.IsNullOrWhiteSpace(line)) Log?.Invoke($"ffmpeg: {line}");
        }
        catch { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer?.Join(TimeSpan.FromSeconds(1));
        try
        {
            if (_proc is { HasExited: false } && !_proc.WaitForExit(1000)) _proc.Kill(true);
        }
        catch { }
        _reader?.Join(TimeSpan.FromSeconds(1));
        _proc?.Dispose();
        _queue.Dispose();
    }
}
