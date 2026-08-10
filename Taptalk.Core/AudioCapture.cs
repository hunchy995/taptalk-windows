using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Taptalk.Core;

/// <summary>
/// Captures microphone audio at 16kHz mono into a growing float buffer.
///
/// ARCHITECTURE (final, Aug 9 2026): RAW-capture + post-conversion.
/// The initial WASAPI design used a LIVE streaming pipeline
/// (BufferedWaveProvider → WdlResamplingSampleProvider → SampleToWaveProvider16)
/// drained on every DataAvailable. On the user's machine that pipeline produced
/// ZERO samples despite raw bytes arriving (FIRST CHUNK logged, Samples=0 at stop) —
/// the WDL resampler buffers internally and the live read loop never yielded.
/// Fix: capture RAW native bytes into a growing buffer; convert the WHOLE recording
/// deterministically in GetSnapshot()/Stop() (RawSourceWaveStream → ToSampleProvider
/// → mono → 16kHz → 16-bit PCM). VAD gets a lightweight inline RMS from the raw float
/// bytes (energy detection needs no resampling). This is immune to live-stream stalls.
///
/// ALSO (regression history):
/// - MME/WaveInEvent misreads 32-bit-float mics as silence → WASAPI.
/// - useEventSync:true never fires on AMD/Realtek/USB drivers → polling mode.
/// - Stop(): sever ref + unsubscribe FIRST + time-box StopRecording (2s) + orphan.
/// - OnRecordingStopped never disposes from the capture thread (self-join deadlock).
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public const int SampleRate = 16000;

    private WasapiCapture? _capture;
    private readonly object _lock = new();
    private readonly List<byte> _rawBytes = new();          // native-format raw bytes
    private WaveFormat? _nativeFormat;
    private MMDevice? _selectedDevice;

    // Metrics (updated on the WASAPI callback thread)
    private long _totalBytesReceived;
    private DateTime? _recordingStartTime;
    private DateTime _lastAudioLogTime = DateTime.MinValue;
    private int _chunksSinceLog;
    private float _maxPeakInWindow;
    private double _rmsSum;
    private int _silenceRunsMs;
    private string _negotiatedFormat = "";
    private bool _firstChunkLogged;
    private bool _isStopping;

    /// <summary>Raised per chunk with float32 samples at the NATIVE rate (for VAD energy only).</summary>
    public event Action<float[]>? OnChunk;
    public event Action<string>? OnError;

    /// <summary>-1 = Windows default recording device.</summary>
    public int DeviceNumber { get; set; } = -1;

    public bool IsRecording { get; private set; }

    /// <summary>Approximate 16kHz-mono sample count (for the no-audio watchdog).</summary>
    public int TotalSamples
    {
        get
        {
            lock (_lock)
            {
                if (_nativeFormat == null || _nativeFormat.Channels == 0) return 0;
                long frames = _rawBytes.Count / Math.Max(1, _nativeFormat.BitsPerSample / 8);
                long perChannel = frames / Math.Max(1, _nativeFormat.Channels);
                return (int)(perChannel * SampleRate / Math.Max(1, _nativeFormat.SampleRate));
            }
        }
    }

    public static List<string> EnumerateDevices()
    {
        var names = new List<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var d in devices) names.Add(d.FriendlyName);
        }
        catch { }
        return names;
    }

    public static string GetDeviceName(int index)
    {
        if (index < 0) return "System Default Microphone";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            return index < devices.Count ? devices[index].FriendlyName : $"Device {index}";
        }
        catch { return $"Device {index}"; }
    }

    public void Start()
    {
        if (IsRecording) return;

        lock (_lock)
        {
            _rawBytes.Clear();
            _nativeFormat = null;
            _totalBytesReceived = 0;
            _recordingStartTime = DateTime.Now;
            _lastAudioLogTime = DateTime.Now;
            _chunksSinceLog = 0;
            _maxPeakInWindow = 0f;
            _rmsSum = 0;
            _silenceRunsMs = 0;
            _negotiatedFormat = "";
            _firstChunkLogged = false;
            _isStopping = false;
        }

        DebugRecorder.Log("REC", $"Initializing WASAPI capture: DeviceIdx={DeviceNumber} Name='{GetDeviceName(DeviceNumber)}'");

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (DeviceNumber < 0)
                _selectedDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            else
            {
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                _selectedDevice = DeviceNumber < devices.Count
                    ? devices[DeviceNumber]
                    : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            if (_selectedDevice == null)
                throw new InvalidOperationException("No microphone device found.");

            // POLLING mode — event-sync never fires on AMD/Realtek/USB drivers (regression Aug 9).
            _capture = new WasapiCapture(_selectedDevice, useEventSync: false);

            var native = _capture.WaveFormat;
            _nativeFormat = native;
            _negotiatedFormat = $"{native.SampleRate}Hz {native.BitsPerSample}-bit {native.Channels}ch ({native.Encoding})";
            DebugRecorder.Log("REC", $"WASAPI native format: {_negotiatedFormat}");

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            IsRecording = true;
            DebugRecorder.Log("REC", $"Recording started at {DateTime.Now:HH:mm:ss.fff}");
        }
        catch (Exception ex)
        {
            DebugRecorder.Error("REC", "WASAPI start failed", ex);
            OnError?.Invoke(ex.Message);
            Cleanup();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _isStopping) return;

        if (!_firstChunkLogged)
        {
            _firstChunkLogged = true;
            DebugRecorder.Log("REC", $"FIRST CHUNK: {e.BytesRecorded} raw bytes arrived in callback");
        }

        lock (_lock)
        {
            if (_isStopping || _rawBytes == null) return;
            _rawBytes.AddRange(new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded).ToArray());
            _totalBytesReceived += e.BytesRecorded;
        }

        // Lightweight inline RMS for VAD energy (native float bytes → floats; no resample)
        var fmt = _nativeFormat;
        float[]? energyChunk = null;
        if (fmt != null && fmt.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            int bytes = e.BytesRecorded / 4 * 4;
            energyChunk = new float[bytes / 4];
            for (int i = 0; i < bytes / 4; i++)
                energyChunk[i] = BitConverter.ToSingle(e.Buffer, i * 4);
        }
        else if (fmt != null && fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            int bytes = e.BytesRecorded / 2 * 2;
            energyChunk = new float[bytes / 2];
            for (int i = 0; i < bytes / 2; i++)
                energyChunk[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        }

        if (energyChunk != null)
        {
            float peak = 0f; double sumSq = 0;
            foreach (var v in energyChunk)
            {
                float abs = Math.Abs(v);
                if (abs > peak) peak = abs;
                sumSq += v * v;
            }
            double rms = energyChunk.Length > 0 ? Math.Sqrt(sumSq / energyChunk.Length) : 0;

            _chunksSinceLog++;
            if (peak > _maxPeakInWindow) _maxPeakInWindow = peak;
            _rmsSum += rms;

            var now = DateTime.Now;
            if ((now - _lastAudioLogTime).TotalMilliseconds >= 1000)
            {
                double avgRms = _chunksSinceLog > 0 ? _rmsSum / _chunksSinceLog : 0;
                DebugRecorder.Log("AUDIO",
                    $"Bytes={_totalBytesReceived} | Chunks={_chunksSinceLog} | Peak={_maxPeakInWindow:F4} | AvgRMS={avgRms:F4} | Silence={_silenceRunsMs}ms");
                _lastAudioLogTime = now;
                _chunksSinceLog = 0;
                _maxPeakInWindow = 0f;
                _rmsSum = 0;
            }
            OnChunk?.Invoke(energyChunk);
        }
    }

    public void Stop()
    {
        IsRecording = false;

        WasapiCapture? cap;
        lock (_lock)
        {
            _isStopping = true;
            cap = _capture;
            _capture = null;   // sever FIRST — no re-entrancy
        }

        if (cap != null)
        {
            cap.DataAvailable -= OnDataAvailable;
            cap.RecordingStopped -= OnRecordingStopped;

            try
            {
                var stopTask = Task.Run(() =>
                {
                    try { cap.StopRecording(); }
                    catch (Exception ex) { DebugRecorder.Error("REC", "StopRecording", ex); }
                });
                if (Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(2))).Result != stopTask)
                    DebugRecorder.Log("REC", "⚠️ WASAPI StopRecording timed out — orphaning device");
            }
            catch (Exception ex)
            {
                DebugRecorder.Error("REC", "WASAPI stop", ex);
            }

            _ = Task.Run(() =>
            {
                try { cap.Dispose(); }
                catch { /* orphaned — OS reclaims */ }
            });
        }

        LogStopMetrics();
    }

    private void LogStopMetrics()
    {
        double elapsedMs = _recordingStartTime.HasValue
            ? (DateTime.Now - _recordingStartTime.Value).TotalMilliseconds : 0;
        int samples;
        lock (_lock) samples = TotalSamples;
        double measuredRate = elapsedMs > 0 ? samples / (elapsedMs / 1000.0) : 0;
        DebugRecorder.Log("REC",
            $"Capture stopped. Samples={samples} | Audio={samples / (double)SampleRate:F2}s | Active={elapsedMs / 1000.0:F2}s | Rate={measuredRate:F0} samples/s (target {SampleRate}) | Format={_negotiatedFormat}");
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Runs on the CAPTURE thread. NEVER dispose here (self-join deadlock).
        if (e.Exception != null)
        {
            DebugRecorder.Error("REC", "WASAPI capture stopped unexpectedly", e.Exception);
            OnError?.Invoke(e.Exception.Message);
        }
    }

    private void Cleanup()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
    }

    public void NoteSilence(int ms) => _silenceRunsMs = ms;

    /// <summary>Deterministic full conversion: raw native bytes → 16kHz mono float PCM.</summary>
    public float[] GetSnapshot()
    {
        byte[] raw;
        WaveFormat? fmt;
        lock (_lock)
        {
            raw = _rawBytes.ToArray();
            fmt = _nativeFormat;
        }
        if (raw.Length == 0 || fmt == null) return Array.Empty<float>();

        try
        {
            using var ms = new MemoryStream(raw);
            using var reader = new RawSourceWaveStream(ms, fmt);
            ISampleProvider stream = reader.ToSampleProvider();

            if (fmt.Channels > 1)
                stream = new MonoDownmixSampleProvider(stream);
            if (fmt.SampleRate != SampleRate)
                stream = new WdlResamplingSampleProvider(stream, SampleRate);

            using var outMs = new MemoryStream();
            var pcm16 = new SampleToWaveProvider16(stream);
            byte[] buf = new byte[8192];
            int read;
            while ((read = pcm16.Read(buf, 0, buf.Length)) > 0)
                outMs.Write(buf, 0, read);

            byte[] pcm = outMs.ToArray();
            var result = new float[pcm.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
            return result;
        }
        catch (Exception ex)
        {
            DebugRecorder.Error("REC", "Snapshot conversion failed", ex);
            return Array.Empty<float>();
        }
    }

    public void Dispose() => Stop();
}

/// <summary>Downmixes any multi-channel sample provider to mono without clipping.</summary>
public class MonoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer;

    public MonoDownmixSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        _sourceBuffer = Array.Empty<float>();
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int sourceSamplesRequired = count * _sourceChannels;
        if (_sourceBuffer.Length < sourceSamplesRequired)
            _sourceBuffer = new float[sourceSamplesRequired];

        int samplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesRequired);
        int outSamples = samplesRead / _sourceChannels;

        for (int i = 0; i < outSamples; i++)
        {
            float sum = 0;
            for (int c = 0; c < _sourceChannels; c++)
                sum += _sourceBuffer[i * _sourceChannels + c];
            buffer[offset + i] = sum / _sourceChannels;
        }
        return outSamples;
    }
}
