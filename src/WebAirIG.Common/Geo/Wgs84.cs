namespace WebAirIG.Common.Geo;

/// <summary>Geodetic position: latitude/longitude in degrees, altitude in meters above the WGS-84 ellipsoid.</summary>
public readonly record struct GeoPoint(double LatDeg, double LonDeg, double AltM)
{
    public override string ToString() => $"{LatDeg:F7}, {LonDeg:F7}, {AltM:F1} m";
}

/// <summary>WGS-84 ellipsoid conversions.</summary>
public static class Wgs84
{
    public const double A = 6378137.0;
    public const double F = 1.0 / 298.257223563;
    public const double B = A * (1 - F);
    public const double E2 = F * (2 - F);
    public const double Deg2Rad = Math.PI / 180.0;
    public const double Rad2Deg = 180.0 / Math.PI;

    public static Vec3d GeodeticToEcef(GeoPoint p)
    {
        double lat = p.LatDeg * Deg2Rad, lon = p.LonDeg * Deg2Rad;
        double sLat = Math.Sin(lat), cLat = Math.Cos(lat);
        double n = A / Math.Sqrt(1 - E2 * sLat * sLat);
        return new Vec3d(
            (n + p.AltM) * cLat * Math.Cos(lon),
            (n + p.AltM) * cLat * Math.Sin(lon),
            (n * (1 - E2) + p.AltM) * sLat);
    }

    public static GeoPoint EcefToGeodetic(Vec3d ecef)
    {
        double x = ecef.X, y = ecef.Y, z = ecef.Z;
        double lon = Math.Atan2(y, x);
        double p = Math.Sqrt(x * x + y * y);
        double lat = Math.Atan2(z, p * (1 - E2));
        for (int i = 0; i < 6; i++)
        {
            double s = Math.Sin(lat);
            double n = A / Math.Sqrt(1 - E2 * s * s);
            double h0 = p * Math.Cos(lat) + z * s - A * Math.Sqrt(1 - E2 * s * s);
            lat = Math.Atan2(z, p * (1 - E2 * n / (n + h0)));
        }
        double sl = Math.Sin(lat);
        double h = p * Math.Cos(lat) + z * sl - A * Math.Sqrt(1 - E2 * sl * sl);
        return new GeoPoint(lat * Rad2Deg, lon * Rad2Deg, h);
    }

    /// <summary>R_ecef_from_ned at the given geodetic position (columns = north, east, down in ECEF).</summary>
    public static Mat3d EcefFromNed(double latDeg, double lonDeg)
    {
        double lat = latDeg * Deg2Rad, lon = lonDeg * Deg2Rad;
        double sLat = Math.Sin(lat), cLat = Math.Cos(lat), sLon = Math.Sin(lon), cLon = Math.Cos(lon);
        var north = new Vec3d(-sLat * cLon, -sLat * sLon, cLat);
        var east = new Vec3d(-sLon, cLon, 0);
        var down = new Vec3d(-cLat * cLon, -cLat * sLon, -sLat);
        return Mat3d.FromColumns(north, east, down);
    }

    /// <summary>R_ecef_from_enu at the given geodetic position (columns = east, north, up in ECEF).</summary>
    public static Mat3d EcefFromEnu(double latDeg, double lonDeg)
    {
        var ned = EcefFromNed(latDeg, lonDeg);
        return Mat3d.FromColumns(ned.Column1, ned.Column0, -ned.Column2);
    }
}
