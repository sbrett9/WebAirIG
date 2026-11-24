using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace WebAirIG.Common.Transport;

public sealed class ReceiverStats
{
    public long MessagesReceived;
    public long BytesReceived;
    public long ParseErrors;
    public long FragmentsDropped;
    public int ConnectedClients;
}

public interface IFrameReceiver : IDisposable
{
    event Action<FrameMessage>? MessageReceived;
    event Action<string>? Log;
    void Start();
    ReceiverStats Stats { get; }
    string Description { get; }
}

public static class FrameReceiverFactory
{
    public static IFrameReceiver Create(string protocol, string bindAddress, int port) =>
        protocol.Trim().ToLowerInvariant() switch
        {
            "tcp" => new TcpFrameReceiver(bindAddress, port),
            "udp" => new UdpFrameReceiver(bindAddress, port),
            _ => throw new ArgumentException($"Unknown protocol '{protocol}'"),
        };
}

/// <summary>Receives fragmented frame messages over UDP. Binding to a multicast address joins that group.</summary>
public sealed class UdpFrameReceiver : IFrameReceiver
{
    private readonly string _bind;
    private readonly int _port;
    private Socket? _socket;
    private Thread? _thread;
    private volatile bool _running;
    private readonly UdpReassembler _reassembler = new(TimeSpan.FromMilliseconds(750));

    public event Action<FrameMessage>? MessageReceived;
    public event Action<string>? Log;
    public ReceiverStats Stats { get; } = new();
    public string Description => $"udp://{_bind}:{_port}";

    public UdpFrameReceiver(string bindAddress, int port)
    {
        _bind = string.IsNullOrWhiteSpace(bindAddress) ? "0.0.0.0" : bindAddress;
        _port = port;
    }

    public void Start()
    {
        var addr = IPAddress.Parse(_bind);
        bool multicast = UdpFrameSender.IsMulticast(addr);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 64 * 1024 * 1024,
        };
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        UdpFrameSender.DisableUdpConnReset(_socket);
        _socket.Bind(new IPEndPoint(multicast ? IPAddress.Any : addr, _port));
        if (multicast) _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(addr));
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "WebAirIG UDP receiver" };
        _thread.Start();
        Log?.Invoke($"Listening on {Description} (receive buffer {_socket.ReceiveBufferSize / (1024 * 1024)} MB)");
    }

    private void Run()
    {
        var buf = new byte[65536];
        while (_running)
        {
            int n;
            try { n = _socket!.Receive(buf); }
            catch (SocketException) { if (!_running) break; continue; }
            catch (ObjectDisposedException) { break; }

            var complete = _reassembler.Add(buf.AsSpan(0, n));
            Stats.FragmentsDropped = _reassembler.Dropped;
            if (complete is null) continue;
            if (FrameMessage.TryParse(complete, out var msg) && msg is not null)
            {
                Interlocked.Increment(ref Stats.MessagesReceived);
                Interlocked.Add(ref Stats.BytesReceived, complete.Length);
                try { MessageReceived?.Invoke(msg); }
                catch (Exception ex) { Log?.Invoke($"Frame handler error: {ex.Message}"); }
            }
            else Interlocked.Increment(ref Stats.ParseErrors);
        }
    }

    public void Dispose()
    {
        _running = false;
        _socket?.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(1));
    }
}

/// <summary>Listens for IG connections and reads length-prefixed frame messages.</summary>
public sealed class TcpFrameReceiver : IFrameReceiver
{
    private readonly string _bind;
    private readonly int _port;
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    public event Action<FrameMessage>? MessageReceived;
    public event Action<string>? Log;
    public ReceiverStats Stats { get; } = new();
    public string Description => $"tcp://{_bind}:{_port}";

    public TcpFrameReceiver(string bindAddress, int port)
    {
        _bind = string.IsNullOrWhiteSpace(bindAddress) ? "0.0.0.0" : bindAddress;
        _port = port;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Parse(_bind), _port);
        _listener.Start();
        _ = AcceptLoop();
        Log?.Invoke($"Listening on {Description}");
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            client.ReceiveBufferSize = 16 * 1024 * 1024;
            var t = new Thread(() => ClientLoop(client)) { IsBackground = true, Name = "WebAirIG TCP client" };
            t.Start();
        }
    }

    private void ClientLoop(TcpClient client)
    {
        Interlocked.Increment(ref Stats.ConnectedClients);
        Log?.Invoke($"IG connected from {client.Client.RemoteEndPoint}");
        try
        {
            using var stream = client.GetStream();
            var lenBuf = new byte[4];
            while (!_cts.IsCancellationRequested)
            {
                stream.ReadExactly(lenBuf);
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
                if (len < FrameMessage.HeaderSize || len > 256 * 1024 * 1024) throw new InvalidDataException($"Bad message length {len}");
                var data = new byte[len];
                stream.ReadExactly(data);
                if (FrameMessage.TryParse(data, out var msg) && msg is not null)
                {
                    Interlocked.Increment(ref Stats.MessagesReceived);
                    Interlocked.Add(ref Stats.BytesReceived, len);
                    try { MessageReceived?.Invoke(msg); }
                    catch (Exception ex) { Log?.Invoke($"Frame handler error: {ex.Message}"); }
                }
                else Interlocked.Increment(ref Stats.ParseErrors);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or ObjectDisposedException)
        {
            Log?.Invoke($"IG connection closed: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref Stats.ConnectedClients);
            client.Dispose();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        _cts.Dispose();
    }
}
