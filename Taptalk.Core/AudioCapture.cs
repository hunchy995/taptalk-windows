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

    public event Action<float[]>? OnChunk; // raised every ~250ms with new PCM

    public bool IsRecording { get; private set; }
    public int TotalSamples { get { lock (_lock) return _pcmSamples.Count; } }

    public void Start()
    {
        if (IsRecording) return;
        lock (_lock) _pcmSamples.Clear();

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 250
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += (_, _) => IsRecording = false;
        _waveIn.StartRecording();
        IsRecording = true;
    }

    public void Stop()
    {
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
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
