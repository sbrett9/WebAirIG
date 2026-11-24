using Microsoft.Extensions.Options;
using WebAirIG.Common.Truth;
using WebAirIG.Consumer.Options;

namespace WebAirIG.Consumer.Services.Detection;

public sealed record EvalRow(
    string Scope, long Frames, long Tp, long Fp, long Fn, double Precision, double Recall, double F1, double MeanIou, long IdSwitches);

public interface IEvaluationService
{
    EvaluationOptions Settings { get; }
    void Evaluate(int cameraId, FrameTruth? truth, IReadOnlyList<TrackedDetection> detections);
    IReadOnlyList<EvalRow> CameraRows();
    IReadOnlyList<EvalRow> ClassRows();
    void Reset();
}

/// <summary>
/// Scores detections against the truth sidecar of the same frame: greedy IoU matching gives TP/FP/FN, mean IoU of
/// matches, and identity switches (a truth entity matched by a different track ID than last time).
/// Truth boxes that are ownship (unless included) or smaller than MinTruthPixels are "don't care": detections on them
/// count as neither TP nor FP, and missing them is not an FN.
/// </summary>
public sealed class EvaluationService : IEvaluationService
{
    private sealed class Counters
    {
        public long Frames, Tp, Fp, Fn, IdSwitches;
        public double IouSum;
        public EvalRow ToRow(string scope)
        {
            double p = Tp + Fp > 0 ? (double)Tp / (Tp + Fp) : 0;
            double r = Tp + Fn > 0 ? (double)Tp / (Tp + Fn) : 0;
            double f1 = p + r > 0 ? 2 * p * r / (p + r) : 0;
            return new EvalRow(scope, Frames, Tp, Fp, Fn, Math.Round(p, 4), Math.Round(r, 4), Math.Round(f1, 4),
                Tp > 0 ? Math.Round(IouSum / Tp, 4) : 0, IdSwitches);
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, Counters> _cameras = new();
    private readonly Dictionary<string, Counters> _classes = new();
    private readonly Dictionary<(int Camera, int Entity), int> _lastTrack = new();

    public EvaluationOptions Settings { get; }

    public EvaluationService(IOptions<ConsumerOptions> options) => Settings = options.Value.Evaluation;

    public void Evaluate(int cameraId, FrameTruth? truth, IReadOnlyList<TrackedDetection> detections)
    {
        if (truth is null) return;
        var s = Settings;
        var gts = truth.Objects.Where(o => o.Bbox.Length == 4).ToList();
        bool Scored(ObjectTruth o) =>
            (s.IncludeOwnship || !o.IsOwnship) &&
            Math.Max(o.Bbox[2] - o.Bbox[0], o.Bbox[3] - o.Bbox[1]) >= s.MinTruthPixels;

        var pairs = new List<(float Iou, int Gt, int Det)>();
        for (int g = 0; g < gts.Count; g++)
        for (int d = 0; d < detections.Count; d++)
        {
            var b = gts[g].Bbox;
            var det = detections[d].Detection;
            if (!s.ClassAgnostic && det.ClassId != gts[g].ClassId) continue;
            float iou = Detection.Iou((float)b[0], (float)b[1], (float)b[2], (float)b[3], det.X1, det.Y1, det.X2, det.Y2);
            if (iou >= s.IouThreshold) pairs.Add((iou, g, d));
        }

        var gtMatch = new int[gts.Count];
        var detMatch = new int[detections.Count];
        Array.Fill(gtMatch, -1);
        Array.Fill(detMatch, -1);
        var ious = new float[gts.Count];
        foreach (var (iou, g, d) in pairs.OrderByDescending(p => p.Iou))
        {
            if (gtMatch[g] >= 0 || detMatch[d] >= 0) continue;
            gtMatch[g] = d;
            detMatch[d] = g;
            ious[g] = iou;
        }

        lock (_gate)
        {
            var cam = Get(_cameras, cameraId);
            cam.Frames++;
            for (int g = 0; g < gts.Count; g++)
            {
                if (!Scored(gts[g])) continue;
                var cls = Get(_classes, gts[g].ClassName);
                if (gtMatch[g] >= 0)
                {
                    cam.Tp++; cls.Tp++;
                    cam.IouSum += ious[g]; cls.IouSum += ious[g];
                    int track = detections[gtMatch[g]].TrackId;
                    var key = (cameraId, gts[g].EntityId);
                    if (_lastTrack.TryGetValue(key, out var prev) && prev != track) { cam.IdSwitches++; cls.IdSwitches++; }
                    _lastTrack[key] = track;
                }
                else
                {
                    cam.Fn++; cls.Fn++;
                }
            }
            for (int d = 0; d < detections.Count; d++)
            {
                if (detMatch[d] >= 0) continue; // matched (scored TP or don't-care)
                cam.Fp++;
                Get(_classes, $"(fp) {detections[d].Detection.ClassName}").Fp++;
            }
        }
    }

    private static Counters Get<TKey>(Dictionary<TKey, Counters> d, TKey key) where TKey : notnull
    {
        if (!d.TryGetValue(key, out var c)) d[key] = c = new Counters();
        return c;
    }

    public IReadOnlyList<EvalRow> CameraRows()
    {
        lock (_gate)
        {
            var rows = _cameras.OrderBy(k => k.Key).Select(k => k.Value.ToRow($"camera {k.Key}")).ToList();
            var all = new Counters();
            foreach (var c in _cameras.Values)
            {
                all.Frames += c.Frames; all.Tp += c.Tp; all.Fp += c.Fp; all.Fn += c.Fn; all.IouSum += c.IouSum; all.IdSwitches += c.IdSwitches;
            }
            if (_cameras.Count > 0) rows.Add(all.ToRow("all cameras"));
            return rows;
        }
    }

    public IReadOnlyList<EvalRow> ClassRows()
    {
        lock (_gate) return _classes.OrderBy(k => k.Key).Select(k => k.Value.ToRow(k.Key)).ToList();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _cameras.Clear();
            _classes.Clear();
            _lastTrack.Clear();
        }
    }
}
