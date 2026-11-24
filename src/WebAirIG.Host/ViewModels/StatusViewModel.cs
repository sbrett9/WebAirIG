using WebAirIG.Web.Mvvm;
using WebAirIG.Host.Services;

namespace WebAirIG.Host.ViewModels;

public sealed record EntityRow(int Id, string Name, string Type, string Lat, string Lon, double AltM, double HeadingDeg, double PitchDeg, double RollDeg, double SpeedMps);

/// <summary>Link health (CIGI/DIS) and a live entity table.</summary>
public sealed class StatusViewModel : PollingViewModel
{
    private readonly ISimulationEngine _engine;
    private readonly IIgLinkStatus _ig;
    private readonly IDisStatus _dis;
    private readonly ISharedConfigService _config;

    public StatusViewModel(ISimulationEngine engine, IIgLinkStatus ig, IDisStatus dis, ISharedConfigService config)
        : base(TimeSpan.FromMilliseconds(500))
    {
        _engine = engine;
        _ig = ig;
        _dis = dis;
        _config = config;
    }

    public IReadOnlyList<KeyValuePair<string, string>> Links { get; private set; } = [];
    public IReadOnlyList<EntityRow> Entities { get; private set; } = [];

    protected override void Refresh()
    {
        var snap = _engine.Latest;
        Links =
        [
            new("CIGI", _ig.Endpoint),
            new("IG connected", _ig.IgConnected ? $"yes, {_ig.IgFrameRateHz:F1} Hz" : "no (free-running)"),
            new("IG frame / host frame", $"{_ig.LastIgFrame} / {_ig.HostFrame}"),
            new("SOF received / messages sent", $"{_ig.SofReceived} / {_ig.MessagesSent}"),
            new("IG overframes", _ig.OverframeCount.ToString()),
            new("DIS", _dis.Enabled ? $"{_dis.Destination}, {_dis.PdusSent} PDUs" : "disabled"),
            new("Sim time", $"{snap.SimTime:F2} s (step {snap.StepIndex}){(_engine.Paused ? " PAUSED" : "")}"),
            new("Geo origin", _config.Ltp.Origin.ToString()),
            new("Shared config", _config.ConfigDir),
        ];
        Entities = snap.Entities.Select(e => new EntityRow(
            e.EntityId, e.Name, e.TypeName,
            e.Geo.LatDeg.ToString("F6"), e.Geo.LonDeg.ToString("F6"),
            Math.Round(e.Geo.AltM, 1), Math.Round(e.YawDeg, 1), Math.Round(e.PitchDeg, 1), Math.Round(e.RollDeg, 1),
            Math.Round(e.State.VelocityEnu.Length, 1))).ToList();
        OnPropertyChanged(string.Empty);
    }
}
