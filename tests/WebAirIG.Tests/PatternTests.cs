using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;
using WebAirIG.Common.Simulation;

namespace WebAirIG.Tests;

public class PatternTests
{
    public static TheoryData<PatternKind> LoopKinds => new()
    {
        PatternKind.Circle, PatternKind.Ellipse, PatternKind.Racetrack, PatternKind.Figure8,
        PatternKind.Box, PatternKind.Lawnmower, PatternKind.OutAndBack, PatternKind.Weave,
    };

    [Theory]
    [MemberData(nameof(LoopKinds))]
    public void Loops_Are_Closed_Continuous_And_ConstantSpeed(PatternKind kind)
    {
        foreach (bool cw in new[] { true, false })
        {
            var def = new PatternDef { Kind = kind, SpeedMps = 120, SizeAM = 8000, SizeBM = 1500, AltitudeM = 3000, Clockwise = cw, Lanes = 4 };
            var motion = new PatternMotion(def);
            Assert.True(motion.LoopLengthM > 1000);

            double period = motion.LoopPeriodS;
            var start = motion.Evaluate(0);
            var end = motion.Evaluate(period);
            Assert.True((start.PositionEnu - end.PositionEnu).Length < 1.0, $"{kind} cw={cw} not closed");

            const double dt = 0.05;
            for (double t = 0; t < period; t += period / 400)
            {
                var a = motion.Evaluate(t);
                var b = motion.Evaluate(t + dt);
                double speed = (b.PositionEnu - a.PositionEnu).Length / dt;
                Assert.InRange(speed, 120 * 0.97, 120 * 1.03);
                Assert.InRange(a.RollDeg, -45.01, 45.01);
                // Heading must match the direction of travel.
                var d = b.PositionEnu - a.PositionEnu;
                double travel = LocalTangentPlane.NormalizeDeg360(Math.Atan2(d.X, d.Y) * 180 / Math.PI);
                double err = Math.Abs(LocalTangentPlane.NormalizeDeg180(travel - a.HeadingDeg));
                Assert.True(err < 3, $"{kind} cw={cw} heading error {err:F2} at t={t:F1}");
            }
        }
    }

    [Fact]
    public void Clockwise_Circle_Banks_Right()
    {
        var m = new PatternMotion(new PatternDef { Kind = PatternKind.Circle, SpeedMps = 100, SizeAM = 2000, Clockwise = true });
        Assert.True(m.Evaluate(10).RollDeg > 20);
        var ccw = new PatternMotion(new PatternDef { Kind = PatternKind.Circle, SpeedMps = 100, SizeAM = 2000, Clockwise = false });
        Assert.True(ccw.Evaluate(10).RollDeg < -20);
    }

    [Fact]
    public void Figure8_Rolls_Through_Zero_At_Crossover()
    {
        var m = new PatternMotion(new PatternDef { Kind = PatternKind.Figure8, SpeedMps = 140, SizeAM = 12000 });
        double period = m.LoopPeriodS;
        bool sawPositive = false, sawNegative = false;
        for (double t = 0; t < period; t += 2)
        {
            var s = m.Evaluate(t);
            if (s.RollDeg > 10) sawPositive = true;
            if (s.RollDeg < -10) sawNegative = true;
        }
        Assert.True(sawPositive && sawNegative);
    }

    [Fact]
    public void Formation_Follows_Ownship_Offset()
    {
        var own = new KinematicState(new Vec3d(1000, 2000, 5000), 90, 0, 0, new Vec3d(140, 0, 0));
        var m = new PatternMotion(new PatternDef { Kind = PatternKind.Formation, FormationFwdM = -300, FormationRightM = 200, FormationDownM = 500 });
        var s = m.Evaluate(0, own);
        // Heading east: forward = +E, right = -N (south), down = -U.
        Assert.Equal(700, s.PositionEnu.X, 6);
        Assert.Equal(1800, s.PositionEnu.Y, 6);
        Assert.Equal(4500, s.PositionEnu.Z, 6);
    }

    [Fact]
    public void Formation_Wander_Stays_Within_Amplitude_And_Velocity_Matches_Position_Rate()
    {
        var own = new KinematicState(new Vec3d(0, 0, 5000), 0, 0, 0, new Vec3d(0, 140, 0));
        var def = new PatternDef
        {
            Kind = PatternKind.Formation, FormationFwdM = 0, FormationRightM = 1000, FormationDownM = 100,
            FormationWanderFwdM = 200, FormationWanderRightM = 300, FormationWanderDownM = 50, FormationWanderPeriodS = 60,
        };
        var m = new PatternMotion(def);
        double minE = double.MaxValue, maxE = double.MinValue;
        for (double t = 0; t < 600; t += 0.5)
        {
            // Hold ownship fixed so the offset alone is visible: heading north, right = +E, fwd = +N, down = -U.
            var s = m.Evaluate(t, own);
            Assert.InRange(s.PositionEnu.Y, -200 - 1e-6, 200 + 1e-6);
            Assert.InRange(s.PositionEnu.X, 700 - 1e-6, 1300 + 1e-6);
            Assert.InRange(s.PositionEnu.Z, 4850 - 1e-6, 4950 + 1e-6);
            minE = Math.Min(minE, s.PositionEnu.X);
            maxE = Math.Max(maxE, s.PositionEnu.X);

            // Finite-difference check of the reported velocity (ownship velocity + offset rate).
            const double h = 1e-3;
            var a = m.Evaluate(t - h, own).PositionEnu;
            var b = m.Evaluate(t + h, own).PositionEnu;
            var fd = (b - a) * (1 / (2 * h));
            var expected = own.VelocityEnu + fd;
            Assert.Equal(expected.X, s.VelocityEnu.X, 3);
            Assert.Equal(expected.Y, s.VelocityEnu.Y, 3);
            Assert.Equal(expected.Z, s.VelocityEnu.Z, 3);
        }
        Assert.True(maxE - minE > 500, "wander should sweep most of its right-axis range");
    }
}
