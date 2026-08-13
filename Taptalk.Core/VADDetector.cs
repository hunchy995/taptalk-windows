namespace Taptalk.Core;

/// <summary>
/// Energy-based VAD: computes RMS of the most recent audio window.
/// Auto-stops recording after N ms of silence below threshold.
/// Port of the Android VAD.
/// </summary>
public sealed class VADDetector
{
    public float SilenceThreshold { get; set; } = 0.01f;  // RMS below this = silence
    public int SilenceDurationMs { get; set; } = 1500;

    private long _silenceStartMs;
    private int _lastSampleCount;
    private int _lastCheckMs;
    private readonly AudioCapture _capture;

    public VADDetector(AudioCapture capture) => _capture = capture;

    public float GetRMS(float[] audio, int windowSamples = 1600)
    {
        if (audio.Length == 0) return 0;
        var start = Math.Max(0, audio.Length - windowSamples);
        double sumSq = 0;
        for (int i = start; i < audio.Length; i++)
            sumSq += audio[i] * audio[i];
        return (float)Math.Sqrt(sumSq / Math.Max(1, audio.Length - start));
    }

    /// <summary>
    /// Call every ~250ms while recording. Returns true when silence threshold exceeded.
    /// Legacy overload that computes RMS from the full audio buffer (used for full-snapshot checks).
    /// </summary>
    public bool Check(float[] audio, int nowMs)
    {
        var total = audio.Length;
        if (total < _lastSampleCount + 2000) return false; // not enough new audio
        _lastSampleCount = total;
        return Check(GetRMS(audio), nowMs);
    }

    /// <summary>
    /// Preferred overload for streaming chunks. Caller computes RMS from the incoming chunk;
    /// this avoids expensive GetSnapshot() calls on every callback.
    /// </summary>
    public bool Check(float rms, int nowMs)
    {
        // Don't allow checks more often than ~100ms to avoid jitter from a single quiet chunk.
        if (nowMs - _lastCheckMs < 100) return false;
        _lastCheckMs = nowMs;

        if (rms < SilenceThreshold)
        {
            if (_silenceStartMs == 0)
                _silenceStartMs = nowMs;
            else if (nowMs - _silenceStartMs >= SilenceDurationMs)
                return true; // auto-stop
        }
        else
        {
            _silenceStartMs = 0;
        }
        return false;
    }

    public void Reset()
    {
        _silenceStartMs = 0;
        _lastSampleCount = 0;
        _lastCheckMs = 0;
    }
}
