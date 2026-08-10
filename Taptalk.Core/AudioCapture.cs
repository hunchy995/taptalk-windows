using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Taptalk.Core;

/// <summary>
/// Captures microphone audio at 16kHz mono into a growing float buffer.
///
/// WHY WASAPI (NOT WaveInEvent/MME) — structural fix Aug 2026:
/// WaveInEvent uses the legacy Windows MME API. Modern USB mics (Xiaomi, many
/// headsets, monitors) run natively at 32-bit IEEE float or 24-bit PCM, NOT 16-bit.
/// When MME is asked for 16kHz/16-bit/mono it silently fails the conversion and hands
/// the app raw 32-bit float bytes — misread as 16-bit integers → normal speech
/// (0.1f) becomes tiny garbage (~0.0135 = -56 dBFS) → the ASR model sees silence
/// → empty transcription. MME also drops ~7% of buffers under load (measured rate
/// 14875 vs target 16000 in the user log).
///
/// WHY EVENT-SYNC + TIME-BOXED STOP — structural fix Aug 2026 (stop hang):
/// WasapiCapture.StopRecording() internally does captureThread.Join(). If the capture
/// thread is blocked in a native driver read, Join blocks forever. Calling it on the
/// UI thread freezes the dispatcher → stop shortcut AND overlay tap both stop working
/// → "records forever until force close". Also: WasapiCapture.RecordingStopped fires
/// ON THE CAPTURE THREAD — disposing the capture from that handler = self-join deadlock.
///
/// Fixes:
/// - useEventSync:true — event-driven wake instead of sleep loop (reliable stop).
/// - Stop() unsubscribes handlers FIRST, then time-boxes StopRecording via Task.Run
///   + Task.WhenAny(2s). Never blocks the UI thread; on timeout the device is orphaned
///   and disposed on a background thread.
/// - OnRecordingStopped never disposes from the capture thread — logs + raises OnError.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public const int SampleRate = 16000;

    private WasapiCapture? _capture;
    private BufferedWaveProvider? _captureBuffer;
    private IWaveProvider? _pipeline;
    private readonly List<float> _pcmSamples = new();
    private readonly object _lock = new();
    private MMDevice? _selectedDevice;

    // Metrics for the debug logger (updated on the WASAPI callback thread)
    private long _totalSamplesCaptured;
    private DateTime? _recordingStartTime;
    private DateTime _lastAudioLogTime = DateTime.MinValue;
    private int _chunksSinceLog;
    private float _maxPeakInWindow;
    private double _rmsSum;
    private int _silenceRunsMs;
    private string _negotiatedFormat = "";

    public event Action<float[]>? OnChunk; // raised per chunk with float32 16kHz mono PCM
    public event Action<string>? OnError;  // raised when the capture device fails

    /// <summary>-1 = Windows default recording device (null/empty name → default endpoint).</summary>
    public int DeviceNumber { get; set; } = -1;

    public bool IsRecording { get; private set; }
    public int TotalSamples { get { lock (_lock) return _pcmSamples.Count; } }

    /// <summary>Enumerate available input devices (0 = first hardware device, -1 = System Default).</summary>
    public static List<string> EnumerateDevices()
    {
        var names = new List<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var d in devices)
                names.Add(d.FriendlyName);
        }
        catch { /* no devices / enumeration failed */ }
        return names;
    }

    public static string GetDeviceName(int index)
    {
        if (index < 0) return "System Default Microphone";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            if (index < devices.Count) return devices[index].FriendlyName;
            return $"Device {index}";
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
        _negotiatedFormat = "";

        DebugRecorder.Log("REC", $"Initializing WASAPI capture: DeviceIdx={DeviceNumber} Name='{GetDeviceName(DeviceNumber)}'");

        try
        {
            // 1. Resolve the MMDevice (default or by index)
            using var enumerator = new MMDeviceEnumerator();
            if (DeviceNumber < 0)
            {
                _selectedDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            else
            {
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                _selectedDevice = DeviceNumber < devices.Count
                    ? devices[DeviceNumber]
                    : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }

            if (_selectedDevice == null)
                throw new InvalidOperationException("No microphone device found.");

            // 2. WASAPI capture — event-sync mode (event-driven wake = reliable StopRecording,
            //    no sleep-loop starvation). Shared mode uses the device's NATIVE format.
            _capture = new WasapiCapture(_selectedDevice, useEventSync: true);

            var native = _capture.WaveFormat;
            _negotiatedFormat = $"{native.SampleRate}Hz {native.BitsPerSample}-bit {native.Channels}ch ({native.Encoding})";
            DebugRecorder.Log("REC", $"WASAPI native format: {_negotiatedFormat}");

            // 3. Pipeline: BufferedWaveProvider → sample provider → mono → 16kHz → 16-bit PCM
            _captureBuffer = new BufferedWaveProvider(native)
            {
                DiscardOnBufferOverflow = true
            };

            ISampleProvider stream = _captureBuffer.ToSampleProvider();

            if (native.Channels > 1)
                stream = new MonoDownmixSampleProvider(stream);

            if (native.SampleRate != SampleRate)
                stream = new WdlResamplingSampleProvider(stream, SampleRate);

            _pipeline = new SampleToWaveProvider16(stream);

            // 4. Wire events + start
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

    public void Stop()
    {
        IsRecording = false;

        WasapiCapture? cap = _capture;
        _capture = null;   // sever the reference FIRST — no re-entrancy from the capture thread
        _captureBuffer = null;
        _pipeline = null;

        if (cap != null)
        {
            // Unsubscribe BEFORE stopping — the capture thread's RecordingStopped must be a no-op
            cap.DataAvailable -= OnDataAvailable;
            cap.RecordingStopped -= OnRecordingStopped;

            try
            {
                // Time-box the stop on a background thread. NEVER block the UI thread on
                // the capture-thread Join (that froze the dispatcher → "records forever").
                var stopTask = Task.Run(() =>
                {
                    try { cap.StopRecording(); }
                    catch (Exception ex) { DebugRecorder.Error("REC", "StopRecording", ex); }
                });

                if (Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(2))).Result != stopTask)
                    DebugRecorder.Log("REC", "⚠️ WASAPI StopRecording timed out — driver locked; orphaning device");
            }
            catch (Exception ex)
            {
                DebugRecorder.Error("REC", "WASAPI stop", ex);
            }

            // Safe background dispose (may itself call StopRecording → Join → hang; orphan on timeout)
            _ = Task.Run(() =>
            {
                try { cap.Dispose(); }
                catch { /* orphaned device — OS reclaims on driver recovery / process exit */ }
            });
        }

        LogStopMetrics();
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
            $"Capture stopped. Samples={_totalSamplesCaptured} | Audio={actualSec:F2}s | Active={elapsedMs / 1000.0:F2}s | Rate={measuredRate:F0} samples/s (target {SampleRate}) | Format={_negotiatedFormat}");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsRecording || e.BytesRecorded == 0 || _captureBuffer == null) return;

        // Push native bytes into the pipeline
        _captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // Read resampled 16-bit mono 16kHz PCM out
        byte[] outBuffer = new byte[e.BytesRecorded];
        var chunkSamples = new List<float>(e.BytesRecorded / 2);

        while (true)
        {
            int bytesRead = _pipeline!.Read(outBuffer, 0, outBuffer.Length);
            if (bytesRead <= 0) break;

            for (int i = 0; i + 1 < bytesRead; i += 2)
            {
                short s = (short)(outBuffer[i] | (outBuffer[i + 1] << 8));
                chunkSamples.Add(s / 32768f);
            }
        }

        if (chunkSamples.Count == 0) return;

        var samples = chunkSamples.ToArray();
        float peak = 0f;
        double sumSq = 0;
        foreach (var v in samples)
        {
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

        // Throttled audio metrics every ~1s
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

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Runs on the CAPTURE thread. NEVER dispose the capture here (self-join deadlock).
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
        _captureBuffer = null;
        _pipeline = null;
        _selectedDevice = null;
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
