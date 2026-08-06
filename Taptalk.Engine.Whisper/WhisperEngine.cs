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

    private IntPtr _ctx;

    public bool LoadModel(string modelPath)
    {
        if (_ctx != IntPtr.Zero) { Native.whisper_free(_ctx); _ctx = IntPtr.Zero; }
        _ctx = Native.whisper_init_from_file(modelPath);
        return _ctx != IntPtr.Zero;
    }

    public string Transcribe(float[] audio)
    {
        if (_ctx == IntPtr.Zero || audio.Length == 0) return "";

        var nThreads = Math.Max(2, Environment.ProcessorCount / 2);
        var p = Native.whisper_full_default_params((int)SamplingStrategy.WhisperSamplingGreedy);
        p.n_threads = nThreads;
        p.single_segment = true;
        p.no_context = true;
        p.suppress_blank = true;
        p.language = Marshal.StringToHGlobalAnsi("en");

        Native.whisper_full(_ctx, ref p, audio, (uint)audio.Length);
        Marshal.FreeHGlobal(p.language);

        int n = Native.whisper_full_n_segments(_ctx);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++)
        {
            IntPtr ptr = Native.whisper_full_get_segment_text(_ctx, i);
            if (ptr != IntPtr.Zero) sb.Append(Marshal.PtrToStringAnsi(ptr));
        }
        return sb.ToString().Trim();
    }

    public string TranscribePartial(float[] audio) => Transcribe(audio);

    public void Dispose()
    {
        if (_ctx != IntPtr.Zero) { Native.whisper_free(_ctx); _ctx = IntPtr.Zero; }
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
        public static extern int whisper_full(IntPtr ctx, ref WhisperParams p, float[] samples, uint n_samples);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int whisper_full_n_segments(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr whisper_full_get_segment_text(IntPtr ctx, int i_segment);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WhisperParams
    {
        public int strategy;
        public int n_threads;
        public int n_max_text_ctx;
        public int offset_ms;
        public int duration_ms;
        public int translate;
        public int no_context;
        public int single_segment;
        public int print_special;
        public int print_progress;
        public int print_realtime;
        public int print_timestamps;
        public int token_timestamps;
        public int thold_pt;
        public int thold_ptsum;
        public int max_len;
        public int split_on_word;
        public int max_tokens;
        public int debug_mode;
        public int audio_ctx;
        public int tdrz_enable;
        public IntPtr language;
        public int suppress_blank;
        public IntPtr suppress_tokens;
        public int suppress_nst;
        public int temperature;
        public float max_initial_ts;
        public float length_penalty;
        public float temperature_inc;
        public float entropy_thold;
        public float logprob_thold;
        public float no_speech_thold;
        public IntPtr grammar_rules;
        public ulong n_grammar_rules;
        public int i_start_rule;
        public int grammar_penalty;
        public int n_threads_batch;
        public int speed_up;
        public int debug_mode_timestamps;
        public int token_timestamps_thold_pt;
        public int flash_attn;
        public int gpu_device;
        public int no_timestamps;
        public int use_gpu;
        public int greedy_best_of;
        public int beam_size;
        public float beam_patience;
        public IntPtr seq;
        public int n_seq;
        public int last_n_tokens;
    }
}
