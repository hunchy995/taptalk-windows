using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Taptalk.Core;

namespace Taptalk.Engine.Parakeet;

/// <summary>
/// NVIDIA Parakeet TDT (Token-and-Duration Transducer) via ONNX Runtime + DirectML.
/// Loads the split encoder + decoder_joint pair from istupakov/parakeet-tdt-0.6b-v3-onnx
/// and runs the autoregressive greedy decode loop, matching onnx-asr's
/// NemoConformerTdt decoding exactly.
///
/// Model I/O (verified against the exported ONNX):
///   encoder:    audio_signal[1,128,T] float + length[1] int64
///               -> outputs[1,1024,T'] float, encoded_lengths[1] int64
///   decoder_joint: encoder_outputs[1,1024,1] float, targets[1,1] int32,
///               target_length[1] int32, input_states_1/2[2,1,640] float
///               -> outputs[1,1,1,V+5] float (V vocab + blank + 5 duration),
///                  output_states_1/2[2,1,640] float
///   vocab:      vocab.txt, 8193 entries, &lt;blk&gt; (blank) at index 8192
/// </summary>
public sealed class TdtEngine : ISttEngine
{
    public string Name => "Parakeet TDT (GPU)";
    public bool IsLoaded => _encoder != null && _decoderJoint != null;
    public bool RequiresModelFile => true;
    public int MinSamplesForPartial => 8000; // 0.5s @16kHz

    private readonly MelScaleFeaturizer _featurizer = new(128);

    private InferenceSession? _encoder;
    private InferenceSession? _decoderJoint;
    private readonly RunOptions _encoderRunOptions = new();
    private readonly RunOptions _decoderRunOptions = new();

    // Encoder metadata
    private string _encoderInputName = "audio_signal";
    private bool _encoderHasLengthInput;
    private string[] _encoderOutputNames = new[] { "outputs", "encoded_lengths" };

    // Decoder metadata
    private string[] _decoderOutputNames = new[] { "outputs", "output_states_1", "output_states_2" };

    // Shapes read from model metadata at load time (fallback = verified values)
    private int _stateLayers = 2;
    private int _stateHidden = 640;

    // Vocab
    private string[]? _vocab;
    private int _vocabSizeInclBlank;
    private int _blankIdx = -1;

    // Serializes ALL session.Run() calls (encoder + the autoregressive joint loop).
    // ONNX Runtime sessions (and DirectML) are not safe for concurrent Run().
    private readonly System.Threading.SemaphoreSlim _runGate = new(1, 1);
    private bool _isDisposed;
    private float _sessionGain = 1.0f;

    public TdtEngine(string modelPath) { }

    public bool LoadModel(string modelPath)
    {
        var (encoderPath, decoderPath, vocabPath) = ResolveFiles(modelPath);
        if (encoderPath == null || decoderPath == null)
        {
            DebugRecorder.Error("INF", "LoadModel", new InvalidOperationException(
                "TDT model needs BOTH encoder-model.onnx and decoder_joint-model.onnx in the same folder. Select the encoder file."));
            return false;
        }

        // Warn if the FP32 encoder is missing its external weight file.
        var encInfo = new FileInfo(encoderPath);
        if (encInfo.Length < 100_000_000)
        {
            string dataPath = encoderPath + ".data";
            if (!File.Exists(dataPath))
                DebugRecorder.Log("INF", $"⚠️ Encoder is only {encInfo.Length / 1e6:F1}MB and missing '{dataPath}'. This is the split FP32 encoder — download encoder-model.onnx.data (2.4GB), or use encoder-model.int8.onnx.");
            else
                DebugRecorder.Log("INF", $"TDT encoder external data found: {dataPath} ({new FileInfo(dataPath).Length / 1e9:F2}GB).");
        }

        try
        {
            var dmlEncoder = BuildDmlOptions();
            // Decoder joint is kept on CPU: INT8-quantized decoder_joint models
            // produce NaN logits under DirectML on AMD GPUs (observed with
            // istupakov/parakeet-tdt-0.6b-v3-onnx), while the encoder runs
            // correctly on DirectML.
            var cpuDecoder = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };
            _encoder = new InferenceSession(encoderPath, dmlEncoder);
            _decoderJoint = new InferenceSession(decoderPath, cpuDecoder);
            ReadMetadata();
            LoadVocab(vocabPath);
            LogMetadata();
            DebugRecorder.Log("INF", "Parakeet TDT model loaded (DirectML GPU)");
            return true;
        }
        catch (Exception dmlEx)
        {
            try
            {
                _encoder?.Dispose();
                _decoderJoint?.Dispose();
                _encoder = new InferenceSession(encoderPath);
                _decoderJoint = new InferenceSession(decoderPath);
                ReadMetadata();
                LoadVocab(vocabPath);
                LogMetadata();
                DebugRecorder.Log("INF", $"Parakeet TDT model loaded (CPU fallback — DML failed: {dmlEx.Message})");
                return true;
            }
            catch (Exception cpuEx)
            {
                DebugRecorder.Error("INF", "LoadModel", cpuEx);
                _encoder?.Dispose();
                _decoderJoint?.Dispose();
                _encoder = null;
                _decoderJoint = null;
                return false;
            }
        }
    }

    private static SessionOptions BuildDmlOptions()
    {
        var o = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false
        };
        o.AppendExecutionProvider_DML(0);
        return o;
    }

    private void ReadMetadata()
    {
        // Encoder input names
        _encoderInputName = "audio_signal";
        _encoderHasLengthInput = false;
        foreach (var kv in _encoder!.InputMetadata)
        {
            if (kv.Key.Equals("length", StringComparison.OrdinalIgnoreCase)) _encoderHasLengthInput = true;
            else if (kv.Key.Equals("audio_signal", StringComparison.OrdinalIgnoreCase)) _encoderInputName = kv.Key;
        }
        _encoderOutputNames = new[] { "outputs", "encoded_lengths" };

        // State shape [layers, batch, hidden] from the decoder joint input metadata.
        try
        {
            var sd = _decoderJoint!.InputMetadata["input_states_1"].Dimensions.ToArray();
            if (sd.Length >= 3)
            {
                if (sd[0] > 0) _stateLayers = sd[0];
                if (sd[2] > 0) _stateHidden = sd[2];
            }
        }
        catch { /* keep verified defaults */ }
    }

    private void LogMetadata()
    {
        if (_encoder != null)
            foreach (var kv in _encoder.InputMetadata)
                DebugRecorder.Log("INF", $"TDT encoder in: '{kv.Key}' type={kv.Value.ElementType} dims=[{string.Join(",", kv.Value.Dimensions)}]");
        if (_decoderJoint != null)
            foreach (var kv in _decoderJoint.InputMetadata)
                DebugRecorder.Log("INF", $"TDT decoder in: '{kv.Key}' type={kv.Value.ElementType} dims=[{string.Join(",", kv.Value.Dimensions)}]");
        DebugRecorder.Log("DEC", $"TDT vocab loaded={_vocab != null}, size={_vocab?.Length ?? 0}, blank={_blankIdx}, stateLayers={_stateLayers}, stateHidden={_stateHidden}");
    }

    private void LoadVocab(string? vocabPath)
    {
        if (vocabPath == null || !File.Exists(vocabPath)) return;
        try
        {
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

            _vocab = new string[Math.Max(maxIndex + 1, 1)];
            foreach (var (token, index) in entries)
                if (index >= 0 && index < _vocab.Length)
                    _vocab[index] = token;

            _vocabSizeInclBlank = _vocab.Length;
            for (int i = 0; i < _vocab.Length; i++)
                if (_vocab[i] == "<blk>") { _blankIdx = i; break; }
        }
        catch (Exception ex)
        {
            DebugRecorder.Error("DEC", "LoadVocab", ex);
        }
    }

    public string Transcribe(float[] audio)
    {
        if (!IsLoaded || audio.Length < MelScaleFeaturizer.WindowSize)
            return "";

        var normalized = NormalizeForInference(audio, isPartial: false);
        var features = _featurizer.Extract(normalized);
        int rawSamples = normalized.Length;
        return RunInference(features, rawSamples);
    }

    public string TranscribePartial(float[] audio)
    {
        var normalized = NormalizeForInference(audio, isPartial: true);
        var features = _featurizer.Extract(normalized);
        return RunInference(features, normalized.Length);
    }

    public void ResetSession() => _sessionGain = 1.0f;

    private float[] NormalizeForInference(float[] audio, bool isPartial)
    {
        if (audio.Length == 0) return audio;

        var (rawPeak, rawRms) = AudioNormalizer.Measure(audio);
        float[] copy = new float[audio.Length];
        Array.Copy(audio, copy, audio.Length);
        float gain = AudioNormalizer.NormalizeInPlace(copy);
        _sessionGain = gain;

        if (rawRms < 0.003)
            DebugRecorder.Log("AUDIO", $"⚠️ Mic level very low! Raw peak={rawPeak:F4} RMS={rawRms:F4} → boosted {gain:F1}x.");
        else
            DebugRecorder.Log("AUDIO", $"Raw peak={rawPeak:F4} RMS={rawRms:F4} → normalized with {gain:F1}x gain.");

        return copy;
    }

    private string RunInference(float[] features, int rawSamples)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(TdtEngine));
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
        int melBins = _featurizer.MelBands; // 128
        if (total == 0 || total % melBins != 0)
        {
            DebugRecorder.Log("ERR", $"Invalid feature tensor length {total}");
            return "";
        }
        int frames = total / melBins;

        using var inputTensor = OrtValue.CreateTensorValueFromMemory(features, new long[] { 1, melBins, frames });
        var inputs = new Dictionary<string, OrtValue> { [_encoderInputName] = inputTensor };
        OrtValue? lenTensor = null;
        long length = Math.Max(1, rawSamples / (long)MelScaleFeaturizer.HopLength);
        if (_encoderHasLengthInput)
        {
            lenTensor = OrtValue.CreateTensorValueFromMemory(new long[] { length }, new long[] { 1 });
            inputs["length"] = lenTensor;
        }

        var sw = Stopwatch.StartNew();
        DebugRecorder.Log("INF", $"TDT encoder Run: '{_encoderInputName}'=[1,{melBins},{frames}] length={length}");
        using var encResults = _encoder!.Run(_encoderRunOptions, inputs, _encoderOutputNames);
        lenTensor?.Dispose();

        var encShape = encResults[0].GetTensorTypeAndShape().Shape; // [1, D, T']
        int D = encShape.Length >= 2 ? (int)encShape[1] : 0;
        int Tprime = encShape.Length >= 3 ? (int)encShape[2] : 0;
        var encData = encResults[0].GetTensorDataAsSpan<float>().ToArray();
        var encLens = encResults[1].GetTensorDataAsSpan<long>().ToArray();
        int encLen = encLens.Length > 0 ? (int)encLens[0] : Tprime;
        if (encLen > Tprime) encLen = Tprime;

        DebugRecorder.Log("INF", $"TDT encoder done in {sw.ElapsedMilliseconds}ms. encShape=[1,{D},{Tprime}] encLen={encLen}");

        if (D <= 0 || Tprime <= 0 || encLen <= 0)
        {
            DebugRecorder.Log("ERR", $"TDT encoder output invalid (D={D} Tprime={Tprime} encLen={encLen})");
            return "";
        }

        string result = DecodeLoop(encLen, D, Tprime, encData);
        sw.Stop();
        DebugRecorder.Log("INF", $"TDT full inference in {sw.ElapsedMilliseconds}ms");
        return result;
    }

    private string DecodeLoop(int encLen, int D, int Tprime, float[] encData)
    {
        if (_vocab == null || _blankIdx < 0)
        {
            DebugRecorder.Log("ERR", "TDT vocab not loaded — cannot decode. Put vocab.txt next to the model files.");
            return "";
        }

        int stateSize = _stateLayers * 1 * _stateHidden; // [layers, 1, hidden]
        var state1 = new float[stateSize];
        var state2 = new float[stateSize];

        var tokens = new List<int>();
        int t = 0;
        int emittedTokens = 0;
        const int maxTokensPerStep = 10;
        bool loggedNaN = false;

        while (t < encLen)
        {
            // Encoder vector at frame t, reshaped to [1, D, 1].
            var encVec = new float[D];
            for (int d = 0; d < D; d++)
                encVec[d] = encData[d * Tprime + t];
            using var encTensor = OrtValue.CreateTensorValueFromMemory(encVec, new long[] { 1, D, 1 });

            int prevToken = tokens.Count > 0 ? tokens[tokens.Count - 1] : _blankIdx;
            using var targets = OrtValue.CreateTensorValueFromMemory(new int[] { prevToken }, new long[] { 1, 1 });
            using var targetLength = OrtValue.CreateTensorValueFromMemory(new int[] { 1 }, new long[] { 1 });
            using var st1 = OrtValue.CreateTensorValueFromMemory(state1, new long[] { _stateLayers, 1, _stateHidden });
            using var st2 = OrtValue.CreateTensorValueFromMemory(state2, new long[] { _stateLayers, 1, _stateHidden });

            var inputs = new Dictionary<string, OrtValue>
            {
                ["encoder_outputs"] = encTensor,
                ["targets"] = targets,
                ["target_length"] = targetLength,
                ["input_states_1"] = st1,
                ["input_states_2"] = st2,
            };

            using var results = _decoderJoint!.Run(_decoderRunOptions, inputs, _decoderOutputNames);

            var logits = results[0].GetTensorDataAsSpan<float>().ToArray();
            var newState1 = results[1].GetTensorDataAsSpan<float>().ToArray();
            var newState2 = results[2].GetTensorDataAsSpan<float>().ToArray();

            if (!loggedNaN)
            {
                for (int i = 0; i < logits.Length; i++)
                {
                    if (float.IsNaN(logits[i])) { loggedNaN = true; break; }
                }
                if (loggedNaN)
                    DebugRecorder.Log("ERR", "⚠️ TDT joint logits contain NaN — likely the int8 model on AMD DirectML. Use the FP32 encoder/decoder pair.");
            }

            // argmax over vocab (indices 0 .. _vocabSizeInclBlank-1)
            int token = 0;
            float best = float.MinValue;
            int vocabEnd = Math.Min(_vocabSizeInclBlank, logits.Length);
            for (int v = 0; v < vocabEnd; v++)
            {
                if (logits[v] > best) { best = logits[v]; token = v; }
            }

            // duration argmax over the remaining logits (vocabSize .. end)
            int duration = 0;
            float bestDur = float.MinValue;
            for (int v = _vocabSizeInclBlank; v < logits.Length; v++)
            {
                if (logits[v] > bestDur) { bestDur = logits[v]; duration = v - _vocabSizeInclBlank; }
            }

            if (token != _blankIdx)
            {
                state1 = newState1;
                state2 = newState2;
                tokens.Add(token);
                emittedTokens++;
            }

            if (duration > 0)
            {
                t += duration;
                emittedTokens = 0;
            }
            else if (token == _blankIdx || emittedTokens == maxTokensPerStep)
            {
                t += 1;
                emittedTokens = 0;
            }
        }

        DebugRecorder.Log("DEC", $"TDT decode: {tokens.Count} tokens (encLen={encLen})");
        return DecodeTokens(tokens);
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
        return result.Trim();
    }

    private static (string? encoder, string? decoder, string? vocab) ResolveFiles(string modelPath)
    {
        string dir = Path.GetDirectoryName(modelPath) ?? "";
        string fileName = Path.GetFileName(modelPath);
        bool isInt8 = fileName.Contains("int8", StringComparison.OrdinalIgnoreCase);

        string? encoder;
        if (fileName.Contains("encoder", StringComparison.OrdinalIgnoreCase))
        {
            encoder = modelPath;
        }
        else
        {
            var encCandidates = Directory.GetFiles(dir, "encoder-model*.onnx", SearchOption.TopDirectoryOnly);
            encoder = encCandidates.Length > 0
                ? encCandidates.FirstOrDefault(f => isInt8 ? f.Contains("int8") : !f.Contains("int8")) ?? encCandidates[0]
                : null;
        }

        string decoderSuffix = isInt8 ? "decoder_joint-model.int8.onnx" : "decoder_joint-model.onnx";
        string decoderPath = Path.Combine(dir, decoderSuffix);
        string? decoder;
        if (File.Exists(decoderPath))
        {
            decoder = decoderPath;
        }
        else
        {
            var decCandidates = Directory.GetFiles(dir, "decoder_joint-model*.onnx", SearchOption.TopDirectoryOnly);
            decoder = decCandidates.Length > 0 ? decCandidates[0] : null;
        }

        string vocabPath = Path.Combine(dir, "vocab.txt");
        string? vocab = File.Exists(vocabPath) ? vocabPath : null;

        return (encoder, decoder, vocab);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _runGate.Wait();
        try
        {
            _encoder?.Dispose();
            _decoderJoint?.Dispose();
            _encoderRunOptions?.Dispose();
            _decoderRunOptions?.Dispose();
        }
        finally
        {
            _runGate.Release();
            _runGate.Dispose();
        }
    }
}
