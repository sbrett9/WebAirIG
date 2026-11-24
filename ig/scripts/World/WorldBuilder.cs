using Godot;
using WebAirIG.Common.Config;
using GEnvironment = Godot.Environment;

namespace WebAirIG.IG.World;

/// <summary>Builds the IR environment: linear tonemapping, IR sky, procedural flat terrain and shader globals.</summary>
public static class WorldBuilder
{
    public static void Build(Node3D root, WorldConfig world)
    {
        var atm = world.Atmosphere;
        RenderingServer.GlobalShaderParameterSet("ir_visibility_m", (float)atm.VisibilityM);
        RenderingServer.GlobalShaderParameterSet("ir_path_temp", (float)atm.PathTemp);
        double az = Mathf.DegToRad(atm.SunAzimuthDeg), el = Mathf.DegToRad(atm.SunElevationDeg);
        // Direction toward the sun in Godot world axes (x = east, y = up, z = south).
        var sun = new Vector3((float)(Mathf.Cos(el) * Mathf.Sin(az)), (float)Mathf.Sin(el), (float)(-Mathf.Cos(el) * Mathf.Cos(az)));
        RenderingServer.GlobalShaderParameterSet("ir_sun_dir", sun.Normalized());

        var skyMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ir_sky.gdshader") };
        skyMat.SetShaderParameter("zenith_temp", (float)atm.SkyZenithTemp);
        skyMat.SetShaderParameter("horizon_temp", (float)atm.SkyHorizonTemp);

        var env = new GEnvironment
        {
            BackgroundMode = GEnvironment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMat, RadianceSize = Sky.RadianceSizeEnum.Size32 },
            AmbientLightSource = GEnvironment.AmbientSource.Disabled,
            ReflectedLightSource = GEnvironment.ReflectionSource.Disabled,
            TonemapMode = GEnvironment.ToneMapper.Linear,
            TonemapExposure = 1.0f,
            GlowEnabled = false,
            SsaoEnabled = false,
            SsrEnabled = false,
            SsilEnabled = false,
            SdfgiEnabled = false,
            FogEnabled = false,
            VolumetricFogEnabled = false,
        };
        root.AddChild(new WorldEnvironment { Name = "IrEnvironment", Environment = env });

        var t = world.Terrain;
        var terrainMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ir_terrain.gdshader") };
        terrainMat.SetShaderParameter("base_temp", (float)t.BaseTemp);
        terrainMat.SetShaderParameter("field_contrast", (float)t.FieldContrast);
        terrainMat.SetShaderParameter("water_fraction", (float)t.WaterFraction);
        terrainMat.SetShaderParameter("water_temp", (float)t.WaterTemp);
        terrainMat.SetShaderParameter("road_temp", (float)t.RoadTemp);
        terrainMat.SetShaderParameter("urban_fraction", (float)t.UrbanFraction);
        terrainMat.SetShaderParameter("seed", (float)t.Seed);

        // Flat ground at the origin altitude (Godot y = 0). Subdivided so the GPU interpolates world positions over
        // small triangles (keeps procedural texture precision high far from the origin).
        var ground = new MeshInstance3D
        {
            Name = "Terrain",
            Mesh = new PlaneMesh { Size = new Vector2((float)t.SizeM, (float)t.SizeM), SubdivideWidth = 63, SubdivideDepth = 63 },
            MaterialOverride = terrainMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        root.AddChild(ground);
    }
}
