namespace Taptalk.Engine.Parakeet;

/// <summary>
/// Log-Mel spectrogram featurizer for NVIDIA Parakeet (NeMo) ONNX models.
/// 80 mel bands, 25ms window, 10ms hop, 512-point FFT, Hann window,
/// pre-emphasis, and per-feature instance normalization.
/// </summary>
public sealed class MelScaleFeaturizer
{
    public const int SampleRate = 16000;
    public const int FftSize = 512;
    public const int HopSize = 160;     // 10ms
    public const int WindowSize = 400;  // 25ms
    public const int MelBins = 80;

    /// <summary>
    /// NeMo models are trained on 16-bit PCM scaled to the original integer range.
    /// Scaling before STFT aligns the log-mel energies with the training distribution.
    /// </summary>
    public const float NeMoScale = 32768.0f;

    /// <summary>
    /// Pre-emphasis coefficient. Standard value for ASR front-ends.
    /// </summary>
    public const float PreEmphasis = 0.97f;

    private readonly double[][] _melFilters;
    private readonly float[] _hannWindow;

    public MelScaleFeaturizer()
    {
        _melFilters = CreateMelFilters();
        _hannWindow = CreateHannWindow(WindowSize);
    }

    private static float[] CreateHannWindow(int size)
    {
        var w = new float[size];
        for (int i = 0; i < size; i++)
            w[i] = 0.5f * (1 - (float)Math.Cos(2 * Math.PI * i / (size - 1)));
        return w;
    }

    private static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
    private static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

    private double[][] CreateMelFilters()
    {
        var filters = new double[MelBins][];
        double minMel = HzToMel(0);
        double maxMel = HzToMel(SampleRate / 2.0);

        var melPoints = new double[MelBins + 2];
        for (int i = 0; i < melPoints.Length; i++)
            melPoints[i] = minMel + i * (maxMel - minMel) / (MelBins + 1);

        var hzPoints = new double[melPoints.Length];
        for (int i = 0; i < hzPoints.Length; i++)
            hzPoints[i] = MelToHz(melPoints[i]);

        var bins = new int[hzPoints.Length];
        for (int i = 0; i < bins.Length; i++)
            bins[i] = (int)Math.Floor((FftSize + 1) * hzPoints[i] / SampleRate);

        for (int i = 0; i < MelBins; i++)
        {
            filters[i] = new double[FftSize / 2 + 1];
            if (bins[i + 1] == bins[i]) continue;
            for (int j = bins[i]; j < bins[i + 1]; j++)
                filters[i][j] = (j - bins[i]) / (double)(bins[i + 1] - bins[i]);
            if (bins[i + 2] == bins[i + 1]) continue;
            for (int j = bins[i + 1]; j < bins[i + 2] && j < filters[i].Length; j++)
                filters[i][j] = (bins[i + 2] - j) / (double)(bins[i + 2] - bins[i + 1]);
        }
        return filters;
    }

    /// <summary>
    /// Extract NeMo-compatible log-mel features.
    /// Returns [melBins, numFrames] float array (transposed for ONNX [1,80,T]).
    /// </summary>
    public float[,] Extract(float[] pcm)
    {
        int numFrames = Math.Max(0, (pcm.Length - WindowSize) / HopSize + 1);
        Taptalk.Core.DebugRecorder.Log("FEAT", $"Extract: {pcm.Length} samples ({pcm.Length / (float)SampleRate:F2}s) → frames={numFrames}");

        if (numFrames == 0) return new float[MelBins, 0];

        var features = new float[MelBins, numFrames];
        var real = new float[FftSize];
        var imag = new float[FftSize];
        var power = new float[FftSize / 2 + 1];

        // Pre-emphasize and scale on the fly while copying windowed samples.
        // We keep a local previous-sample variable; the first sample of each
        // frame still gets the previous raw sample from the original waveform.
        for (int frame = 0; frame < numFrames; frame++)
        {
            int start = frame * HopSize;

            Array.Clear(real);
            Array.Clear(imag);

            for (int i = 0; i < WindowSize; i++)
            {
                float sample = pcm[start + i];
                if (i > 0)
                    sample -= PreEmphasis * pcm[start + i - 1];
                real[i] = sample * NeMoScale * _hannWindow[i];
            }

            FFT(real, imag, FftSize);

            // Power spectrum
            for (int i = 0; i < power.Length; i++)
                power[i] = real[i] * real[i] + imag[i] * imag[i];

            // Mel filterbank
            for (int m = 0; m < MelBins; m++)
            {
                double sum = 0;
                var filter = _melFilters[m];
                for (int j = 0; j < power.Length; j++)
                    sum += power[j] * filter[j];
                features[m, frame] = (float)Math.Log(Math.Max(sum, 1e-5));
            }
        }

        // Per-feature instance normalization across time (NeMo default normalize=True).
        NormalizePerFeature(features);

        return features;
    }

    /// <summary>
    /// For each mel band compute mean/std across all frames, then z-score.
    /// </summary>
    private static void NormalizePerFeature(float[,] features)
    {
        int bins = features.GetLength(0);
        int frames = features.GetLength(1);
        if (frames == 0) return;

        for (int b = 0; b < bins; b++)
        {
            double sum = 0;
            for (int f = 0; f < frames; f++) sum += features[b, f];
            double mean = sum / frames;

            double sq = 0;
            for (int f = 0; f < frames; f++)
            {
                double d = features[b, f] - mean;
                sq += d * d;
            }
            double std = Math.Sqrt(sq / frames) + 1e-5;

            for (int f = 0; f < frames; f++)
                features[b, f] = (float)((features[b, f] - mean) / std);
        }
    }

    private static void FFT(float[] real, float[] imag, int n)
    {
        // Bit-reversal permutation
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            float ang = -2f * (float)Math.PI / len;
            float wRe = (float)Math.Cos(ang);
            float wIm = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float curRe = 1, curIm = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    int idx = i + j;
                    int other = idx + len / 2;
                    float tRe = curRe * real[other] - curIm * imag[other];
                    float tIm = curRe * imag[other] + curIm * real[other];
                    real[other] = real[idx] - tRe;
                    imag[other] = imag[idx] - tIm;
                    real[idx] += tRe;
                    imag[idx] += tIm;
                    (curRe, curIm) = (curRe * wRe - curIm * wIm, curRe * wIm + curIm * wRe);
                }
            }
        }
    }
}
