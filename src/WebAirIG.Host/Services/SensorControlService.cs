using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;
using WebAirIG.Common.Sensors;
using WebAirIG.Common.Simulation;

namespace WebAirIG.Host.Services;

/// <summary>
/// Center = gimbal (0,0) (the mount boresight). Nadir = as close to straight down as the limits allow.
/// TrackEntity / GeoPoint solve the gimbal angles every frame. Scan sweeps azimuth sinusoidally at the commanded elevation.
/// </summary>
public enum CameraMode { Center, Manual, Nadir, TrackEntity, GeoPoint, Scan }

public enum IrPolarity { WhiteHot, BlackHot }

/// <summary>Operator command state for one camera. Mutated only through <see cref="ISensorControlService.Update"/>.</summary>
public sealed class CameraCommand
{
    public CameraMode Mode { get; set; } = CameraMode.Center;
    public double CmdAzDeg { get; set; }
    public double CmdElDeg { get; set; }
    public int TrackEntityId { get; set; } = -1;
    public Vec3d GeoPointEnu { get; set; }
    public double ScanAmplitudeDeg { get; set; } = 40;
    public double ScanPeriodS { get; set; } = 20;
    public double FovDeg { get; set; } = 15;
    public bool SensorOn { get; set; } = true;
    public IrPolarity Polarity { get; set; } = IrPolarity.WhiteHot;
    public double Gain { get; set; } = 1.0;
    public double Level { get; set; } = 0.5;
    public double Noise { get; set; } = 0.01;

    public CameraCommand Clone() => (CameraCommand)MemberwiseClone();
}

/// <param name="DesiredAzDeg">Commanded gimbal azimuth after clamping to the limits.</param>
/// <param name="RequestedAzDeg">What the pointing mode asked for before clamping (differs when <paramref name="LimitReached"/>).</param>
/// <param name="TargetRangeM">Slant range to the tracked entity / geo point, NaN in other modes.</param>
public sealed record CameraSnapshot(
    int Id, string Name, CameraCommand Command,
    double AzDeg, double ElDeg, double DesiredAzDeg, double DesiredElDeg,
    bool OnTarget, bool LimitReached, CameraPose Pose, PinholeIntrinsics Intrinsics, Vec3d[] FootprintEnu,
    double RequestedAzDeg = 0, double RequestedElDeg = 0, double TargetRangeM = double.NaN);

public interface ISensorControlService
{
    IReadOnlyList<CameraDef> Cameras { get; }
    CameraCommand GetCommand(int cameraId);
    void Update(int cameraId, Action<CameraCommand> mutate);
    /// <summary>Manual pointing toward a body-frame direction (x fwd, y right, z down), clamped to the gimbal limits.</summary>
    void PointBodyDirection(int cameraId, Vec3d directionBody);
    /// <summary>Increments whenever any command changes (used to push CIGI view/sensor packets immediately).</summary>
    int Revision { get; }
    IReadOnlyList<CameraSnapshot> Step(double dt, double simTime, KinematicState ownship, IReadOnlyDictionary<int, KinematicState> entities);
}

/// <summary>Gimbal pointing logic (manual, nadir, entity tracking, geo-pointing, scan) with slew-rate limits.</summary>
public sealed class SensorControlService : ISensorControlService
{
    private readonly object _gate = new();
    private readonly Dictionary<int, CameraCommand> _commands = new();
    private readonly Dictionary<int, (double Az, double El)> _actual = new();
    private int _revision;

    public IReadOnlyList<CameraDef> Cameras { get; }
    public int Revision => Volatile.Read(ref _revision);

    public SensorControlService(ISharedConfigService config)
    {
        Cameras = config.Sensors.Cameras;
        foreach (var c in Cameras)
        {
            _commands[c.Id] = new CameraCommand { FovDeg = c.FovDeg };
            _actual[c.Id] = (0, 0);
        }
    }

    public CameraCommand GetCommand(int cameraId)
    {
        lock (_gate) return _commands.TryGetValue(cameraId, out var c) ? c.Clone() : new CameraCommand();
    }

    public void Update(int cameraId, Action<CameraCommand> mutate)
    {
        lock (_gate)
        {
            if (!_commands.TryGetValue(cameraId, out var c)) return;
            mutate(c);
            var def = Cameras.First(x => x.Id == cameraId);
            c.FovDeg = Math.Clamp(c.FovDeg, def.FovMinDeg, def.FovMaxDeg);
            (c.CmdAzDeg, c.CmdElDeg) = GimbalMath.Clamp(def.Limits, c.CmdAzDeg, c.CmdElDeg);
            c.Gain = Math.Clamp(c.Gain, 0.05, 20);
            c.Level = Math.Clamp(c.Level, 0, 1);
            c.Noise = Math.Clamp(c.Noise, 0, 0.5);
        }
        Interlocked.Increment(ref _revision);
    }

    public void PointBodyDirection(int cameraId, Vec3d directionBody)
    {
        var def = Cameras.FirstOrDefault(c => c.Id == cameraId);
        if (def is null) return;
        var (az, el) = GimbalMath.Clamp(def.Limits, GimbalMath.SolveAzEl(def.Mount, directionBody));
        Update(cameraId, c => { c.Mode = CameraMode.Manual; c.CmdAzDeg = az; c.CmdElDeg = el; });
    }

    public IReadOnlyList<CameraSnapshot> Step(double dt, double simTime, KinematicState ownship, IReadOnlyDictionary<int, KinematicState> entities)
    {
        var platform = new PlatformPose(LocalTangentPlane.EnuToNed(ownship.PositionEnu), ownship.NedFromBody);
        var result = new List<CameraSnapshot>(Cameras.Count);
        lock (_gate)
        {
            foreach (var def in Cameras)
            {
                var cmd = _commands[def.Id];
                var (az, el) = _actual[def.Id];
                var camAtZero = CameraPose.Compute(platform, def.Mount, 0, 0);
                Vec3d? targetNed = cmd.Mode switch
                {
                    CameraMode.TrackEntity when entities.TryGetValue(cmd.TrackEntityId, out var tgt) => LocalTangentPlane.EnuToNed(tgt.PositionEnu),
                    CameraMode.GeoPoint => LocalTangentPlane.EnuToNed(cmd.GeoPointEnu),
                    _ => null,
                };

                (double Az, double El) desired = cmd.Mode switch
                {
                    CameraMode.Manual => (cmd.CmdAzDeg, cmd.CmdElDeg),
                    CameraMode.Center => (0, 0),
                    CameraMode.Nadir => GimbalMath.SolveAzEl(def.Mount, new Vec3d(0, 0, 1)),
                    CameraMode.Scan => (cmd.ScanAmplitudeDeg * Math.Sin(2 * Math.PI * simTime / Math.Max(1, cmd.ScanPeriodS)), cmd.CmdElDeg),
                    CameraMode.TrackEntity or CameraMode.GeoPoint when targetNed is { } t => PointAt(def, platform, camAtZero.PositionNed, t),
                    _ => (az, el),
                };

                var clamped = GimbalMath.Clamp(def.Limits, desired.Az, desired.El);
                bool limit = Math.Abs(clamped.AzDeg - desired.Az) > 1e-6 || Math.Abs(clamped.ElDeg - desired.El) > 1e-6;
                double maxStep = def.SlewRateDegPerSec * Math.Max(0, dt);
                az += Math.Clamp(clamped.AzDeg - az, -maxStep, maxStep);
                el += Math.Clamp(clamped.ElDeg - el, -maxStep, maxStep);
                _actual[def.Id] = (az, el);
                bool onTarget = Math.Abs(clamped.AzDeg - az) < 0.05 && Math.Abs(clamped.ElDeg - el) < 0.05;

                var pose = CameraPose.Compute(platform, def.Mount, az, el);
                var k = PinholeIntrinsics.FromVerticalFov(def.Width, def.Height, cmd.FovDeg);
                var footprint = Footprint.Compute(pose, k, 60_000);
                double range = targetNed is { } tn ? (tn - camAtZero.PositionNed).Length : double.NaN;
                result.Add(new CameraSnapshot(def.Id, def.Name, cmd.Clone(), az, el, clamped.AzDeg, clamped.ElDeg, onTarget, limit, pose, k, footprint,
                    desired.Az, desired.El, range));
            }
        }
        return result;
    }

    private static (double Az, double El) PointAt(CameraDef def, PlatformPose platform, Vec3d camNed, Vec3d targetNed)
    {
        var losBody = platform.NedFromBody.Transpose() * (targetNed - camNed);
        return GimbalMath.SolveAzEl(def.Mount, losBody);
    }
}
