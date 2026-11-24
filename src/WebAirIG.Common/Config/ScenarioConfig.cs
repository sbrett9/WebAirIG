namespace WebAirIG.Common.Config;

/// <summary>config/scenario.json — ownship and target behaviour. Edited from the Blazor host UI.</summary>
public sealed class ScenarioConfig
{
    public OwnshipDef Ownship { get; set; } = new();
    public List<TargetDef> Targets { get; set; } = [];
}

public sealed class OwnshipDef
{
    public int TypeId { get; set; } = 1;
    public string Name { get; set; } = "OWNSHIP";
    public PatternDef Pattern { get; set; } = new()
    {
        Kind = PatternKind.Figure8,
        AltitudeM = 5000,
        SpeedMps = 140,
        SizeAM = 12000,
        HeadingDeg = 0,
    };
}

public sealed class TargetDef
{
    /// <summary>CIGI entity ID (ownship is always 0).</summary>
    public int EntityId { get; set; }
    public string Name { get; set; } = "";
    public int TypeId { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>DIS force ID: 0 other, 1 friendly, 2 opposing, 3 neutral.</summary>
    public byte Force { get; set; } = 3;
    public PatternDef Pattern { get; set; } = new();
}

public enum PatternKind
{
    Hold,
    Circle,
    Ellipse,
    Racetrack,
    Figure8,
    Box,
    Lawnmower,
    OutAndBack,
    Weave,
    Formation,
}

/// <summary>
/// A perpetual, cyclic flight pattern. Positions are in the world LTP (meters east/north of the origin).
/// Interpretation of SizeA/SizeB per kind:
///   Circle: SizeA = radius.  Ellipse: SizeA/SizeB = semi-axes.  Racetrack: SizeA = straight length, SizeB = turn radius.
///   Figure8: SizeA = half-width of the lemniscate.  Box: SizeA = side, SizeB = corner radius.
///   Lawnmower: SizeA = lane length, SizeB = lane spacing, Lanes = lane count.  OutAndBack: SizeA = leg length.
///   Weave: racetrack with lateral sinusoid, SizeA = length, SizeB = weave amplitude.
///   Formation: holds an offset from ownship (FormationFwdM/RightM/DownM in ownship body axes).
/// </summary>
public sealed class PatternDef
{
    public PatternKind Kind { get; set; } = PatternKind.Circle;
    public double CenterEastM { get; set; }
    public double CenterNorthM { get; set; }
    public double AltitudeM { get; set; } = 1500;
    public double SpeedMps { get; set; } = 60;
    public double SizeAM { get; set; } = 3000;
    public double SizeBM { get; set; } = 1000;
    /// <summary>Orientation of the pattern's major axis, degrees clockwise from north.</summary>
    public double HeadingDeg { get; set; }
    public bool Clockwise { get; set; } = true;
    /// <summary>Start position along the loop, 0..1.</summary>
    public double Phase { get; set; }
    public int Lanes { get; set; } = 4;
    public double AltitudeAmplitudeM { get; set; }
    public double AltitudePeriodS { get; set; } = 120;
    /// <summary>Scales the coordinated-turn bank angle (0 = wings level, e.g. for multirotors).</summary>
    public double BankFactor { get; set; } = 1.0;
    public double MaxBankDeg { get; set; } = 45;
    public double FormationFwdM { get; set; } = -300;
    public double FormationRightM { get; set; } = 200;
    public double FormationDownM { get; set; } = 500;
    /// <summary>
    /// Optional slow drift around the formation offset (amplitudes in ownship body axes, 0 = rigid formation).
    /// Each axis is a sinusoid with a different multiple of <see cref="FormationWanderPeriodS"/> so the target
    /// traces a Lissajous path across the sensor image instead of sitting still in it.
    /// </summary>
    public double FormationWanderFwdM { get; set; }
    public double FormationWanderRightM { get; set; }
    public double FormationWanderDownM { get; set; }
    public double FormationWanderPeriodS { get; set; } = 90;
}
