using System.IO;
using Microsoft.ML.OnnxRuntime;
using Taptalk.Core;

namespace Taptalk.Engine.Parakeet;

/// <summary>
/// NVIDIA Parakeet ASR via ONNX Runtime + DirectML.
/// Uses the AMD/any GPU through DirectML; falls back to CPU if GPU init fails.
/// Model: istupakov/parakeet-ctc-0.6b-onnx
/// </summary>
public sealed class ParakeetEngine : ISttEngine
{
    public string Name => "Parakeet (GPU)";
    public bool IsLoaded => _session != null;
    public bool RequiresModelFile => true;
    public int MinSamplesForPartial => 8000; // 0.5s @16kHz

    private InferenceSession? _session;
    private readonly MelScaleFeaturizer _featurizer = new();
    private readonly string _modelPath;
    private readonly RunOptions _runOptions = new();
    private string[] _outputNames = Array.Empty<string>();
    private string _inputName = "input";
    private bool _hasLengthInput;

    /// <summary>
    /// Serializes ALL session.Run() calls. The DirectML EP is NOT reliably safe for
    /// concurrent Run() calls (partial transcription timer + full transcription at stop
    /// can overlap → native 0xC0000005 access violation in dml/onnxruntime.dll, silent
    /// process death). This gate is the single sync point protecting the session.
    /// </summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private bool _isDisposed;

    public ParakeetEngine(string modelPath) => _modelPath = modelPath;

    private void ReadModelMetadata()
    {
        if (_session == null) return;
        _outputNames = _session.OutputMetadata.Keys.ToArray();
        foreach (var kv in _session.InputMetadata)
        {
            var name = kv.Key;
            if (name.Equals("length", StringComparison.OrdinalIgnoreCase))
            {
                _hasLengthInput = true;
                continue;
            }
            if (_inputName == "input") _inputName = name;
        }
    }

    public bool LoadModel(string modelPath)
    {
        var fileInfo = new FileInfo(modelPath);
        DebugRecorder.Log("INF", $"Loading Parakeet model: {modelPath} ({fileInfo.Length / 1e6:F0}MB)");

        if (fileInfo.Length < 100_000_000)
        {
            string dataPath = modelPath + ".data";
            if (!File.Exists(dataPath))
            {
                DebugRecorder.Log("INF", $"⚠️ Model file is only {fileInfo.Length / 1e6:F1}MB and missing external data file '{dataPath}'. This is the split model. Use the self-contained model.int8.onnx (~650MB) instead.");
            }
            else
            {
                DebugRecorder.Log("INF", $"External model data found: {dataPath} ({new FileInfo(dataPath).Length / 1e9:F2}GB).");
            }
        }

        try
        {
            var dmlOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableMemoryPattern = false
            };
            dmlOptions.AppendExecutionProvider_DML(0);
            _session = new InferenceSession(modelPath, dmlOptions);
            ReadModelMetadata();
            AutoLoadSiblingVocab(modelPath);
            LogModelMetadata();
            DebugRecorder.Log("INF", "Parakeet model loaded (DirectML GPU)");
            return true;
        }
        catch (Exception dmlEx)
        {
            try
            {
                _session = new InferenceSession(modelPath);
                ReadModelMetadata();
                AutoLoadSiblingVocab(modelPath);
                LogModelMetadata();
                DebugRecorder.Log("INF", $"Parakeet model loaded (CPU fallback — DML failed: {dmlEx.Message})");
                return true;
            }
            catch (Exception cpuEx)
            {
                DebugRecorder.Error("INF", "LoadModel", cpuEx);
                _session = null;
                return false;
            }
        }
    }

    private void LogModelMetadata()
    {
        if (_session == null) return;
        foreach (var kv in _session.InputMetadata)
            DebugRecorder.Log("INF", $"Model input: '{kv.Key}' type={kv.Value.ElementType} dims=[{string.Join(",", kv.Value.Dimensions)}]");
        foreach (var kv in _session.OutputMetadata)
            DebugRecorder.Log("INF", $"Model output: '{kv.Key}' type={kv.Value.ElementType} dims=[{string.Join(",", kv.Value.Dimensions)}]");
        DebugRecorder.Log("DEC", $"Vocab loaded: {_vocab != null}, size={_vocab?.Length ?? 0}");
    }

    private void AutoLoadSiblingVocab(string modelPath)
    {
        var dir = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrEmpty(dir)) return;

        string? vocabPath = null;
        try
        {
            var candidates = Directory.GetFiles(dir, "vocab.txt", SearchOption.TopDirectoryOnly);
            if (candidates.Length > 0)
                vocabPath = candidates[0];
            else
            {
                var all = Directory.GetFiles(dir, "*.txt", SearchOption.TopDirectoryOnly);
                vocabPath = all.FirstOrDefault(f => Path.GetFileName(f).StartsWith("vocab", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { return; }

        if (vocabPath != null && LoadVocabularyFromFile(vocabPath))
            System.Diagnostics.Debug.WriteLine($"[ParakeetEngine] Vocabulary loaded: {vocabPath}");
    }

    public string Transcribe(float[] audio)
    {
        if (_session == null || audio.Length < MelScaleFeaturizer.WindowSize)
            return "";

        var normalized = NormalizeForInference(audio, isPartial: false);
        var features = _featurizer.Extract(normalized);
        int rawSamples = normalized.Length;

        FeatureStats(features, out float featMin, out float featMax, out float featMean);
        DebugRecorder.Log("FEAT", $"Feature tensor min={featMin:F2} max={featMax:F2} mean={featMean:F2}");

        return RunInference(features, rawSamples);
    }

    public string TranscribePartial(float[] audio)
    {
        var normalized = NormalizeForInference(audio, isPartial: true);
        var features = _featurizer.Extract(normalized);
        return RunInference(features, normalized.Length);
    }

    private static void FeatureStats(float[] features, out float min, out float max, out float mean)
    {
        min = float.MaxValue; max = float.MinValue; double sum = 0;
        for (int i = 0; i < features.Length; i++)
        {
            var v = features[i];
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        mean = features.Length > 0 ? (float)(sum / features.Length) : 0f;
    }

    public void ResetSession() => _sessionGain = 1.0f;

    private float _sessionGain = 1.0f;
    private float[] NormalizeForInference(float[] audio, bool isPartial)
    {
        if (audio.Length == 0) return audio;

        var (rawPeak, rawRms) = AudioNormalizer.Measure(audio);

        float[] copy = new float[audio.Length];
        Array.Copy(audio, copy, audio.Length);

        float gain = AudioNormalizer.NormalizeInPlace(copy);
        _sessionGain = gain;

        if (rawRms < 0.003)
            DebugRecorder.Log("AUDIO", $"⚠️ Mic level very low! Raw peak={rawPeak:F4} RMS={rawRms:F4} → boosted {gain:F1}x. Check Windows Sound Settings → Microphone → Input level/Boost.");
        else
            DebugRecorder.Log("AUDIO", $"Raw peak={rawPeak:F4} RMS={rawRms:F4} → normalized with {gain:F1}x gain (session gain {_sessionGain:F1}x)");

        return copy;
    }

    private string RunInference(float[] features, int rawSamples)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ParakeetEngine));
        _runGate.Wait();
        try
        {
            return RunInferenceCore(features, rawSamples);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private string RunInferenceCore(float[] features, int rawSamples)
    {
        int total = features.Length;
        if (total == 0 || total % MelScaleFeaturizer.MelBands != 0)
        {
            DebugRecorder.Log("ERR", $"Invalid feature tensor length {total}");
            return "";
        }

        int frames = total / MelScaleFeaturizer.MelBands;
        int melBins = MelScaleFeaturizer.MelBands;

        using var inputTensor = OrtValue.CreateTensorValueFromMemory(features, new long[] { 1, melBins, frames });

        var inputs = new Dictionary<string, OrtValue> { [_inputName] = inputTensor };
        OrtValue? lenTensor = null;
        long length = Math.Max(1, rawSamples / (long)MelScaleFeaturizer.HopLength);
        if (_hasLengthInput)
        {
            lenTensor = OrtValue.CreateTensorValueFromMemory(new long[] { length }, new long[] { 1 });
            inputs["length"] = lenTensor;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        DebugRecorder.Log("INF", $"Run: '{_inputName}'=[1,{melBins},{frames}] length={length} → outputs=[{string.Join(",", _outputNames)}]");

        using var results = _session!.Run(_runOptions, inputs, _outputNames);
        sw.Stop();
        lenTensor?.Dispose();

        var output = results[0];
        var shapeInfo = output.GetTensorTypeAndShape();
        var shape = shapeInfo.Shape;
        long T = shape.Length >= 2 ? shape[^2] : 1;
        long V = shape.Length >= 1 ? shape[^1] : 1;

        var logits = output.GetTensorDataAsSpan<float>().ToArray();

        bool hasNaN = false;
        float minLogit = float.MaxValue, maxLogit = float.MinValue;
        for (int i = 0; i < logits.Length; i++)
        {
            var v = logits[i];
            if (float.IsNaN(v)) hasNaN = true;
            if (v < minLogit) minLogit = v;
            if (v > maxLogit) maxLogit = v;
        }

        DebugRecorder.Log("INF", $"Inference done in {sw.ElapsedMilliseconds}ms. Output shape=[{string.Join(",", shape)}] logits min={minLogit:F2} max={maxLogit:F2} hasNaN={hasNaN}");
        var tokens = new List<int>();
        for (int t = 0; t < (int)T; t++)
        {
            int best = 0;
            float bestVal = float.MinValue;
            for (int v = 0; v < (int)V; v++)
            {
                float val = logits[t * (int)V + v];
                if (val > bestVal) { bestVal = val; best = v; }
            }
            tokens.Add(best);
        }

        int blank = (int)V - 1;
        var collapsed = new List<int>();
        int prev = -1;
        int blankCount = 0;
        foreach (var t in tokens)
        {
            if (t == blank) { blankCount++; continue; }
            if (t != prev) collapsed.Add(t);
            prev = t;
        }
        DebugRecorder.Log("DEC", $"Raw frames={tokens.Count} | blank={blankCount} | collapsed tokens={collapsed.Count} | first tokens=[{string.Join(",", collapsed.Take(10))}]");

        return DecodeTokens(collapsed);
    }

    private string DecodeTokens(List<int> tokens)
    {
        if (_vocab == null || _vocab.Length == 0)
        {
            if (tokens.Count > 0)
                return $"[No Vocab Loaded! Raw Tokens: {string.Join(" ", tokens)}]";
            return "";
        }

        var sb = new System.Text.StringBuilder();
        foreach (var t in tokens)
        {
            if (t >= 0 && t < _vocab.Length)
            {
                string token = _vocab[t];
                if (string.IsNullOrEmpty(token)) continue;
                if (token.StartsWith("<") && token.EndsWith(">")) continue;
                sb.Append(token);
            }
        }

        string result = sb.ToString();
        result = result.Replace("\u2581", " ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ");
        var trimmed = result.Trim();
        DebugRecorder.Log("DEC", $"Decoded raw: \"{trimmed}\"");
        return trimmed;
    }

    private string[]? _vocab;

    public void LoadVocabulary(string[] vocab) => _vocab = vocab;

    public bool LoadVocabularyFromFile(string vocabPath)
    {
        try
        {
            if (!File.Exists(vocabPath)) return false;

            var lines = File.ReadAllLines(vocabPath);
            int maxIndex = -1;
            var entries = new List<(string token, int index)>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int lastSpace = line.LastIndexOf(' ');
                if (lastSpace > 0 && int.TryParse(line.Substring(lastSpace + 1), out int idx))
                {
                    entries.Add((line.Substring(0, lastSpace), idx));
                    if (idx > maxIndex) maxIndex = idx;
                }
                else
                {
                    int fallback = entries.Count;
                    entries.Add((line.Trim(), fallback));
                    if (fallback > maxIndex) maxIndex = fallback;
                }
            }

            int vocabSize = Math.Max(maxIndex + 1, 1025);
            _vocab = new string[vocabSize];
            foreach (var (token, index) in entries)
            {
                if (index >= 0 && index < _vocab.Length)
                    _vocab[index] = token;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _runGate.Wait();
        try
        {
            _session?.Dispose();
            _runOptions?.Dispose();
        }
        finally
        {
            _runGate.Release();
            _runGate.Dispose();
        }
    }
}
