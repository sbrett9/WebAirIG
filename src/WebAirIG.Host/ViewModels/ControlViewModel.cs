using WebAirIG.Web.Mvvm;
using CommunityToolkit.Mvvm.Input;
using WebAirIG.Host.Services;

namespace WebAirIG.Host.ViewModels;

/// <summary>Main operator page: coverage map + the four camera control panels.</summary>
public sealed class ControlViewModel : PollingViewModel
{
    private readonly ISimulationEngine _engine;
    private readonly IIgLinkStatus _ig;
    private readonly ISharedConfigService _config;
    private string _igStatusText = "";
    private string _simTimeText = "";
    private bool _igConnected;
    private bool _show3D = true;

    public ControlViewModel(ISimulationEngine engine, ISensorControlService sensors, IIgLinkStatus ig, ISharedConfigService config)
        : base(TimeSpan.FromMilliseconds(200))
    {
        _engine = engine;
        _ig = ig;
        _config = config;
        Cameras = sensors.Cameras.Select(c => new CameraViewModel(c, sensors)).ToList();
        Map = new MapViewModel(sensors);
        Scene3D = new Scene3DViewModel(config, sensors, Cameras.ToDictionary(c => c.Id, c => c.Color));
        TogglePauseCommand = new RelayCommand(() => { _engine.Paused = !_engine.Paused; OnPropertyChanged(nameof(Paused)); });
        ResetTimeCommand = new RelayCommand(_engine.ResetTime);
        AllCenterCommand = new RelayCommand(() => { foreach (var c in Cameras) c.CenterCommand.Execute(null); });
    }

    public IReadOnlyList<CameraViewModel> Cameras { get; }
    public MapViewModel Map { get; }
    public Scene3DViewModel Scene3D { get; }
    /// <summary>Coverage panel shows the 3D view (true) or the 2D top-down map (false).</summary>
    public bool Show3D { get => _show3D; set => SetProperty(ref _show3D, value); }
    public bool Paused => _engine.Paused;
    public string IgStatusText { get => _igStatusText; private set => SetProperty(ref _igStatusText, value); }
    public string SimTimeText { get => _simTimeText; private set => SetProperty(ref _simTimeText, value); }
    public bool IgConnected { get => _igConnected; private set => SetProperty(ref _igConnected, value); }

    public IRelayCommand TogglePauseCommand { get; }
    public IRelayCommand ResetTimeCommand { get; }
    public IRelayCommand AllCenterCommand { get; }

    protected override void Refresh()
    {
        var snap = _engine.Latest;
        IgConnected = _ig.IgConnected;
        IgStatusText = IgConnected ? $"IG frame {_ig.LastIgFrame} @ {_ig.IgFrameRateHz:F1} Hz" : "IG not connected (free-run)";
        var t = TimeSpan.FromSeconds(snap.SimTime);
        SimTimeText = $"T+{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";

        var options = snap.Entities.Where(e => !e.IsOwnship).Select(e => new EntityOption(e.EntityId, $"{e.EntityId}: {e.Name} ({e.TypeName})")).ToList();
        foreach (var cam in Cameras)
        {
            if (!cam.TargetOptions.Select(o => o.Id).SequenceEqual(options.Select(o => o.Id))) cam.TargetOptions = options;
            var cs = snap.Cameras.FirstOrDefault(c => c.Id == cam.Id);
            if (cs is null) continue;
            var tracked = cs.Command.Mode == CameraMode.TrackEntity ? snap.Entities.FirstOrDefault(e => e.EntityId == cs.Command.TrackEntityId) : null;
            cam.UpdateFrom(cs, tracked is null ? null : TypeSizeM(tracked.TypeId));
        }
        if (Show3D) Scene3D.Update(snap, _engine.Paused ? 0 : _engine.TimeScale);
        else Map.Update(snap, Cameras);
        OnPropertyChanged(nameof(Paused));
    }

    private double? TypeSizeM(int typeId) =>
        _config.Catalog.Find(typeId)?.Placeholder is { } p ? Math.Max(p.LengthM, p.SpanM) : null;
}
