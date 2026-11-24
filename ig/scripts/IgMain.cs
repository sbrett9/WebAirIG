using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using WebAirIG.Common.Config;
using WebAirIG.Common.Geo;
using WebAirIG.Common.Truth;
using WebAirIG.IG.Cigi;
using WebAirIG.IG.Core;
using WebAirIG.IG.Entities;
using WebAirIG.IG.Sensors;
using WebAirIG.IG.Streaming;
using WebAirIG.IG.Ui;
using WebAirIG.IG.World;

namespace WebAirIG.IG;

/// <summary>
/// WebAirIG image generator root. Per frame:
///   1. CIGI: send Start of Frame, wait for the host's state for this frame (sync mode)
///   2. update entity nodes and the 4 gimbaled sensor cameras from that state
///   3. build the truth sidecar for every camera from the same state
///   4. after the frame is drawn, read each sensor's output back asynchronously and hand it to NVENC with its truth
///
/// Command line (after "--"): --config path, --no-stream, --snapshot-frame N, --snapshot-dir dir,
/// --quit-after-snapshot, --quit-after N
/// </summary>
public partial class IgMain : Node3D
{
    private IgConfig _cfg = new();
    private LocalTangentPlane _ltp = null!;
    private readonly IgState _state = new();
    private CigiIgLink? _cigi;
    private EntityManager _entities = null!;
    private ModelLibrary? _models;
    private readonly List<SensorChannel> _channels = new();
    private TruthBuilder _truth = null!;
    private StreamService? _stream;
    private TruthLogWriter? _truthLog;
    private IgHud _hud = null!;
    private RenderingDevice? _rd;
    private uint _session;
    private ulong _frame;
    private double _fpsEma;
    private readonly Dictionary<ulong, FrameTruth[]> _pendingCapture = new();

    private ulong _snapshotFrame = ulong.MaxValue;
    private string? _snapshotDir;
    private bool _quitAfterSnapshot;
    private int _snapshotsSaved;
    private ulong _quitAfterFrames = ulong.MaxValue;
    private bool _readbackErrorLogged;

    public override void _Ready()
    {
        var args = ParseArgs(OS.GetCmdlineUserArgs());
        var configPath = args.TryGetValue("config", out var c) ? Path.GetFullPath(c) : ProjectSettings.GlobalizePath("res://config/ig.json");
        _cfg = JsonConfig.LoadOrDefault<IgConfig>(configPath);
        var sharedDir = JsonConfig.Resolve(Path.GetDirectoryName(configPath)!, _cfg.SharedConfigDir);
        var repoRoot = Path.GetFullPath(Path.Combine(sharedDir, ".."));
        var world = JsonConfig.LoadOrDefault<WorldConfig>(Path.Combine(sharedDir, "world.json"));
        var catalog = JsonConfig.LoadOrDefault<EntityCatalog>(Path.Combine(sharedDir, "entity_catalog.json"));
        var sensors = JsonConfig.LoadOrDefault<SensorsConfig>(Path.Combine(sharedDir, "sensors.json"));
        _ltp = world.CreateTangentPlane();
        _session = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        IgLog.Info($"Config {configPath}; shared {sharedDir}; origin {_ltp.Origin}; session {_session:X8}");

        if (args.TryGetValue("snapshot-frame", out var sf) && ulong.TryParse(sf, out var sfn)) _snapshotFrame = sfn;
        // Godot runs with the project folder (ig/) as working directory, so resolve relative paths from the repo root.
        if (args.TryGetValue("snapshot-dir", out var sd)) _snapshotDir = JsonConfig.Resolve(repoRoot, sd);
        _quitAfterSnapshot = args.ContainsKey("quit-after-snapshot");
        if (args.TryGetValue("quit-after", out var qa) && ulong.TryParse(qa, out var qan)) _quitAfterFrames = qan;

        int fps = Math.Clamp(_cfg.Render.Fps, 1, 240);
        Engine.MaxFps = fps;
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

        WorldBuilder.Build(this, world);
        var irShader = GD.Load<Shader>("res://shaders/ir_object.gdshader");
        var postShader = GD.Load<Shader>("res://shaders/sensor_post.gdshader");
        _models = new ModelLibrary(catalog, sharedDir, irShader, _cfg.Render.MaxSamplePoints);
        _entities = new EntityManager(this, _models);

        var sensorsNode = new Node { Name = "Sensors" };
        AddChild(sensorsNode);
        foreach (var cam in sensors.Cameras)
        {
            var view = new IgViewState
            {
                ViewId = cam.Id,
                EntityId = 0,
                Mount = new MountDef
                {
                    XFwdM = cam.Mount.XFwdM, YRightM = cam.Mount.YRightM, ZDownM = cam.Mount.ZDownM,
                    YawDeg = cam.Mount.YawDeg, PitchDeg = cam.Mount.PitchDeg, RollDeg = cam.Mount.RollDeg,
                },
                VfovDeg = cam.FovDeg,
                NearM = cam.NearM,
                FarM = cam.FarM,
            };
            _state.Views[cam.Id] = view;
            _channels.Add(new SensorChannel(sensorsNode, cam, view, postShader, _cfg.Render.Msaa));
        }

        _hud = new IgHud { Name = "Hud" };
        AddChild(_hud);
        _hud.Build(_channels);

        _cigi = new CigiIgLink(_cfg.Cigi, _state, _ltp);
        _truth = new TruthBuilder(_ltp, _session);
        if (_cfg.Truth.LogToDisk)
            _truthLog = new TruthLogWriter(JsonConfig.Resolve(Path.GetDirectoryName(configPath)!, _cfg.Truth.LogDir), _session);
        if (!args.ContainsKey("no-stream"))
            _stream = new StreamService(_cfg.Stream, _cfg.Truth, sensors.Cameras, _session, repoRoot, fps);

        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null) IgLog.Warn("No RenderingDevice (Compatibility renderer?) - using synchronous readback");
        RenderingServer.FramePostDraw += OnFramePostDraw;
        IgLog.Info($"Ready: {_channels.Count} sensors at {fps} Hz, {sensors.Cameras.FirstOrDefault()?.Width}x{sensors.Cameras.FirstOrDefault()?.Height}");
    }

    public override void _Process(double delta)
    {
        int fps = Math.Clamp(_cfg.Render.Fps, 1, 240);
        _frame++;
        if (delta > 0) _fpsEma = _fpsEma <= 0 ? 1 / delta : _fpsEma * 0.9 + 0.1 / delta;

        _cigi?.BeginFrame((uint)_frame, _frame / (double)fps, delta > 1.5 / fps);
        _entities.Sync(_state);

        var utc = DateTime.UtcNow;
        var truths = new FrameTruth[_channels.Count];
        for (int i = 0; i < _channels.Count; i++)
        {
            var ch = _channels[i];
            ch.Update(_state, _frame);
            var t = _truth.Build(ch, _state, _entities, _frame, utc);
            ch.LatestTruth = t;
            truths[i] = t;
            _truthLog?.Enqueue(t);
        }
        if (ShouldCapture(_frame)) _pendingCapture[_frame] = truths;

        if (_frame % 3 == 0) _hud.UpdateStatus(StatusText());
        if (_frame >= _quitAfterFrames) GetTree().Quit();
    }

    private bool ShouldCapture(ulong frame) =>
        frame == _snapshotFrame ||
        (_stream?.Active == true && frame % (ulong)Math.Max(1, _cfg.Stream.EveryNFrames) == 0);

    /// <summary>Runs after the frame's viewports were drawn: queue async GPU readbacks of every sensor output.</summary>
    private void OnFramePostDraw()
    {
        ulong frame = _frame;
        if (!_pendingCapture.Remove(frame, out var truths)) return;
        foreach (var stale in _pendingCapture.Keys.Where(k => k < frame).ToList()) _pendingCapture.Remove(stale);

        for (int i = 0; i < _channels.Count; i++)
        {
            var ch = _channels[i];
            var truth = truths[i];
            if (ch.ReadbacksInFlight >= 3 && frame != _snapshotFrame) continue; // GPU readback backlog: drop
            ch.ReadbacksInFlight++;
            if (_rd is not null)
            {
                var rid = RenderingServer.TextureGetRdTexture(ch.PostViewport.GetTexture().GetRid());
                var err = _rd.TextureGetDataAsync(rid, 0, Callable.From<byte[]>(data => OnReadback(ch, frame, truth, data)));
                if (err != Error.Ok)
                {
                    ch.ReadbacksInFlight--;
                    if (!_readbackErrorLogged) IgLog.Error($"TextureGetDataAsync failed: {err}");
                    _readbackErrorLogged = true;
                }
            }
            else
            {
                var img = ch.PostViewport.GetTexture().GetImage();
                if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
                OnReadback(ch, frame, truth, img.GetData());
            }
        }
    }

    private void OnReadback(SensorChannel ch, ulong frame, FrameTruth truth, byte[] rgba)
    {
        ch.ReadbacksInFlight--;
        int expected = ch.Def.Width * ch.Def.Height * 4;
        if (rgba.Length < expected)
        {
            if (!_readbackErrorLogged) IgLog.Error($"Readback size {rgba.Length} != expected {expected} (format?)");
            _readbackErrorLogged = true;
            return;
        }

        if (frame == _snapshotFrame && _snapshotDir is not null) SaveSnapshot(ch, frame, truth, rgba);

        if (_stream?.Active == true && frame % (ulong)Math.Max(1, _cfg.Stream.EveryNFrames) == 0)
        {
            _stream.Submit(ch.Def.Id, rgba, new FrameMeta(ch.Def.Id, frame, truth.SimTime, truth.UtcTime.Ticks, ch.Def.Width, ch.Def.Height, truth));
        }
    }

    private void SaveSnapshot(SensorChannel ch, ulong frame, FrameTruth truth, byte[] rgba)
    {
        Directory.CreateDirectory(_snapshotDir!);
        var stem = Path.Combine(_snapshotDir!, $"frame{frame:D6}_cam{ch.Def.Id}_{ch.Def.Name}");
        var img = Image.CreateFromData(ch.Def.Width, ch.Def.Height, false, Image.Format.Rgba8, rgba);
        img.SavePng(stem + ".png");
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(truth, JsonConfig.Options));

        // Annotated copy with truth boxes (green = target, white = ownship) for quick visual checks.
        var annotated = (Image)img.Duplicate();
        foreach (var o in truth.Objects)
        {
            if (o.Bbox.Length < 4) continue;
            var color = o.IsOwnship ? Colors.White : new Color(0.1f, 1f, 0.1f);
            int x1 = (int)o.Bbox[0], y1 = (int)o.Bbox[1], x2 = Math.Max(x1 + 1, (int)o.Bbox[2]), y2 = Math.Max(y1 + 1, (int)o.Bbox[3]);
            const int pad = 3, w = 3;
            x1 = Math.Max(0, x1 - pad); y1 = Math.Max(0, y1 - pad);
            x2 = Math.Min(ch.Def.Width - 1, x2 + pad); y2 = Math.Min(ch.Def.Height - 1, y2 + pad);
            annotated.FillRect(new Rect2I(x1, y1, x2 - x1, w), color);
            annotated.FillRect(new Rect2I(x1, Math.Max(y1, y2 - w), x2 - x1, w), color);
            annotated.FillRect(new Rect2I(x1, y1, w, y2 - y1), color);
            annotated.FillRect(new Rect2I(Math.Max(x1, x2 - w), y1, w, y2 - y1), color);
        }
        annotated.SavePng(stem + "_truth.png");
        IgLog.Info($"Snapshot {stem}.png ({truth.Objects.Count} truth objects)");
        if (++_snapshotsSaved >= _channels.Count && _quitAfterSnapshot) GetTree().Quit();
    }

    private string StatusText()
    {
        var host = _cigi is { HostAlive: true } ? $"host OK (wait {_cigi.LastWaitMs:F1} ms)" : "host --";
        string stream = "stream off";
        if (_stream is not null)
        {
            var (sub, enc, drop) = _stream.EncoderStats();
            var tx = _stream.TransportStats;
            stream = _stream.Active
                ? $"{_stream.Status}  enc {enc}/{sub} drop {drop}  tx {tx?.MessagesSent ?? 0} ({(tx?.BytesSent ?? 0) / 1_000_000.0:F0} MB){(tx is { Connected: false } ? " [not connected]" : "")}"
                : _stream.Status;
        }
        return $" WebAirIG IG  frame {_frame}  {_fpsEma:F1} fps  {host}  sim {_state.HostSimTime:F2}s  entities {_entities.Count}  |  {stream}  |  [T] truth  [1-4] solo  [0] grid";
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            var eq = key.IndexOf('=');
            if (eq > 0) { d[key[..eq]] = key[(eq + 1)..]; continue; }
            d[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        }
        return d;
    }

    public override void _ExitTree()
    {
        RenderingServer.FramePostDraw -= OnFramePostDraw;
        _stream?.Dispose();
        _truthLog?.Dispose();
        _cigi?.Dispose();
        _models?.FreeAll();
    }
}
