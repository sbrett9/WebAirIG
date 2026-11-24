using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WebAirIG.Consumer.Services.Detection;

/// <summary>
/// Ultralytics YOLO (YOLO26 and earlier) ONNX detector. Handles both export heads:
///   * NMS-free end-to-end: output [1, 300, 6] = x1, y1, x2, y2, score, class   (export with nms=False)
///   * classic:             output [1, 4+nc, N] = cx, cy, w, h, class scores... (default export in ultralytics >= 8.4.142; NMS done here)
/// Input: [1, 3, H, W] float RGB 0..1, letterboxed with gray 114 padding. Preprocessing uses an area (box) filter so
/// small MWIR targets survive the 2048 -> model-size reduction.
/// </summary>
public sealed partial class YoloOnnxDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly float[] _input;

    public int InputWidth { get; }
    public int InputHeight { get; }
    public IReadOnlyDictionary<int, string> ClassNames { get; }
    public string Provider { get; }
    public string ModelPath { get; }

    private YoloOnnxDetector(string path, InferenceSession session, string provider)
    {
        ModelPath = path;
        _session = session;
        Provider = provider;
        var input = session.InputMetadata.First();
        _inputName = input.Key;
        var dims = input.Value.Dimensions;
        InputHeight = dims.Length == 4 && dims[2] > 0 ? dims[2] : 640;
        InputWidth = dims.Length == 4 && dims[3] > 0 ? dims[3] : 640;
        _input = new float[3 * InputWidth * InputHeight];
        ClassNames = ParseNames(session.ModelMetadata.CustomMetadataMap);
    }

    public static YoloOnnxDetector Load(string path, int deviceId)
    {
        var so = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        string provider = "CPU";
        if (deviceId >= 0)
        {
            try
            {
                so.AppendExecutionProvider_DML(deviceId);
                provider = $"DirectML (adapter {deviceId})";
            }
            catch (Exception) { provider = "CPU (DirectML unavailable)"; }
        }
        return new YoloOnnxDetector(path, new InferenceSession(path, so), provider);
    }

    [GeneratedRegex(@"(\d+)\s*:\s*['""]([^'""]*)['""]")]
    private static partial Regex NamesRegex();

    private static Dictionary<int, string> ParseNames(IReadOnlyDictionary<string, string> meta)
    {
        var d = new Dictionary<int, string>();
        if (meta.TryGetValue("names", out var names))
            foreach (Match m in NamesRegex().Matches(names))
                d[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
        return d;
    }

    public List<Detection> Detect(byte[] bgra, int width, int height, float confidence, float nmsIou)
    {
        float scale = Math.Min((float)InputWidth / width, (float)InputHeight / height);
        int nw = (int)Math.Round(width * scale), nh = (int)Math.Round(height * scale);
        int padX = (InputWidth - nw) / 2, padY = (InputHeight - nh) / 2;
        Preprocess(bgra, width, height, scale, nw, nh, padX, padY);

        var tensor = new DenseTensor<float>(_input, [1, 3, InputHeight, InputWidth]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var output = results[0].AsTensor<float>();
        var dims = output.Dimensions.ToArray();
        float[] data = output.ToArray();

        var dets = new List<Detection>();
        if (dims.Length == 3 && dims[2] == 6 && dims[1] <= 1000)
        {
            // End-to-end (NMS-free) head.
            for (int i = 0; i < dims[1]; i++)
            {
                int o = i * 6;
                float score = data[o + 4];
                if (score < confidence) continue;
                int cls = (int)data[o + 5];
                dets.Add(Unletterbox(data[o], data[o + 1], data[o + 2], data[o + 3], score, cls, scale, padX, padY, width, height));
            }
            return dets;
        }

        if (dims.Length != 3) return dets;
        // Classic head: [1, C, N] (channels first) or [1, N, C].
        bool channelsFirst = dims[1] < dims[2];
        int c = channelsFirst ? dims[1] : dims[2];
        int n = channelsFirst ? dims[2] : dims[1];
        int nc = c - 4;
        if (nc <= 0) return dets;
        for (int i = 0; i < n; i++)
        {
            float V(int ch) => channelsFirst ? data[ch * n + i] : data[i * c + ch];
            int best = 0;
            float bestScore = 0;
            for (int k = 0; k < nc; k++)
            {
                float s = V(4 + k);
                if (s > bestScore) { bestScore = s; best = k; }
            }
            if (bestScore < confidence) continue;
            float cx = V(0), cy = V(1), w = V(2), h = V(3);
            dets.Add(Unletterbox(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2, bestScore, best, scale, padX, padY, width, height));
        }
        return Nms(dets, nmsIou);
    }

    private Detection Unletterbox(float x1, float y1, float x2, float y2, float score, int cls, float scale, int padX, int padY, int w, int h) =>
        new(Math.Clamp((x1 - padX) / scale, 0, w), Math.Clamp((y1 - padY) / scale, 0, h),
            Math.Clamp((x2 - padX) / scale, 0, w), Math.Clamp((y2 - padY) / scale, 0, h),
            score, cls, ClassNames.TryGetValue(cls, out var name) ? name : cls.ToString());

    private static List<Detection> Nms(List<Detection> dets, float iou)
    {
        var keep = new List<Detection>();
        foreach (var group in dets.GroupBy(d => d.ClassId))
        {
            var sorted = group.OrderByDescending(d => d.Score).ToList();
            var removed = new bool[sorted.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                if (removed[i]) continue;
                keep.Add(sorted[i]);
                for (int j = i + 1; j < sorted.Count; j++)
                    if (!removed[j] && sorted[i].Iou(sorted[j]) > iou) removed[j] = true;
            }
        }
        return keep;
    }

    /// <summary>Area-averaged letterbox resize from BGRA to the planar RGB input tensor.</summary>
    private void Preprocess(byte[] bgra, int width, int height, float scale, int nw, int nh, int padX, int padY)
    {
        Array.Fill(_input, 114f / 255f);
        int plane = InputWidth * InputHeight;
        var input = _input;
        int inW = InputWidth;
        Parallel.For(0, nh, y =>
        {
            int sy0 = (int)(y / scale);
            int sy1 = Math.Min(height, Math.Max(sy0 + 1, (int)MathF.Ceiling((y + 1) / scale)));
            for (int x = 0; x < nw; x++)
            {
                int sx0 = (int)(x / scale);
                int sx1 = Math.Min(width, Math.Max(sx0 + 1, (int)MathF.Ceiling((x + 1) / scale)));
                int r = 0, g = 0, b = 0, count = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int row = sy * width * 4;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int o = row + sx * 4;
                        b += bgra[o];
                        g += bgra[o + 1];
                        r += bgra[o + 2];
                        count++;
                    }
                }
                float inv = 1f / (255f * count);
                int idx = (y + padY) * inW + x + padX;
                input[idx] = r * inv;
                input[plane + idx] = g * inv;
                input[2 * plane + idx] = b * inv;
            }
        });
    }

    public void Dispose() => _session.Dispose();
}
