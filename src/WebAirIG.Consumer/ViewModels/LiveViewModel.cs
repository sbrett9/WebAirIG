using CommunityToolkit.Mvvm.Input;
using WebAirIG.Consumer.Services;
using WebAirIG.Consumer.Services.Detection;
using WebAirIG.Web.Mvvm;

namespace WebAirIG.Consumer.ViewModels;

public sealed record ViewOption(int Id, string Label);

public sealed record CameraRow(
    int Id, string Name, long Received, long Decoded, double Fps, double LatencyMs, long Gaps, long Skipped,
    ulong LastFrame, int TruthObjects, int Detections, double InferenceMs, string Decoder);

/// <summary>Live view: receiver control, MJPEG preview selection and per-camera pipeline health.</summary>
public sealed class LiveViewModel : PollingViewModel
{
    private readonly IStreamReceiverService _receiver;
    private readonly ICameraPipelineService _pipelines;
    private readonly IDetectorService _detector;
    private string _protocol, _bindAddress;
    private int _port, _selectedView = -1, _previewSize = 1024, _nonce;
    private bool _showTruth = true, _showDetections = true;
    private IReadOnlyList<ViewOption> _viewOptions = [new(-1, "All cameras (2x2)")];

    public LiveViewModel(IStreamReceiverService receiver, ICameraPipelineService pipelines, IDetectorService detector)
        : base(TimeSpan.FromMilliseconds(500))
    {
        _receiver = receiver;
        _pipelines = pipelines;
        _detector = detector;
        _protocol = receiver.Protocol;
        _bindAddress = receiver.BindAddress;
        _port = receiver.Port;
        StartReceiverCommand = new RelayCommand(() => { _receiver.Start(Protocol, BindAddress, Port); Refresh(); });
        StopReceiverCommand = new RelayCommand(() => { _receiver.Stop(); Refresh(); });
        ReconnectPreviewCommand = new RelayCommand(() => { _nonce++; OnPropertyChanged(nameof(PreviewUrl)); });
    }

    public IReadOnlyList<string> ProtocolOptions { get; } = ["udp", "tcp"];
    public IReadOnlyList<int> PreviewSizeOptions { get; } = [512, 768, 1024, 1536, 2048];

    public string Protocol { get => _protocol; set => SetProperty(ref _protocol, value); }
    public string BindAddress { get => _bindAddress; set => SetProperty(ref _bindAddress, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public bool ReceiverRunning => _receiver.IsRunning;
    public string ReceiverStatus => _receiver.Status;
    public string ReceiverCounters => _receiver.Stats is { } s
        ? $"{s.MessagesReceived} frames, {s.BytesReceived / 1_000_000.0:F0} MB, {s.FragmentsDropped} incomplete, {s.ParseErrors} bad, {s.ConnectedClients} TCP clients"
        : "";
    public string FfmpegStatus => _pipelines.FfmpegStatus;

    public IReadOnlyList<ViewOption> ViewOptions { get => _viewOptions; private set => SetProperty(ref _viewOptions, value); }
    public int SelectedView { get => _selectedView; set { if (SetProperty(ref _selectedView, value)) OnPropertyChanged(nameof(PreviewUrl)); } }
    public int PreviewSize { get => _previewSize; set { if (SetProperty(ref _previewSize, value)) OnPropertyChanged(nameof(PreviewUrl)); } }
    public bool ShowTruth { get => _showTruth; set { if (SetProperty(ref _showTruth, value)) OnPropertyChanged(nameof(PreviewUrl)); } }
    public bool ShowDetections { get => _showDetections; set { if (SetProperty(ref _showDetections, value)) OnPropertyChanged(nameof(PreviewUrl)); } }
    public string PreviewUrl =>
        $"/preview/stream?cam={SelectedView}&size={PreviewSize}&truth={(ShowTruth ? "true" : "false")}&det={(ShowDetections ? "true" : "false")}&n={_nonce}";

    public IReadOnlyList<CameraRow> Cameras { get; private set; } = [];

    public IRelayCommand StartReceiverCommand { get; }
    public IRelayCommand StopReceiverCommand { get; }
    public IRelayCommand ReconnectPreviewCommand { get; }

    protected override void Refresh()
    {
        var stats = _pipelines.GetStats();
        Cameras = stats.Select(s =>
        {
            var det = _detector.Latest(s.CameraId);
            return new CameraRow(s.CameraId, s.Name, s.Received, s.Decoded, s.Fps, s.LatencyMs, s.Gaps, s.SkippedWaitingKeyframe,
                s.LastFrame, s.TruthObjects, det?.Detections.Count ?? 0, Math.Round(det?.InferenceMs ?? 0, 1),
                s.DecoderRunning ? $"running (restarts {s.DecoderRestarts})" : "stopped");
        }).ToList();

        var options = new List<ViewOption> { new(-1, "All cameras (2x2)") };
        options.AddRange(stats.Select(s => new ViewOption(s.CameraId, $"Camera {s.CameraId} {s.Name}")));
        if (!options.Select(o => o.Label).SequenceEqual(ViewOptions.Select(o => o.Label))) ViewOptions = options;

        OnPropertyChanged(nameof(Cameras));
        OnPropertyChanged(nameof(ReceiverStatus));
        OnPropertyChanged(nameof(ReceiverCounters));
        OnPropertyChanged(nameof(ReceiverRunning));
    }
}
