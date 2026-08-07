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

    public event Action<float[]>? OnChunk; // raised every ~250ms with new PCM
    public event Action<string>? OnError;  // raised when the capture device fails

    /// <summary>0 = Windows default input device.</summary>
    public int DeviceIndex { get; set; }

    public bool IsRecording { get; private set; }
    public int TotalSamples { get { lock (_lock) return _pcmSamples.Count; } }

    /// <summary>Enumerate available input devices (0 = default).</summary>
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
        if (names.Count == 0) names.Add("Default Microphone");
        return names;
    }

    public void Start()
    {
        if (IsRecording) return;
        lock (_lock) _pcmSamples.Clear();

        var gen = ++_generation;
        var waveIn = new WaveInEvent
        {
            DeviceNumber = DeviceIndex,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 250
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
        var w = _waveIn;
        _waveIn = null;
        w?.StopRecording();
        w?.Dispose();
        IsRecording = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
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
