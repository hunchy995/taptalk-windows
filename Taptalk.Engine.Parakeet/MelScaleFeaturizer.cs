namespace Taptalk.Engine.Parakeet;

/// <summary>
/// Log-Mel spectrogram featurizer matching the official onnx-asr NeMo preprocessor
/// (https://github.com/istupakov/onnx-asr). This is the exact preprocessing the
/// exported Parakeet CTC model was trained with.
///
/// Pipeline:
/// 1. Pre-emphasis (coefficient 0.97) on [-1,1] float waveform
/// 2. Constant zero pad by n_fft/2 on both sides (matches np.pad default)
/// 3. 512-point FFT with a 400-point Hann window zero-padded to 512
/// 4. Slaney mel scale filterbank (80 bands) with Slaney bandwidth normalization
/// 5. log(mel + 2^-24)
/// 6. Per-feature instance normalization across valid frames (zero-mean / std)
///
/// Output layout: flattened [1, 80, frames]
/// </summary>
public sealed class MelScaleFeaturizer
{
    public const int SampleRate = 16000;
    public const int WindowSize = 400;   // win_length
    public const int HopLength = 160;
    public const int FftSize = 512;      // n_fft
    public const int MelBands = 80;
    public const float Preemphasis = 0.97f;
    public const float LogZeroGuard = 5.96046448e-08f; // 2^-24

    private readonly float[] _window = new float[FftSize];
    private readonly float[,] _melFilterbank; // [257, 80]

    public MelScaleFeaturizer()
    {
        // 400-point symmetric Hann window, then zero-pad to 512 (centered).
        var hann400 = CreateHannWindow(WindowSize);
        int pad = (FftSize - WindowSize) / 2; // 56
        for (int i = 0; i < WindowSize; i++)
            _window[pad + i] = hann400[i];

        _melFilterbank = BuildSlaneyMelFilterbank(
            nFreqs: FftSize / 2 + 1,
            fMin: 0f,
            fMax: SampleRate / 2f,
            nMels: MelBands,
            sampleRate: SampleRate,
            norm: true);
    }

    /// <summary>Convert mono 16kHz float PCM (range roughly [-1,1]) to NeMo log-mel features.</summary>
    public float[] Extract(float[] waveform)
    {
        if (waveform == null || waveform.Length < WindowSize)
            return Array.Empty<float>();

        // 1. Pre-emphasis: x[t] - 0.97 * x[t-1], with x[-1] = 0
        float[] pcm = new float[waveform.Length];
        pcm[0] = waveform[0];
        for (int i = 1; i < waveform.Length; i++)
            pcm[i] = waveform[i] - Preemphasis * waveform[i - 1];

        // 2. Constant zero pad by FftSize/2 on each side (matches reference np.pad default)
        int pad = FftSize / 2;
        int paddedLen = pcm.Length + 2 * pad;
        float[] padded = new float[paddedLen];
        Array.Copy(pcm, 0, padded, pad, pcm.Length);

        // 3. Number of frames after padding (reference produces len/hop + 1 and masks last)
        int frames = (paddedLen - FftSize) / HopLength + 1;
        if (frames <= 0) return Array.Empty<float>();

        // 4. Compute log-mel spectrogram [frames, MelBands]
        float[,] logMel = new float[frames, MelBands];
        float[] real = new float[FftSize];
        float[] imag = new float[FftSize];

        for (int t = 0; t < frames; t++)
        {
            int start = t * HopLength;
            Array.Clear(real);
            Array.Clear(imag);
            for (int i = 0; i < FftSize; i++)
                real[i] = padded[start + i] * _window[i];

            Fft(real, imag, FftSize);

            for (int m = 0; m < MelBands; m++)
            {
                double melEnergy = 0.0;
                for (int b = 0; b < FftSize / 2 + 1; b++)
                {
                    double power = real[b] * real[b] + imag[b] * imag[b];
                    melEnergy += power * _melFilterbank[b, m];
                }
                logMel[t, m] = (float)Math.Log(Math.Max(melEnergy, 0.0) + LogZeroGuard);
            }
        }

        // 5. Per-feature instance normalization across valid frames.
        // Valid length in frames equals waveform_len // hop_length (reference).
        int validFrames = waveform.Length / HopLength;
        if (validFrames <= 0) validFrames = frames;
        if (validFrames > frames) validFrames = frames;

        float[] means = new float[MelBands];
        float[] vars = new float[MelBands];
        for (int m = 0; m < MelBands; m++)
        {
            double sum = 0;
            for (int t = 0; t < validFrames; t++) sum += logMel[t, m];
            means[m] = (float)(sum / validFrames);

            double sq = 0;
            for (int t = 0; t < validFrames; t++)
            {
                float d = logMel[t, m] - means[m];
                sq += d * d;
            }
            vars[m] = (float)(sq / Math.Max(1, validFrames - 1));
        }

        // 6. Transpose to [1, MelBands, frames] and normalize only valid frames; leave padding frames as-is
        float[] features = new float[1 * MelBands * frames];
        int idx = 0;
        for (int m = 0; m < MelBands; m++)
            for (int t = 0; t < frames; t++)
            {
                float v = t < validFrames ? (logMel[t, m] - means[m]) / (MathF.Sqrt(vars[m]) + 1e-5f) : 0f;
                features[idx++] = v;
            }

        return features;
    }

    /// <summary>Number of valid frames this waveform produces (matches reference length formula).</summary>
    public int FrameCount(int sampleCount) => sampleCount > 0 ? (sampleCount / HopLength) : 0;

    private static float[] CreateHannWindow(int size)
    {
        var w = new float[size];
        for (int i = 0; i < size; i++)
            w[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (size - 1)));
        return w;
    }

    private static float[,] BuildSlaneyMelFilterbank(int nFreqs, float fMin, float fMax, int nMels, int sampleRate, bool norm)
    {
        // Slaney mel scale (matches torchaudio/librosa / onnx-asr reference)
        double mMin = HzToMel(fMin);
        double mMax = HzToMel(fMax);
        double[] mPts = new double[nMels + 2];
        for (int i = 0; i < nMels + 2; i++)
            mPts[i] = mMin + i * (mMax - mMin) / (nMels + 1);

        double[] hzPts = new double[nMels + 2];
        for (int i = 0; i < nMels + 2; i++)
            hzPts[i] = MelToHz(mPts[i]);

        int[] bins = new int[nMels + 2];
        for (int i = 0; i < nMels + 2; i++)
            bins[i] = (int)Math.Floor((nFreqs - 1) * hzPts[i] / (sampleRate / 2.0));

        var fb = new float[nFreqs, nMels];
        for (int i = 0; i < nMels; i++)
        {
            int from = Math.Max(0, bins[i]);
            int to = Math.Min(nFreqs, bins[i + 2] + 1);
            for (int j = from; j < to; j++)
            {
                double left = (j - bins[i]) / (double)(bins[i + 1] - bins[i]);
                double right = (bins[i + 2] - j) / (double)(bins[i + 2] - bins[i + 1]);
                double v = Math.Max(0, Math.Min(left, right));
                fb[j, i] = (float)v;
            }
        }

        if (norm)
        {
            for (int i = 0; i < nMels; i++)
            {
                double width = hzPts[i + 2] - hzPts[i];
                float scale = width > 0 ? (float)(2.0 / width) : 1f;
                for (int j = 0; j < nFreqs; j++)
                    fb[j, i] *= scale;
            }
        }

        return fb;
    }

    private static double HzToMel(float hz)
    {
        // Slaney mel scale: linear below 1kHz, log above
        if (hz < 1000f)
            return 3.0 * hz / 200.0;
        return 15.0 + 27.0 * Math.Log(hz / 1000.0 + double.Epsilon) / Math.Log(6.4);
    }

    private static double MelToHz(double mel)
    {
        if (mel < 15.0)
            return 200.0 * mel / 3.0;
        return 1000.0 * Math.Pow(6.4, (mel - 15.0) / 27.0);
    }

    private static void Fft(float[] real, float[] imag, int n)
    {
        // Cooley-Tukey radix-2 iterative FFT
        int j = 0;
        for (int i = 0; i < n - 1; i++)
        {
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
            int k = n >> 1;
            while (k <= j) { j -= k; k >>= 1; }
            j += k;
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            float ang = -2f * MathF.PI / len;
            float wlr = MathF.Cos(ang);
            float wli = MathF.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float ur = 1f, ui = 0f;
                for (int m = 0; m < len / 2; m++)
                {
                    int even = i + m;
                    int odd = i + m + len / 2;
                    float tr = ur * real[odd] - ui * imag[odd];
                    float ti = ur * imag[odd] + ui * real[odd];
                    real[odd] = real[even] - tr;
                    imag[odd] = imag[even] - ti;
                    real[even] += tr;
                    imag[even] += ti;
                    float t = ur * wlr - ui * wli;
                    ui = ur * wli + ui * wlr;
                    ur = t;
                }
            }
        }
    }
}
