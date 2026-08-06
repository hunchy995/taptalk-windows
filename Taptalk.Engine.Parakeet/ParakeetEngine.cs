using Microsoft.ML.OnnxRuntime;
using Taptalk.Core;

namespace Taptalk.Engine.Parakeet;

/// <summary>
/// NVIDIA Parakeet ASR via ONNX Runtime + DirectML.
/// Uses the AMD/any GPU through DirectML; falls back to CPU if GPU init fails.
/// Model: istupakov/parakeet-tdt-0.6b-v3-onnx
/// </summary>
public sealed class ParakeetEngine : ISttEngine
{
    public string Name => "Parakeet (GPU)";
    public bool IsLoaded => _session != null;
    public bool RequiresModelFile => true;

    private InferenceSession? _session;
    private readonly MelScaleFeaturizer _featurizer = new();
    private readonly string _modelPath;
    private readonly RunOptions _runOptions = new();
    private readonly string[] _outputNames;

    public ParakeetEngine(string modelPath) => _modelPath = modelPath;

    public bool LoadModel(string modelPath)
    {
        try
        {
            // Try DirectML (GPU) first — works on AMD Radeon/NVIDIA/Intel
            var dmlOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            dmlOptions.AppendExecutionProvider_DML(0);
            _session = new InferenceSession(modelPath, dmlOptions);
            _outputNames = _session.OutputMetadata.Keys.ToArray();
            return true;
        }
        catch
        {
            // Fallback to CPU
            try
            {
                _session = new InferenceSession(modelPath);
                _outputNames = _session.OutputMetadata.Keys.ToArray();
                return true;
            }
            catch
            {
                _session = null;
                return false;
            }
        }
    }

    public string Transcribe(float[] audio)
    {
        if (_session == null || audio.Length < MelScaleFeaturizer.WindowSize)
            return "";

        var features = _featurizer.Extract(audio);
        return RunInference(features);
    }

    public string TranscribePartial(float[] audio)
    {
        // Same path — Parakeet handles partial buffers well
        return Transcribe(audio);
    }

    private string RunInference(float[,] features)
    {
        int melBins = features.GetLength(0);
        int frames = features.GetLength(1);

        // Flatten to [1, 80, T]
        var input = new float[1 * melBins * frames];
        int idx = 0;
        for (int f = 0; f < frames; f++)
            for (int m = 0; m < melBins; m++)
                input[idx++] = features[m, f];

        using var inputTensor = OrtValue.CreateTensorValueFromMemory(input.AsMemory(), new long[] { 1, melBins, frames });

        var inputs = new Dictionary<string, OrtValue> { ["input"] = inputTensor };

        using var results = _session.Run(_runOptions, inputs, _outputNames);

        // Assume first output is logits; get argmax per timestep → tokens
        var output = results[0];
        var shape = output.Shape;
        // [1, T, vocab] typical for TDT
        long T = shape.Length >= 2 ? shape[^2] : 1;
        long V = shape.Length >= 1 ? shape[^1] : 1;

        var logits = output.GetTensorDataAsSpan<float>();
        var tokens = new List<int>();
        for (int t = 0; t < T; t++)
        {
            int best = 0;
            float bestVal = float.MinValue;
            for (int v = 0; v < V; v++)
            {
                float val = logits[t * V + v];
                if (val > bestVal) { bestVal = val; best = v; }
            }
            tokens.Add(best);
        }

        // Strip blanks (blank token = V-1 or 0 for CTC) and collapse repeats
        int blank = (int)V - 1;
        var collapsed = new List<int>();
        int prev = -1;
        foreach (var t in tokens)
        {
            if (t != blank && t != prev) collapsed.Add(t);
            prev = t;
        }

        // Decode via BPE/WordPiece vocab (simplified: use vocab file if bundled)
        return DecodeTokens(collapsed);
    }

    private string DecodeTokens(List<int> tokens)
    {
        // Simplified decoder — if a vocab file is present use it, else return raw IDs
        if (_vocab != null)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in tokens)
            {
                if (t >= 0 && t < _vocab.Length)
                    sb.Append(_vocab[t]);
            }
            return sb.ToString();
        }
        return string.Join(" ", tokens);
    }

    private string[]? _vocab;

    public void LoadVocabulary(string[] vocab) => _vocab = vocab;

    public void Dispose() => _session?.Dispose();
}
