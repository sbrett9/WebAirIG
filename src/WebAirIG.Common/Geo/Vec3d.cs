namespace WebAirIG.Common.Geo;

/// <summary>Double precision 3-vector. Meaning of X/Y/Z depends on the frame (ENU, NED, ECEF, FRD body...).</summary>
public readonly record struct Vec3d(double X, double Y, double Z)
{
    public static readonly Vec3d Zero = new(0, 0, 0);
    public static readonly Vec3d UnitX = new(1, 0, 0);
    public static readonly Vec3d UnitY = new(0, 1, 0);
    public static readonly Vec3d UnitZ = new(0, 0, 1);

    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3d operator -(Vec3d a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3d operator *(Vec3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3d operator *(double s, Vec3d a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3d operator /(Vec3d a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;

    public Vec3d Normalized()
    {
        double l = Length;
        return l > 1e-15 ? this / l : this;
    }

    public static double Dot(Vec3d a, Vec3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3d Cross(Vec3d a, Vec3d b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static Vec3d Lerp(Vec3d a, Vec3d b, double t) => a + (b - a) * t;

    public double[] ToArray() => [X, Y, Z];

    public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
}
