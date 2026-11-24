namespace WebAirIG.Consumer.Services.Detection;

/// <summary>
/// Minimal multi-object tracker (SORT-style without a Kalman filter): constant-velocity box prediction, greedy IoU
/// association, track birth on unmatched detections and deletion after <see cref="MaxMisses"/> frames.
/// Good enough to measure ID stability; swap for ByteTrack/BoT-SORT later if needed.
/// </summary>
public sealed class IouTracker
{
    private sealed class Track
    {
        public int Id;
        public float X1, Y1, X2, Y2;
        public float Vx, Vy;
        public int Misses;
        public int ClassId;
    }

    private readonly List<Track> _tracks = [];
    private int _nextId = 1;

    public float MatchIou { get; set; } = 0.2f;
    public int MaxMisses { get; set; } = 15;
    public bool ClassAgnostic { get; set; } = true;

    public List<TrackedDetection> Update(IReadOnlyList<Detection> detections)
    {
        foreach (var t in _tracks)
        {
            t.X1 += t.Vx; t.X2 += t.Vx;
            t.Y1 += t.Vy; t.Y2 += t.Vy;
        }

        var pairs = new List<(float Iou, int Track, int Det)>();
        for (int ti = 0; ti < _tracks.Count; ti++)
        for (int di = 0; di < detections.Count; di++)
        {
            var t = _tracks[ti];
            var d = detections[di];
            if (!ClassAgnostic && t.ClassId != d.ClassId) continue;
            float iou = Detection.Iou(t.X1, t.Y1, t.X2, t.Y2, d.X1, d.Y1, d.X2, d.Y2);
            if (iou >= MatchIou) pairs.Add((iou, ti, di));
        }

        var trackUsed = new bool[_tracks.Count];
        var detTrack = new int[detections.Count];
        Array.Fill(detTrack, -1);
        foreach (var (_, ti, di) in pairs.OrderByDescending(p => p.Iou))
        {
            if (trackUsed[ti] || detTrack[di] >= 0) continue;
            trackUsed[ti] = true;
            detTrack[di] = ti;
        }

        var result = new List<TrackedDetection>(detections.Count);
        for (int di = 0; di < detections.Count; di++)
        {
            var d = detections[di];
            Track t;
            if (detTrack[di] >= 0)
            {
                t = _tracks[detTrack[di]];
                float cxOld = (t.X1 + t.X2) / 2 - t.Vx, cyOld = (t.Y1 + t.Y2) / 2 - t.Vy;
                float cx = (d.X1 + d.X2) / 2, cy = (d.Y1 + d.Y2) / 2;
                t.Vx = 0.6f * t.Vx + 0.4f * (cx - cxOld);
                t.Vy = 0.6f * t.Vy + 0.4f * (cy - cyOld);
                t.Misses = 0;
            }
            else
            {
                t = new Track { Id = _nextId++ };
                _tracks.Add(t);
            }
            t.X1 = d.X1; t.Y1 = d.Y1; t.X2 = d.X2; t.Y2 = d.Y2;
            t.ClassId = d.ClassId;
            result.Add(new TrackedDetection(d, t.Id));
        }

        for (int ti = 0; ti < trackUsed.Length; ti++)
            if (!trackUsed[ti]) _tracks[ti].Misses++;
        _tracks.RemoveAll(t => t.Misses > MaxMisses);
        return result;
    }

    public void Reset()
    {
        _tracks.Clear();
        _nextId = 1;
    }
}
