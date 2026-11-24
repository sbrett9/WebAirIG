using Godot;
using WebAirIG.Common.Geo;

namespace WebAirIG.IG.Core;

/// <summary>Conversions between WebAirIG double-precision math types and Godot types.</summary>
public static class GodotConv
{
    public static Vector3 ToVector3(this Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
    public static Vec3d ToVec3d(this Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Godot world transform for a frame given its LTP NED position and R_ltpNed_from_frd.</summary>
    public static Transform3D FromNedPose(Vec3d positionNed, Mat3d nedFromFrd)
    {
        var (x, y, z) = FrameConventions.GodotBasisFromNedFrd(nedFromFrd);
        return new Transform3D(new Basis(x.ToVector3(), y.ToVector3(), z.ToVector3()), FrameConventions.NedToGodot(positionNed).ToVector3());
    }

    public static Vector3 FrdToGodotLocal(double[] frd) =>
        FrameConventions.FrdToGodotLocal(new Vec3d(At(frd, 0), At(frd, 1), At(frd, 2))).ToVector3();

    public static Vec3d GodotLocalToFrd(Vector3 g) => FrameConventions.GodotLocalToFrd(g.ToVec3d());

    private static double At(double[] a, int i) => a is not null && a.Length > i ? a[i] : 0.0;
}
