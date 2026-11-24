using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WebAirIG.Common.Transport;

public sealed class TransportStats
{
    private long _messagesSent, _bytesSent, _messagesDropped, _errors;
    public long MessagesSent => Interlocked.Read(ref _messagesSent);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long MessagesDropped => Interlocked.Read(ref _messagesDropped);
    public long Errors => Interlocked.Read(ref _errors);
    public volatile bool Connected;

    internal void AddSent(long bytes) { Interlocked.Increment(ref _messagesSent); Interlocked.Add(ref _bytesSent, bytes); }
    internal void AddDropped() => Interlocked.Increment(ref _messagesDropped);
    internal void AddError() => Interlocked.Increment(ref _errors);
}

public interface IFrameSender : IDisposable
{
    /// <summary>Queues a message without blocking. Returns false if it was dropped (queue full / not connected).</summary>
    bool TrySend(FrameMessage message);
    TransportStats Stats { get; }
    string Description { get; }
}

public static class FrameSenderFactory
{
    public static IFrameSender Create(string protocol, string host, int port, int maxDatagramBytes, double pacingMbps, Action<string>? log = null) =>
        protocol.Trim().ToLowerInvariant() switch
        {
            "tcp" => new TcpFrameSender(host, port, log: log),
            "udp" => new UdpFrameSender(host, port, maxDatagramBytes, pacingMbps: pacingMbps, log: log),
            _ => throw new ArgumentException($"Unknown stream protocol '{protocol}' (expected udp or tcp)"),
        };
}

/// <summary>Sends frame messages as fragmented UDP datagrams to a unicast/broadcast/multicast destination.</summary>
public sealed class UdpFrameSender : IFrameSender
{
    private readonly Socket _socket;
    private readonly BlockingCollection<FrameMessage> _queue;
    private readonly Thread _thread;
    private readonly int _maxDatagram;
    private readonly double _bytesPerSecond;
    private readonly Action<string>? _log;

    public TransportStats Stats { get; } = new();
    public string Description { get; }

    public UdpFrameSender(string host, int port, int maxDatagramBytes = 1400, int queueCapacity = 16, double pacingMbps = 0, Action<string>? log = null)
    {
        _log = log;
        _maxDatagram = Math.Clamp(maxDatagramBytes, UdpFragmentation.HeaderSize + 64, 65507);
        _bytesPerSecond = pacingMbps > 0 ? pacingMbps * 125_000.0 : 0;
        var address = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            SendBufferSize = 8 * 1024 * 1024,
        };
        if (address.Equals(IPAddress.Broadcast) || host.EndsWith(".255", StringComparison.Ordinal)) _socket.EnableBroadcast = true;
        if (IsMulticast(address)) _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
        DisableUdpConnReset(_socket);
        _socket.Connect(new IPEndPoint(address, port));
        Stats.Connected = true;
        Description = $"udp://{address}:{port} (datagram {_maxDatagram} B)";
        _queue = new BlockingCollection<FrameMessage>(queueCapacity);
        _thread = new Thread(Run) { IsBackground = true, Name = "WebAirIG UDP sender" };
        _thread.Start();
    }

    public bool TrySend(FrameMessage message)
    {
        if (_queue.IsAddingCompleted || !_queue.TryAdd(message)) { Stats.AddDropped(); return false; }
        return true;
    }

    private void Run()
    {
        var scratch = new byte[_maxDatagram];
        var sw = Stopwatch.StartNew();
        double budgetStart = 0;
        long pacedBytes = 0;
        try
        {
            foreach (var msg in _queue.GetConsumingEnumerable())
            {
                var bytes = msg.Serialize();
                try
                {
                    if (_bytesPerSecond > 0) { budgetStart = sw.Elapsed.TotalSeconds; pacedBytes = 0; }
                    UdpFragmentation.Fragment(bytes, msg.CameraId, msg.SessionId, msg.FrameNumber, _maxDatagram, scratch, dg =>
                    {
                        _socket.Send(dg);
                        if (_bytesPerSecond > 0)
                        {
                            pacedBytes += dg.Length;
                            double due = budgetStart + pacedBytes / _bytesPerSecond;
                            var spin = new SpinWait();
                            while (sw.Elapsed.TotalSeconds < due) spin.SpinOnce(-1);
                        }
                    });
                    Stats.AddSent(bytes.Length);
                }
                catch (SocketException ex)
                {
                    Stats.AddError();
                    if (Stats.Errors % 100 == 1) _log?.Invoke($"UDP send error: {ex.SocketErrorCode}");
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    internal static bool IsMulticast(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b.Length == 4 && b[0] >= 224 && b[0] <= 239;
    }

    internal static void DisableUdpConnReset(Socket s)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            s.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(1));
        _socket.Dispose();
        _queue.Dispose();
    }
}

/// <summary>
/// Connects to a listening consumer over TCP and writes length-prefixed frame messages ([u32 LE length][message]).
/// Reconnects automatically. Frames submitted while disconnected are dropped.
/// </summary>
public sealed class TcpFrameSender : IFrameSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly BlockingCollection<FrameMessage> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly Action<string>? _log;

    public TransportStats Stats { get; } = new();
    public string Description => $"tcp://{_host}:{_port}";

    public TcpFrameSender(string host, int port, int queueCapacity = 16, Action<string>? log = null)
    {
        _host = host;
        _port = port;
        _log = log;
        _queue = new BlockingCollection<FrameMessage>(queueCapacity);
        _thread = new Thread(Run) { IsBackground = true, Name = "WebAirIG TCP sender" };
        _thread.Start();
    }

    public bool TrySend(FrameMessage message)
    {
        if (!Stats.Connected || _queue.IsAddingCompleted || !_queue.TryAdd(message)) { Stats.AddDropped(); return false; }
        return true;
    }

    private void Run()
    {
        var lenBuf = new byte[4];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true, SendBufferSize = 8 * 1024 * 1024 };
                var connectTask = client.ConnectAsync(_host, _port, _cts.Token).AsTask();
                if (!connectTask.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException();
                using var stream = client.GetStream();
                Stats.Connected = true;
                _log?.Invoke($"TCP video connected to {_host}:{_port}");
                foreach (var msg in _queue.GetConsumingEnumerable(_cts.Token))
                {
                    var bytes = msg.Serialize();
                    BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)bytes.Length);
                    stream.Write(lenBuf);
                    stream.Write(bytes);
                    Stats.AddSent(bytes.Length);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (Stats.Connected) _log?.Invoke($"TCP video disconnected: {ex.GetBaseException().Message}");
                Stats.Connected = false;
                while (_queue.TryTake(out _)) Stats.AddDropped();
                try { Task.Delay(1000, _cts.Token).Wait(); } catch { break; }
            }
        }
        Stats.Connected = false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
        _cts.Dispose();
    }
}
