using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebAirIG.Common.Geo;
using WebAirIG.Host.Services;

namespace WebAirIG.Host.ViewModels;

public sealed record MapMarker(double X, double Y, double HeadingDeg, string Label, string Color, bool IsOwnship, string Tooltip);
public sealed record MapPolygon(string Points, string Color, double Opacity, bool Selected, double BoresightX, double BoresightY);
public sealed record MapPolyline(string Points, string Color);
public sealed record MapLine(double X1, double Y1, double X2, double Y2, bool Major);
/// <summary>Click position in CSS pixels relative to the map, plus the map's rendered (square) size.</summary>
public sealed record MapClick(double OffsetX, double OffsetY, double RenderedSizePx);

/// <summary>
/// Top-down coverage map (north up) in the world LTP: ownship, targets, trails and camera ground footprints.
/// Produces render-ready SVG coordinates in a 1000x1000 view box. Clicking points the selected camera at the ground.
/// </summary>
public sealed class MapViewModel : ObservableObject
{
    public const double ViewBox = 1000;
    public const int PixelSize = 760;
    private const int TrailLength = 150;

    private readonly ISensorControlService _sensors;
    private readonly Dictionary<int, Queue<Vec3d>> _trails = new();
    private double _halfExtentM = 16_000;
    private bool _followOwnship;
    private int _selectedCameraId;
    private Vec3d _center;
    private int _trailTick;

    public MapViewModel(ISensorControlService sensors)
    {
        _sensors = sensors;
        CameraOptions = sensors.Cameras.Select(c => new EntityOption(c.Id, c.Name)).ToList();
        _selectedCameraId = CameraOptions.FirstOrDefault()?.Id ?? 0;
        ZoomInCommand = new RelayCommand(() => HalfExtentM = Math.Max(500, HalfExtentM / 1.5));
        ZoomOutCommand = new RelayCommand(() => HalfExtentM = Math.Min(200_000, HalfExtentM * 1.5));
        ToggleFollowCommand = new RelayCommand(() => FollowOwnship = !FollowOwnship);
        ClickCommand = new RelayCommand<MapClick>(OnClick);
    }

    public IReadOnlyList<EntityOption> CameraOptions { get; }
    public IReadOnlyList<MapMarker> Markers { get; private set; } = [];
    public IReadOnlyList<MapPolygon> Footprints { get; private set; } = [];
    public IReadOnlyList<MapPolyline> Trails { get; private set; } = [];
    public IReadOnlyList<MapLine> Grid { get; private set; } = [];
    public string ScaleText { get; private set; } = "";
    public string CursorHint => $"Click map to point {CameraOptions.FirstOrDefault(c => c.Id == SelectedCameraId)?.Label} at the ground";

    public double HalfExtentM { get => _halfExtentM; set => SetProperty(ref _halfExtentM, value); }
    public bool FollowOwnship { get => _followOwnship; set => SetProperty(ref _followOwnship, value); }
    public int SelectedCameraId { get => _selectedCameraId; set { SetProperty(ref _selectedCameraId, value); OnPropertyChanged(nameof(CursorHint)); } }

    public IRelayCommand ZoomInCommand { get; }
    public IRelayCommand ZoomOutCommand { get; }
    public IRelayCommand ToggleFollowCommand { get; }
    public IRelayCommand<MapClick> ClickCommand { get; }

    private double Sx(double east) => ViewBox / 2 + (east - _center.X) / _halfExtentM * (ViewBox / 2);
    private double Sy(double north) => ViewBox / 2 - (north - _center.Y) / _halfExtentM * (ViewBox / 2);

    public void Update(SimSnapshot snap, IReadOnlyList<CameraViewModel> cameras)
    {
        var own = snap.Ownship;
        _center = FollowOwnship && own is not null ? own.State.PositionEnu with { Z = 0 } : Vec3d.Zero;

        if (++_trailTick % 2 == 0)
        {
            foreach (var e in snap.Entities)
            {
                if (!_trails.TryGetValue(e.EntityId, out var q)) _trails[e.EntityId] = q = new Queue<Vec3d>();
                q.Enqueue(e.State.PositionEnu);
                while (q.Count > TrailLength) q.Dequeue();
            }
            foreach (var id in _trails.Keys.Where(id => snap.Entities.All(e => e.EntityId != id)).ToList()) _trails.Remove(id);
        }

        Markers = snap.Entities.Select(e => new MapMarker(
            Sx(e.State.PositionEnu.X), Sy(e.State.PositionEnu.Y), e.State.HeadingDeg, e.Name,
            e.IsOwnship ? "#ffffff" : ForceColor(e.Force), e.IsOwnship,
            $"{e.Name} ({e.TypeName}) alt {e.State.PositionEnu.Z:F0} m, hdg {e.State.HeadingDeg:F0}, {e.State.VelocityEnu.Length:F0} m/s")).ToList();

        Trails = _trails.Select(kv => new MapPolyline(
            Points(kv.Value.Select(p => (Sx(p.X), Sy(p.Y)))),
            kv.Key == 0 ? "#ffffff" : "#90a4ae")).ToList();

        Footprints = snap.Cameras.Select(c =>
        {
            var color = cameras.FirstOrDefault(v => v.Id == c.Id)?.Color ?? "#4fc3f7";
            var pts = c.FootprintEnu.Take(4).Select(p => (Sx(p.X), Sy(p.Y)));
            var b = c.FootprintEnu[4];
            return new MapPolygon(Points(pts), color, c.Command.SensorOn ? 0.22 : 0.05, c.Id == SelectedCameraId, Sx(b.X), Sy(b.Y));
        }).ToList();

        Grid = BuildGrid(out var spacing);
        ScaleText = $"grid {spacing / 1000:0.#} km, view {2 * _halfExtentM / 1000:0.#} km";
        OnPropertyChanged(string.Empty);
    }

    private List<MapLine> BuildGrid(out double spacing)
    {
        double[] nice = [500, 1000, 2000, 5000, 10_000, 20_000, 50_000];
        spacing = nice.FirstOrDefault(s => 2 * _halfExtentM / s <= 12, 100_000);
        var lines = new List<MapLine>();
        double minE = _center.X - _halfExtentM, maxE = _center.X + _halfExtentM;
        double minN = _center.Y - _halfExtentM, maxN = _center.Y + _halfExtentM;
        for (double e = Math.Ceiling(minE / spacing) * spacing; e <= maxE; e += spacing)
            lines.Add(new MapLine(Sx(e), 0, Sx(e), ViewBox, Math.Abs(e) < 1e-6));
        for (double n = Math.Ceiling(minN / spacing) * spacing; n <= maxN; n += spacing)
            lines.Add(new MapLine(0, Sy(n), ViewBox, Sy(n), Math.Abs(n) < 1e-6));
        return lines;
    }

    private void OnClick(MapClick? click)
    {
        if (click is null) return;
        double px = click.RenderedSizePx > 1 ? click.RenderedSizePx : PixelSize;
        double vx = click.OffsetX * ViewBox / px, vy = click.OffsetY * ViewBox / px;
        double east = _center.X + (vx - ViewBox / 2) / (ViewBox / 2) * _halfExtentM;
        double north = _center.Y - (vy - ViewBox / 2) / (ViewBox / 2) * _halfExtentM;
        _sensors.Update(SelectedCameraId, c => { c.Mode = CameraMode.GeoPoint; c.GeoPointEnu = new Vec3d(east, north, 0); });
    }

    internal static string ForceColor(byte force) => force switch
    {
        1 => "#4fc3f7",
        2 => "#ef5350",
        3 => "#66bb6a",
        _ => "#ffee58",
    };

    private static string Points(IEnumerable<(double X, double Y)> pts)
    {
        var sb = new StringBuilder();
        foreach (var (x, y) in pts)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(x.ToString("F1", CultureInfo.InvariantCulture)).Append(',').Append(y.ToString("F1", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
