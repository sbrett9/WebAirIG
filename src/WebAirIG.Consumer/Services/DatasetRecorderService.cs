using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SkiaSharp;
using WebAirIG.Common.Config;
using WebAirIG.Consumer.Options;

namespace WebAirIG.Consumer.Services;

public interface IDatasetRecorderService
{
    RecorderOptions Settings { get; }
    bool Recording { get; }
    string Status { get; }
    string? DatasetPath { get; }
    long ImagesWritten { get; }
    long ObjectsWritten { get; }
    long Dropped { get; }
    IReadOnlyDictionary<string, long> ObjectsPerClass { get; }
    void Start();
    void Stop();
}

/// <summary>
/// Turns the stream into a YOLO training set: every Nth decoded frame per camera is written as an image plus a
/// label file built from the truth sidecar, and the raw truth JSON. Train/val split uses 10-second blocks of sim
/// time (every 10th block is val) so near-duplicate consecutive frames do not leak across the split.
/// Layout: {OutputDir}/{DatasetName}_{timestamp}/images/{train|val}, labels/{train|val}, truth/, dataset.yaml
/// </summary>
public sealed class DatasetRecorderService : IDatasetRecorderService, IDisposable
{
    private readonly ClassCatalogService _classes;
    private readonly ILogger<DatasetRecorderService> _log;
    private readonly string _contentRoot;
    private readonly ConcurrentDictionary<string, long> _perClass = new();
    private BlockingCollection<DecodedFrame>? _queue;
    private Thread? _worker;
    private long _images, _objects, _dropped;

    public RecorderOptions Settings { get; }
    public bool Recording => _queue is not null;
    public string Status { get; private set; } = "idle";
    public string? DatasetPath { get; private set; }
    public long ImagesWritten => Interlocked.Read(ref _images);
    public long ObjectsWritten => Interlocked.Read(ref _objects);
    public long Dropped => Interlocked.Read(ref _dropped);
    public IReadOnlyDictionary<string, long> ObjectsPerClass => _perClass;

    public DatasetRecorderService(ICameraPipelineService pipelines, ClassCatalogService classes, IOptions<ConsumerOptions> options,
        IWebHostEnvironment env, ILogger<DatasetRecorderService> log)
    {
        _classes = classes;
        _log = log;
        _contentRoot = env.ContentRootPath;
        Settings = options.Value.Recorder;
        pipelines.FrameDecoded += OnFrame;
    }

    public void Start()
    {
        if (Recording) return;
        var root = JsonConfig.Resolve(_contentRoot, Settings.OutputDir);
        var name = string.Concat(Settings.DatasetName.Split(Path.GetInvalidFileNameChars()));
        DatasetPath = Path.Combine(root, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}");
        foreach (var sub in new[] { "images/train", "images/val", "labels/train", "labels/val", "truth" })
            Directory.CreateDirectory(Path.Combine(DatasetPath, sub));
        WriteDatasetYaml(DatasetPath);

        Interlocked.Exchange(ref _images, 0);
        Interlocked.Exchange(ref _objects, 0);
        Interlocked.Exchange(ref _dropped, 0);
        _perClass.Clear();
        var queue = new BlockingCollection<DecodedFrame>(32);
        _worker = new Thread(() => WorkerLoop(queue, DatasetPath)) { IsBackground = true, Name = "dataset writer" };
        _worker.Start();
        _queue = queue;
        Status = $"recording to {DatasetPath}";
        _log.LogInformation("{Status}", Status);
    }

    public void Stop()
    {
        var q = _queue;
        if (q is null) return;
        _queue = null;
        q.CompleteAdding();
        _worker?.Join(TimeSpan.FromSeconds(10));
        Status = $"stopped: {ImagesWritten} images, {ObjectsWritten} labeled objects in {DatasetPath}";
    }

    private void OnFrame(DecodedFrame frame)
    {
        var q = _queue;
        if (q is null || frame.Truth is null) return;
        if (frame.FrameNumber % (ulong)Math.Max(1, Settings.EveryNFrames) != 0) return;
        frame.AddRef();
        if (!q.TryAdd(frame))
        {
            frame.Release();
            Interlocked.Increment(ref _dropped);
        }
    }

    private void WorkerLoop(BlockingCollection<DecodedFrame> queue, string dir)
    {
        foreach (var frame in queue.GetConsumingEnumerable())
        {
            try { Write(frame, dir); }
            catch (Exception ex) { _log.LogError(ex, "Dataset write failed"); }
            finally { frame.Release(); }
        }
    }

    private void Write(DecodedFrame frame, string dir)
    {
        var truth = frame.Truth!;
        var s = Settings;
        var labels = new StringBuilder();
        int count = 0;
        foreach (var o in truth.Objects)
        {
            if (o.Bbox.Length != 4 || (o.IsOwnship && !s.IncludeOwnship)) continue;
            double w = o.Bbox[2] - o.Bbox[0], h = o.Bbox[3] - o.Bbox[1];
            if (Math.Max(w, h) < s.MinBoxPixels) continue;
            double cx = (o.Bbox[0] + w / 2) / frame.Width, cy = (o.Bbox[1] + h / 2) / frame.Height;
            labels.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{o.ClassId} {cx:F6} {cy:F6} {w / frame.Width:F6} {h / frame.Height:F6}"));
            _perClass.AddOrUpdate(o.ClassName, 1, (_, v) => v + 1);
            count++;
        }
        if (count == 0 && !s.IncludeEmptyFrames) return;

        // 10-second blocks of sim time; every 10th block goes to validation.
        bool val = (long)Math.Floor(frame.SimTime / 10.0) % 10 == 9;
        string split = val ? "val" : "train";
        string stem = $"s{frame.SessionId:X8}_c{frame.CameraId}_f{frame.FrameNumber:D7}";
        bool png = s.Format.Equals("png", StringComparison.OrdinalIgnoreCase);
        var bytes = ImageCodec.Encode(frame.Bgra, frame.Width, frame.Height, png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, s.JpegQuality);
        File.WriteAllBytes(Path.Combine(dir, "images", split, stem + (png ? ".png" : ".jpg")), bytes);
        File.WriteAllText(Path.Combine(dir, "labels", split, stem + ".txt"), labels.ToString());
        File.WriteAllText(Path.Combine(dir, "truth", stem + ".json"), JsonSerializer.Serialize(truth, JsonConfig.Compact));
        Interlocked.Increment(ref _images);
        Interlocked.Add(ref _objects, count);
    }

    private void WriteDatasetYaml(string dir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by WebAirIG consumer. Train with: python tools/yolo/train.py --data <this file>");
        sb.AppendLine($"path: {dir.Replace('\\', '/')}");
        sb.AppendLine("train: images/train");
        sb.AppendLine("val: images/val");
        sb.AppendLine("names:");
        int maxId = _classes.Classes.Count == 0 ? -1 : _classes.Classes.Keys.Max();
        for (int i = 0; i <= maxId; i++)
            sb.AppendLine($"  {i}: {(_classes.Classes.TryGetValue(i, out var n) ? n : $"class{i}")}");
        File.WriteAllText(Path.Combine(dir, "dataset.yaml"), sb.ToString());
    }

    public void Dispose() => Stop();
}
