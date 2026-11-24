namespace WebAirIG.Common.Config;

/// <summary>config/sensors.json — the camera rig on ownship. Shared by host (limits, footprints) and IG (rendering).</summary>
public sealed class SensorsConfig
{
    public List<CameraDef> Cameras { get; set; } = [];
}

public sealed class CameraDef
{
    /// <summary>CIGI view ID. Gimbal articulated part ID = <see cref="GimbalPartBaseId"/> + Id.</summary>
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public MountDef Mount { get; set; } = new();
    public GimbalLimitsDef Limits { get; set; } = new();
    public double SlewRateDegPerSec { get; set; } = 40;
    public int Width { get; set; } = 2048;
    public int Height { get; set; } = 2048;
    /// <summary>Vertical (= horizontal for square sensors) field of view.</summary>
    public double FovDeg { get; set; } = 15;
    public double FovMinDeg { get; set; } = 1;
    public double FovMaxDeg { get; set; } = 45;
    public double NearM { get; set; } = 0.5;
    public double FarM { get; set; } = 90_000;

    public const int GimbalPartBaseId = 100;
}

/// <summary>Camera mount on the ownship body. Offsets in body FRD meters; rotation = mount frame orientation.</summary>
public sealed class MountDef
{
    public double XFwdM { get; set; }
    public double YRightM { get; set; }
    public double ZDownM { get; set; }
    public double YawDeg { get; set; }
    /// <summary>-90 = nadir (boresight straight down at zero gimbal angles).</summary>
    public double PitchDeg { get; set; } = -90;
    public double RollDeg { get; set; }
}

public sealed class GimbalLimitsDef
{
    public double AzMinDeg { get; set; } = -46.4;
    public double AzMaxDeg { get; set; } = 46.4;
    public double ElMinDeg { get; set; } = -23.2;
    public double ElMaxDeg { get; set; } = 23.2;
}
