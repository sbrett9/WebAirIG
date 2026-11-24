namespace WebAirIG.Consumer.Options;

public sealed class ConsumerOptions
{
    public const string Section = "Consumer";

    public string SharedConfigDir { get; set; } = "../../config";
    public ReceiverOptions Receiver { get; set; } = new();
    public DecoderOptions Decoder { get; set; } = new();
    public DetectorOptions Detector { get; set; } = new();
    public EvaluationOptions Evaluation { get; set; } = new();
    public RecorderOptions Recorder { get; set; } = new();
    public PreviewOptions Preview { get; set; } = new();
}

public sealed class ReceiverOptions
{
    public string Protocol { get; set; } = "udp";
    public string BindAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 5600;
    public bool AutoStart { get; set; } = true;
}

public sealed class DecoderOptions
{
    public string FfmpegPath { get; set; } = "";
    public string HwAccel { get; set; } = "cuda";
}

public sealed class DetectorOptions
{
    public string ModelPath { get; set; } = "../../models/yolo/yolo26n.onnx";
    public bool Enabled { get; set; } = true;
    public double ConfidenceThreshold { get; set; } = 0.25;
    public double NmsIouThreshold { get; set; } = 0.5;
    public int DeviceId { get; set; }
}

public sealed class EvaluationOptions
{
    public double IouThreshold { get; set; } = 0.5;
    public bool IncludeOwnship { get; set; }
    public double MinTruthPixels { get; set; } = 4;
    /// <summary>Match detections to truth regardless of class (useful with pretrained COCO models).</summary>
    public bool ClassAgnostic { get; set; } = true;
}

public sealed class RecorderOptions
{
    public string OutputDir { get; set; } = "../../output/datasets";
    public string DatasetName { get; set; } = "webairig";
    public int EveryNFrames { get; set; } = 15;
    public string Format { get; set; } = "jpg";
    public int JpegQuality { get; set; } = 95;
    public bool IncludeOwnship { get; set; }
    public double MinBoxPixels { get; set; } = 3;
    public bool IncludeEmptyFrames { get; set; } = true;
}

public sealed class PreviewOptions
{
    public int MaxFps { get; set; } = 10;
    public int JpegQuality { get; set; } = 80;
}
