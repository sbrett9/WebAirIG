using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using WebAirIG.Cigi;
using WebAirIG.Common.Config;
using WebAirIG.Host.Options;

namespace WebAirIG.Host.Services;

public interface IIgLinkStatus
{
    bool IgConnected { get; }
    uint LastIgFrame { get; }
    uint HostFrame { get; }
    double IgFrameRateHz { get; }
    long SofReceived { get; }
    long MessagesSent { get; }
    long OverframeCount { get; }
    string Endpoint { get; }
}

/// <summary>
/// CIGI 4.0 Host. While an IG is sending Start of Frame the simulation is frame-locked to it: each SOF advances sim
/// time by the IG's timestamp delta and is answered immediately with that frame's state (IG Control, Entity Control,
/// Entity Position, Articulated Part Control for gimbals, plus periodic View Control / View Definition / Sensor Control).
/// Without an IG the simulation free-runs at <see cref="CigiOptions.FreeRunRateHz"/>.
/// </summary>
public sealed class CigiHostService : BackgroundService, IIgLinkStatus
{
    private readonly CigiOptions _opt;
    private readonly ISimulationEngine _engine;
    private readonly ISensorControlService _sensors;
    private readonly ISharedConfigService _config;
    private readonly ILogger<CigiHostService> _log;
    private readonly CigiMessageBuilder _builder;
    private readonly HashSet<int> _knownEntities = [];
    private readonly Dictionary<int, int> _pendingRemovals = new();

    private Socket? _socket;
    private IPEndPoint? _igEndpoint;
    private long _lastSofTicks;
    private uint _lastIgTimestamp;
    private bool _haveIgTimestamp;
    private uint _hostFrame;
    private double _igRateEma;
    private int _lastScenarioRevision = -1;
    private int _lastSensorRevision = -1;

    public bool IgConnected => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSofTicks)).TotalSeconds < _opt.IgTimeoutSeconds && _lastSofTicks != 0;
    public uint LastIgFrame { get; private set; }
    public uint HostFrame => _hostFrame;
    public double IgFrameRateHz => IgConnected ? _igRateEma : 0;
    public long SofReceived { get; private set; }
    public long MessagesSent { get; private set; }
    public long OverframeCount { get; private set; }
    public string Endpoint => $"IG {_opt.IgAddress}:{_opt.IgPort}, host listens :{_opt.HostPort}";

    public CigiHostService(IOptions<WebAirIgOptions> options, ISimulationEngine engine, ISensorControlService sensors,
        ISharedConfigService config, ILogger<CigiHostService> log)
    {
        _opt = options.Value.Cigi;
        _engine = engine;
        _sensors = sensors;
        _config = config;
        _log = log;
        _builder = new CigiMessageBuilder { BigEndian = _opt.BigEndian };
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Run(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        _igEndpoint = new IPEndPoint(IPAddress.Parse(_opt.IgAddress), _opt.IgPort);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            try { _socket.IOControl(-1744830452, [0, 0, 0, 0], null); } catch { /* SIO_UDP_CONNRESET */ }
        }
        _socket.Bind(new IPEndPoint(IPAddress.Any, _opt.HostPort));
        _log.LogInformation("CIGI host: {Endpoint}", Endpoint);

        var buf = new byte[65536];
        var handler = new SofHandler();
        double freeRunPeriod = 1.0 / Math.Max(1, _opt.FreeRunRateHz);
        long lastFreeRunTicks = Stopwatch.GetTimestamp();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool igAlive = IgConnected;
                double untilTick = freeRunPeriod - Stopwatch.GetElapsedTime(lastFreeRunTicks).TotalSeconds;
                int waitUs = igAlive ? 50_000 : (int)Math.Clamp(untilTick * 1e6, 0, 50_000);

                if (_socket.Poll(waitUs, SelectMode.SelectRead))
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = _socket.ReceiveFrom(buf, ref from);
                    handler.Reset();
                    CigiMessageParser.Parse(buf.AsSpan(0, n), handler);
                    if (handler.Sof is { } sof) OnStartOfFrame(sof);
                    continue;
                }

                if (!IgConnected && Stopwatch.GetElapsedTime(lastFreeRunTicks).TotalSeconds >= freeRunPeriod)
                {
                    double dt = Math.Min(Stopwatch.GetElapsedTime(lastFreeRunTicks).TotalSeconds, 0.25);
                    lastFreeRunTicks = Stopwatch.GetTimestamp();
                    _haveIgTimestamp = false;
                    var snap = _engine.Step(dt);
                    Send(snap);
                }
            }
            catch (SocketException ex)
            {
                _log.LogDebug("CIGI socket: {Error}", ex.SocketErrorCode);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "CIGI host loop error");
                Thread.Sleep(100);
            }
        }
        _socket.Dispose();
    }

    private void OnStartOfFrame(StartOfFramePacket sof)
    {
        long now = Stopwatch.GetTimestamp();
        long prev = Interlocked.Exchange(ref _lastSofTicks, now);
        if (prev != 0)
        {
            double wall = Stopwatch.GetElapsedTime(prev, now).TotalSeconds;
            if (wall > 1e-4 && wall < 1) _igRateEma = _igRateEma <= 0 ? 1 / wall : _igRateEma * 0.95 + 0.05 / wall;
        }
        SofReceived++;
        if (sof.Overframing) OverframeCount++;
        LastIgFrame = sof.IgFrameNumber;

        double dt = 0;
        if (sof.TimestampValid && _haveIgTimestamp)
        {
            dt = CigiMessageParser.FromTimestamp(unchecked(sof.Timestamp - _lastIgTimestamp));
            if (dt is < 0 or > 0.5) dt = 0; // IG restart or wrap: hold time for one frame
        }
        _lastIgTimestamp = sof.Timestamp;
        _haveIgTimestamp = sof.TimestampValid;

        var snap = _engine.Step(dt);
        Send(snap);
    }

    private void Send(SimSnapshot snap)
    {
        _hostFrame++;
        _builder.Reset();
        _builder.Add(new IgControlPacket
        {
            Mode = IgMode.Operate,
            HostFrameNumber = _hostFrame,
            LastIgFrameNumber = LastIgFrame,
            Timestamp = CigiMessageParser.ToTimestamp(snap.SimTime),
            TimestampValid = true,
        });

        int scenarioRev = _engine.ScenarioRevision, sensorRev = _sensors.Revision;
        bool periodic = _hostFrame % (uint)Math.Max(1, _opt.PeriodicResendFrames) == 1
                        || scenarioRev != _lastScenarioRevision || sensorRev != _lastSensorRevision;
        _lastScenarioRevision = scenarioRev;
        _lastSensorRevision = sensorRev;

        // Entity lifecycle.
        var current = snap.Entities.Select(e => e.EntityId).ToHashSet();
        foreach (var id in _knownEntities.Where(id => !current.Contains(id)).ToList())
        {
            _knownEntities.Remove(id);
            _pendingRemovals[id] = 3;
        }
        foreach (var e in snap.Entities)
        {
            bool isNew = _knownEntities.Add(e.EntityId);
            _pendingRemovals.Remove(e.EntityId);
            if (isNew || periodic)
            {
                _builder.Add(new EntityControlPacket
                {
                    EntityId = (ushort)e.EntityId,
                    State = EntityState.Active,
                    Alpha = 255,
                    ExtendedEntityType = false,
                    CountryOrShortType = (ushort)e.TypeId,
                });
            }
        }
        foreach (var id in _pendingRemovals.Keys.ToList())
        {
            _builder.Add(new EntityControlPacket { EntityId = (ushort)id, State = EntityState.Remove });
            if (--_pendingRemovals[id] <= 0) _pendingRemovals.Remove(id);
        }

        foreach (var e in snap.Entities)
        {
            _builder.Add(new EntityPositionPacket
            {
                EntityId = (ushort)e.EntityId,
                LatOrX = e.Geo.LatDeg,
                LonOrY = e.Geo.LonDeg,
                AltOrZ = e.Geo.AltM,
                YawDeg = (float)e.YawDeg,
                PitchDeg = (float)e.PitchDeg,
                RollDeg = (float)e.RollDeg,
            });
        }

        // Gimbals: articulated parts on ownship (entity 0), part ID = 100 + view ID.
        foreach (var cam in snap.Cameras)
        {
            _builder.Add(new ArticulatedPartControlPacket
            {
                EntityId = 0,
                PartId = (byte)(CameraDef.GimbalPartBaseId + cam.Id),
                PartEnable = true,
                YawEnable = true,
                PitchEnable = true,
                YawDeg = (float)cam.AzDeg,
                PitchDeg = (float)cam.ElDeg,
            });
        }

        if (periodic)
        {
            foreach (var def in _sensors.Cameras)
            {
                var cmd = snap.Cameras.FirstOrDefault(c => c.Id == def.Id)?.Command ?? _sensors.GetCommand(def.Id);
                _builder.Add(new ViewControlPacket
                {
                    ViewId = (ushort)def.Id,
                    EntityId = 0,
                    XEnable = true, YEnable = true, ZEnable = true, RollEnable = true, PitchEnable = true, YawEnable = true,
                    X = (float)def.Mount.XFwdM, Y = (float)def.Mount.YRightM, Z = (float)def.Mount.ZDownM,
                    RollDeg = (float)def.Mount.RollDeg, PitchDeg = (float)def.Mount.PitchDeg, YawDeg = (float)def.Mount.YawDeg,
                });
                double half = cmd.FovDeg / 2;
                double halfH = Math.Atan(Math.Tan(half * Math.PI / 180) * def.Width / def.Height) * 180 / Math.PI;
                _builder.Add(new ViewDefinitionPacket
                {
                    ViewId = (ushort)def.Id,
                    NearEnable = true, FarEnable = true, LeftEnable = true, RightEnable = true, TopEnable = true, BottomEnable = true,
                    Near = (float)def.NearM, Far = (float)def.FarM,
                    LeftDeg = (float)-halfH, RightDeg = (float)halfH, TopDeg = (float)half, BottomDeg = (float)-half,
                });
                _builder.Add(new SensorControlPacket
                {
                    ViewId = (ushort)def.Id,
                    SensorId = (byte)def.Id,
                    SensorOn = cmd.SensorOn,
                    BlackHot = cmd.Polarity == IrPolarity.BlackHot,
                    Gain = (float)cmd.Gain,
                    Level = (float)cmd.Level,
                    Noise = (float)cmd.Noise,
                });
            }
        }

        _socket!.SendTo(_builder.Span, _igEndpoint!);
        MessagesSent++;
    }

    private sealed class SofHandler : CigiPacketHandler
    {
        public StartOfFramePacket? Sof;
        public void Reset() => Sof = null;
        public override void OnStartOfFrame(in StartOfFramePacket p) => Sof = p;
    }
}
