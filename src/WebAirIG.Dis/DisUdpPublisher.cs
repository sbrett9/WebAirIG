using System.Net;
using System.Net.Sockets;

namespace WebAirIG.Dis;

/// <summary>Sends DIS PDUs over UDP (broadcast, multicast or unicast).</summary>
public sealed class DisUdpPublisher : IDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _dest;
    public long PdusSent { get; private set; }
    public long Errors { get; private set; }
    public string Description => $"udp://{_dest}";

    public DisUdpPublisher(string address, int port, int multicastTtl = 4)
    {
        var ip = IPAddress.Parse(address);
        _dest = new IPEndPoint(ip, port);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        var bytes = ip.GetAddressBytes();
        if (bytes[0] is >= 224 and <= 239)
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, multicastTtl);
    }

    public void Send(EntityStatePdu pdu)
    {
        Span<byte> buf = stackalloc byte[EntityStatePdu.Length];
        pdu.WriteTo(buf);
        try
        {
            _socket.SendTo(buf.ToArray(), _dest);
            PdusSent++;
        }
        catch (SocketException)
        {
            Errors++;
        }
    }

    public void Dispose() => _socket.Dispose();
}
