using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;

namespace WebAirIG.Common.Simulation;

/// <summary>Kinematic state in the world LTP. Heading is clockwise from north; angles in degrees.</summary>
public readonly record struct KinematicState(Vec3d PositionEnu, double HeadingDeg, double PitchDeg, double RollDeg, Vec3d VelocityEnu)
{
    /// <summary>R_ltpNed_from_body for this state (attitude interpreted in the LTP frame).</summary>
    public Mat3d NedFromBody => Mat3d.FromYawPitchRoll(HeadingDeg * Wgs84.Deg2Rad, PitchDeg * Wgs84.Deg2Rad, RollDeg * Wgs84.Deg2Rad);
}

/// <summary>Evaluates a <see cref="PatternDef"/> at a simulation time. Construct once per pattern change.</summary>
public sealed class PatternMotion
{
    private const double G = 9.80665;
    private readonly PatternDef _def;
    private readonly SampledLoop? _loop;

    public PatternDef Definition => _def;
    public double LoopLengthM => _loop?.Length ?? 0;
    public double LoopPeriodS => _loop is null || _def.SpeedMps <= 0 ? 0 : _loop.Length / _def.SpeedMps;

    public PatternMotion(PatternDef def)
    {
        _def = def;
        _loop = def.Kind is PatternKind.Hold or PatternKind.Formation ? null : BuildLoop(def);
    }

    /// <summary>Minimum turn radius for a coordinated turn at <paramref name="speed"/> and 30 deg bank.</summary>
    public static double MinTurnRadius(double speed) => Math.Max(20, speed * speed / (G * Math.Tan(30 * Math.PI / 180)));

    public static SampledLoop BuildLoop(PatternDef d)
    {
        double a = Math.Max(1, d.SizeAM), b = Math.Max(1, d.SizeBM);
        double mirror = d.Clockwise ? 1 : -1;
        IReadOnlyList<P2> pts;
        switch (d.Kind)
        {
            case PatternKind.Circle:
                pts = new PathBuilder(new P2(-a, 0), 0).Arc(a, 360).Points;
                break;
            case PatternKind.Ellipse:
                return SampledLoop.FromFunction(u => new P2(mirror * b * Math.Sin(2 * Math.PI * u), a * Math.Cos(2 * Math.PI * u)));
            case PatternKind.Figure8:
                // Lemniscate of Bernoulli with its long axis along local north; curvature is 0 at the crossover.
                return SampledLoop.FromFunction(u =>
                {
                    double t = 2 * Math.PI * u;
                    double den = 1 + Math.Sin(t) * Math.Sin(t);
                    return new P2(mirror * a * Math.Sin(t) * Math.Cos(t) / den, a * Math.Cos(t) / den);
                }, 8192);
            case PatternKind.Racetrack:
                pts = new PathBuilder(new P2(-b, -a / 2), 0).Straight(a).Arc(b, 180).Straight(a).Arc(b, 180).Points;
                break;
            case PatternKind.OutAndBack:
            {
                double r = MinTurnRadius(d.SpeedMps);
                pts = new PathBuilder(new P2(-r, -a / 2), 0).Straight(a).Arc(r, 180).Straight(a).Arc(r, 180).Points;
                break;
            }
            case PatternKind.Box:
            {
                double rc = Math.Clamp(b, 1, a / 2 - 1);
                double side = a - 2 * rc;
                var pb = new PathBuilder(new P2(-a / 2, -a / 2 + rc), 0);
                for (int i = 0; i < 4; i++) pb.Straight(side).Arc(rc, 90);
                pts = pb.Points;
                break;
            }
            case PatternKind.Lawnmower:
            {
                int lanes = Math.Max(2, d.Lanes + (d.Lanes % 2)); // even so the return leg is on the start side
                double spacing = b, r = spacing / 2;
                var pb = new PathBuilder(new P2(-(lanes - 1) * spacing / 2, -a / 2), 0);
                for (int i = 0; i < lanes; i++)
                {
                    pb.Straight(a);
                    if (i < lanes - 1) pb.Arc(r, i % 2 == 0 ? 180 : -180);
                }
                // Last lane ends heading south at the south edge: return west along the bottom.
                pb.Arc(r, 90).Straight((lanes - 1) * spacing - 2 * r).Arc(r, 90);
                pts = pb.Points;
                break;
            }
            case PatternKind.Weave:
            {
                double r = MinTurnRadius(d.SpeedMps);
                var basePts = new PathBuilder(new P2(-r, -a / 2), 0).Straight(a).Arc(r, 180).Straight(a).Arc(r, 180).Points;
                var baseLoop = SampledLoop.FromPoints(basePts);
                double wavelength = Math.Max(500, a / 3);
                int waves = Math.Max(1, (int)Math.Round(baseLoop.Length / wavelength));
                int n = 4096;
                var woven = new List<P2>(n);
                for (int i = 0; i < n; i++)
                {
                    double s = baseLoop.Length * i / n;
                    var (p, h, _) = baseLoop.Evaluate(s);
                    var normal = new P2(Math.Cos(h), -Math.Sin(h));
                    woven.Add(p + normal * (b * Math.Sin(2 * Math.PI * waves * s / baseLoop.Length)));
                }
                pts = woven;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(d), d.Kind, "Pattern has no loop");
        }

        if (!d.Clockwise) pts = pts.Select(p => new P2(-p.X, p.Y)).ToList();
        return SampledLoop.FromPoints(pts);
    }

    /// <summary>State at simulation time <paramref name="t"/> (s). Formation patterns need the ownship state.</summary>
    public KinematicState Evaluate(double t, KinematicState? ownship = null)
    {
        var d = _def;
        double altitude = d.AltitudeM;
        double vz = 0;
        if (d.AltitudeAmplitudeM != 0 && d.AltitudePeriodS > 0)
        {
            double w = 2 * Math.PI / d.AltitudePeriodS;
            altitude += d.AltitudeAmplitudeM * Math.Sin(w * t);
            vz = d.AltitudeAmplitudeM * w * Math.Cos(w * t);
        }

        if (d.Kind == PatternKind.Formation && ownship is { } own)
        {
            var (offset, offsetRate) = FormationOffset(d, t);
            var pos = own.PositionEnu + LocalTangentPlane.NedToEnu(own.NedFromBody * offset);
            // Velocity ignores the ownship's rotation rate (small next to its translation for these offsets).
            var velocity = own.VelocityEnu + LocalTangentPlane.NedToEnu(own.NedFromBody * offsetRate);
            return own with { PositionEnu = pos, VelocityEnu = velocity };
        }

        if (_loop is null)
        {
            return new KinematicState(new Vec3d(d.CenterEastM, d.CenterNorthM, altitude), d.HeadingDeg, 0, 0, new Vec3d(0, 0, vz));
        }

        double speed = Math.Max(0, d.SpeedMps);
        var (p, headingLocal, curvature) = _loop.Evaluate(d.Phase * _loop.Length + speed * t);

        double hRad = d.HeadingDeg * Math.PI / 180;
        double east = d.CenterEastM + p.X * Math.Cos(hRad) + p.Y * Math.Sin(hRad);
        double north = d.CenterNorthM - p.X * Math.Sin(hRad) + p.Y * Math.Cos(hRad);
        double heading = headingLocal + hRad;

        double bank = Math.Atan(speed * speed * curvature / G) * d.BankFactor;
        double maxBank = d.MaxBankDeg * Math.PI / 180;
        bank = Math.Clamp(bank, -maxBank, maxBank);
        double pitch = speed > 0.1 ? Math.Atan2(vz, speed) : 0;

        var vel = new Vec3d(speed * Math.Sin(heading), speed * Math.Cos(heading), vz);
        return new KinematicState(
            new Vec3d(east, north, altitude),
            LocalTangentPlane.NormalizeDeg360(heading * 180 / Math.PI),
            pitch * 180 / Math.PI,
            bank * 180 / Math.PI,
            vel);
    }

    /// <summary>Formation offset and its rate (body FRD) at time <paramref name="t"/>, including the optional wander.</summary>
    public static (Vec3d Offset, Vec3d Rate) FormationOffset(PatternDef d, double t)
    {
        var offset = new Vec3d(d.FormationFwdM, d.FormationRightM, d.FormationDownM);
        if (d.FormationWanderPeriodS <= 0 ||
            (d.FormationWanderFwdM == 0 && d.FormationWanderRightM == 0 && d.FormationWanderDownM == 0))
        {
            return (offset, new Vec3d(0, 0, 0));
        }

        // Incommensurate frequency ratios (1 : 0.73 : 1.31) give a Lissajous path that does not repeat quickly.
        double w = 2 * Math.PI / d.FormationWanderPeriodS;
        double wf = w, wr = 0.73 * w, wd = 1.31 * w;
        double pf = wf * t, pr = wr * t + 1.0, pd = wd * t + 2.0;
        var wander = new Vec3d(d.FormationWanderFwdM * Math.Sin(pf), d.FormationWanderRightM * Math.Sin(pr), d.FormationWanderDownM * Math.Sin(pd));
        var rate = new Vec3d(d.FormationWanderFwdM * wf * Math.Cos(pf), d.FormationWanderRightM * wr * Math.Cos(pr), d.FormationWanderDownM * wd * Math.Cos(pd));
        return (offset + wander, rate);
    }
}
