using Microsoft.Extensions.Options;
using WebAirIG.Common.Config;
using WebAirIG.Consumer.Options;

namespace WebAirIG.Consumer.Services;

/// <summary>YOLO class names (id to name) from the shared entity catalog, so dataset.yaml is stable across runs.</summary>
public sealed class ClassCatalogService
{
    public IReadOnlyDictionary<int, string> Classes { get; }
    public string Source { get; }

    public ClassCatalogService(IOptions<ConsumerOptions> options, IWebHostEnvironment env)
    {
        var path = Path.Combine(JsonConfig.Resolve(env.ContentRootPath, options.Value.SharedConfigDir), "entity_catalog.json");
        var catalog = JsonConfig.LoadOrDefault<EntityCatalog>(path);
        Classes = catalog.Types
            .GroupBy(t => t.ClassId)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.First().ClassName);
        Source = path;
    }
}
