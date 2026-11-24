using CommunityToolkit.Mvvm.Input;
using WebAirIG.Consumer.Services.Detection;
using WebAirIG.Web.Mvvm;

namespace WebAirIG.Consumer.ViewModels;

/// <summary>YOLO model, thresholds, evaluation options and live detection metrics against truth.</summary>
public sealed class DetectorViewModel : PollingViewModel
{
    private readonly IDetectorService _detector;
    private readonly IEvaluationService _evaluation;
    private string _modelPath;
    private int _deviceId;

    public DetectorViewModel(IDetectorService detector, IEvaluationService evaluation) : base(TimeSpan.FromSeconds(1))
    {
        _detector = detector;
        _evaluation = evaluation;
        _modelPath = detector.Settings.ModelPath;
        _deviceId = detector.Settings.DeviceId;
        LoadModelCommand = new RelayCommand(() => { _detector.LoadModel(ModelPath, DeviceId); Refresh(); });
        ResetMetricsCommand = new RelayCommand(() => { _evaluation.Reset(); Refresh(); });
    }

    public string ModelPath { get => _modelPath; set => SetProperty(ref _modelPath, value); }
    public int DeviceId { get => _deviceId; set => SetProperty(ref _deviceId, value); }

    public bool Enabled { get => _detector.Settings.Enabled; set { _detector.Settings.Enabled = value; OnPropertyChanged(); } }
    public double Confidence { get => _detector.Settings.ConfidenceThreshold; set { _detector.Settings.ConfidenceThreshold = Math.Clamp(value, 0.01, 0.99); OnPropertyChanged(); } }
    public double NmsIou { get => _detector.Settings.NmsIouThreshold; set { _detector.Settings.NmsIouThreshold = Math.Clamp(value, 0.05, 0.95); OnPropertyChanged(); } }
    public double MatchIou { get => _evaluation.Settings.IouThreshold; set { _evaluation.Settings.IouThreshold = Math.Clamp(value, 0.05, 0.95); OnPropertyChanged(); } }
    public bool IncludeOwnship { get => _evaluation.Settings.IncludeOwnship; set { _evaluation.Settings.IncludeOwnship = value; OnPropertyChanged(); } }
    public double MinTruthPixels { get => _evaluation.Settings.MinTruthPixels; set { _evaluation.Settings.MinTruthPixels = Math.Max(0, value); OnPropertyChanged(); } }
    public bool ClassAgnostic { get => _evaluation.Settings.ClassAgnostic; set { _evaluation.Settings.ClassAgnostic = value; OnPropertyChanged(); } }

    public string Status => _detector.Status;
    public bool ModelLoaded => _detector.ModelLoaded;
    public string ModelInfo => _detector.ModelInfo;
    public double MeanInferenceMs => _detector.MeanInferenceMs;
    public IReadOnlyList<EvalRow> CameraRows { get; private set; } = [];
    public IReadOnlyList<EvalRow> ClassRows { get; private set; } = [];

    public IRelayCommand LoadModelCommand { get; }
    public IRelayCommand ResetMetricsCommand { get; }

    protected override void Refresh()
    {
        CameraRows = _evaluation.CameraRows();
        ClassRows = _evaluation.ClassRows();
        OnPropertyChanged(string.Empty);
    }
}
