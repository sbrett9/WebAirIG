using System.Collections.Generic;
using WebAirIG.Cigi;
using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;

namespace WebAirIG.IG.Cigi;

/// <summary>State of one CIGI entity as last commanded by the host (double precision, world LTP).</summary>
public sealed class IgEntityState
{
    public int Id;
    public int TypeId = -1;
    public bool Active;
    public bool HasPosition;
    public GeoPoint Geo;
    public double YawDeg, PitchDeg, RollDeg;
    public Vec3d PositionNed;
    public Mat3d NedFromBody = Mat3d.Identity;
    public Vec3d VelocityLtpNed;
    public double PositionTime = double.NaN;
    /// <summary>Articulated parts: part ID to (yaw/az, pitch/el) degrees.</summary>
    public readonly Dictionary<int, (double Yaw, double Pitch)> Parts = new();
}

/// <summary>State of one CIGI view (= one sensor camera).</summary>
public sealed class IgViewState
{
    public int ViewId;
    public int EntityId;
    public MountDef Mount = new();
    public double VfovDeg = 15;
    public double NearM = 0.5;
    public double FarM = 90_000;
    public bool SensorOn = true;
    public bool BlackHot;
    public double Gain = 1.0;
    public double Level = 0.5;
    public double Noise = 0.01;
}

/// <summary>Everything the host has told the IG. Mutated only on the Godot main thread.</summary>
public sealed class IgState
{
    public readonly Dictionary<int, IgEntityState> Entities = new();
    public readonly Dictionary<int, IgViewState> Views = new();
    public uint LastHostFrame;
    public double HostSimTime;
    public bool HostTimeValid;
    public IgMode HostMode = IgMode.ResetStandby;
    /// <summary>Increments when entities are created/removed or change type.</summary>
    public int EntityRevision;

    public IgEntityState GetOrCreateEntity(int id)
    {
        if (!Entities.TryGetValue(id, out var e))
        {
            e = new IgEntityState { Id = id };
            Entities[id] = e;
        }
        return e;
    }
}
