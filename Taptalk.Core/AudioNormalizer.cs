using System;

namespace Taptalk.Core;

/// <summary>
/// Rescues quiet microphone input before ASR featurization.
/// The debug log showed Peak=0.0135 / RMS=0.0016 (~-56 dBFS) from real hardware mics —
/// near the noise floor, which the model correctly decodes as all-blank (empty text).
/// Peak-normalize to 0.90 with DC-offset removal and a 30x gain ceiling.
/// </summary>
public static class AudioNormalizer
{
    private const float SilenceFloor = 0.0005f; // ~ -66 dBFS — below this treat as digital silence
    private const float TargetPeak = 0.90f;     // leave 10% headroom
    private const float MaxGainLimit = 30.0f;   // +29.5 dB max boost; beyond this SNR is unrecoverable

    /// <summary>In-place DC-offset removal + peak normalization. Returns the gain applied.</summary>
    public static float NormalizeInPlace(Span<float> samples)
    {
        if (samples.Length == 0) return 1.0f;

        // 1. Remove DC offset (zero-mean) — cheap USB/jack mics often have a constant shift
        //    that gets magnified by gain and ruins featurization.
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i];
        float dcOffset = (float)(sum / samples.Length);

        float peak = 0.0f;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] -= dcOffset;
            float absVal = Math.Abs(samples[i]);
            if (absVal > peak) peak = absVal;
        }

        // 2. Digital silence → do nothing (don't amplify the noise floor into hallucinations)
        if (peak < SilenceFloor)
            return 1.0f;

        float idealGain = TargetPeak / peak;
        float appliedGain = Math.Min(idealGain, MaxGainLimit);

        // 3. Apply gain with hard clamp
        if (Math.Abs(appliedGain - 1.0f) > 0.01f)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float v = samples[i] * appliedGain;
                samples[i] = v > 1.0f ? 1.0f : (v < -1.0f ? -1.0f : v);
            }
        }

        return appliedGain;
    }

    /// <summary>Applies a previously-computed gain to a segment (partials use the session gain,
    /// NOT an independent per-window gain — avoids volume pumping + noise hallucinations).</summary>
    public static void ApplyGainInPlace(Span<float> samples, float gain)
    {
        if (Math.Abs(gain - 1.0f) < 0.01f || samples.Length == 0) return;

        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i] * gain;
            samples[i] = v > 1.0f ? 1.0f : (v < -1.0f ? -1.0f : v);
        }
    }

    /// <summary>Compute peak + RMS of a raw buffer (for diagnostics, before normalization).</summary>
    public static (float peak, double rms) Measure(float[] samples)
    {
        float peak = 0f;
        double sumSq = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            float a = Math.Abs(v);
            if (a > peak) peak = a;
            sumSq += v * v;
        }
        double rms = samples.Length > 0 ? Math.Sqrt(sumSq / samples.Length) : 0;
        return (peak, rms);
    }
}
