using WebAirIG.Common.Geo;
using WebAirIG.Host.Services;

namespace WebAirIG.Host.Api;

/// <summary>Body for POST /api/cameras/{id}. Only the fields that are set are applied.</summary>
public sealed record CameraCommandRequest(
    CameraMode? Mode = null, double? AzDeg = null, double? ElDeg = null, double? FovDeg = null,
    int? TrackEntityId = null, double? GeoEastM = null, double? GeoNorthM = null,
    IrPolarity? Polarity = null, double? Gain = null, double? Level = null, double? Noise = null, bool? SensorOn = null);

/// <summary>Small REST API for scripting and automated tests (the Blazor UI does not use it).</summary>
public static class HostApi
{
    public static void MapHostApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/state", (ISimulationEngine engine, IIgLinkStatus ig) =>
        {
            var s = engine.Latest;
            return Results.Ok(new
            {
                s.SimTime,
                engine.Paused,
                Ig = new { ig.IgConnected, ig.LastIgFrame, ig.IgFrameRateHz },
                Entities = s.Entities.Select(e => new
                {
                    e.EntityId, e.Name, e.TypeName, e.Geo.LatDeg, e.Geo.LonDeg, e.Geo.AltM, e.YawDeg, e.PitchDeg, e.RollDeg,
                    EnuM = e.State.PositionEnu.ToArray(),
                }),
                Cameras = s.Cameras.Select(c => new
                {
                    c.Id, c.Name, Mode = c.Command.Mode.ToString(), c.AzDeg, c.ElDeg, c.Command.FovDeg, c.LimitReached, c.OnTarget,
                    c.Command.TrackEntityId,
                }),
            });
        });

        api.MapPost("/cameras/{id:int}", (int id, CameraCommandRequest req, ISensorControlService sensors) =>
        {
            if (sensors.Cameras.All(c => c.Id != id)) return Results.NotFound();
            sensors.Update(id, c =>
            {
                if (req.AzDeg is { } az) c.CmdAzDeg = az;
                if (req.ElDeg is { } el) c.CmdElDeg = el;
                if (req.FovDeg is { } fov) c.FovDeg = fov;
                if (req.TrackEntityId is { } t) c.TrackEntityId = t;
                if (req.GeoEastM is { } ge && req.GeoNorthM is { } gn) c.GeoPointEnu = new Vec3d(ge, gn, 0);
                if (req.Polarity is { } p) c.Polarity = p;
                if (req.Gain is { } g) c.Gain = g;
                if (req.Level is { } l) c.Level = l;
                if (req.Noise is { } n) c.Noise = n;
                if (req.SensorOn is { } on) c.SensorOn = on;
                if (req.Mode is { } m) c.Mode = m;
            });
            return Results.Ok(sensors.GetCommand(id));
        });

        // Apply a scenario in memory (not saved); /api/scenario/reload restores config/scenario.json.
        api.MapGet("/scenario", (IScenarioService s) => Results.Ok(s.GetCopy()));
        api.MapPost("/scenario", (WebAirIG.Common.Config.ScenarioConfig scenario, IScenarioService s) => { s.Apply(scenario); return Results.Ok(); });
        api.MapPost("/scenario/reload", (IScenarioService s) => { s.Reload(); return Results.Ok(); });

        // Entity model files for the browser 3D view. Only files referenced by the shared entity catalog are served.
        api.MapGet("/models/{typeId:int}", (int typeId, ISharedConfigService config, HttpContext http) =>
        {
            var type = config.Catalog.Find(typeId);
            if (type is null || string.IsNullOrWhiteSpace(type.Model)) return Results.NotFound();
            var path = WebAirIG.Common.Config.JsonConfig.Resolve(config.ConfigDir, type.Model);
            if (!File.Exists(path)) return Results.NotFound();
            string contentType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".glb" => "model/gltf-binary",
                ".gltf" => "model/gltf+json",
                ".obj" => "text/plain",
                _ => "application/octet-stream",
            };
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.File(path, contentType, enableRangeProcessing: true);
        });

        api.MapPost("/sim/pause", (ISimulationEngine e) => { e.Paused = true; return Results.Ok(); });
        api.MapPost("/sim/resume", (ISimulationEngine e) => { e.Paused = false; return Results.Ok(); });
        api.MapPost("/sim/reset", (ISimulationEngine e) => { e.ResetTime(); return Results.Ok(); });
    }
}
