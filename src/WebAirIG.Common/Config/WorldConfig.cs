using WebAirIG.Common.Geo;

namespace WebAirIG.Common.Config;

/// <summary>config/world.json — shared by host and IG so both use the same geodetic origin.</summary>
public sealed class WorldConfig
{
    public OriginDef Origin { get; set; } = new();
    public TerrainDef Terrain { get; set; } = new();
    public AtmosphereDef Atmosphere { get; set; } = new();

    public LocalTangentPlane CreateTangentPlane() => new(new GeoPoint(Origin.LatDeg, Origin.LonDeg, Origin.AltM));
}

public sealed class OriginDef
{
    /// <summary>Default: an arbitrary inland desert site; change to your area of interest.</summary>
    public double LatDeg { get; set; } = 35.0;
    public double LonDeg { get; set; } = -117.0;
    /// <summary>Ellipsoid height of the origin. The flat ground plane is rendered at this altitude.</summary>
    public double AltM { get; set; } = 700.0;
}

public sealed class TerrainDef
{
    /// <summary>"procedural" (built in). Reserved for future providers such as "cesium".</summary>
    public string Provider { get; set; } = "procedural";
    public double SizeM { get; set; } = 240_000;
    public int Seed { get; set; } = 1337;
    /// <summary>Base apparent temperature of the ground in normalized units (0 = cold, 1 = hot).</summary>
    public double BaseTemp { get; set; } = 0.42;
    public double FieldContrast { get; set; } = 0.12;
    public double WaterFraction { get; set; } = 0.08;
    public double WaterTemp { get; set; } = 0.25;
    public double RoadTemp { get; set; } = 0.58;
    public double UrbanFraction { get; set; } = 0.05;
}

public sealed class AtmosphereDef
{
    /// <summary>MWIR extinction length scale: transmission = exp(-range / VisibilityM).</summary>
    public double VisibilityM { get; set; } = 45_000;
    /// <summary>Apparent temperature of the atmospheric path radiance.</summary>
    public double PathTemp { get; set; } = 0.30;
    public double SkyZenithTemp { get; set; } = 0.06;
    public double SkyHorizonTemp { get; set; } = 0.32;
    public double SunAzimuthDeg { get; set; } = 135;
    public double SunElevationDeg { get; set; } = 45;
}
