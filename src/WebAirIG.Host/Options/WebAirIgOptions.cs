namespace WebAirIG.Host.Options;

public sealed class WebAirIgOptions
{
    public const string Section = "WebAirIG";

    public string SharedConfigDir { get; set; } = "../../config";
    public CigiOptions Cigi { get; set; } = new();
    public DisOptions Dis { get; set; } = new();
}

public sealed class CigiOptions
{
    public string IgAddress { get; set; } = "127.0.0.1";
    public int IgPort { get; set; } = 8004;
    public int HostPort { get; set; } = 8005;
    /// <summary>Simulation rate when no IG is sending Start of Frame.</summary>
    public double FreeRunRateHz { get; set; } = 30;
    /// <summary>The IG is considered gone after this long without a Start of Frame.</summary>
    public double IgTimeoutSeconds { get; set; } = 1.0;
    /// <summary>Entity Control / View Control / View Definition / Sensor Control are re-sent every N frames.</summary>
    public int PeriodicResendFrames { get; set; } = 30;
    public bool BigEndian { get; set; }
}

public sealed class DisOptions
{
    public bool Enabled { get; set; } = true;
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3000;
    public byte ExerciseId { get; set; } = 1;
    public ushort SiteId { get; set; } = 1;
    public ushort ApplicationId { get; set; } = 3001;
    public double RateHz { get; set; } = 10;
}
