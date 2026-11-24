using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using WebAirIG.Common.Config;
using WebAirIG.Consumer.Options;

namespace WebAirIG.Consumer.Services.Detection;

public interface IDetectorService
{
    DetectorOptions Settings { get; }
    string Status { get; }
    bool ModelLoaded { get; }
    string ModelInfo { get; }
    void LoadModel(string path, int deviceId);
    DetectionResult? Latest(int cameraId);
    double MeanInferenceMs { get; }
}

/// <summary>
/// Runs YOLO on the newest decoded frame of each camera (round robin, latest-frame-wins), tracks the detections
/// and feeds the evaluator.
/// </summary>
public sealed class DetectorService : BackgroundService, IDetectorService
{
    private readonly ICameraPipelineService _pipelines;
    private readonly IEvaluationService _evaluation;
    private readonly ILogger<DetectorService> _log;
    private readonly string _contentRoot;
    private readonly ConcurrentDictionary<int, DetectionResult> _latest = new();
    private readonly Dictionary<int, IouTracker> _trackers = new();
    private readonly Dictionary<int, ulong> _lastFrame = new();
    private readonly object _modelGate = new();
    private YoloOnnxDetector? _detector;
    private double _meanMs;

    public DetectorOptions Settings { get; }
    public string Status { get; private set; } = "no model loaded";
    public bool ModelLoaded => _detector is not null;
    public string ModelInfo => _detector is { } d
        ? $"{Path.GetFileName(d.ModelPath)}: input {d.InputWidth}x{d.InputHeight}, {d.ClassNames.Count} classes, {d.Provider}"
        : "";
    public double MeanInferenceMs => Math.Round(_meanMs, 2);

    public DetectorService(ICameraPipelineService pipelines, IEvaluationService evaluation, IOptions<ConsumerOptions> options,
        IWebHostEnvironment env, ILogger<DetectorService> log)
    {
        _pipelines = pipelines;
        _evaluation = evaluation;
        _log = log;
        _contentRoot = env.ContentRootPath;
        Settings = options.Value.Detector;
    }

    public void LoadModel(string path, int deviceId)
    {
        var full = JsonConfig.Resolve(_contentRoot, path);
        lock (_modelGate)
        {
            _detector?.Dispose();
            _detector = null;
            Settings.ModelPath = path;
            Settings.DeviceId = deviceId;
            if (!File.Exists(full))
            {
                Status = $"model not found: {full} (see tools/yolo/README.md to export one)";
                _log.LogWarning("{Status}", Status);
                return;
            }
            try
            {
                _detector = YoloOnnxDetector.Load(full, deviceId);
                foreach (var t in _trackers.Values) t.Reset();
                Status = $"loaded {ModelInfo}";
                _log.LogInformation("{Status}", Status);
            }
            catch (Exception ex)
            {
                Status = $"load failed: {ex.Message}";
                _log.LogError(ex, "Model load failed");
            }
        }
    }

    public DetectionResult? Latest(int cameraId) => _latest.TryGetValue(cameraId, out var r) ? r : null;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadModel(Settings.ModelPath, Settings.DeviceId);
        return Task.Factory.StartNew(() => Loop(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool worked = false;
            if (Settings.Enabled && _detector is not null)
            {
                foreach (var cam in _pipelines.CameraIds)
                {
                    var frame = _pipelines.AcquireLatest(cam);
                    if (frame is null) continue;
                    try
                    {
                        if (_lastFrame.TryGetValue(cam, out var last) && last == frame.FrameNumber) continue;
                        _lastFrame[cam] = frame.FrameNumber;
                        RunOne(frame);
                        worked = true;
                    }
                    catch (Exception ex)
                    {
                        Status = $"inference error: {ex.Message}";
                        _log.LogError(ex, "Inference failed");
                        Thread.Sleep(500);
                    }
                    finally { frame.Release(); }
                }
            }
            if (!worked) Thread.Sleep(5);
        }
    }

    private void RunOne(DecodedFrame frame)
    {
        List<Detection> dets;
        var sw = Stopwatch.StartNew();
        lock (_modelGate)
        {
            if (_detector is null) return;
            dets = _detector.Detect(frame.Bgra, frame.Width, frame.Height, (float)Settings.ConfidenceThreshold, (float)Settings.NmsIouThreshold);
        }
        double ms = sw.Elapsed.TotalMilliseconds;
        _meanMs = _meanMs <= 0 ? ms : _meanMs * 0.95 + ms * 0.05;

        if (!_trackers.TryGetValue(frame.CameraId, out var tracker)) _trackers[frame.CameraId] = tracker = new IouTracker();
        tracker.ClassAgnostic = _evaluation.Settings.ClassAgnostic;
        var tracked = tracker.Update(dets);
        _latest[frame.CameraId] = new DetectionResult(frame.CameraId, frame.FrameNumber, tracked, ms, DateTime.UtcNow);
        _evaluation.Evaluate(frame.CameraId, frame.Truth, tracked);
    }

    public override void Dispose()
    {
        lock (_modelGate) _detector?.Dispose();
        base.Dispose();
    }
}
