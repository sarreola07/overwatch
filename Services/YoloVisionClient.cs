using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Overwatch.Services;

/// <summary>
/// Local inference with a COCO-pretrained YOLOv8 ONNX model. Runs on the GPU
/// via DirectML when available, otherwise CPU. No per-call cost, no rate limit,
/// ~5-20ms per frame on a discrete GPU vs ~500ms round trip to the cloud API.
/// </summary>
public class YoloVisionClient : IVisionClient, IDisposable
{
    private const float ConfidenceThreshold = 0.35f;
    private const float IouThreshold = 0.45f;
    private const int MaxDetections = 50;

    private static readonly string[] CocoLabels =
    [
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
        "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
        "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
        "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
        "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
        "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
        "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair",
        "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse",
        "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink",
        "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier",
        "toothbrush"
    ];

    private readonly InferenceSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _inputSize;
    private readonly ILogger<YoloVisionClient> _logger;

    public string ExecutionProvider { get; }

    public YoloVisionClient(IConfiguration config, IHostEnvironment env, ILogger<YoloVisionClient> logger)
    {
        _logger = logger;
        var modelPath = Path.Combine(env.ContentRootPath,
            config["Vision:YoloModelPath"] ?? Path.Combine("models", "yolov8s.onnx"));
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"YOLO model not found at {modelPath}. See README for the download link.", modelPath);

        var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
        try
        {
            options.AppendExecutionProvider_DML(0);
            ExecutionProvider = "DirectML (GPU)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DirectML unavailable ({Error}); falling back to CPU", ex.Message);
            ExecutionProvider = "CPU";
        }
        _session = new InferenceSession(modelPath, options);

        var input = _session.InputMetadata.First();
        _inputName = input.Key;
        // Dims are [batch, 3, H, W]; H may be -1 (dynamic) — default to 640.
        _inputSize = input.Value.Dimensions.Length == 4 && input.Value.Dimensions[2] > 0
            ? input.Value.Dimensions[2] : 640;
        _outputName = _session.OutputMetadata.First().Key;

        _logger.LogInformation(
            "YOLO model loaded: {Path} | input {Input} {Size}px | output {Output} [{Dims}] | {EP}",
            Path.GetFileName(modelPath), _inputName, _inputSize, _outputName,
            string.Join("x", _session.OutputMetadata.First().Value.Dimensions), ExecutionProvider);
    }

    public async Task<IReadOnlyList<Detection>> DetectObjectsAsync(byte[] imageBytes, CancellationToken ct)
    {
        using var image = Image.Load<Rgb24>(imageBytes);
        int origW = image.Width, origH = image.Height;

        // Letterbox: scale to fit, pad the rest with neutral gray (YOLO convention).
        float scale = Math.Min((float)_inputSize / origW, (float)_inputSize / origH);
        int newW = (int)Math.Round(origW * scale), newH = (int)Math.Round(origH * scale);
        float dx = (_inputSize - newW) / 2f, dy = (_inputSize - newH) / 2f;

        image.Mutate(x => x.Resize(newW, newH));
        using var canvas = new Image<Rgb24>(_inputSize, _inputSize, new Rgb24(114, 114, 114));
        canvas.Mutate(x => x.DrawImage(image, new Point((int)dx, (int)dy), 1f));

        var tensor = new DenseTensor<float>([1, 3, _inputSize, _inputSize]);
        canvas.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    tensor[0, 0, y, x] = row[x].R / 255f;
                    tensor[0, 1, y, x] = row[x].G / 255f;
                    tensor[0, 2, y, x] = row[x].B / 255f;
                }
            }
        });

        await _gate.WaitAsync(ct);
        try
        {
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var results = _session.Run(inputs, [_outputName]);
            var output = (DenseTensor<float>)results[0].AsTensor<float>();
            return Postprocess(output, scale, dx, dy, origW, origH);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<Detection> Postprocess(
        DenseTensor<float> output, float scale, float dx, float dy, int origW, int origH)
    {
        // YOLOv8 exports as [1, 4+classes, anchors]; some tools transpose to
        // [1, anchors, 4+classes]. The attribute dim is always the smaller one.
        var d1 = output.Dimensions[1];
        var d2 = output.Dimensions[2];
        bool attrsFirst = d1 < d2;
        int attrs = attrsFirst ? d1 : d2;
        int anchors = attrsFirst ? d2 : d1;
        var data = output.Buffer.ToArray();
        float At(int attr, int anchor) =>
            attrsFirst ? data[attr * anchors + anchor] : data[anchor * attrs + attr];

        // 4+80 = YOLOv8 (class scores only); 5+80 = YOLOv5 (objectness at index 4).
        bool v5Style = attrs == CocoLabels.Length + 5;
        int classOffset = v5Style ? 5 : 4;
        int numClasses = attrs - classOffset;

        var candidates = new List<(float Score, int Class, float X1, float Y1, float X2, float Y2)>();
        for (var i = 0; i < anchors; i++)
        {
            var best = 0f;
            var bestClass = -1;
            for (var c = 0; c < numClasses; c++)
            {
                var score = At(classOffset + c, i);
                if (score > best) { best = score; bestClass = c; }
            }
            if (v5Style) best *= At(4, i);
            if (best < ConfidenceThreshold) continue;

            float cx = At(0, i), cy = At(1, i), w = At(2, i), h = At(3, i);
            candidates.Add((best, bestClass, cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2));
        }

        // Non-max suppression: keep the highest-scoring box, drop overlaps.
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<(float Score, int Class, float X1, float Y1, float X2, float Y2)>();
        foreach (var c in candidates)
        {
            if (kept.Count >= MaxDetections) break;
            var overlaps = kept.Any(k => k.Class == c.Class && Iou(k, c) > IouThreshold);
            if (!overlaps) kept.Add(c);
        }

        return kept.Select(k =>
        {
            // Undo the letterbox, normalize to the original image.
            var x = Math.Clamp((k.X1 - dx) / scale / origW, 0, 1);
            var y = Math.Clamp((k.Y1 - dy) / scale / origH, 0, 1);
            var x2 = Math.Clamp((k.X2 - dx) / scale / origW, 0, 1);
            var y2 = Math.Clamp((k.Y2 - dy) / scale / origH, 0, 1);
            var label = k.Class >= 0 && k.Class < CocoLabels.Length ? CocoLabels[k.Class] : $"class {k.Class}";
            return new Detection(label, Math.Round(k.Score, 3), x, y, x2 - x, y2 - y);
        }).ToList();
    }

    private static float Iou(
        (float Score, int Class, float X1, float Y1, float X2, float Y2) a,
        (float Score, int Class, float X1, float Y1, float X2, float Y2) b)
    {
        var ix = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        var iy = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        var inter = ix * iy;
        var union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public void Dispose()
    {
        _session.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
