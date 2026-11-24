using WebAirIG.Common.Geo;

namespace WebAirIG.Dis;

/// <summary>Geodetic/NED to DIS (ECEF) conversions and DIS timestamps.</summary>
public static class DisConversions
{
    /// <summary>
    /// DIS orientation (psi, theta, phi in radians) of a body whose attitude is given relative to local NED at
    /// <paramref name="position"/>. DIS Euler angles rotate ECEF axes into body axes with the Z-Y-X sequence.
    /// </summary>
    public static (float Psi, float Theta, float Phi) OrientationFromLocalNed(GeoPoint position, Mat3d localNedFromBody)
    {
        var ecefFromBody = Wgs84.EcefFromNed(position.LatDeg, position.LonDeg) * localNedFromBody;
        var (psi, theta, phi) = ecefFromBody.ToYawPitchRoll();
        return ((float)psi, (float)theta, (float)phi);
    }

    /// <summary>Inverse of <see cref="OrientationFromLocalNed"/>: returns R_localNed_from_body.</summary>
    public static Mat3d LocalNedFromOrientation(GeoPoint position, float psi, float theta, float phi)
    {
        var ecefFromBody = Mat3d.FromYawPitchRoll(psi, theta, phi);
        return Wgs84.EcefFromNed(position.LatDeg, position.LonDeg).Transpose() * ecefFromBody;
    }

    public static DisVec3f VelocityEcef(GeoPoint position, Vec3d velocityLocalNed)
    {
        var v = Wgs84.EcefFromNed(position.LatDeg, position.LonDeg) * velocityLocalNed;
        return new DisVec3f((float)v.X, (float)v.Y, (float)v.Z);
    }

    /// <summary>
    /// DIS absolute timestamp: time past the hour in units of 3600/2^31 s, shifted left one bit with LSB = 1.
    /// </summary>
    public static uint AbsoluteTimestamp(DateTime utc)
    {
        double secondsPastHour = utc.Minute * 60.0 + utc.Second + utc.Millisecond / 1000.0 + (utc.Ticks % TimeSpan.TicksPerMillisecond) / 1e7;
        uint units = (uint)Math.Min(secondsPastHour / 3600.0 * 2147483648.0, 2147483647.0);
        return (units << 1) | 1u;
    }
}
