using Microsoft.Extensions.Options;
using WebAirIG.Common.Transport;
using WebAirIG.Consumer.Options;

namespace WebAirIG.Consumer.Services;

public interface IStreamReceiverService
{
    bool IsRunning { get; }
    string Status { get; }
    string Protocol { get; }
    string BindAddress { get; }
    int Port { get; }
    ReceiverStats? Stats { get; }
    void Start(string protocol, string bindAddress, int port);
    void Stop();
    event Action<FrameMessage>? FrameReceived;
}

/// <summary>Owns the UDP/TCP frame receiver; can be reconfigured at runtime from the UI.</summary>
public sealed class StreamReceiverService : IStreamReceiverService, IHostedService, IDisposable
{
    private readonly ReceiverOptions _opt;
    private readonly ILogger<StreamReceiverService> _log;
    private readonly object _gate = new();
    private IFrameReceiver? _receiver;

    public event Action<FrameMessage>? FrameReceived;
    public bool IsRunning => _receiver is not null;
    public string Status { get; private set; } = "stopped";
    public string Protocol { get; private set; }
    public string BindAddress { get; private set; }
    public int Port { get; private set; }
    public ReceiverStats? Stats => _receiver?.Stats;

    public StreamReceiverService(IOptions<ConsumerOptions> options, ILogger<StreamReceiverService> log)
    {
        _opt = options.Value.Receiver;
        _log = log;
        Protocol = _opt.Protocol;
        BindAddress = _opt.BindAddress;
        Port = _opt.Port;
    }

    public void Start(string protocol, string bindAddress, int port)
    {
        lock (_gate)
        {
            StopCore();
            Protocol = protocol;
            BindAddress = bindAddress;
            Port = port;
            try
            {
                var r = FrameReceiverFactory.Create(protocol, bindAddress, port);
                r.MessageReceived += m => FrameReceived?.Invoke(m);
                r.Log += m => _log.LogInformation("{Message}", m);
                r.Start();
                _receiver = r;
                Status = $"listening {r.Description}";
            }
            catch (Exception ex)
            {
                Status = $"failed: {ex.Message}";
                _log.LogError(ex, "Receiver start failed");
            }
        }
    }

    public void Stop()
    {
        lock (_gate) StopCore();
    }

    private void StopCore()
    {
        _receiver?.Dispose();
        _receiver = null;
        Status = "stopped";
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_opt.AutoStart) Start(_opt.Protocol, _opt.BindAddress, _opt.Port);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => Stop();
}
