using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;

namespace WebAirIG.Common.Sensors;

/// <summary>Pose of a platform (entity) in the LTP NED frame.</summary>
public readonly record struct PlatformPose(Vec3d PositionNed, Mat3d NedFromBody);

/// <summary>Pose of a camera in the LTP NED frame. NedFromCam columns: boresight, image-right, image-down.</summary>
public readonly record struct CameraPose(Vec3d PositionNed, Mat3d NedFromCam)
{
    public static CameraPose Compute(PlatformPose platform, MountDef mount, double azDeg, double elDeg)
    {
        var pos = platform.PositionNed + platform.NedFromBody * GimbalMath.MountOffsetFrd(mount);
        var rot = platform.NedFromBody * GimbalMath.BodyFromCamera(mount, azDeg, elDeg);
        return new CameraPose(pos, rot);
    }

    public Vec3d Boresight => NedFromCam.Column0;

    /// <summary>Transforms a world (LTP NED) point into camera frame C.</summary>
    public Vec3d WorldToCamera(Vec3d pNed) => NedFromCam.Transpose() * (pNed - PositionNed);
}

/// <summary>Ground footprint of a camera on the flat LTP ground plane (NED down = groundDownM).</summary>
public static class Footprint
{
    /// <summary>
    /// Intersects the four image-corner rays and the boresight with the ground. Rays that miss the ground (or exceed
    /// <paramref name="maxRangeM"/>) are clipped to a horizontal point at max range so the polygon stays drawable.
    /// Returned points are ENU (x = east, y = north, z = up) in order: TL, TR, BR, BL, then the boresight point.
    /// </summary>
    public static Vec3d[] Compute(CameraPose pose, PinholeIntrinsics k, double maxRangeM, double groundUpM = 0.0)
    {
        var rays = new[]
        {
            k.Ray(0, 0), k.Ray(k.Width, 0), k.Ray(k.Width, k.Height), k.Ray(0, k.Height), new Vec3d(1, 0, 0),
        };
        var result = new Vec3d[rays.Length];
        double groundDown = -groundUpM;
        for (int i = 0; i < rays.Length; i++)
        {
            var dirNed = pose.NedFromCam * rays[i];
            Vec3d hit;
            double heightAbove = groundDown - pose.PositionNed.Z; // positive when camera is above ground
            if (dirNed.Z > 1e-6 && heightAbove > 0)
            {
                double t = heightAbove / dirNed.Z;
                hit = t <= maxRangeM ? pose.PositionNed + dirNed * t : ClipHorizontal(pose.PositionNed, dirNed, maxRangeM, groundDown);
            }
            else
            {
                hit = ClipHorizontal(pose.PositionNed, dirNed, maxRangeM, groundDown);
            }
            result[i] = LocalTangentPlane.NedToEnu(hit);
        }
        return result;
    }

    private static Vec3d ClipHorizontal(Vec3d origin, Vec3d dir, double range, double groundDown)
    {
        var h = new Vec3d(dir.X, dir.Y, 0);
        if (h.Length < 1e-9) h = new Vec3d(1, 0, 0);
        h = h.Normalized();
        return new Vec3d(origin.X + h.X * range, origin.Y + h.Y * range, groundDown);
    }
}
