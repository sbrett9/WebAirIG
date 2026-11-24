using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebAirIG.Common.Config;
using WebAirIG.Common.Simulation;
using WebAirIG.Host.Services;

namespace WebAirIG.Host.ViewModels;

/// <summary>Edits one cyclic flight pattern.</summary>
public sealed class PatternEditorViewModel : ObservableObject
{
    private readonly PatternDef _p;

    public PatternEditorViewModel(PatternDef pattern) => _p = pattern;

    public PatternDef Build() => _p;

    public IReadOnlyList<PatternKind> KindOptions { get; } = Enum.GetValues<PatternKind>();

    private void Set<T>(T current, T value, Action<PatternDef, T> assign)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(_p, value);
        OnPropertyChanged(string.Empty);
    }

    public PatternKind Kind { get => _p.Kind; set => Set(_p.Kind, value, (p, v) => p.Kind = v); }
    public double CenterEastM { get => _p.CenterEastM; set => Set(_p.CenterEastM, value, (p, v) => p.CenterEastM = v); }
    public double CenterNorthM { get => _p.CenterNorthM; set => Set(_p.CenterNorthM, value, (p, v) => p.CenterNorthM = v); }
    public double AltitudeM { get => _p.AltitudeM; set => Set(_p.AltitudeM, value, (p, v) => p.AltitudeM = v); }
    public double SpeedMps { get => _p.SpeedMps; set => Set(_p.SpeedMps, value, (p, v) => p.SpeedMps = Math.Max(0, v)); }
    public double SizeAM { get => _p.SizeAM; set => Set(_p.SizeAM, value, (p, v) => p.SizeAM = Math.Max(1, v)); }
    public double SizeBM { get => _p.SizeBM; set => Set(_p.SizeBM, value, (p, v) => p.SizeBM = Math.Max(1, v)); }
    public double HeadingDeg { get => _p.HeadingDeg; set => Set(_p.HeadingDeg, value, (p, v) => p.HeadingDeg = v); }
    public bool Clockwise { get => _p.Clockwise; set => Set(_p.Clockwise, value, (p, v) => p.Clockwise = v); }
    public double Phase { get => _p.Phase; set => Set(_p.Phase, value, (p, v) => p.Phase = Math.Clamp(v, 0, 1)); }
    public int Lanes { get => _p.Lanes; set => Set(_p.Lanes, value, (p, v) => p.Lanes = Math.Max(2, v)); }
    public double AltitudeAmplitudeM { get => _p.AltitudeAmplitudeM; set => Set(_p.AltitudeAmplitudeM, value, (p, v) => p.AltitudeAmplitudeM = v); }
    public double AltitudePeriodS { get => _p.AltitudePeriodS; set => Set(_p.AltitudePeriodS, value, (p, v) => p.AltitudePeriodS = Math.Max(1, v)); }
    public double BankFactor { get => _p.BankFactor; set => Set(_p.BankFactor, value, (p, v) => p.BankFactor = Math.Clamp(v, 0, 2)); }
    public double MaxBankDeg { get => _p.MaxBankDeg; set => Set(_p.MaxBankDeg, value, (p, v) => p.MaxBankDeg = Math.Clamp(v, 0, 80)); }
    public double FormationFwdM { get => _p.FormationFwdM; set => Set(_p.FormationFwdM, value, (p, v) => p.FormationFwdM = v); }
    public double FormationRightM { get => _p.FormationRightM; set => Set(_p.FormationRightM, value, (p, v) => p.FormationRightM = v); }
    public double FormationDownM { get => _p.FormationDownM; set => Set(_p.FormationDownM, value, (p, v) => p.FormationDownM = v); }
    public double FormationWanderFwdM { get => _p.FormationWanderFwdM; set => Set(_p.FormationWanderFwdM, value, (p, v) => p.FormationWanderFwdM = Math.Max(0, v)); }
    public double FormationWanderRightM { get => _p.FormationWanderRightM; set => Set(_p.FormationWanderRightM, value, (p, v) => p.FormationWanderRightM = Math.Max(0, v)); }
    public double FormationWanderDownM { get => _p.FormationWanderDownM; set => Set(_p.FormationWanderDownM, value, (p, v) => p.FormationWanderDownM = Math.Max(0, v)); }
    public double FormationWanderPeriodS { get => _p.FormationWanderPeriodS; set => Set(_p.FormationWanderPeriodS, value, (p, v) => p.FormationWanderPeriodS = Math.Max(5, v)); }

    public bool IsFormation => Kind == PatternKind.Formation;
    public bool IsHold => Kind == PatternKind.Hold;
    public bool HasLoop => !IsFormation && !IsHold;
    public bool UsesSizeB => Kind is PatternKind.Ellipse or PatternKind.Racetrack or PatternKind.Box or PatternKind.Lawnmower or PatternKind.Weave;
    public bool UsesLanes => Kind == PatternKind.Lawnmower;

    public string SizeALabel => Kind switch
    {
        PatternKind.Circle => "Radius (m)",
        PatternKind.Ellipse => "Semi-major axis (m)",
        PatternKind.Racetrack or PatternKind.Weave => "Straight length (m)",
        PatternKind.Figure8 => "Half-width (m)",
        PatternKind.Box => "Side (m)",
        PatternKind.Lawnmower => "Lane length (m)",
        PatternKind.OutAndBack => "Leg length (m)",
        _ => "Size A (m)",
    };

    public string SizeBLabel => Kind switch
    {
        PatternKind.Ellipse => "Semi-minor axis (m)",
        PatternKind.Racetrack => "Turn radius (m)",
        PatternKind.Box => "Corner radius (m)",
        PatternKind.Lawnmower => "Lane spacing (m)",
        PatternKind.Weave => "Weave amplitude (m)",
        _ => "Size B (m)",
    };

    public string LoopSummary
    {
        get
        {
            if (!HasLoop) return IsHold ? "Stationary" : "Holds offset from ownship";
            try
            {
                var m = new PatternMotion(_p);
                return $"Loop {m.LoopLengthM / 1000:F1} km, period {TimeSpan.FromSeconds(m.LoopPeriodS):mm\\:ss}";
            }
            catch (Exception ex) { return $"Invalid: {ex.Message}"; }
        }
    }
}

public sealed class TargetEditorViewModel : ObservableObject
{
    private readonly TargetDef _t;

    public TargetEditorViewModel(TargetDef target)
    {
        _t = target;
        Pattern = new PatternEditorViewModel(target.Pattern);
    }

    public PatternEditorViewModel Pattern { get; }
    public int EntityId { get => _t.EntityId; set { _t.EntityId = Math.Clamp(value, 1, 65535); OnPropertyChanged(); } }
    public string Name { get => _t.Name; set { _t.Name = value ?? ""; OnPropertyChanged(); } }
    public int TypeId { get => _t.TypeId; set { _t.TypeId = value; OnPropertyChanged(); } }
    public bool Enabled { get => _t.Enabled; set { _t.Enabled = value; OnPropertyChanged(); } }
    public byte Force { get => _t.Force; set { _t.Force = value; OnPropertyChanged(); } }

    public TargetDef Build()
    {
        _t.Pattern = Pattern.Build();
        return _t;
    }
}

/// <summary>Scenario editor: ownship pattern and the list of targets with their cyclic patterns.</summary>
public sealed class ScenarioViewModel : ObservableObject
{
    private readonly IScenarioService _scenarios;
    private readonly ISimulationEngine _engine;
    private readonly ISharedConfigService _config;
    private ScenarioConfig _working = new();
    private PatternEditorViewModel _ownship = new(new PatternDef());
    private IList<TargetEditorViewModel> _selection = [];
    private string _status = "";

    public ScenarioViewModel(IScenarioService scenarios, ISimulationEngine engine, ISharedConfigService config)
    {
        _scenarios = scenarios;
        _engine = engine;
        _config = config;
        TypeOptions = config.Catalog.Types.Select(t => new TypeOption(t.TypeId, $"{t.TypeId}: {t.Name} [{t.ClassName}]")).ToList();
        AddTargetCommand = new RelayCommand(AddTarget);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected);
        ApplyCommand = new RelayCommand(Apply);
        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new RelayCommand(Reload);
        RevertCommand = new RelayCommand(() => { Load(_scenarios.GetCopy()); Status = "Reverted to the running scenario"; });
        ResetTimeCommand = new RelayCommand(() => { _engine.ResetTime(); Status = "Simulation time reset to 0"; });
        Load(_scenarios.GetCopy());
    }

    public IReadOnlyList<TypeOption> TypeOptions { get; }
    public IReadOnlyList<ForceOption> ForceOptions { get; } =
        [new(0, "Other"), new(1, "Friendly"), new(2, "Opposing"), new(3, "Neutral")];

    public PatternEditorViewModel Ownship { get => _ownship; private set => SetProperty(ref _ownship, value); }
    public int OwnshipTypeId { get => _working.Ownship.TypeId; set { _working.Ownship.TypeId = value; OnPropertyChanged(); } }
    public ObservableCollection<TargetEditorViewModel> Targets { get; } = [];

    public IList<TargetEditorViewModel> Selection
    {
        get => _selection;
        set { SetProperty(ref _selection, value ?? []); OnPropertyChanged(nameof(Selected)); }
    }

    public TargetEditorViewModel? Selected => _selection.FirstOrDefault();
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ScenarioPath => _config.ScenarioPath;

    public IRelayCommand AddTargetCommand { get; }
    public IRelayCommand RemoveSelectedCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand ReloadCommand { get; }
    public IRelayCommand RevertCommand { get; }
    public IRelayCommand ResetTimeCommand { get; }

    private void Load(ScenarioConfig s)
    {
        _working = s;
        Ownship = new PatternEditorViewModel(s.Ownship.Pattern);
        Targets.Clear();
        foreach (var t in s.Targets) Targets.Add(new TargetEditorViewModel(t));
        Selection = Targets.Take(1).ToList();
        OnPropertyChanged(string.Empty);
    }

    private ScenarioConfig Build()
    {
        _working.Ownship.Pattern = Ownship.Build();
        _working.Targets = Targets.Select(t => t.Build()).ToList();
        return _working;
    }

    private void AddTarget()
    {
        int id = Targets.Count == 0 ? 1 : Targets.Max(t => t.EntityId) + 1;
        var type = _config.Catalog.Types.FirstOrDefault(t => t.TypeId != _working.Ownship.TypeId) ?? _config.Catalog.Types.FirstOrDefault();
        var vm = new TargetEditorViewModel(new TargetDef
        {
            EntityId = id,
            Name = $"TGT{id}",
            TypeId = type?.TypeId ?? 0,
            Pattern = new PatternDef { Kind = PatternKind.Circle, AltitudeM = 1500, SpeedMps = type?.DefaultSpeedMps ?? 60, SizeAM = 2500 },
        });
        Targets.Add(vm);
        Selection = [vm];
        Status = $"Added target {id} (not applied yet)";
    }

    private void RemoveSelected()
    {
        if (Selected is not { } s) return;
        Targets.Remove(s);
        Selection = Targets.Take(1).ToList();
        Status = $"Removed target {s.EntityId} (not applied yet)";
    }

    private void Apply()
    {
        var dupes = Targets.GroupBy(t => t.EntityId).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0)
        {
            Status = $"Duplicate entity IDs: {string.Join(", ", dupes)}";
            return;
        }
        _scenarios.Apply(Build());
        Load(_scenarios.GetCopy());
        Status = $"Applied at {DateTime.Now:T}";
    }

    private void Save()
    {
        Apply();
        _scenarios.Save();
        Status = $"Applied and saved to {_config.ScenarioPath}";
    }

    private void Reload()
    {
        _scenarios.Reload();
        Load(_scenarios.GetCopy());
        Status = "Reloaded from disk";
    }
}
