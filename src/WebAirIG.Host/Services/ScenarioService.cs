using System.Text.Json;
using WebAirIG.Common.Config;

namespace WebAirIG.Host.Services;

public interface IScenarioService
{
    /// <summary>A deep copy of the active scenario (safe to edit; push changes back with <see cref="Apply"/>).</summary>
    ScenarioConfig GetCopy();
    void Apply(ScenarioConfig scenario);
    void Save();
    void Reload();
    event Action<ScenarioConfig>? Changed;
}

/// <summary>Holds the active scenario and persists it to config/scenario.json.</summary>
public sealed class ScenarioService : IScenarioService
{
    private readonly ISharedConfigService _config;
    private readonly ILogger<ScenarioService> _log;
    private readonly object _gate = new();
    private ScenarioConfig _active;

    public event Action<ScenarioConfig>? Changed;

    public ScenarioService(ISharedConfigService config, ILogger<ScenarioService> log)
    {
        _config = config;
        _log = log;
        _active = JsonConfig.LoadOrDefault<ScenarioConfig>(config.ScenarioPath);
    }

    public ScenarioConfig GetCopy()
    {
        lock (_gate) return Clone(_active);
    }

    public void Apply(ScenarioConfig scenario)
    {
        ScenarioConfig copy;
        lock (_gate)
        {
            _active = Clone(scenario);
            copy = Clone(_active);
        }
        _log.LogInformation("Scenario applied: {Count} targets", copy.Targets.Count);
        Changed?.Invoke(copy);
    }

    public void Save()
    {
        lock (_gate) JsonConfig.Save(_config.ScenarioPath, _active);
        _log.LogInformation("Scenario saved to {Path}", _config.ScenarioPath);
    }

    public void Reload()
    {
        var loaded = JsonConfig.LoadOrDefault<ScenarioConfig>(_config.ScenarioPath);
        Apply(loaded);
    }

    private static ScenarioConfig Clone(ScenarioConfig s) =>
        JsonSerializer.Deserialize<ScenarioConfig>(JsonSerializer.Serialize(s, JsonConfig.Compact), JsonConfig.Compact)!;
}
