using System.IO;
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
        // Pick the first non-length float input as the audio input
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
        DebugRecorder.Log("INF", $"Loading Parakeet model: {modelPath} ({new FileInfo(modelPath).Length / 1e6:F0}MB)");
        try
        {
            // Try DirectML (GPU) first — works on AMD Radeon/NVIDIA/Intel
            var dmlOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                // DML EP stabilizers (coding-partner + research): sequential execution
                // avoids internal DML concurrency bugs; disabling the memory pattern
                // optimizer avoids AVs when input shapes fluctuate under DirectML.
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
            // Fallback to CPU
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

    /// <summary>Look for vocab.txt next to the model file and load it (case-insensitive).</summary>
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
        if (_isDisposed) throw new ObjectDisposedException(nameof(ParakeetEngine));

        // SERIALIZE: partial + full transcriptions must NEVER hit the DML session
        // concurrently (native crash class). Blocking is fine — we're on a Task.Run thread.
        _runGate.Wait();
        try
        {
            return RunInferenceCore(features);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private string RunInferenceCore(float[,] features)
    {
        int melBins = features.GetLength(0);
        int frames = features.GetLength(1);

        // Flatten to [1, 80, T]
        var input = new float[1 * melBins * frames];
        int idx = 0;
        for (int f = 0; f < frames; f++)
            for (int m = 0; m < melBins; m++)
                input[idx++] = features[m, f];

        using var inputTensor = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, melBins, frames });

        var inputs = new Dictionary<string, OrtValue> { [_inputName] = inputTensor };
        OrtValue? lenTensor = null;
        if (_hasLengthInput)
        {
            // length = number of frames AFTER 8x subsampling (per config subsampling_factor=8)
            // NOTE: do NOT use 'using var' here — it would dispose before Run() below
            long len = Math.Max(1, frames / 8);
            lenTensor = OrtValue.CreateTensorValueFromMemory(new long[] { len }, new long[] { 1 });
            inputs["length"] = lenTensor;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        DebugRecorder.Log("INF", $"Run: '{_inputName}'=[1,{melBins},{frames}]" + (_hasLengthInput ? $" length={inputs["length"]}" : "") + $" → outputs=[{string.Join(",", _outputNames)}]");
        using var results = _session.Run(_runOptions, inputs, _outputNames);
        sw.Stop();
        lenTensor?.Dispose();

        // Assume first output is logits; get argmax per timestep → tokens
        var output = results[0];
        var shapeInfo = output.GetTensorTypeAndShape();
        var shape = shapeInfo.Shape;
        // [1, T, vocab] typical for CTC models
        long T = shape.Length >= 2 ? shape[^2] : 1;
        long V = shape.Length >= 1 ? shape[^1] : 1;
        DebugRecorder.Log("INF", $"Inference done in {sw.ElapsedMilliseconds}ms. Output shape=[{string.Join(",", shape)}]");

        var logits = output.GetTensorDataAsSpan<float>().ToArray(); // copy NOW — span dangles after results.Dispose()
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

        // Strip blanks (blank = V-1 = 1024 for this model) and collapse repeats
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

        // Decode via BPE/WordPiece vocab
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
                // Skip special/control tokens (marked with <> or empty)
                if (token.StartsWith("<") && token.EndsWith(">")) continue;
                sb.Append(token);
            }
        }

        string result = sb.ToString();
        // SentencePiece space marker → real space
        result = result.Replace("\u2581", " ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ");
        var trimmed = result.Trim();
        DebugRecorder.Log("DEC", $"Decoded raw: \"{trimmed}\"");
        return trimmed;
    }

    private string[]? _vocab;

    public void LoadVocabulary(string[] vocab) => _vocab = vocab;

    /// <summary>Parse a vocab.txt ("token index" per line, SentencePiece format).</summary>
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

        // Wait for any in-flight Run to finish before tearing down the session
        // (never dispose the DML session mid-Run — native use-after-free crash).
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
