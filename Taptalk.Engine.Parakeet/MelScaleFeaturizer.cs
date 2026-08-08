namespace Taptalk.Engine.Parakeet;

/// <summary>
/// Log-Mel spectrogram featurizer for Parakeet ONNX models.
/// 80 mel bands, 25ms window, 10ms hop, 512-point FFT, Hann window.
/// </summary>
public sealed class MelScaleFeaturizer
{
    public const int SampleRate = 16000;
    public const int FftSize = 512;
    public const int HopSize = 160;     // 10ms
    public const int WindowSize = 400;  // 25ms
    public const int MelBins = 80;

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
    /// Extract log-mel features. Returns [melBins, numFrames] float array (transposed for ONNX [1,80,T]).
    /// </summary>
    public float[,] Extract(float[] pcm)
    {
        int numFrames = Math.Max(0, (pcm.Length - WindowSize) / HopSize + 1);
        Taptalk.Core.DebugRecorder.Log("FEAT", $"Extract: {pcm.Length} samples ({pcm.Length / (float)SampleRate:F2}s) → frames={numFrames}");
        var features = new float[MelBins, numFrames];

        var real = new float[FftSize];
        var imag = new float[FftSize];

        for (int frame = 0; frame < numFrames; frame++)
        {
            int start = frame * HopSize;

            // Apply Hann window
            Array.Clear(real);
            Array.Clear(imag);
            for (int i = 0; i < WindowSize; i++)
                real[i] = pcm[start + i] * _hannWindow[i];

            // Simple radix-2 FFT (size 512)
            FFT(real, imag, FftSize);

            // Power spectrum
            var power = new float[FftSize / 2 + 1];
            for (int i = 0; i < power.Length; i++)
                power[i] = real[i] * real[i] + imag[i] * imag[i];

            // Mel filterbank
            for (int m = 0; m < MelBins; m++)
            {
                double sum = 0;
                for (int j = 0; j < power.Length; j++)
                    sum += power[j] * _melFilters[m][j];
                features[m, frame] = (float)Math.Log(Math.Max(sum, 1e-5));
            }
        }
        return features;
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
