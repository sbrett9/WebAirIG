using System;
using System.Linq;
using WebAirIG.Common.Geo;
using WebAirIG.Common.Sensors;
using WebAirIG.Common.Truth;
using WebAirIG.IG.Cigi;
using WebAirIG.IG.Entities;

namespace WebAirIG.IG.Sensors;

/// <summary>Builds the per-camera, per-frame truth sidecar from the exact state that was rendered.</summary>
public sealed class TruthBuilder
{
    private readonly LocalTangentPlane _ltp;
    private readonly uint _sessionId;

    public TruthBuilder(LocalTangentPlane ltp, uint sessionId)
    {
        _ltp = ltp;
        _sessionId = sessionId;
    }

    public FrameTruth Build(SensorChannel ch, IgState state, EntityManager entities, ulong frame, DateTime utc)
    {
        var truth = new FrameTruth
        {
            SessionId = _sessionId,
            Frame = frame,
            SimTime = state.HostSimTime,
            UtcTime = utc,
        };
        if (!ch.HasPose) return truth;

        var k = ch.Intrinsics;
        var pose = ch.Pose;
        var camGeo = _ltp.EnuToGeodetic(LocalTangentPlane.NedToEnu(pose.PositionNed));
        var localFromCam = _ltp.LocalNedFromLtpNed(camGeo) * pose.NedFromCam;
        var (cy, cp, cr) = localFromCam.ToYawPitchRoll();
        var footprint = Footprint.Compute(pose, k, 100_000);

        truth.Camera = new CameraTruth
        {
            Id = ch.Def.Id,
            Name = ch.Def.Name,
            Width = k.Width,
            Height = k.Height,
            HfovDeg = Math.Round(k.HorizontalFovDeg, 6),
            VfovDeg = Math.Round(k.VerticalFovDeg, 6),
            Fx = k.Fx, Fy = k.Fy, Cx = k.Cx, Cy = k.Cy,
            GimbalAzDeg = ch.AzDeg,
            GimbalElDeg = ch.ElDeg,
            Lat = camGeo.LatDeg, Lon = camGeo.LonDeg, Alt = camGeo.AltM,
            Enu = LocalTangentPlane.NedToEnu(pose.PositionNed).ToArray(),
            RNedFromCam = localFromCam.ToRowMajorArray(),
            YawDeg = LocalTangentPlane.NormalizeDeg360(cy * Wgs84.Rad2Deg),
            PitchDeg = cp * Wgs84.Rad2Deg,
            RollDeg = cr * Wgs84.Rad2Deg,
            BoresightGround = pose.Boresight.Z > 1e-6 ? LatLon(footprint[4]) : null,
            Footprint = footprint.Take(4).Select(LatLon).ToArray(),
        };

        if (state.Entities.TryGetValue(0, out var own) && own.HasPosition)
            truth.Ownship = Fill(new EntityTruth(), own, entities);

        foreach (var e in state.Entities.Values)
        {
            if (!e.Active || !e.HasPosition) continue;
            var template = entities.TemplateFor(e.Id);
            if (template is null) continue;
            var proj = TruthProjector.Project(pose, k, ch.View.NearM, new PlatformPose(e.PositionNed, e.NedFromBody), template.SamplePointsFrd);
            if (!proj.InView) continue;

            var obj = Fill(new ObjectTruth(), e, entities);
            obj.ClassId = template.Type.ClassId;
            obj.ClassName = template.Type.ClassName;
            obj.IsOwnship = e.Id == ch.View.EntityId;
            obj.Bbox = [R(proj.X1), R(proj.Y1), R(proj.X2), R(proj.Y2)];
            obj.BboxUnclipped = [R(proj.Ux1), R(proj.Uy1), R(proj.Ux2), R(proj.Uy2)];
            obj.Truncated = proj.Truncated;
            obj.VisibleFraction = Math.Round(proj.VisibleFraction, 4);
            obj.CenterPx = [R(proj.CenterU), R(proj.CenterV)];
            obj.RangeM = Math.Round(proj.RangeM, 2);
            obj.OffBoresightXDeg = Math.Round(proj.OffBoresightXDeg, 5);
            obj.OffBoresightYDeg = Math.Round(proj.OffBoresightYDeg, 5);
            truth.Objects.Add(obj);
        }
        return truth;
    }

    private T Fill<T>(T t, IgEntityState e, EntityManager entities) where T : EntityTruth
    {
        t.EntityId = e.Id;
        t.TypeId = e.TypeId;
        t.TypeName = entities.TemplateFor(e.Id)?.Type.Name ?? "";
        t.Lat = e.Geo.LatDeg;
        t.Lon = e.Geo.LonDeg;
        t.Alt = Math.Round(e.Geo.AltM, 3);
        t.YawDeg = Math.Round(e.YawDeg, 4);
        t.PitchDeg = Math.Round(e.PitchDeg, 4);
        t.RollDeg = Math.Round(e.RollDeg, 4);
        var v = _ltp.LtpNedToLocalNed(e.Geo, e.VelocityLtpNed);
        t.VelNed = [Math.Round(v.X, 3), Math.Round(v.Y, 3), Math.Round(v.Z, 3)];
        return t;
    }

    private double[] LatLon(Vec3d enu)
    {
        var g = _ltp.EnuToGeodetic(enu);
        return [Math.Round(g.LatDeg, 8), Math.Round(g.LonDeg, 8)];
    }

    private static double R(double v) => Math.Round(v, 2);
}
