using WebAirIG.Common.Geo;
using WebAirIG.Common.Sensors;

namespace WebAirIG.Common.Truth;

/// <summary>Projects an entity's model sample points into a camera to produce a tight 2D bounding box.</summary>
public static class TruthProjector
{
    public readonly record struct Projection(
        bool InView,
        double X1, double Y1, double X2, double Y2,
        double Ux1, double Uy1, double Ux2, double Uy2,
        bool Truncated, double VisibleFraction,
        double CenterU, double CenterV, double RangeM,
        double OffBoresightXDeg, double OffBoresightYDeg);

    /// <param name="samplePointsFrd">Model points in body FRD meters (relative to entity origin).</param>
    public static Projection Project(
        CameraPose cam, PinholeIntrinsics k, double nearM,
        PlatformPose entity, ReadOnlySpan<Vec3d> samplePointsFrd)
    {
        var camFromNed = cam.NedFromCam.Transpose();
        var camFromBody = camFromNed * entity.NedFromBody;
        var entityInCam = camFromNed * (entity.PositionNed - cam.PositionNed);

        double minU = double.MaxValue, minV = double.MaxValue, maxU = double.MinValue, maxV = double.MinValue;
        int inFront = 0, inside = 0, total = samplePointsFrd.Length;

        foreach (var p in samplePointsFrd)
        {
            var pc = entityInCam + camFromBody * p;
            if (!k.TryProject(pc, nearM, out double u, out double v)) continue;
            inFront++;
            if (u < minU) minU = u;
            if (u > maxU) maxU = u;
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
            if (u >= 0 && u < k.Width && v >= 0 && v < k.Height) inside++;
        }

        double range = entityInCam.Length;
        double offX = Math.Atan2(entityInCam.Y, entityInCam.X) * Wgs84.Rad2Deg;
        double offY = Math.Atan2(entityInCam.Z, entityInCam.X) * Wgs84.Rad2Deg;
        k.TryProject(entityInCam, 1e-6, out double cu, out double cv);

        if (inFront == 0)
            return new Projection(false, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, cu, cv, range, offX, offY);

        double x1 = Math.Clamp(minU, 0, k.Width), x2 = Math.Clamp(maxU, 0, k.Width);
        double y1 = Math.Clamp(minV, 0, k.Height), y2 = Math.Clamp(maxV, 0, k.Height);
        bool overlaps = x2 > x1 && y2 > y1;
        // An object entirely in front of the camera may fill the frame with no vertex inside it (close-up), so trust
        // the box. If it straddles the camera plane (e.g. ownship around its own camera) the box of the in-front
        // points is unreliable, so require at least one sampled point to actually land in the image.
        bool inView = overlaps && (inside > 0 || inFront == total);
        bool truncated = inFront < total || minU < 0 || minV < 0 || maxU > k.Width || maxV > k.Height;
        double visible = total > 0 ? (double)inside / total : 0;

        return new Projection(inView, x1, y1, x2, y2, minU, minV, maxU, maxV, truncated, visible, cu, cv, range, offX, offY);
    }
}
