using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;
using WebAirIG.Common.Sensors;

namespace WebAirIG.Tests;

public class GeoTests
{
    private static readonly LocalTangentPlane Ltp = new(new GeoPoint(35.0, -117.0, 700));

    [Theory]
    [InlineData(35.0, -117.0, 700)]
    [InlineData(-33.9, 151.2, 12000)]
    [InlineData(89.0, 10.0, 0)]
    [InlineData(0, 0, -50)]
    public void Geodetic_Ecef_RoundTrip(double lat, double lon, double alt)
    {
        var p = new GeoPoint(lat, lon, alt);
        var back = Wgs84.EcefToGeodetic(Wgs84.GeodeticToEcef(p));
        Assert.Equal(lat, back.LatDeg, 9);
        Assert.Equal(lon, back.LonDeg, 9);
        Assert.Equal(alt, back.AltM, 4);
    }

    [Fact]
    public void Ltp_Enu_RoundTrip_And_Axes()
    {
        var enu = new Vec3d(25_000, -18_000, 6_000);
        var geo = Ltp.EnuToGeodetic(enu);
        var back = Ltp.GeodeticToEnu(geo);
        Assert.Equal(enu.X, back.X, 4);
        Assert.Equal(enu.Y, back.Y, 4);
        Assert.Equal(enu.Z, back.Z, 4);

        var north = Ltp.EnuToGeodetic(new Vec3d(0, 1000, 0));
        Assert.True(north.LatDeg > Ltp.Origin.LatDeg);
        var east = Ltp.EnuToGeodetic(new Vec3d(1000, 0, 0));
        Assert.True(east.LonDeg > Ltp.Origin.LonDeg);
    }

    [Fact]
    public void YawPitchRoll_RoundTrip()
    {
        var m = Mat3d.FromYawPitchRoll(1.1, -0.3, 0.7);
        var (y, p, r) = m.ToYawPitchRoll();
        Assert.Equal(1.1, y, 9);
        Assert.Equal(-0.3, p, 9);
        Assert.Equal(0.7, r, 9);
    }

    [Fact]
    public void Attitude_Ltp_Local_RoundTrip_AwayFromOrigin()
    {
        var pos = Ltp.EnuToGeodetic(new Vec3d(40_000, 30_000, 5_000));
        var r = Ltp.LtpNedFromBody(pos, 45, 5, -20);
        var (yaw, pitch, roll) = Ltp.LocalYprFromLtp(pos, r);
        Assert.Equal(45, yaw, 6);
        Assert.Equal(5, pitch, 6);
        Assert.Equal(-20, roll, 6);
    }

    [Fact]
    public void NadirMount_ZeroGimbal_LooksDown_ImageTopIsNose()
    {
        var mount = new MountDef { PitchDeg = -90 };
        var r = GimbalMath.BodyFromCamera(mount, 0, 0);
        AssertVec(new Vec3d(0, 0, 1), r.Column0);   // boresight = body down
        AssertVec(new Vec3d(0, 1, 0), r.Column1);   // image right = body right
        AssertVec(new Vec3d(-1, 0, 0), r.Column2);  // image down = body aft  => image top = nose
    }

    [Fact]
    public void NadirMount_PositiveAzimuth_TiltsRight_PositiveElevation_TiltsForward()
    {
        var mount = new MountDef { PitchDeg = -90 };
        var az = GimbalMath.BodyFromCamera(mount, 30, 0).Column0;
        Assert.True(az.Y > 0.49 && az.Z > 0.85);
        var el = GimbalMath.BodyFromCamera(mount, 0, 20).Column0;
        Assert.True(el.X > 0.3 && el.Z > 0.9);
    }

    [Fact]
    public void LateralLeftMount_LooksLeft_NoseOnImageRight_PositiveAzTurnsForward_PositiveElUp()
    {
        var left = new MountDef { YawDeg = -90, PitchDeg = 0 };
        var r = GimbalMath.BodyFromCamera(left, 0, 0);
        AssertVec(new Vec3d(0, -1, 0), r.Column0);  // boresight = out the left side
        AssertVec(new Vec3d(1, 0, 0), r.Column1);   // image right = nose
        AssertVec(new Vec3d(0, 0, 1), r.Column2);   // image down = down (horizon level)
        Assert.True(GimbalMath.BodyFromCamera(left, 30, 0).Column0.X > 0.49);
        Assert.True(GimbalMath.BodyFromCamera(left, 0, 20).Column0.Z < -0.3);
        var (az, el) = GimbalMath.SolveAzEl(left, new Vec3d(0, 0, 1));
        Assert.Equal(-90, el, 6); // nadir is below the elevation limit; callers clamp
    }

    [Fact]
    public void LateralRightMount_PositiveAzTurnsAft()
    {
        var right = new MountDef { YawDeg = 90, PitchDeg = 0 };
        AssertVec(new Vec3d(0, 1, 0), GimbalMath.BodyFromCamera(right, 0, 0).Column0);
        Assert.True(GimbalMath.BodyFromCamera(right, 30, 0).Column0.X < -0.49);
        var (az, _) = GimbalMath.SolveAzEl(right, new Vec3d(1, 0, 0));
        Assert.Equal(-90, az, 6); // forward for a right-side camera is negative azimuth
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 10)]
    [InlineData(-46.4, -23.2)]
    [InlineData(12.5, 22)]
    public void SolveAzEl_Inverts_BodyFromCamera(double azDeg, double elDeg)
    {
        var mount = new MountDef { PitchDeg = -90, YawDeg = 3 };
        var los = GimbalMath.BodyFromCamera(mount, azDeg, elDeg).Column0;
        var (az, el) = GimbalMath.SolveAzEl(mount, los);
        Assert.Equal(azDeg, az, 6);
        Assert.Equal(elDeg, el, 6);
    }

    [Fact]
    public void Godot_Basis_From_LevelNorth_Is_Identity()
    {
        // Level body heading north: Godot forward (-Z) must be north, up (+Y) up, right (+X) east.
        var (x, y, z) = FrameConventions.GodotBasisFromNedFrd(Mat3d.Identity);
        AssertVec(new Vec3d(1, 0, 0), x);
        AssertVec(new Vec3d(0, 1, 0), y);
        AssertVec(new Vec3d(0, 0, 1), z);
    }

    [Fact]
    public void Pinhole_Projects_Center_And_Edge()
    {
        var k = PinholeIntrinsics.FromVerticalFov(2048, 2048, 20);
        Assert.True(k.TryProject(new Vec3d(100, 0, 0), 0.1, out var u, out var v));
        Assert.Equal(1024, u, 6);
        Assert.Equal(1024, v, 6);
        double edge = 100 * Math.Tan(10 * Math.PI / 180);
        Assert.True(k.TryProject(new Vec3d(100, edge, edge), 0.1, out u, out v));
        Assert.Equal(2048, u, 4);
        Assert.Equal(2048, v, 4);
        Assert.False(k.TryProject(new Vec3d(-5, 0, 0), 0.1, out _, out _));
    }

    [Fact]
    public void Footprint_Nadir_IsCenteredBelowCamera()
    {
        var mount = new MountDef { PitchDeg = -90 };
        var platform = new PlatformPose(new Vec3d(0, 0, -5000), Mat3d.Identity);
        var pose = CameraPose.Compute(platform, mount, 0, 0);
        var k = PinholeIntrinsics.FromVerticalFov(2048, 2048, 10);
        var fp = Footprint.Compute(pose, k, 100_000);
        Assert.Equal(0, fp[4].X, 3);
        Assert.Equal(0, fp[4].Y, 3);
        double half = 5000 * Math.Tan(5 * Math.PI / 180);
        Assert.Equal(-half, fp[0].X, 1); // top-left: west
        Assert.Equal(half, fp[0].Y, 1);  // and north (image top = nose = north)
    }

    private static void AssertVec(Vec3d expected, Vec3d actual, int precision = 9)
    {
        Assert.Equal(expected.X, actual.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
        Assert.Equal(expected.Z, actual.Z, precision);
    }
}
