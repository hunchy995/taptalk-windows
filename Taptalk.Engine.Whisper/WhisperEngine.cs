using System.Runtime.InteropServices;
using Taptalk.Core;

namespace Taptalk.Engine.Whisper;

/// <summary>
/// whisper.cpp fallback engine via P/Invoke into whisper.dll.
/// Built with AVX-512/AVX2 for modern CPUs.
/// </summary>
public sealed class WhisperEngine : ISttEngine
{
    public string Name => "Whisper (CPU)";
    public bool IsLoaded => _ctx != IntPtr.Zero;
    public bool RequiresModelFile => true;
    public int MinSamplesForPartial => 8000; // 0.5s @16kHz

    private IntPtr _ctx;

    /// <summary>whisper_full on the same context is NOT thread-safe — serialize all calls
    /// (partial timer + full transcription must never run concurrently → native crash).</summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private bool _isDisposed;

    public bool LoadModel(string modelPath)
    {
        DebugRecorder.Log("INF", $"Loading Whisper model: {modelPath} ({new FileInfo(modelPath).Length / 1e6:F0}MB)");
        if (_ctx != IntPtr.Zero) { Native.whisper_free(_ctx); _ctx = IntPtr.Zero; }
        _ctx = Native.whisper_init_from_file(modelPath);
        if (_ctx != IntPtr.Zero)
            DebugRecorder.Log("INF", $"Whisper loaded. Threads={Math.Max(2, Environment.ProcessorCount / 2)}, CPU={Environment.ProcessorCount} cores");
        else
            DebugRecorder.Error("INF", "whisper_init_from_file", new Exception($"Failed to init whisper.cpp with {modelPath}"));
        return _ctx != IntPtr.Zero;
    }

    public string Transcribe(float[] audio)
    {
        if (_ctx == IntPtr.Zero || audio.Length == 0) return "";
        if (_isDisposed) throw new ObjectDisposedException(nameof(WhisperEngine));

        _runGate.Wait();
        try
        {
            return TranscribeCore(audio);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private string TranscribeCore(float[] audio)
    {
        if (_ctx == IntPtr.Zero || audio.Length == 0) return "";

        var nThreads = Math.Max(2, Environment.ProcessorCount / 2);
        var p = Native.whisper_full_default_params((int)SamplingStrategy.WhisperSamplingGreedy);
        p.n_threads = nThreads;
        p.single_segment = 1;
        p.no_context = 1;
        p.suppress_blank = 1;
        p.language = Marshal.StringToHGlobalAnsi("en");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Native.whisper_full(_ctx, p, audio, audio.Length);
        sw.Stop();
        Marshal.FreeHGlobal(p.language);

        int n = Native.whisper_full_n_segments(_ctx);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++)
        {
            IntPtr ptr = Native.whisper_full_get_segment_text(_ctx, i);
            if (ptr != IntPtr.Zero) sb.Append(Marshal.PtrToStringAnsi(ptr));
        }
        var text = sb.ToString().Trim();
        var rtf = audio.Length > 0 ? sw.ElapsedMilliseconds / (audio.Length / 16000.0) / 1000.0 : 0;
        DebugRecorder.Log("INF", $"Whisper inference {sw.ElapsedMilliseconds}ms for {audio.Length / 16000.0:F1}s audio (RTF {rtf:F2}x), segments={n}, text=\"{text}\"");
        return text;
    }

    public string TranscribePartial(float[] audio) => Transcribe(audio);

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // Never free the whisper context mid-inference (native use-after-free crash)
        _runGate.Wait();
        try
        {
            if (_ctx != IntPtr.Zero) { Native.whisper_free(_ctx); _ctx = IntPtr.Zero; }
        }
        finally
        {
            _runGate.Release();
            _runGate.Dispose();
        }
    }

    private enum SamplingStrategy { WhisperSamplingGreedy = 0 }

    private static class Native
    {
        private const string Dll = "whisper.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr whisper_init_from_file(string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void whisper_free(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern WhisperParams whisper_full_default_params(int strategy);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int whisper_full(IntPtr ctx, WhisperParams p, float[] samples, int n_samples);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int whisper_full_n_segments(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr whisper_full_get_segment_text(IntPtr ctx, int i_segment);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WhisperParams
    {
        public int strategy;          // enum whisper_sampling_strategy (int)
        public int n_threads;
        public int n_max_text_ctx;
        public int offset_ms;
        public int duration_ms;
        public byte translate;
        public byte no_context;
        public byte no_timestamps;
        public byte single_segment;
        public byte print_special;
        public byte print_progress;
        public byte print_realtime;
        public byte print_timestamps;
        public byte token_timestamps;
        public float thold_pt;
        public float thold_ptsum;
        public int max_len;
        public byte split_on_word;
        public int max_tokens;
        public byte debug_mode;
        public int audio_ctx;
        public byte tdrz_enable;
        public IntPtr suppress_regex;
        public IntPtr initial_prompt;
        public byte carry_initial_prompt;
        public IntPtr prompt_tokens;
        public int prompt_n_tokens;
        public IntPtr language;
        public byte detect_language;
        public byte suppress_blank;
        public byte suppress_nst;
        public float temperature;
        public float max_initial_ts;
        public float length_penalty;
        public float temperature_inc;
        public float entropy_thold;
        public float logprob_thold;
        public float no_speech_thold;
        public int greedy_best_of;    // struct { int best_of; } greedy;
        public int beam_size;         // struct { int beam_size; float patience; } beam_search;
        public float beam_patience;
        public IntPtr new_segment_callback;
        public IntPtr new_segment_callback_user_data;
        public IntPtr progress_callback;
        public IntPtr progress_callback_user_data;
        public IntPtr encoder_begin_callback;
        public IntPtr encoder_begin_callback_user_data;
        public IntPtr abort_callback;
        public IntPtr abort_callback_user_data;
        public IntPtr logits_filter_callback;
        public IntPtr logits_filter_callback_user_data;
        public IntPtr grammar_rules;
        public ulong n_grammar_rules;
        public ulong i_start_rule;
        public float grammar_penalty;
        public byte vad;
        public IntPtr vad_model_path;
        public float vad_threshold;            // whisper_vad_params (by value)
        public int vad_min_speech_duration_ms;
        public int vad_min_silence_duration_ms;
        public float vad_max_speech_duration_s;
        public int vad_speech_pad_ms;
        public float vad_samples_overlap;
    }
}
