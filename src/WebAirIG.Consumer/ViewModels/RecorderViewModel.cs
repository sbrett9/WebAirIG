using CommunityToolkit.Mvvm.Input;
using WebAirIG.Consumer.Services;
using WebAirIG.Web.Mvvm;

namespace WebAirIG.Consumer.ViewModels;

/// <summary>Records auto-labeled YOLO datasets from the stream's truth sidecar.</summary>
public sealed class RecorderViewModel : PollingViewModel
{
    private readonly IDatasetRecorderService _recorder;
    private readonly ClassCatalogService _classes;

    public RecorderViewModel(IDatasetRecorderService recorder, ClassCatalogService classes) : base(TimeSpan.FromSeconds(1))
    {
        _recorder = recorder;
        _classes = classes;
        StartCommand = new RelayCommand(() => { _recorder.Start(); Refresh(); });
        StopCommand = new RelayCommand(() => { _recorder.Stop(); Refresh(); });
    }

    private Options.RecorderOptions S => _recorder.Settings;

    public IReadOnlyList<string> FormatOptions { get; } = ["jpg", "png"];
    public string OutputDir { get => S.OutputDir; set { S.OutputDir = value; OnPropertyChanged(); } }
    public string DatasetName { get => S.DatasetName; set { S.DatasetName = value; OnPropertyChanged(); } }
    public int EveryNFrames { get => S.EveryNFrames; set { S.EveryNFrames = Math.Max(1, value); OnPropertyChanged(); } }
    public string Format { get => S.Format; set { S.Format = value; OnPropertyChanged(); } }
    public bool IncludeOwnship { get => S.IncludeOwnship; set { S.IncludeOwnship = value; OnPropertyChanged(); } }
    public double MinBoxPixels { get => S.MinBoxPixels; set { S.MinBoxPixels = Math.Max(0, value); OnPropertyChanged(); } }
    public bool IncludeEmptyFrames { get => S.IncludeEmptyFrames; set { S.IncludeEmptyFrames = value; OnPropertyChanged(); } }

    public bool Recording => _recorder.Recording;
    public string Status => _recorder.Status;
    public string DatasetPath => _recorder.DatasetPath ?? "";
    public long Images => _recorder.ImagesWritten;
    public long Objects => _recorder.ObjectsWritten;
    public long Dropped => _recorder.Dropped;
    public IReadOnlyList<KeyValuePair<string, long>> ClassCounts { get; private set; } = [];
    public string ClassMap => string.Join(", ", _classes.Classes.Select(kv => $"{kv.Key}={kv.Value}"));
    public string TrainCommand => string.IsNullOrEmpty(_recorder.DatasetPath)
        ? "Start a recording to get a dataset.yaml"
        : $"python tools/yolo/train.py --data \"{Path.Combine(_recorder.DatasetPath, "dataset.yaml")}\"";

    public IRelayCommand StartCommand { get; }
    public IRelayCommand StopCommand { get; }

    protected override void Refresh()
    {
        ClassCounts = _recorder.ObjectsPerClass.OrderBy(k => k.Key).ToList();
        OnPropertyChanged(string.Empty);
    }
}
