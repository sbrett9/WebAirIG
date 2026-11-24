using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;
using WebAirIG.Common.Simulation;

namespace WebAirIG.Host.Services;

public sealed record EntitySnapshot(
    int EntityId, string Name, int TypeId, string TypeName, byte Force, bool IsOwnship,
    KinematicState State, GeoPoint Geo, double YawDeg, double PitchDeg, double RollDeg, Vec3d VelNedLocal);

public sealed record SimSnapshot(
    long StepIndex, double SimTime, DateTime Utc,
    IReadOnlyList<EntitySnapshot> Entities, IReadOnlyList<CameraSnapshot> Cameras)
{
    public static readonly SimSnapshot Empty = new(0, 0, DateTime.UtcNow, [], []);
    public EntitySnapshot? Ownship => Entities.FirstOrDefault(e => e.IsOwnship);
}

public interface ISimulationEngine
{
    SimSnapshot Latest { get; }
    bool Paused { get; set; }
    double TimeScale { get; set; }
    /// <summary>Increments whenever the entity set or types change.</summary>
    int ScenarioRevision { get; }
    void ResetTime();
    /// <summary>Advances simulation time by <paramref name="dt"/> seconds (scaled, unless paused). Single caller: the CIGI loop.</summary>
    SimSnapshot Step(double dt);
    event Action<SimSnapshot>? Stepped;
}

/// <summary>
/// Host-side scenario simulation. Ownship flies its pattern (default figure-8); each target flies its own cyclic
/// pattern. Positions are computed in the world LTP and converted to geodetic for CIGI and DIS.
/// </summary>
public sealed class SimulationEngine : ISimulationEngine
{
    private sealed record Plan(EntityPlan Ownship, IReadOnlyList<EntityPlan> Targets);
    private sealed record EntityPlan(int EntityId, string Name, EntityTypeDef? Type, int TypeId, byte Force, PatternMotion Motion);

    private readonly ISharedConfigService _config;
    private readonly ISensorControlService _sensors;
    private volatile Plan _plan;
    private volatile SimSnapshot _latest = SimSnapshot.Empty;
    private double _simTime;
    private long _step;
    private int _scenarioRevision;
    private volatile bool _resetRequested;

    public event Action<SimSnapshot>? Stepped;
    public SimSnapshot Latest => _latest;
    public bool Paused { get; set; }
    public double TimeScale { get; set; } = 1.0;
    public int ScenarioRevision => Volatile.Read(ref _scenarioRevision);

    public SimulationEngine(ISharedConfigService config, IScenarioService scenarios, ISensorControlService sensors)
    {
        _config = config;
        _sensors = sensors;
        _plan = BuildPlan(scenarios.GetCopy());
        scenarios.Changed += s =>
        {
            _plan = BuildPlan(s);
            Interlocked.Increment(ref _scenarioRevision);
        };
    }

    public void ResetTime() => _resetRequested = true;

    private Plan BuildPlan(ScenarioConfig s)
    {
        EntityPlan Make(int id, string name, int typeId, byte force, PatternDef p) =>
            new(id, name, _config.Catalog.Find(typeId), typeId, force, new PatternMotion(p));

        var own = Make(0, s.Ownship.Name, s.Ownship.TypeId, 1, s.Ownship.Pattern);
        var targets = s.Targets
            .Where(t => t.Enabled && t.EntityId > 0)
            .GroupBy(t => t.EntityId).Select(g => g.First())
            .Select(t => Make(t.EntityId, t.Name, t.TypeId, t.Force, t.Pattern))
            .ToList();
        return new Plan(own, targets);
    }

    public SimSnapshot Step(double dt)
    {
        if (_resetRequested) { _simTime = 0; _resetRequested = false; }
        double scaled = Paused ? 0 : Math.Max(0, dt) * TimeScale;
        _simTime += scaled;
        var plan = _plan;
        var ltp = _config.Ltp;

        var ownState = plan.Ownship.Motion.Evaluate(_simTime);
        var entities = new List<EntitySnapshot>(plan.Targets.Count + 1) { Snapshot(ltp, plan.Ownship, ownState, true) };
        var states = new Dictionary<int, KinematicState> { [0] = ownState };
        foreach (var t in plan.Targets)
        {
            var st = t.Motion.Evaluate(_simTime, ownState);
            states[t.EntityId] = st;
            entities.Add(Snapshot(ltp, t, st, false));
        }

        // Gimbals slew in real (unscaled) time so operators can still point cameras while the scenario is paused.
        var cameras = _sensors.Step(Math.Max(0, dt), _simTime, ownState, states);
        var snap = new SimSnapshot(++_step, _simTime, DateTime.UtcNow, entities, cameras);
        _latest = snap;
        Stepped?.Invoke(snap);
        return snap;
    }

    private static EntitySnapshot Snapshot(LocalTangentPlane ltp, EntityPlan p, KinematicState s, bool ownship)
    {
        var geo = ltp.EnuToGeodetic(s.PositionEnu);
        var (yaw, pitch, roll) = ltp.LocalYprFromLtp(geo, s.NedFromBody);
        var velLocal = ltp.LtpNedToLocalNed(geo, LocalTangentPlane.EnuToNed(s.VelocityEnu));
        return new EntitySnapshot(p.EntityId, p.Name, p.TypeId, p.Type?.Name ?? $"type{p.TypeId}", p.Force, ownship,
            s, geo, yaw, pitch, roll, velLocal);
    }
}
