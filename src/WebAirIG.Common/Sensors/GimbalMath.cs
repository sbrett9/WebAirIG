using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;

namespace WebAirIG.Common.Sensors;

/// <summary>
/// Two-axis (azimuth-over-elevation) gimbal model.
///
/// Mount frame M is the body FRD frame rotated by the mount's yaw/pitch/roll. The camera frame C (x = boresight,
/// y = image right, z = image down) is M rotated by azimuth about M.z then elevation about the new y axis:
///     R_body_from_cam = R_body_from_mount * Rz(az) * Ry(el)
///
/// Nadir mount (pitch = -90 deg): (0,0) looks straight down with image top = nose; +az tilts toward the right wing,
/// +el toward the nose.
/// Lateral mount (yaw = -90 left / +90 right, pitch 0): (0,0) looks horizontally out the side with image up = aircraft
/// up; +el looks up; +az turns clockwise seen from above (toward the nose for left cameras, toward the tail for right).
/// </summary>
public static class GimbalMath
{
    public static Mat3d BodyFromMount(MountDef mount) =>
        Mat3d.FromYawPitchRoll(mount.YawDeg * Wgs84.Deg2Rad, mount.PitchDeg * Wgs84.Deg2Rad, mount.RollDeg * Wgs84.Deg2Rad);

    public static Mat3d BodyFromCamera(MountDef mount, double azDeg, double elDeg) =>
        BodyFromMount(mount) * Mat3d.RotZ(azDeg * Wgs84.Deg2Rad) * Mat3d.RotY(elDeg * Wgs84.Deg2Rad);

    /// <summary>Solves the gimbal angles that put the boresight on a line of sight given in the BODY frame.</summary>
    public static (double AzDeg, double ElDeg) SolveAzEl(MountDef mount, Vec3d losBody)
    {
        var losMount = BodyFromMount(mount).Transpose() * losBody.Normalized();
        double el = -Math.Asin(Math.Clamp(losMount.Z, -1.0, 1.0));
        double az = Math.Atan2(losMount.Y, losMount.X);
        return (az * Wgs84.Rad2Deg, el * Wgs84.Rad2Deg);
    }

    public static (double AzDeg, double ElDeg) Clamp(GimbalLimitsDef limits, double azDeg, double elDeg) =>
        (Math.Clamp(azDeg, limits.AzMinDeg, limits.AzMaxDeg), Math.Clamp(elDeg, limits.ElMinDeg, limits.ElMaxDeg));

    public static (double AzDeg, double ElDeg) Clamp(GimbalLimitsDef limits, (double AzDeg, double ElDeg) angles) =>
        Clamp(limits, angles.AzDeg, angles.ElDeg);

    public static Vec3d MountOffsetFrd(MountDef mount) => new(mount.XFwdM, mount.YRightM, mount.ZDownM);
}

/// <summary>Pinhole intrinsics for a square-pixel camera with symmetric FOV.</summary>
public readonly record struct PinholeIntrinsics(int Width, int Height, double Fx, double Fy, double Cx, double Cy)
{
    /// <summary>Builds intrinsics from the vertical FOV (Godot Camera3D keep-height convention).</summary>
    public static PinholeIntrinsics FromVerticalFov(int width, int height, double vfovDeg)
    {
        double fy = (height / 2.0) / Math.Tan(vfovDeg * Wgs84.Deg2Rad / 2.0);
        return new PinholeIntrinsics(width, height, fy, fy, width / 2.0, height / 2.0);
    }

    public double HorizontalFovDeg => 2.0 * Math.Atan((Width / 2.0) / Fx) * Wgs84.Rad2Deg;
    public double VerticalFovDeg => 2.0 * Math.Atan((Height / 2.0) / Fy) * Wgs84.Rad2Deg;

    /// <summary>Projects a point in camera frame C (x = forward, y = right, z = down). Returns false if behind the near plane.</summary>
    public bool TryProject(Vec3d pCam, double nearM, out double u, out double v)
    {
        if (pCam.X <= nearM) { u = v = double.NaN; return false; }
        u = Cx + Fx * (pCam.Y / pCam.X);
        v = Cy + Fy * (pCam.Z / pCam.X);
        return true;
    }

    /// <summary>Unit ray in camera frame C for pixel (u, v).</summary>
    public Vec3d Ray(double u, double v) => new Vec3d(1.0, (u - Cx) / Fx, (v - Cy) / Fy).Normalized();
}
