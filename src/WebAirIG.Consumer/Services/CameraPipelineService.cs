using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WebAirIG.Common.Transport;
using WebAirIG.Common.Truth;
using WebAirIG.Common.Video;
using WebAirIG.Consumer.Options;

namespace WebAirIG.Consumer.Services;

public sealed record CameraStats(
    int CameraId, string Name, long Received, long Decoded, long Gaps, long SkippedWaitingKeyframe, long DecoderRestarts,
    double Fps, double LatencyMs, ulong LastFrame, int TruthObjects, bool DecoderRunning, uint SessionId);

public interface ICameraPipelineService
{
    string FfmpegStatus { get; }
    IReadOnlyList<CameraStats> GetStats();
    IReadOnlyList<int> CameraIds { get; }
    /// <summary>Latest decoded frame with an added reference; the caller must call Release().</summary>
    DecodedFrame? AcquireLatest(int cameraId);
    /// <summary>Raised on a decoder thread for every decoded frame. Handlers that keep the frame must AddRef().</summary>
    event Action<DecodedFrame>? FrameDecoded;
}

/// <summary>One decode pipeline per camera: sequence-gap detection, keyframe resync, ffmpeg NVDEC decode.</summary>
public sealed class CameraPipelineService : ICameraPipelineService, IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<int, CameraPipeline> _pipelines = new();
    private readonly IStreamReceiverService _receiver;
    private readonly ILogger<CameraPipelineService> _log;
    private readonly string? _ffmpeg;
    private readonly string _hwAccel;

    public event Action<DecodedFrame>? FrameDecoded;
    public string FfmpegStatus { get; }
    public IReadOnlyList<int> CameraIds => _pipelines.Keys.OrderBy(k => k).ToList();

    public CameraPipelineService(IStreamReceiverService receiver, IOptions<ConsumerOptions> options, IWebHostEnvironment env, ILogger<CameraPipelineService> log)
    {
        _receiver = receiver;
        _log = log;
        _hwAccel = options.Value.Decoder.HwAccel;
        _ffmpeg = FfmpegLocator.Find(options.Value.Decoder.FfmpegPath, env.ContentRootPath);
        FfmpegStatus = _ffmpeg is null ? "ffmpeg NOT FOUND - install it (winget install Gyan.FFmpeg) or set Decoder:FfmpegPath" : $"{_ffmpeg} (hwaccel {_hwAccel})";
        _log.LogInformation("Decoder: {Status}", FfmpegStatus);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _receiver.FrameReceived += OnMessage;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _receiver.FrameReceived -= OnMessage;
        return Task.CompletedTask;
    }

    private void OnMessage(FrameMessage m)
    {
        var p = _pipelines.GetOrAdd(m.CameraId, id => new CameraPipeline(id, _ffmpeg, _hwAccel, _log, f => FrameDecoded?.Invoke(f)));
        p.Submit(m);
    }

    public DecodedFrame? AcquireLatest(int cameraId) => _pipelines.TryGetValue(cameraId, out var p) ? p.AcquireLatest() : null;

    public IReadOnlyList<CameraStats> GetStats() => _pipelines.Values.OrderBy(p => p.CameraId).Select(p => p.Stats()).ToList();

    public void Dispose()
    {
        foreach (var p in _pipelines.Values) p.Dispose();
        _pipelines.Clear();
    }
}

internal sealed record FrameInfo(int CameraId, ulong Frame, double SimTime, long UtcTicks, uint Session, int Width, int Height, FrameTruth? Truth);

internal sealed class CameraPipeline : IDisposable
{
    private readonly string? _ffmpeg;
    private readonly string _hwAccel;
    private readonly ILogger _log;
    private readonly Action<DecodedFrame> _onFrame;
    private readonly object _gate = new();
    private FfmpegDecoder<FrameInfo>? _decoder;
    private (uint Session, VideoCodec Codec, int W, int H) _format;
    private uint _lastSeq;
    private bool _haveSeq;
    private bool _waitKey = true;
    private DecodedFrame? _latest;
    private long _received, _decoded, _gaps, _skipped, _restarts;
    private double _fps, _latencyMs;
    private long _lastDecodedTicks;
    private string _name = "";
    private int _truthObjects;

    public int CameraId { get; }

    public CameraPipeline(int cameraId, string? ffmpeg, string hwAccel, ILogger log, Action<DecodedFrame> onFrame)
    {
        CameraId = cameraId;
        _ffmpeg = ffmpeg;
        _hwAccel = hwAccel;
        _log = log;
        _onFrame = onFrame;
    }

    /// <summary>Called on the receiver thread for each complete frame message of this camera.</summary>
    public void Submit(FrameMessage m)
    {
        Interlocked.Increment(ref _received);
        if (_ffmpeg is null) return;

        var fmt = (m.SessionId, m.Codec, (int)m.Width, (int)m.Height);
        if (_decoder is null || fmt != _format || !_decoder.IsRunning)
        {
            RestartDecoder(fmt);
            _waitKey = true;
            _haveSeq = false;
        }

        if (_haveSeq && m.Sequence != unchecked(_lastSeq + 1))
        {
            Interlocked.Increment(ref _gaps);
            _waitKey = true;
        }
        _lastSeq = m.Sequence;
        _haveSeq = true;

        if (_waitKey)
        {
            if (!m.IsKeyframe)
            {
                Interlocked.Increment(ref _skipped);
                return;
            }
            _waitKey = false;
        }

        FrameTruth? truth = null;
        try { truth = FrameTruth.FromUtf8Json(m.Truth); }
        catch (Exception ex) { _log.LogDebug("Bad truth JSON on cam {Cam}: {Error}", CameraId, ex.Message); }

        var info = new FrameInfo(CameraId, m.FrameNumber, m.SimTime, m.UtcTicks, m.SessionId, m.Width, m.Height, truth);
        if (!_decoder!.TrySubmit(m.Payload, info))
        {
            // Dropping a coded picture breaks the reference chain: resync on the next keyframe.
            _waitKey = true;
            Interlocked.Increment(ref _skipped);
        }
    }

    private void RestartDecoder((uint Session, VideoCodec Codec, int W, int H) fmt)
    {
        _decoder?.Dispose();
        _format = fmt;
        var d = new FfmpegDecoder<FrameInfo>(new DecoderSettings
        {
            FfmpegPath = _ffmpeg!,
            Codec = fmt.Codec,
            Width = fmt.W,
            Height = fmt.H,
            HwAccel = _hwAccel,
        });
        d.Log += msg => _log.LogInformation("[cam{Cam} decoder] {Msg}", CameraId, msg);
        d.FrameDecoded += (info, buffer) => OnDecoded(d, info, buffer);
        d.Start();
        _decoder = d;
        Interlocked.Increment(ref _restarts);
        _log.LogInformation("Camera {Cam}: decoder started ({Codec} {W}x{H}, session {Session:X8})", CameraId, fmt.Codec, fmt.W, fmt.H, fmt.Session);
    }

    private void OnDecoded(FfmpegDecoder<FrameInfo> decoder, FrameInfo info, byte[] buffer)
    {
        var frame = new DecodedFrame(decoder.Pool, buffer, info.Width, info.Height, info.CameraId, info.Frame,
            info.SimTime, info.UtcTicks, info.Session, info.Truth);
        DecodedFrame? old;
        lock (_gate)
        {
            old = _latest;
            _latest = frame;
        }
        old?.Release();

        long now = DateTime.UtcNow.Ticks;
        if (_lastDecodedTicks != 0)
        {
            double dt = (now - _lastDecodedTicks) / 1e7;
            if (dt > 0) _fps = _fps <= 0 ? 1 / dt : _fps * 0.9 + 0.1 / dt;
        }
        _lastDecodedTicks = now;
        double latency = (now - info.UtcTicks) / 1e4;
        _latencyMs = _latencyMs <= 0 ? latency : _latencyMs * 0.9 + latency * 0.1;
        Interlocked.Increment(ref _decoded);
        if (info.Truth is { } t)
        {
            _name = t.Camera.Name;
            _truthObjects = t.Objects.Count;
        }

        frame.AddRef();
        try { _onFrame(frame); }
        finally { frame.Release(); }
    }

    public DecodedFrame? AcquireLatest()
    {
        lock (_gate) return _latest?.AddRef();
    }

    public CameraStats Stats()
    {
        ulong last;
        lock (_gate) last = _latest?.FrameNumber ?? 0;
        return new CameraStats(CameraId, _name, Interlocked.Read(ref _received), Interlocked.Read(ref _decoded),
            Interlocked.Read(ref _gaps), Interlocked.Read(ref _skipped), Interlocked.Read(ref _restarts),
            Math.Round(_fps, 1), Math.Round(_latencyMs, 1), last, _truthObjects, _decoder?.IsRunning ?? false, _format.Session);
    }

    public void Dispose()
    {
        _decoder?.Dispose();
        lock (_gate)
        {
            _latest?.Release();
            _latest = null;
        }
    }
}
