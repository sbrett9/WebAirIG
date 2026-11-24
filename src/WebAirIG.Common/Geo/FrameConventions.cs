namespace WebAirIG.Common.Geo;

/// <summary>
/// Mapping between WebAirIG's aerospace frames and Godot's coordinate system.
/// Godot world: +X = East, +Y = Up, -Z = North (so +Z = South).
/// Godot node local: +X = right, +Y = up, -Z = forward. A camera looks along its local -Z with +Y = image up.
/// </summary>
public static class FrameConventions
{
    public static Vec3d EnuToGodot(Vec3d enu) => new(enu.X, enu.Z, -enu.Y);
    public static Vec3d GodotToEnu(Vec3d g) => new(g.X, -g.Z, g.Y);
    public static Vec3d NedToGodot(Vec3d ned) => new(ned.Y, -ned.Z, -ned.X);
    public static Vec3d GodotToNed(Vec3d g) => new(-g.Z, g.X, -g.Y);

    /// <summary>Body FRD (x fwd, y right, z down) to Godot node-local (x right, y up, -z fwd).</summary>
    public static Vec3d FrdToGodotLocal(Vec3d frd) => new(frd.Y, -frd.Z, -frd.X);
    public static Vec3d GodotLocalToFrd(Vec3d g) => new(-g.Z, g.X, -g.Y);

    /// <summary>
    /// Given R_ltpNed_from_frd (for an entity body or a camera frame whose x axis is the look direction),
    /// returns the Godot basis columns (X, Y, Z) expressed in Godot world coordinates.
    /// </summary>
    public static (Vec3d X, Vec3d Y, Vec3d Z) GodotBasisFromNedFrd(Mat3d nedFromFrd)
    {
        var fwd = nedFromFrd.Column0;
        var right = nedFromFrd.Column1;
        var down = nedFromFrd.Column2;
        return (NedToGodot(right), NedToGodot(-down), NedToGodot(-fwd));
    }
}
