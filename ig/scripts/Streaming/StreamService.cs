using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using WebAirIG.Common.Config;
using WebAirIG.Common.Transport;
using WebAirIG.Common.Truth;
using WebAirIG.Common.Video;
using WebAirIG.IG.Core;

namespace WebAirIG.IG.Streaming;

/// <summary>Metadata travelling with each raw frame through the encoder FIFO.</summary>
public sealed record FrameMeta(int CameraId, ulong Frame, double SimTime, long UtcTicks, int Width, int Height, FrameTruth Truth);

/// <summary>
/// One NVENC encoder (ffmpeg child process) per camera; encoded access units are wrapped with their truth sidecar
/// into <see cref="FrameMessage"/>s and sent over UDP or TCP.
/// </summary>
public sealed class StreamService : IDisposable
{
    private readonly StreamSettings _cfg;
    private readonly TruthSettings _truth;
    private readonly uint _sessionId;
    private readonly VideoCodec _codec;
    private readonly Dictionary<int, FfmpegEncoder<FrameMeta>> _encoders = new();
    private readonly IFrameSender? _sender;

    public bool Active { get; }
    public string Status { get; private set; } = "disabled";
    public TransportStats? TransportStats => _sender?.Stats;

    public StreamService(StreamSettings cfg, TruthSettings truth, IReadOnlyList<CameraDef> cameras, uint sessionId, string repoRoot, int fps)
    {
        _cfg = cfg;
        _truth = truth;
        _sessionId = sessionId;
        _codec = cfg.Codec.Trim().ToLowerInvariant() is "hevc" or "h265" ? VideoCodec.Hevc : VideoCodec.H264;
        if (!cfg.Enabled) return;

        var ffmpeg = FfmpegLocator.Find(cfg.FfmpegPath, repoRoot);
        if (ffmpeg is null)
        {
            Status = "ffmpeg not found (set stream.ffmpegPath, WEBAIRIG_FFMPEG, or put it in tools/ffmpeg/bin)";
            IgLog.Error($"Streaming disabled: {Status}");
            return;
        }

        try
        {
            _sender = FrameSenderFactory.Create(cfg.Protocol, cfg.Host, cfg.Port, cfg.MaxDatagramBytes, cfg.PacingMbps, IgLog.Info);
            foreach (var cam in cameras)
            {
                var settings = new EncoderSettings
                {
                    FfmpegPath = ffmpeg,
                    Width = cam.Width,
                    Height = cam.Height,
                    Fps = Math.Max(1, fps / Math.Max(1, cfg.EveryNFrames)),
                    Codec = _codec,
                    BitrateKbps = cfg.BitrateKbps,
                    Gop = cfg.Gop,
                    Preset = cfg.Preset,
                    EncoderOverride = string.IsNullOrWhiteSpace(cfg.EncoderOverride) ? null : cfg.EncoderOverride,
                    MirrorUrl = string.IsNullOrWhiteSpace(cfg.MirrorUrlTemplate) ? null : cfg.MirrorUrlTemplate.Replace("{cam}", cam.Id.ToString()),
                };
                var enc = new FfmpegEncoder<FrameMeta>(settings);
                enc.Log += m => IgLog.Info($"[cam{cam.Id}] {m}");
                enc.AccessUnitEncoded += OnAccessUnit;
                enc.Start();
                _encoders[cam.Id] = enc;
                if (cam.Id == cameras[0].Id) IgLog.Info($"ffmpeg {ffmpeg} {enc.CommandLine}");
            }
            Active = true;
            Status = $"{_codec} -> {_sender.Description}";
            IgLog.Info($"Streaming {cameras.Count} cameras: {Status}");
        }
        catch (Exception ex)
        {
            Status = $"failed: {ex.Message}";
            IgLog.Error($"Streaming setup failed: {ex}");
        }
    }

    public bool Submit(int cameraId, byte[] rgba, FrameMeta meta) =>
        Active && _encoders.TryGetValue(cameraId, out var enc) && enc.TrySubmit(rgba, meta);

    public (long Submitted, long Encoded, long Dropped) EncoderStats()
    {
        long s = 0, e = 0, d = 0;
        foreach (var enc in _encoders.Values)
        {
            s += Interlocked.Read(ref enc.FramesSubmitted);
            e += Interlocked.Read(ref enc.FramesEncoded);
            d += Interlocked.Read(ref enc.FramesDropped);
        }
        return (s, e, d);
    }

    private readonly uint[] _sequence = new uint[256];

    private void OnAccessUnit(FrameMeta meta, byte[] au, bool keyframe)
    {
        // Each encoder's reader thread only touches its own camera's slot.
        uint seq = ++_sequence[meta.CameraId & 0xFF];
        _sender?.TrySend(new FrameMessage
        {
            Sequence = seq,
            Codec = _codec,
            CameraId = (byte)meta.CameraId,
            Flags = keyframe ? FrameFlags.Keyframe : FrameFlags.None,
            FrameNumber = meta.Frame,
            SimTime = meta.SimTime,
            UtcTicks = meta.UtcTicks,
            Width = (ushort)meta.Width,
            Height = (ushort)meta.Height,
            SessionId = _sessionId,
            Payload = au,
            Truth = _truth.EmbedInStream ? meta.Truth.ToUtf8Json() : [],
        });
    }

    public void Dispose()
    {
        foreach (var enc in _encoders.Values) enc.Dispose();
        _encoders.Clear();
        _sender?.Dispose();
    }
}

/// <summary>Appends truth to output/truth/session_XXXXXXXX/camN.jsonl on a background thread.</summary>
public sealed class TruthLogWriter : IDisposable
{
    private readonly BlockingCollection<FrameTruth> _queue = new(512);
    private readonly Thread _thread;
    private readonly string _dir;
    private readonly Dictionary<int, StreamWriter> _writers = new();

    public string Directory => _dir;

    public TruthLogWriter(string baseDir, uint sessionId)
    {
        _dir = Path.Combine(baseDir, $"session_{sessionId:X8}");
        System.IO.Directory.CreateDirectory(_dir);
        _thread = new Thread(Run) { IsBackground = true, Name = "truth log" };
        _thread.Start();
        IgLog.Info($"Truth log: {_dir}");
    }

    public void Enqueue(FrameTruth truth) => _queue.TryAdd(truth);

    private void Run()
    {
        foreach (var t in _queue.GetConsumingEnumerable())
        {
            if (!_writers.TryGetValue(t.Camera.Id, out var w))
            {
                w = new StreamWriter(Path.Combine(_dir, $"cam{t.Camera.Id}.jsonl"), append: true) { AutoFlush = false };
                _writers[t.Camera.Id] = w;
            }
            w.WriteLine(JsonSerializer.Serialize(t, JsonConfig.Compact));
            if (_queue.Count == 0) foreach (var x in _writers.Values) x.Flush();
        }
        foreach (var w in _writers.Values) w.Dispose();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(3));
    }
}
