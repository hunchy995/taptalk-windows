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

    public void Start()
    {
        if (IsRecording) return;
        lock (_lock) _pcmSamples.Clear();

        var gen = ++_generation;
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
                if (args.Exception != null)
                    OnError?.Invoke(args.Exception.Message);
            }
        };
        _waveIn = waveIn;
        waveIn.StartRecording();
        IsRecording = true;
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

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsRecording) return;

        var samples = new float[e.BytesRecorded / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            // 16-bit little-endian PCM → float [-1, 1]
            short s = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }

        lock (_lock) _pcmSamples.AddRange(samples);
        OnChunk?.Invoke(samples);
    }

    /// <summary>Snapshot of all PCM captured so far (zero-copy copy).</summary>
    public float[] GetSnapshot()
    {
        lock (_lock) return _pcmSamples.ToArray();
    }

    public void Dispose() => Stop();
}
