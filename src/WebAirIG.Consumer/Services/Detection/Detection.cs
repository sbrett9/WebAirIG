namespace WebAirIG.Consumer.Services.Detection;

/// <summary>Detection in full-resolution image pixels.</summary>
public readonly record struct Detection(float X1, float Y1, float X2, float Y2, float Score, int ClassId, string ClassName)
{
    public float Width => X2 - X1;
    public float Height => Y2 - Y1;

    public static float Iou(float ax1, float ay1, float ax2, float ay2, float bx1, float by1, float bx2, float by2)
    {
        float ix = Math.Max(0, Math.Min(ax2, bx2) - Math.Max(ax1, bx1));
        float iy = Math.Max(0, Math.Min(ay2, by2) - Math.Max(ay1, by1));
        float inter = ix * iy;
        float union = (ax2 - ax1) * (ay2 - ay1) + (bx2 - bx1) * (by2 - by1) - inter;
        return union > 0 ? inter / union : 0;
    }

    public float Iou(in Detection o) => Iou(X1, Y1, X2, Y2, o.X1, o.Y1, o.X2, o.Y2);
}

public readonly record struct TrackedDetection(Detection Detection, int TrackId);

public sealed record DetectionResult(int CameraId, ulong Frame, IReadOnlyList<TrackedDetection> Detections, double InferenceMs, DateTime Utc);
