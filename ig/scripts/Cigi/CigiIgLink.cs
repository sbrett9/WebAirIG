using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WebAirIG.Cigi;
using WebAirIG.Common.Geo;
using WebAirIG.IG.Core;

namespace WebAirIG.IG.Cigi;

/// <summary>
/// IG side of the CIGI 4.0 link. Each frame: send Start of Frame, then (sync mode) wait for the host message that
/// acknowledges this frame, applying every packet to <see cref="IgState"/>. Runs entirely on the Godot main thread.
/// </summary>
public sealed class CigiIgLink : CigiPacketHandler, IDisposable
{
    private readonly CigiSettings _cfg;
    private readonly IgState _state;
    private readonly LocalTangentPlane _ltp;
    private readonly Socket _socket;
    private readonly EndPoint _host;
    private readonly CigiMessageBuilder _builder;
    private readonly byte[] _buf = new byte[65536];
    private uint _waitingForFrame;
    private bool _replied;
    private int _consecutiveTimeouts;

    public bool HostAlive { get; private set; }
    public long MessagesReceived { get; private set; }
    public long SyncTimeouts { get; private set; }
    public double LastWaitMs { get; private set; }

    public CigiIgLink(CigiSettings cfg, IgState state, LocalTangentPlane ltp)
    {
        _cfg = cfg;
        _state = state;
        _ltp = ltp;
        _builder = new CigiMessageBuilder { BigEndian = cfg.BigEndian };
        _host = new IPEndPoint(IPAddress.Parse(cfg.HostAddress), cfg.HostPort);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveBufferSize = 4 * 1024 * 1024 };
        if (OperatingSystem.IsWindows())
        {
            try { _socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch { /* SIO_UDP_CONNRESET off */ }
        }
        _socket.Bind(new IPEndPoint(IPAddress.Any, cfg.ListenPort));
        IgLog.Info($"CIGI: listening on :{cfg.ListenPort}, host {cfg.HostAddress}:{cfg.HostPort}, sync={cfg.SyncMode}");
    }

    /// <summary>Sends SOF for <paramref name="frame"/> and absorbs the host's response.</summary>
    public void BeginFrame(uint frame, double igTimeSeconds, bool overframing)
    {
        _builder.Reset();
        _builder.Add(new StartOfFramePacket
        {
            Mode = IgMode.Operate,
            IgFrameNumber = frame,
            Timestamp = CigiMessageParser.ToTimestamp(igTimeSeconds),
            TimestampValid = true,
            LastHostFrameNumber = _state.LastHostFrame,
            Overframing = overframing,
        });
        try { _socket.SendTo(_builder.Span, _host); }
        catch (SocketException) { /* host not up yet */ }

        _waitingForFrame = frame;
        _replied = false;
        var sw = Stopwatch.StartNew();
        ReceiveAvailable();

        if (_cfg.SyncMode && HostAlive)
        {
            while (!_replied)
            {
                double remainingMs = _cfg.SyncTimeoutMs - sw.Elapsed.TotalMilliseconds;
                if (remainingMs <= 0) break;
                if (_socket.Poll((int)(remainingMs * 1000), SelectMode.SelectRead)) ReceiveAvailable();
            }
            if (_replied) _consecutiveTimeouts = 0;
            else
            {
                SyncTimeouts++;
                if (++_consecutiveTimeouts >= 15)
                {
                    HostAlive = false;
                    IgLog.Warn("CIGI: host stopped responding; running unsynchronized until it returns");
                }
            }
        }
        LastWaitMs = sw.Elapsed.TotalMilliseconds;
    }

    private void ReceiveAvailable()
    {
        while (_socket.Available > 0)
        {
            int n;
            try { n = _socket.Receive(_buf); }
            catch (SocketException) { return; }
            if (n <= 0) return;
            if (CigiMessageParser.MajorVersion(_buf.AsSpan(0, n)) != 4) continue;
            if (!HostAlive) IgLog.Info("CIGI: host connected");
            HostAlive = true;
            MessagesReceived++;
            CigiMessageParser.Parse(_buf.AsSpan(0, n), this);
        }
    }

    public override void OnIgControl(in IgControlPacket p)
    {
        _state.LastHostFrame = p.HostFrameNumber;
        _state.HostMode = p.Mode;
        if (p.TimestampValid)
        {
            _state.HostSimTime = CigiMessageParser.FromTimestamp(p.Timestamp);
            _state.HostTimeValid = true;
        }
        if (p.LastIgFrameNumber == _waitingForFrame) _replied = true;
    }

    public override void OnEntityControl(in EntityControlPacket p)
    {
        var e = _state.GetOrCreateEntity(p.EntityId);
        switch (p.State)
        {
            case EntityState.Active:
                int type = p.ExtendedEntityType ? -1 : p.CountryOrShortType;
                if (!e.Active || e.TypeId != type) _state.EntityRevision++;
                e.Active = true;
                e.TypeId = type;
                break;
            case EntityState.Remove:
                if (_state.Entities.Remove(p.EntityId)) _state.EntityRevision++;
                break;
            default:
                if (e.Active) _state.EntityRevision++;
                e.Active = false;
                break;
        }
    }

    public override void OnEntityPosition(in EntityPositionPacket p)
    {
        if (!_state.Entities.TryGetValue(p.EntityId, out var e) || p.Attached) return; // attached entities not used yet
        var geo = new GeoPoint(p.LatOrX, p.LonOrY, p.AltOrZ);
        var posNed = LocalTangentPlane.EnuToNed(_ltp.GeodeticToEnu(geo));
        double t = _state.HostSimTime;
        if (e.HasPosition && !double.IsNaN(e.PositionTime) && t > e.PositionTime)
            e.VelocityLtpNed = (posNed - e.PositionNed) / (t - e.PositionTime);
        else if (!e.HasPosition)
            e.VelocityLtpNed = Vec3d.Zero;
        if (!e.HasPosition || t != e.PositionTime) e.PositionTime = t;

        e.Geo = geo;
        e.YawDeg = p.YawDeg;
        e.PitchDeg = p.PitchDeg;
        e.RollDeg = p.RollDeg;
        e.PositionNed = posNed;
        e.NedFromBody = _ltp.LtpNedFromBody(geo, p.YawDeg, p.PitchDeg, p.RollDeg);
        e.HasPosition = true;
    }

    public override void OnArticulatedPartControl(in ArticulatedPartControlPacket p)
    {
        if (!_state.Entities.TryGetValue(p.EntityId, out var e)) return;
        e.Parts.TryGetValue(p.PartId, out var cur);
        if (p.YawEnable) cur.Yaw = p.YawDeg;
        if (p.PitchEnable) cur.Pitch = p.PitchDeg;
        e.Parts[p.PartId] = cur;
    }

    public override void OnViewControl(in ViewControlPacket p)
    {
        if (!_state.Views.TryGetValue(p.ViewId, out var v)) return;
        v.EntityId = p.EntityId;
        if (p.XEnable) v.Mount.XFwdM = p.X;
        if (p.YEnable) v.Mount.YRightM = p.Y;
        if (p.ZEnable) v.Mount.ZDownM = p.Z;
        if (p.RollEnable) v.Mount.RollDeg = p.RollDeg;
        if (p.PitchEnable) v.Mount.PitchDeg = p.PitchDeg;
        if (p.YawEnable) v.Mount.YawDeg = p.YawDeg;
    }

    public override void OnViewDefinition(in ViewDefinitionPacket p)
    {
        if (!_state.Views.TryGetValue(p.ViewId, out var v)) return;
        if (p.TopEnable && p.BottomEnable && p.TopDeg > p.BottomDeg) v.VfovDeg = p.TopDeg - p.BottomDeg;
        if (p.NearEnable && p.Near > 0) v.NearM = p.Near;
        if (p.FarEnable && p.Far > v.NearM) v.FarM = p.Far;
    }

    public override void OnSensorControl(in SensorControlPacket p)
    {
        if (!_state.Views.TryGetValue(p.ViewId, out var v)) return;
        v.SensorOn = p.SensorOn;
        v.BlackHot = p.BlackHot;
        v.Gain = p.Gain;
        v.Level = p.Level;
        v.Noise = p.Noise;
    }

    public void Dispose() => _socket.Dispose();
}
