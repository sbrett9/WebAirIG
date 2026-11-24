using Microsoft.Extensions.Options;
using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;
using WebAirIG.Host.Options;

namespace WebAirIG.Host.Services;

public interface ISharedConfigService
{
    string ConfigDir { get; }
    WorldConfig World { get; }
    EntityCatalog Catalog { get; }
    SensorsConfig Sensors { get; }
    LocalTangentPlane Ltp { get; }
    string ScenarioPath { get; }
}

/// <summary>Loads the shared config folder (same files the IG reads).</summary>
public sealed class SharedConfigService : ISharedConfigService
{
    public string ConfigDir { get; }
    public WorldConfig World { get; }
    public EntityCatalog Catalog { get; }
    public SensorsConfig Sensors { get; }
    public LocalTangentPlane Ltp { get; }
    public string ScenarioPath => Path.Combine(ConfigDir, "scenario.json");

    public SharedConfigService(IOptions<WebAirIgOptions> options, IWebHostEnvironment env, ILogger<SharedConfigService> log)
    {
        ConfigDir = JsonConfig.Resolve(env.ContentRootPath, options.Value.SharedConfigDir);
        World = JsonConfig.LoadOrDefault<WorldConfig>(Path.Combine(ConfigDir, "world.json"));
        Catalog = JsonConfig.LoadOrDefault<EntityCatalog>(Path.Combine(ConfigDir, "entity_catalog.json"));
        Sensors = JsonConfig.LoadOrDefault<SensorsConfig>(Path.Combine(ConfigDir, "sensors.json"));
        Ltp = World.CreateTangentPlane();
        log.LogInformation("Shared config {Dir}: origin {Origin}, {Types} entity types, {Cams} cameras",
            ConfigDir, Ltp.Origin, Catalog.Types.Count, Sensors.Cameras.Count);
    }
}
