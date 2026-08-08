using NAudio.Wave;

namespace Taptalk.Core;

/// <summary>
/// Captures microphone audio at 16kHz mono into a growing float buffer.
/// Port of the Android AudioRecorder with progressive streaming support.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public const int SampleRate = 16000;

    private WaveInEvent? _waveIn;
    private readonly List<float> _pcmSamples = new();
    private readonly object _lock = new();
    private int _generation; // incremented each Start; stale RecordingStopped events ignored

    // Metrics for the debug logger (updated on the NAudio callback thread)
    private long _totalSamplesCaptured;
    private DateTime? _recordingStartTime;
    private DateTime _lastAudioLogTime = DateTime.MinValue;
    private int _chunksSinceLog;
    private float _maxPeakInWindow;
    private double _rmsSum;
    private int _silenceRunsMs;

    public event Action<float[]>? OnChunk; // raised every ~100ms with new PCM
    public event Action<string>? OnError;  // raised when the capture device fails

    /// <summary>-1 = Windows default recording device (WAVE_MAPPER).</summary>
    public int DeviceNumber { get; set; } = -1;

    public bool IsRecording { get; private set; }
    public int TotalSamples { get { lock (_lock) return _pcmSamples.Count; } }

    /// <summary>Enumerate available input devices (0 = first hardware device).</summary>
    public static List<string> EnumerateDevices()
    {
        var names = new List<string>();
        try
        {
            int count = WaveInEvent.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                names.Add(caps.ProductName);
            }
        }
        catch { /* no devices / enumeration failed */ }
        return names;
    }

    public static string GetDeviceName(int index)
    {
        try
        {
            if (index < 0) return "System Default Microphone";
            var caps = WaveInEvent.GetCapabilities(index);
            return caps.ProductName;
        }
        catch { return $"Device {index}"; }
    }

    public void Start()
    {
        if (IsRecording) return;
        lock (_lock) _pcmSamples.Clear();

        // Reset metrics
        _totalSamplesCaptured = 0;
        _recordingStartTime = DateTime.Now;
        _lastAudioLogTime = DateTime.Now;
        _chunksSinceLog = 0;
        _maxPeakInWindow = 0f;
        _rmsSum = 0;
        _silenceRunsMs = 0;

        var gen = ++_generation;
        DebugRecorder.Log("REC", $"Initializing capture: DeviceIdx={DeviceNumber} Name='{GetDeviceName(DeviceNumber)}' 16kHz mono 100ms buffers");

        var waveIn = new WaveInEvent
        {
            DeviceNumber = DeviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 100 // lower latency chunks for agile VAD response
        };
        waveIn.DataAvailable += OnDataAvailable;
        waveIn.RecordingStopped += (_, args) =>
        {
            // Ignore events from a previous (stale) capture session
            if (gen == _generation)
            {
                IsRecording = false;
                LogStopMetrics();
                if (args.Exception != null)
                {
                    DebugRecorder.Error("REC", "capture stopped with exception", args.Exception);
                    OnError?.Invoke(args.Exception.Message);
                }
            }
        };
        _waveIn = waveIn;
        waveIn.StartRecording();
        IsRecording = true;
        DebugRecorder.Log("REC", $"Recording started at {DateTime.Now:HH:mm:ss.fff}");
    }

    public void Stop()
    {
        IsRecording = false;
        var w = _waveIn;
        _waveIn = null;
        if (w != null)
        {
            try
            {
                w.StopRecording();
                w.Dispose();
            }
            catch { }
        }
    }

    private void LogStopMetrics()
    {
        double elapsedMs = _recordingStartTime.HasValue
            ? (DateTime.Now - _recordingStartTime.Value).TotalMilliseconds
            : 0;
        double actualSec = _totalSamplesCaptured / (double)SampleRate;
        double measuredRate = elapsedMs > 0
            ? _totalSamplesCaptured / (elapsedMs / 1000.0)
            : 0;
        DebugRecorder.Log("REC",
            $"Capture stopped. Samples={_totalSamplesCaptured} | Audio={actualSec:F2}s | Active={elapsedMs / 1000.0:F2}s | Rate={measuredRate:F0} samples/s (target {SampleRate})");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsRecording) return;

        var samples = new float[e.BytesRecorded / 2];
        float peak = 0f;
        double sumSq = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            // 16-bit little-endian PCM → float [-1, 1]
            short s = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
            float v = s / 32768f;
            samples[i] = v;
            float abs = Math.Abs(v);
            if (abs > peak) peak = abs;
            sumSq += v * v;
        }

        double rms = samples.Length > 0 ? Math.Sqrt(sumSq / samples.Length) : 0;

        lock (_lock) _pcmSamples.AddRange(samples);
        _totalSamplesCaptured += samples.Length;
        _chunksSinceLog++;
        if (peak > _maxPeakInWindow) _maxPeakInWindow = peak;
        _rmsSum += rms;

        // Throttled audio metrics every ~1s (10 chunks of 100ms)
        var now = DateTime.Now;
        if ((now - _lastAudioLogTime).TotalMilliseconds >= 1000)
        {
            double avgRms = _chunksSinceLog > 0 ? _rmsSum / _chunksSinceLog : 0;
            double totalSec = _totalSamplesCaptured / (double)SampleRate;
            DebugRecorder.Log("AUDIO",
                $"Buffered={totalSec:F2}s | Chunks={_chunksSinceLog} | Peak={_maxPeakInWindow:F4} | AvgRMS={avgRms:F4} | Silence={_silenceRunsMs}ms");
            _lastAudioLogTime = now;
            _chunksSinceLog = 0;
            _maxPeakInWindow = 0f;
            _rmsSum = 0;
        }

        OnChunk?.Invoke(samples);
    }

    /// <summary>Called by VAD on the callback thread when silence is detected (never blocks).</summary>
    public void NoteSilence(int ms) => _silenceRunsMs = ms;

    /// <summary>Snapshot of all PCM captured so far (zero-copy copy).</summary>
    public float[] GetSnapshot()
    {
        lock (_lock) return _pcmSamples.ToArray();
    }

    public void Dispose() => Stop();
}
