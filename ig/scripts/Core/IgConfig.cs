namespace WebAirIG.IG.Core;

/// <summary>ig/config/ig.json — IG-only settings (networking, rendering, streaming, truth).</summary>
public sealed class IgConfig
{
    public string SharedConfigDir { get; set; } = "../../config";
    public CigiSettings Cigi { get; set; } = new();
    public RenderSettings Render { get; set; } = new();
    public StreamSettings Stream { get; set; } = new();
    public TruthSettings Truth { get; set; } = new();
}

public sealed class CigiSettings
{
    public int ListenPort { get; set; } = 8004;
    public string HostAddress { get; set; } = "127.0.0.1";
    public int HostPort { get; set; } = 8005;
    public bool SyncMode { get; set; } = true;
    public int SyncTimeoutMs { get; set; } = 25;
    public bool BigEndian { get; set; }
}

public sealed class RenderSettings
{
    public int Fps { get; set; } = 30;
    public int Msaa { get; set; } = 4;
    public int MaxSamplePoints { get; set; } = 600;
}

public sealed class StreamSettings
{
    public bool Enabled { get; set; } = true;
    public string Protocol { get; set; } = "udp";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5600;
    public string Codec { get; set; } = "h264";
    public int BitrateKbps { get; set; } = 25_000;
    public int Gop { get; set; } = 30;
    public string Preset { get; set; } = "p2";
    public string FfmpegPath { get; set; } = "";
    public string EncoderOverride { get; set; } = "";
    public int MaxDatagramBytes { get; set; } = 1400;
    public double PacingMbps { get; set; }
    public int EveryNFrames { get; set; } = 1;
    public string MirrorUrlTemplate { get; set; } = "";
}

public sealed class TruthSettings
{
    public bool EmbedInStream { get; set; } = true;
    public bool LogToDisk { get; set; }
    public string LogDir { get; set; } = "../../output/truth";
}
