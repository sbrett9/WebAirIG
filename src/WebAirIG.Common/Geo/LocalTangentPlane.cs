namespace WebAirIG.Common.Geo;

/// <summary>
/// Local tangent plane anchored at a geodetic origin. The IG renders a flat world in this frame and the host
/// simulates motion in it. Conversions to/from geodetic are exact (via ECEF), so CIGI lat/lon round-trips cleanly.
///
/// Frames used throughout WebAirIG:
///   ENU  : x = East,  y = North, z = Up      (meters from origin)
///   NED  : x = North, y = East,  z = Down    (meters from origin, "LTP NED")
///   FRD  : body frame x = Forward, y = Right, z = Down
/// </summary>
public sealed class LocalTangentPlane
{
    public GeoPoint Origin { get; }
    private readonly Vec3d _originEcef;
    private readonly Mat3d _ecefFromEnu;
    private readonly Mat3d _enuFromEcef;
    private readonly Mat3d _ecefFromLtpNed;

    public LocalTangentPlane(GeoPoint origin)
    {
        Origin = origin;
        _originEcef = Wgs84.GeodeticToEcef(origin);
        _ecefFromEnu = Wgs84.EcefFromEnu(origin.LatDeg, origin.LonDeg);
        _enuFromEcef = _ecefFromEnu.Transpose();
        _ecefFromLtpNed = Wgs84.EcefFromNed(origin.LatDeg, origin.LonDeg);
    }

    public Vec3d EcefToEnu(Vec3d ecef) => _enuFromEcef * (ecef - _originEcef);
    public Vec3d EnuToEcef(Vec3d enu) => _originEcef + _ecefFromEnu * enu;
    public Vec3d GeodeticToEnu(GeoPoint p) => EcefToEnu(Wgs84.GeodeticToEcef(p));
    public GeoPoint EnuToGeodetic(Vec3d enu) => Wgs84.EcefToGeodetic(EnuToEcef(enu));

    public static Vec3d EnuToNed(Vec3d enu) => new(enu.Y, enu.X, -enu.Z);
    public static Vec3d NedToEnu(Vec3d ned) => new(ned.Y, ned.X, -ned.Z);

    /// <summary>R_ecef_from_ltpNed (NED axes at the origin).</summary>
    public Mat3d EcefFromLtpNed => _ecefFromLtpNed;

    /// <summary>
    /// Converts an attitude expressed relative to local NED at the entity's own position (what CIGI and DIS-style
    /// heading/pitch/roll mean) into R_ltpNed_from_body. Accounts for meridian convergence away from the origin.
    /// </summary>
    public Mat3d LtpNedFromBody(GeoPoint position, double yawDeg, double pitchDeg, double rollDeg)
    {
        var localNedFromBody = Mat3d.FromYawPitchRoll(yawDeg * Wgs84.Deg2Rad, pitchDeg * Wgs84.Deg2Rad, rollDeg * Wgs84.Deg2Rad);
        var ecefFromLocalNed = Wgs84.EcefFromNed(position.LatDeg, position.LonDeg);
        return _ecefFromLtpNed.Transpose() * ecefFromLocalNed * localNedFromBody;
    }

    /// <summary>Inverse of <see cref="LtpNedFromBody"/>: yaw/pitch/roll (deg) relative to local NED at <paramref name="position"/>.</summary>
    public (double YawDeg, double PitchDeg, double RollDeg) LocalYprFromLtp(GeoPoint position, Mat3d ltpNedFromBody)
    {
        var ecefFromLocalNed = Wgs84.EcefFromNed(position.LatDeg, position.LonDeg);
        var localNedFromBody = ecefFromLocalNed.Transpose() * _ecefFromLtpNed * ltpNedFromBody;
        var (y, p, r) = localNedFromBody.ToYawPitchRoll();
        return (NormalizeDeg360(y * Wgs84.Rad2Deg), p * Wgs84.Rad2Deg, r * Wgs84.Rad2Deg);
    }

    /// <summary>R_localNed_from_ltpNed at <paramref name="position"/> (meridian convergence correction).</summary>
    public Mat3d LocalNedFromLtpNed(GeoPoint position) =>
        Wgs84.EcefFromNed(position.LatDeg, position.LonDeg).Transpose() * _ecefFromLtpNed;

    /// <summary>Rotates a vector from LTP NED into local NED at <paramref name="position"/>.</summary>
    public Vec3d LtpNedToLocalNed(GeoPoint position, Vec3d vLtpNed)
    {
        var ecefFromLocalNed = Wgs84.EcefFromNed(position.LatDeg, position.LonDeg);
        return ecefFromLocalNed.Transpose() * (_ecefFromLtpNed * vLtpNed);
    }

    public static double NormalizeDeg360(double deg)
    {
        deg %= 360.0;
        return deg < 0 ? deg + 360.0 : deg;
    }

    public static double NormalizeDeg180(double deg)
    {
        deg = NormalizeDeg360(deg);
        return deg > 180.0 ? deg - 360.0 : deg;
    }
}
