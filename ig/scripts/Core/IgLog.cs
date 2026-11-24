using Godot;

namespace WebAirIG.IG.Core;

/// <summary>Thread-safe logging to the Godot console (GD.Print is safe to call from worker threads).</summary>
public static class IgLog
{
    public static void Info(string message) => GD.Print($"[WebAirIG] {message}");
    public static void Warn(string message) => GD.PushWarning($"[WebAirIG] {message}");
    public static void Error(string message) => GD.PushError($"[WebAirIG] {message}");
}
