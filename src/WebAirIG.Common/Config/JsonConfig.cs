using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebAirIG.Common.Config;

/// <summary>JSON helpers shared by the IG, host and consumer (camelCase, comments allowed, enums as strings).</summary>
public static class JsonConfig
{
    public static readonly JsonSerializerOptions Options = CreateOptions(indented: true);
    public static readonly JsonSerializerOptions Compact = CreateOptions(indented: false);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    public static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}", path);
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(fs, Options) ?? new T();
    }

    public static T LoadOrDefault<T>(string path) where T : new() => File.Exists(path) ? Load<T>(path) : new T();

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Resolves <paramref name="path"/> relative to <paramref name="baseDir"/> unless it is already absolute.</summary>
    public static string Resolve(string baseDir, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));
}
