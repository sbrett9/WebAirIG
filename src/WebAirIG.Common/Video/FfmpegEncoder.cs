using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using WebAirIG.Common.Transport;

namespace WebAirIG.Common.Video;

public sealed record EncoderSettings
{
    public required string FfmpegPath { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int Fps { get; init; } = 30;
    public VideoCodec Codec { get; init; } = VideoCodec.H264;
    public int BitrateKbps { get; init; } = 25_000;
    public int Gop { get; init; } = 30;
    /// <summary>NVENC preset p1 (fastest) .. p7 (best quality).</summary>
    public string Preset { get; init; } = "p2";
    /// <summary>"h264_nvenc"/"hevc_nvenc" by default; set to e.g. "libx264" to test without an NVIDIA GPU.</summary>
    public string? EncoderOverride { get; init; }
    /// <summary>Optional standard MPEG-TS mirror (e.g. "udp://127.0.0.1:5610?pkt_size=1316") viewable in VLC/ffplay.</summary>
    public string? MirrorUrl { get; init; }
    public string InputPixelFormat { get; init; } = "rgba";
    public int QueueCapacity { get; init; } = 3;
}

/// <summary>
/// Wraps an ffmpeg child process that encodes raw RGBA frames (stdin) with NVENC and writes an Annex-B elementary
/// stream (stdout). B-frames are disabled so every input frame yields exactly one access unit, in order; that lets us
/// pair each AU with the metadata (frame number, truth) that was queued with the raw frame.
/// </summary>
public sealed class FfmpegEncoder<TMeta> : IDisposable
{
    private readonly EncoderSettings _s;
    private readonly BlockingCollection<(byte[] Data, TMeta Meta)> _queue;
    private readonly ConcurrentQueue<TMeta> _inFlight = new();
    private Process? _proc;
    private Thread? _writer, _reader, _stderr;
    private volatile bool _failed;

    public event Action<TMeta, byte[], bool>? AccessUnitEncoded;
    public event Action<string>? Log;

    public long FramesSubmitted;
    public long FramesDropped;
    public long FramesEncoded;
    public bool IsRunning => _proc is { HasExited: false } && !_failed;
    public string CommandLine { get; private set; } = "";

    public FfmpegEncoder(EncoderSettings settings)
    {
        _s = settings;
        _queue = new BlockingCollection<(byte[], TMeta)>(Math.Max(1, settings.QueueCapacity));
    }

    public static string BuildArguments(EncoderSettings s)
    {
        var ci = CultureInfo.InvariantCulture;
        string encoder = s.EncoderOverride ?? (s.Codec == VideoCodec.Hevc ? "hevc_nvenc" : "h264_nvenc");
        bool nvenc = encoder.EndsWith("_nvenc", StringComparison.Ordinal);
        string rawFormat = s.Codec == VideoCodec.Hevc ? "hevc" : "h264";
        int kbps = s.BitrateKbps;
        int bufsize = Math.Max(kbps / Math.Max(1, s.Fps) * 2, 1000);

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats",
            "-f", "rawvideo", "-pix_fmt", s.InputPixelFormat, "-video_size", $"{s.Width}x{s.Height}",
            "-framerate", s.Fps.ToString(ci), "-i", "pipe:0",
            "-an", "-map", "0:v", "-c:v", encoder,
        };
        if (nvenc)
        {
            // VBR capped at maxrate: CBR makes NVENC pad every H.264 frame with filler data up to the bitrate.
            args.AddRange(["-preset", s.Preset, "-tune", "ll", "-rc", "vbr", "-zerolatency", "1", "-delay", "0", "-aud", "1"]);
        }
        else if (encoder is "libx264" or "libx265")
        {
            args.AddRange(["-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p"]);
        }
        args.AddRange([
            "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{bufsize}k",
            "-g", s.Gop.ToString(ci), "-bf", "0", "-fps_mode", "passthrough",
        ]);

        if (!string.IsNullOrWhiteSpace(s.MirrorUrl))
        {
            args.AddRange(["-f", "tee", $"[f={rawFormat}:onfail=abort]pipe:1|[f=mpegts:onfail=ignore]{s.MirrorUrl}"]);
        }
        else
        {
            args.AddRange(["-f", rawFormat, "pipe:1"]);
        }
        return string.Join(' ', args.Select(Quote));
    }

    private static string Quote(string a) => a.Contains(' ') || a.Contains('|') || a.Contains('[') ? $"\"{a}\"" : a;

    public void Start()
    {
        CommandLine = BuildArguments(_s);
        var psi = new ProcessStartInfo(_s.FfmpegPath, CommandLine)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "ffmpeg enc writer" };
        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "ffmpeg enc reader" };
        _stderr = new Thread(StderrLoop) { IsBackground = true, Name = "ffmpeg enc stderr" };
        _writer.Start();
        _reader.Start();
        _stderr.Start();
    }

    /// <summary>Queues a raw frame (Width*Height*4 bytes). Drops (returns false) if the encoder is backlogged.</summary>
    public bool TrySubmit(byte[] rawFrame, TMeta meta)
    {
        if (_failed || _queue.IsAddingCompleted || !_queue.TryAdd((rawFrame, meta)))
        {
            Interlocked.Increment(ref FramesDropped);
            return false;
        }
        Interlocked.Increment(ref FramesSubmitted);
        return true;
    }

    private void WriterLoop()
    {
        try
        {
            var stdin = _proc!.StandardInput.BaseStream;
            foreach (var (data, meta) in _queue.GetConsumingEnumerable())
            {
                _inFlight.Enqueue(meta);
                stdin.Write(data, 0, data.Length);
                stdin.Flush();
            }
            stdin.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _failed = true;
            Log?.Invoke($"ffmpeg encoder input closed: {ex.Message}");
        }
    }

    private void ReaderLoop()
    {
        var splitter = new AnnexBAccessUnitSplitter(_s.Codec);
        var buf = new byte[1 << 20];
        void Emit(byte[] au, bool key)
        {
            if (!_inFlight.TryDequeue(out var meta))
            {
                Log?.Invoke("ffmpeg produced an access unit with no pending frame metadata (dropped)");
                return;
            }
            Interlocked.Increment(ref FramesEncoded);
            try { AccessUnitEncoded?.Invoke(meta, au, key); }
            catch (Exception ex) { Log?.Invoke($"AccessUnitEncoded handler error: {ex.Message}"); }
        }
        try
        {
            var stdout = _proc!.StandardOutput.BaseStream;
            int n;
            while ((n = stdout.Read(buf, 0, buf.Length)) > 0)
                splitter.Push(buf.AsSpan(0, n), Emit);
            splitter.Flush(Emit);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Log?.Invoke($"ffmpeg encoder output closed: {ex.Message}");
        }
        _failed = true;
    }

    private void StderrLoop()
    {
        try
        {
            string? line;
            while ((line = _proc!.StandardError.ReadLine()) is not null)
                if (!string.IsNullOrWhiteSpace(line)) Log?.Invoke($"ffmpeg: {line}");
        }
        catch { /* process gone */ }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer?.Join(TimeSpan.FromSeconds(2));
        try
        {
            if (_proc is { HasExited: false } && !_proc.WaitForExit(2000)) _proc.Kill(true);
        }
        catch { }
        _reader?.Join(TimeSpan.FromSeconds(1));
        _proc?.Dispose();
        _queue.Dispose();
    }
}
