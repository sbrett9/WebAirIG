using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;
using WebAirIG.Common.Sensors;
using WebAirIG.Host.Services;

namespace WebAirIG.Host.ViewModels;

public enum Scene3DMode { Ownship, Tactical }

// ---------------------------------------------------------------------------------------------------------------
// Render DTOs for the browser-side three.js view (wwwroot/js/scene3d.js). All vectors use Godot axes so the browser
// shows exactly what the IG renders: world X = East, Y = Up, Z = South; entity-local X = right, Y = up, -Z = forward;
// camera-local X = image right, Y = image up, -Z = boresight. Bases are 3x3 column-major [X axis, Y axis, Z axis].
// ---------------------------------------------------------------------------------------------------------------

/// <summary>Entity type: model URL plus the same fix-up transform the IG applies (ModelLibrary "ModelFix" node).</summary>
public sealed record Scene3DType(
    int TypeId, string Name, string ClassName, string? ModelUrl, string ModelFormat,
    double ModelScale, double[] ModelRotationDeg, double[] ModelOffsetLocal,
    string PlaceholderKind, double LengthM, double SpanM, double HeightM);

/// <param name="MountLocal">Camera position in ownship-local coordinates (m).</param>
/// <param name="ForGrid">Boresight unit directions over the gimbal limits, ownship-local, flattened [az][el][xyz].</param>
public sealed record Scene3DCamera(
    int Id, string Name, string Color, double[] MountLocal, double Aspect,
    double[] ForGrid, int ForAzSteps, int ForElSteps, double AzMinDeg, double AzMaxDeg, double ElMinDeg, double ElMaxDeg);

public sealed record Scene3DStatic(double GroundSizeM, IReadOnlyList<Scene3DType> Types, IReadOnlyList<Scene3DCamera> Cameras);

/// <param name="Velocity">World velocity (m/s, already multiplied by the sim time scale) for smooth extrapolation.</param>
public sealed record Scene3DEntity(
    int Id, string Name, int TypeId, string TypeName, bool IsOwnship, string Color, double[] Position, double[] Velocity, double[] Basis);

/// <param name="Basis">Camera orientation relative to the ownship (ownship-local, column-major).</param>
/// <param name="Footprint">Ground footprint polygon, world coordinates flattened [xyz...], empty if none.</param>
public sealed record Scene3DCameraState(
    int Id, double[] Basis, double FovDeg, double AzDeg, double ElDeg, bool LimitReached, bool SensorOn,
    int TrackEntityId, double[] Footprint);

public sealed record Scene3DFrame(long Step, double SimTime, IReadOnlyList<Scene3DEntity> Entities, IReadOnlyList<Scene3DCameraState> Cameras);

public sealed record Scene3DOptions(
    string Mode, double FrustumLengthM, double SymbolScale, bool ShowFor, bool ShowFootprints, bool ShowLabels,
    bool FollowHeading, int SelectedEntityId);

/// <summary>Per-camera answer to "can this camera see the selected entity?".</summary>
public sealed record CameraReach(int CameraId, string CameraName, string Color, bool InReach, bool Tracking, string Text);

/// <summary>
/// 3D coverage view: the ownship at true scale with its camera mounts, live frustums (unit length or long range),
/// gimbal field-of-regard surfaces, other entities and ground footprints. Selecting an entity reports which cameras
/// can reach it and lets the operator assign a camera to track it.
/// </summary>
public sealed class Scene3DViewModel : ObservableObject
{
    private const int ForAzSteps = 17, ForElSteps = 9;
    private readonly ISensorControlService _sensors;
    private readonly ISharedConfigService _config;
    private Scene3DMode _mode = Scene3DMode.Ownship;
    private double _frustumLengthM = 60, _symbolScale = 1;
    private bool _showFor = true, _showFootprints = true, _showLabels = true, _followHeading = true;
    private int _selectedEntityId = -1;
    private Scene3DFrame _frame = new(0, 0, [], []);
    private IReadOnlyList<CameraReach> _selectedReach = [];
    private string _selectedTitle = "";
    private string _selectedDetail = "";
    private SimSnapshot _last = SimSnapshot.Empty;

    public Scene3DViewModel(ISharedConfigService config, ISensorControlService sensors, IReadOnlyDictionary<int, string> cameraColors)
    {
        _config = config;
        _sensors = sensors;
        Static = BuildStatic(config, sensors, cameraColors);
        ModeOptions = Enum.GetValues<Scene3DMode>();
        SelectEntityCommand = new RelayCommand<int>(id => SelectedEntityId = id);
        ClearSelectionCommand = new RelayCommand(() => SelectedEntityId = -1);
        TrackWithCameraCommand = new RelayCommand<int>(TrackWithCamera);
        ResetViewCommand = new RelayCommand(() => { ViewResetRevision++; OnPropertyChanged(nameof(ViewResetRevision)); });
    }

    public Scene3DStatic Static { get; }
    public Scene3DFrame Frame { get => _frame; private set => SetProperty(ref _frame, value); }
    public IReadOnlyList<Scene3DMode> ModeOptions { get; }
    public IReadOnlyList<double> SymbolScaleOptions { get; } = [1, 5, 10, 25, 50, 100];

    public Scene3DMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            // Each mode has a sensible scale: true size close up, exaggerated symbols and long frustums when zoomed out.
            (FrustumLengthM, SymbolScale) = value == Scene3DMode.Ownship ? (60.0, 1.0) : (6000.0, 25.0);
            ViewResetRevision++;
            OnOptionsChanged();
        }
    }

    public double FrustumLengthM { get => _frustumLengthM; set { if (SetProperty(ref _frustumLengthM, Math.Clamp(value, 5, 60_000))) OnOptionsChanged(); } }
    public double SymbolScale { get => _symbolScale; set { if (SetProperty(ref _symbolScale, Math.Clamp(value, 1, 200))) OnOptionsChanged(); } }
    public bool ShowFor { get => _showFor; set { if (SetProperty(ref _showFor, value)) OnOptionsChanged(); } }
    public bool ShowFootprints { get => _showFootprints; set { if (SetProperty(ref _showFootprints, value)) OnOptionsChanged(); } }
    public bool ShowLabels { get => _showLabels; set { if (SetProperty(ref _showLabels, value)) OnOptionsChanged(); } }
    public bool FollowHeading { get => _followHeading; set { if (SetProperty(ref _followHeading, value)) OnOptionsChanged(); } }

    public int SelectedEntityId
    {
        get => _selectedEntityId;
        set { if (SetProperty(ref _selectedEntityId, value)) { RefreshSelection(); OnOptionsChanged(); } }
    }

    public Scene3DOptions Options => new(_mode.ToString(), _frustumLengthM, _symbolScale, _showFor, _showFootprints, _showLabels, _followHeading, _selectedEntityId);
    public int OptionsRevision { get; private set; }
    /// <summary>Incremented to ask the view to re-frame its 3D camera (mode change or the reset button).</summary>
    public int ViewResetRevision { get; private set; }

    public bool HasSelection => _selectedEntityId >= 0 && !string.IsNullOrEmpty(_selectedTitle);
    public string SelectedTitle { get => _selectedTitle; private set => SetProperty(ref _selectedTitle, value); }
    public string SelectedDetail { get => _selectedDetail; private set => SetProperty(ref _selectedDetail, value); }
    public IReadOnlyList<CameraReach> SelectedReach { get => _selectedReach; private set => SetProperty(ref _selectedReach, value); }
    public string FrustumLengthText => _frustumLengthM >= 1000 ? $"{_frustumLengthM / 1000:0.#} km" : $"{_frustumLengthM:0} m";

    public IRelayCommand<int> SelectEntityCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }
    public IRelayCommand<int> TrackWithCameraCommand { get; }
    public IRelayCommand ResetViewCommand { get; }

    /// <summary>Called by the owning page ViewModel on every refresh tick.</summary>
    /// <param name="timeScale">Simulation time scale (0 while paused) so the browser extrapolates at the right rate.</param>
    public void Update(SimSnapshot snap, double timeScale)
    {
        _last = snap;
        var entities = snap.Entities.Select(e => new Scene3DEntity(
            e.EntityId, e.Name, e.TypeId, e.TypeName, e.IsOwnship,
            e.IsOwnship ? "#ffffff" : MapViewModel.ForceColor(e.Force),
            FrameConventions.EnuToGodot(e.State.PositionEnu).ToArray(),
            (FrameConventions.EnuToGodot(e.State.VelocityEnu) * timeScale).ToArray(),
            WorldBasis(e.State.NedFromBody))).ToList();

        var cams = snap.Cameras.Select(c =>
        {
            var def = _sensors.Cameras.First(d => d.Id == c.Id);
            var bodyFromCam = GimbalMath.BodyFromCamera(def.Mount, c.AzDeg, c.ElDeg);
            var footprint = c.FootprintEnu.SelectMany(p => FrameConventions.EnuToGodot(p).ToArray()).ToArray();
            return new Scene3DCameraState(c.Id, LocalCameraBasis(bodyFromCam), c.Command.FovDeg, c.AzDeg, c.ElDeg, c.LimitReached,
                c.Command.SensorOn, c.Command.Mode == CameraMode.TrackEntity ? c.Command.TrackEntityId : -1, footprint);
        }).ToList();

        Frame = new Scene3DFrame(snap.StepIndex, snap.SimTime, entities, cams);
        RefreshSelection();
    }

    private void OnOptionsChanged()
    {
        OptionsRevision++;
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(FrustumLengthText));
    }

    private void TrackWithCamera(int cameraId)
    {
        if (_selectedEntityId < 0) return;
        int target = _selectedEntityId;
        _sensors.Update(cameraId, c => { c.Mode = CameraMode.TrackEntity; c.TrackEntityId = target; });
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        var e = _last.Entities.FirstOrDefault(x => x.EntityId == _selectedEntityId);
        var own = _last.Ownship;
        if (e is null || own is null)
        {
            SelectedTitle = "";
            SelectedDetail = "";
            SelectedReach = [];
            OnPropertyChanged(nameof(HasSelection));
            return;
        }

        var type = _config.Catalog.Find(e.TypeId);
        double sizeM = type?.Placeholder is { } p ? Math.Max(p.LengthM, p.SpanM) : 10;
        var platform = new PlatformPose(LocalTangentPlane.EnuToNed(own.State.PositionEnu), own.State.NedFromBody);
        var targetNed = LocalTangentPlane.EnuToNed(e.State.PositionEnu);
        double range = (targetNed - platform.PositionNed).Length;
        double dUp = e.State.PositionEnu.Z - own.State.PositionEnu.Z;
        SelectedTitle = $"{e.Name} · {e.TypeName}";
        SelectedDetail = e.IsOwnship ? "Ownship" : $"{FormatRange(range)} · {(dUp >= 0 ? "+" : "")}{dUp:F0} m relative altitude · {sizeM:F0} m class size";

        var reach = new List<CameraReach>();
        if (!e.IsOwnship)
        {
            foreach (var def in _sensors.Cameras)
            {
                var cam0 = CameraPose.Compute(platform, def.Mount, 0, 0);
                var los = targetNed - cam0.PositionNed;
                var (az, el) = GimbalMath.SolveAzEl(def.Mount, platform.NedFromBody.Transpose() * los);
                var l = def.Limits;
                bool inReach = az >= l.AzMinDeg && az <= l.AzMaxDeg && el >= l.ElMinDeg && el <= l.ElMaxDeg;
                var cmd = _sensors.GetCommand(def.Id);
                bool tracking = cmd.Mode == CameraMode.TrackEntity && cmd.TrackEntityId == e.EntityId;
                double px = sizeM / los.Length / (cmd.FovDeg * Math.PI / 180 / def.Height);
                string text = inReach
                    ? $"az {az:F1}°, el {el:F1}° · ~{px:F0} px"
                    : $"out of reach (az {az:F0}°, el {el:F0}°)";
                var color = Static.Cameras.FirstOrDefault(c => c.Id == def.Id)?.Color ?? "#ffffff";
                reach.Add(new CameraReach(def.Id, def.Name, color, inReach, tracking, text));
            }
        }
        SelectedReach = reach;
        OnPropertyChanged(nameof(HasSelection));
    }

    private static string FormatRange(double m) => m >= 1000 ? $"{m / 1000:F1} km" : $"{m:F0} m";

    // ------------------------------------------------------------------ geometry helpers

    private static Scene3DStatic BuildStatic(ISharedConfigService config, ISensorControlService sensors, IReadOnlyDictionary<int, string> colors)
    {
        var types = config.Catalog.Types.Select(t =>
        {
            string ext = string.IsNullOrWhiteSpace(t.Model) ? "" : Path.GetExtension(t.Model).ToLowerInvariant();
            string format = ext switch { ".glb" or ".gltf" => "gltf", ".obj" => "obj", _ => "" };
            return new Scene3DType(
                t.TypeId, t.Name, t.ClassName, format == "" ? null : $"api/models/{t.TypeId}", format,
                t.ModelScale, Pad3(t.ModelRotationDeg), FrameConventions.FrdToGodotLocal(ToVec(t.ModelOffsetFrdM)).ToArray(),
                t.Placeholder.Kind, t.Placeholder.LengthM, t.Placeholder.SpanM, t.Placeholder.HeightM);
        }).ToList();

        var cams = sensors.Cameras.Select(c =>
        {
            var grid = new double[ForAzSteps * ForElSteps * 3];
            for (int i = 0; i < ForAzSteps; i++)
            for (int j = 0; j < ForElSteps; j++)
            {
                double az = c.Limits.AzMinDeg + (c.Limits.AzMaxDeg - c.Limits.AzMinDeg) * i / (ForAzSteps - 1);
                double el = c.Limits.ElMinDeg + (c.Limits.ElMaxDeg - c.Limits.ElMinDeg) * j / (ForElSteps - 1);
                var dirFrd = GimbalMath.BodyFromCamera(c.Mount, az, el).Column0;
                var d = FrameConventions.FrdToGodotLocal(dirFrd);
                int k = (i * ForElSteps + j) * 3;
                grid[k] = d.X; grid[k + 1] = d.Y; grid[k + 2] = d.Z;
            }
            return new Scene3DCamera(
                c.Id, c.Name, colors.TryGetValue(c.Id, out var col) ? col : "#4fc3f7",
                FrameConventions.FrdToGodotLocal(GimbalMath.MountOffsetFrd(c.Mount)).ToArray(),
                (double)c.Width / Math.Max(1, c.Height), grid, ForAzSteps, ForElSteps,
                c.Limits.AzMinDeg, c.Limits.AzMaxDeg, c.Limits.ElMinDeg, c.Limits.ElMaxDeg);
        }).ToList();

        return new Scene3DStatic(config.World.Terrain.SizeM, types, cams);
    }

    /// <summary>Entity orientation as Godot world basis columns (same as the IG's GodotConv.FromNedPose).</summary>
    private static double[] WorldBasis(Mat3d nedFromBody)
    {
        var (x, y, z) = FrameConventions.GodotBasisFromNedFrd(nedFromBody);
        return [x.X, x.Y, x.Z, y.X, y.Y, y.Z, z.X, z.Y, z.Z];
    }

    /// <summary>Camera orientation (frame C: boresight, right, down) as Godot camera-local axes in ownship-local coordinates.</summary>
    private static double[] LocalCameraBasis(Mat3d bodyFromCam)
    {
        var x = FrameConventions.FrdToGodotLocal(bodyFromCam.Column1);   // image right
        var y = FrameConventions.FrdToGodotLocal(-bodyFromCam.Column2);  // image up
        var z = FrameConventions.FrdToGodotLocal(-bodyFromCam.Column0);  // behind the camera (camera looks along -Z)
        return [x.X, x.Y, x.Z, y.X, y.Y, y.Z, z.X, z.Y, z.Z];
    }

    private static Vec3d ToVec(double[]? a) => new(At(a, 0), At(a, 1), At(a, 2));
    private static double[] Pad3(double[]? a) => [At(a, 0), At(a, 1), At(a, 2)];
    private static double At(double[]? a, int i) => a is not null && a.Length > i ? a[i] : 0.0;
}
