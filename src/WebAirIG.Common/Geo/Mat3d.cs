namespace WebAirIG.Common.Geo;

/// <summary>
/// Row-major 3x3 double matrix. Rotation matrices here are "parent from child":
/// v_parent = R * v_child, and the columns of R are the child axes expressed in the parent frame.
/// </summary>
public readonly struct Mat3d
{
    public readonly double M11, M12, M13;
    public readonly double M21, M22, M23;
    public readonly double M31, M32, M33;

    public Mat3d(double m11, double m12, double m13,
                 double m21, double m22, double m23,
                 double m31, double m32, double m33)
    {
        M11 = m11; M12 = m12; M13 = m13;
        M21 = m21; M22 = m22; M23 = m23;
        M31 = m31; M32 = m32; M33 = m33;
    }

    public static readonly Mat3d Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public static Mat3d FromColumns(Vec3d c0, Vec3d c1, Vec3d c2) =>
        new(c0.X, c1.X, c2.X,
            c0.Y, c1.Y, c2.Y,
            c0.Z, c1.Z, c2.Z);

    public Vec3d Column0 => new(M11, M21, M31);
    public Vec3d Column1 => new(M12, M22, M32);
    public Vec3d Column2 => new(M13, M23, M33);

    public Mat3d Transpose() => new(M11, M21, M31, M12, M22, M32, M13, M23, M33);

    public static Mat3d operator *(Mat3d a, Mat3d b) => new(
        a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31,
        a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32,
        a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33,
        a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31,
        a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32,
        a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33,
        a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31,
        a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32,
        a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33);

    public static Vec3d operator *(Mat3d m, Vec3d v) => new(
        m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z,
        m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z,
        m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z);

    /// <summary>Active rotation about X by <paramref name="rad"/> (right-handed).</summary>
    public static Mat3d RotX(double rad)
    {
        double c = Math.Cos(rad), s = Math.Sin(rad);
        return new(1, 0, 0, 0, c, -s, 0, s, c);
    }

    public static Mat3d RotY(double rad)
    {
        double c = Math.Cos(rad), s = Math.Sin(rad);
        return new(c, 0, s, 0, 1, 0, -s, 0, c);
    }

    public static Mat3d RotZ(double rad)
    {
        double c = Math.Cos(rad), s = Math.Sin(rad);
        return new(c, -s, 0, s, c, 0, 0, 0, 1);
    }

    /// <summary>
    /// Aerospace Z-Y-X (yaw, pitch, roll) Euler sequence: R_parent_from_child = Rz(yaw) * Ry(pitch) * Rx(roll).
    /// For an aircraft with parent = local NED and child = body FRD this is the standard attitude matrix.
    /// </summary>
    public static Mat3d FromYawPitchRoll(double yawRad, double pitchRad, double rollRad) =>
        RotZ(yawRad) * RotY(pitchRad) * RotX(rollRad);

    /// <summary>Inverse of <see cref="FromYawPitchRoll"/>. At gimbal lock (|pitch| = 90 deg) roll is set to 0.</summary>
    public (double YawRad, double PitchRad, double RollRad) ToYawPitchRoll()
    {
        double sp = Math.Clamp(-M31, -1.0, 1.0);
        double pitch = Math.Asin(sp);
        if (Math.Abs(sp) > 0.999999)
        {
            // Gimbal lock: yaw and roll are coupled; put everything into yaw.
            double yawLock = Math.Atan2(-M12, M22);
            return (yawLock, pitch, 0.0);
        }
        double roll = Math.Atan2(M32, M33);
        double yaw = Math.Atan2(M21, M11);
        return (yaw, pitch, roll);
    }

    /// <summary>Row-major 9 element array, handy for JSON output.</summary>
    public double[] ToRowMajorArray() => [M11, M12, M13, M21, M22, M23, M31, M32, M33];
}
