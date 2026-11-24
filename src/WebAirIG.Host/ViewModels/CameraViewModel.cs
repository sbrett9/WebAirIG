using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;
using WebAirIG.Host.Services;

namespace WebAirIG.Host.ViewModels;

/// <summary>Operator controls for one gimbaled camera. Writes go straight to <see cref="ISensorControlService"/>.</summary>
public sealed class CameraViewModel : ObservableObject
{
    private static readonly string[] Palette = ["#4fc3f7", "#ffb74d", "#81c784", "#f06292", "#ba68c8", "#fff176"];
    private readonly ISensorControlService _sensors;
    private CameraCommand _cmd;
    private double _actualAz, _actualEl;
    private bool _limitReached, _onTarget;
    private string _pointingNote = "";
    private IReadOnlyList<EntityOption> _targetOptions = [];

    public CameraViewModel(CameraDef def, ISensorControlService sensors)
    {
        _sensors = sensors;
        Definition = def;
        _cmd = sensors.GetCommand(def.Id);
        Color = Palette[def.Id % Palette.Length];
        // Presets point toward aircraft-frame directions (x fwd, y right, z down) as far as the gimbal limits allow,
        // so they behave sensibly for nadir and lateral mounts alike.
        CenterCommand = new RelayCommand(() => Apply(c => { c.Mode = CameraMode.Center; c.CmdAzDeg = 0; c.CmdElDeg = 0; }));
        NadirCommand = new RelayCommand(() => Apply(c => c.Mode = CameraMode.Nadir));
        LookLeftCommand = new RelayCommand(() => Point(new Vec3d(0, -1, 0)));
        LookRightCommand = new RelayCommand(() => Point(new Vec3d(0, 1, 0)));
        LookForwardCommand = new RelayCommand(() => Point(new Vec3d(1, 0, 0)));
        LookAftCommand = new RelayCommand(() => Point(new Vec3d(-1, 0, 0)));
        LookUpCommand = new RelayCommand(() => Point(new Vec3d(0, 0, -1)));
        LookDownCommand = new RelayCommand(() => Point(new Vec3d(0, 0, 1)));
        ScanCommand = new RelayCommand(() => Apply(c => c.Mode = CameraMode.Scan));
    }

    public CameraDef Definition { get; }
    public int Id => Definition.Id;
    public string Name => Definition.Name;
    public string Color { get; }
    public double AzMin => Definition.Limits.AzMinDeg;
    public double AzMax => Definition.Limits.AzMaxDeg;
    public double ElMin => Definition.Limits.ElMinDeg;
    public double ElMax => Definition.Limits.ElMaxDeg;
    public double FovMin => Definition.FovMinDeg;
    public double FovMax => Definition.FovMaxDeg;
    public string MountText => $"FRD ({Definition.Mount.XFwdM:F1}, {Definition.Mount.YRightM:F1}, {Definition.Mount.ZDownM:F1}) m, {BoresightText}, {Definition.Width}x{Definition.Height}";

    public IReadOnlyList<CameraMode> ModeOptions { get; } = Enum.GetValues<CameraMode>();
    public IReadOnlyList<IrPolarity> PolarityOptions { get; } = Enum.GetValues<IrPolarity>();

    public IReadOnlyList<EntityOption> TargetOptions
    {
        get => _targetOptions;
        set => SetProperty(ref _targetOptions, value);
    }

    public CameraMode Mode
    {
        get => _cmd.Mode;
        set => Apply(c => c.Mode = value);
    }

    public bool IsTrackMode => _cmd.Mode == CameraMode.TrackEntity;
    public bool IsScanMode => _cmd.Mode == CameraMode.Scan;
    public bool IsGeoMode => _cmd.Mode == CameraMode.GeoPoint;

    /// <summary>Moving the azimuth control switches to Manual (except in Scan, where only elevation is manual).</summary>
    public double CmdAzDeg
    {
        get => _cmd.CmdAzDeg;
        set => Apply(c => { c.CmdAzDeg = value; c.Mode = CameraMode.Manual; });
    }

    public double CmdElDeg
    {
        get => _cmd.CmdElDeg;
        set => Apply(c => { c.CmdElDeg = value; if (c.Mode != CameraMode.Scan) c.Mode = CameraMode.Manual; });
    }

    public double FovDeg { get => _cmd.FovDeg; set => Apply(c => c.FovDeg = value); }
    public int TrackEntityId { get => _cmd.TrackEntityId; set => Apply(c => { c.TrackEntityId = value; c.Mode = CameraMode.TrackEntity; }); }
    public double ScanAmplitudeDeg { get => _cmd.ScanAmplitudeDeg; set => Apply(c => c.ScanAmplitudeDeg = Math.Clamp(value, 0, Definition.Limits.AzMaxDeg)); }
    public double ScanPeriodS { get => _cmd.ScanPeriodS; set => Apply(c => c.ScanPeriodS = Math.Max(1, value)); }
    public bool SensorOn { get => _cmd.SensorOn; set => Apply(c => c.SensorOn = value); }
    public IrPolarity Polarity { get => _cmd.Polarity; set => Apply(c => c.Polarity = value); }
    public double Gain { get => _cmd.Gain; set => Apply(c => c.Gain = value); }
    public double Level { get => _cmd.Level; set => Apply(c => c.Level = value); }
    public double Noise { get => _cmd.Noise; set => Apply(c => c.Noise = value); }

    public double ActualAzDeg { get => _actualAz; private set => SetProperty(ref _actualAz, value); }
    public double ActualElDeg { get => _actualEl; private set => SetProperty(ref _actualEl, value); }
    public bool LimitReached { get => _limitReached; private set => SetProperty(ref _limitReached, value); }
    public bool OnTarget { get => _onTarget; private set => SetProperty(ref _onTarget, value); }
    public string GeoPointText => IsGeoMode ? $"E {_cmd.GeoPointEnu.X:F0} m, N {_cmd.GeoPointEnu.Y:F0} m" : "";
    public string StatusText => LimitReached ? "AT LIMIT" : OnTarget ? "ON TARGET" : "SLEWING";
    /// <summary>Why the camera is at a limit, or the tracked target's range and expected image size.</summary>
    public string PointingNote { get => _pointingNote; private set => SetProperty(ref _pointingNote, value); }

    public IRelayCommand CenterCommand { get; }
    public IRelayCommand NadirCommand { get; }
    public IRelayCommand LookLeftCommand { get; }
    public IRelayCommand LookRightCommand { get; }
    public IRelayCommand LookForwardCommand { get; }
    public IRelayCommand LookAftCommand { get; }
    public IRelayCommand LookUpCommand { get; }
    public IRelayCommand LookDownCommand { get; }
    public IRelayCommand ScanCommand { get; }

    /// <summary>Lateral mounts (yaw +/-90) look out the side at gimbal zero; nadir mounts look down.</summary>
    public string BoresightText => Math.Abs(Definition.Mount.PitchDeg) > 45 ? "nadir boresight"
        : Definition.Mount.YawDeg < -45 ? "lateral (left)" : Definition.Mount.YawDeg > 45 ? "lateral (right)" : "forward boresight";

    private void Point(Vec3d directionBody)
    {
        _sensors.PointBodyDirection(Id, directionBody);
        _cmd = _sensors.GetCommand(Id);
        OnPropertyChanged(string.Empty);
    }

    private void Apply(Action<CameraCommand> mutate)
    {
        _sensors.Update(Id, mutate);
        _cmd = _sensors.GetCommand(Id);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Refreshes measured gimbal state (and commands changed by other operators).</summary>
    /// <param name="targetSizeM">Largest dimension of the tracked entity, when known (for the expected pixel size).</param>
    public void UpdateFrom(CameraSnapshot snap, double? targetSizeM = null)
    {
        _cmd = snap.Command;
        ActualAzDeg = Math.Round(snap.AzDeg, 2);
        ActualElDeg = Math.Round(snap.ElDeg, 2);
        LimitReached = snap.LimitReached;
        OnTarget = snap.OnTarget;
        PointingNote = BuildPointingNote(snap, targetSizeM);
        OnPropertyChanged(string.Empty);
    }

    private string BuildPointingNote(CameraSnapshot snap, double? targetSizeM)
    {
        bool hasTarget = !double.IsNaN(snap.TargetRangeM);
        if (snap.Command.Mode == CameraMode.TrackEntity && !hasTarget) return "Select an entity to track";
        if (!hasTarget) return "";

        string range = snap.TargetRangeM >= 1000 ? $"{snap.TargetRangeM / 1000:F1} km" : $"{snap.TargetRangeM:F0} m";
        if (snap.LimitReached)
        {
            var l = Definition.Limits;
            var parts = new List<string>(2);
            if (snap.RequestedAzDeg < l.AzMinDeg || snap.RequestedAzDeg > l.AzMaxDeg) parts.Add($"az {snap.RequestedAzDeg:F0}° (limit {l.AzMinDeg:F1}..{l.AzMaxDeg:F1})");
            if (snap.RequestedElDeg < l.ElMinDeg || snap.RequestedElDeg > l.ElMaxDeg) parts.Add($"el {snap.RequestedElDeg:F0}° (limit {l.ElMinDeg:F1}..{l.ElMaxDeg:F1})");
            return $"Target outside gimbal reach: {string.Join(", ", parts)} · {range}";
        }

        if (targetSizeM is not { } size || snap.TargetRangeM <= 0) return $"Target {range}";
        double ifovRad = snap.Command.FovDeg * Math.PI / 180 / Definition.Height;
        return $"Target {range} · ~{size / snap.TargetRangeM / ifovRad:F0} px across";
    }
}
