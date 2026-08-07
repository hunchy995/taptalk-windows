using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Taptalk.Core;
using Taptalk.Engine.Parakeet;
using Taptalk.Engine.Whisper;

namespace Taptalk.WPF;

public partial class MainWindow : Window
{
    private readonly AudioCapture _capture = new();
    private readonly VADDetector _vad;
    private ISttEngine? _engine;
    private HotKeyManager? _hotKey;
    private OverlayWindow? _overlay;
    private System.Windows.Forms.NotifyIcon? _tray;
    private CancellationTokenSource? _cts;
    private bool _isTranscribing;
    private bool _recordingRequested; // OUR intent — device may stop itself (mic perms), we still know user asked
    private bool _autoStop = true;    // cached on UI thread; read from audio thread safely
    private readonly Stopwatch _sessionTimer = new();

    public MainWindow()
    {
        InitializeComponent();
        _vad = new VADDetector(_capture);
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Microphone list (0 = Windows default)
        var mics = AudioCapture.EnumerateDevices();
        MicCombo.ItemsSource = mics;
        MicCombo.SelectedIndex = 0;
        Log($"🎤 {mics.Count} microphone(s) found — using default");

        // Surface mic device failures with privacy guidance
        _capture.OnError += msg =>
        {
            Log($"❌ Microphone error: {msg}");
            Dispatcher.Invoke(() => MicStatusText.Text =
                "Microphone is blocked. Windows needs permission to use your mic.\nClick 'Mic Privacy' and allow Taptalk, then restart recording.");
        };

        // Overlay
        _overlay = new OverlayWindow();
        _overlay.OnTap += OnMicTap;
        _overlay.OnDragEnd += () => { };
        _overlay.Show();

        // Hotkey Alt+Space
        _hotKey = new HotKeyManager();
        _hotKey.OnHotKeyPressed += OnMicTap;
        _hotKey.Register(new System.Windows.Interop.WindowInteropHelper(this).Handle);

        // Tray icon
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Taptalk — Alt+Space to record",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Settings", null, (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); });
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;

        _capture.OnChunk += Chunk => CheckVad(Chunk);

        Log("Taptalk ready. Alt+Space or tap the mic to record.");
    }

    private void OnMicTap()
    {
        // Ignore taps while transcribing — prevents "click again starts a new recording"
        if (_isTranscribing) return;

        if (_engine == null || !_engine.IsLoaded)
        {
            Log("⚠️ No model loaded — click Browse and select a model file first.");
            Show();
            Activate();
            return;
        }

        if (_recordingRequested || _capture.IsRecording)
            _ = StopAndTranscribeAsync();
        else
            StartRecording();
    }

    private void StartRecording()
    {
        _recordingRequested = true;
        _warnedNoAudio = false;
        _cts = new CancellationTokenSource();
        _vad.Reset();
        _sessionTimer.Restart();
        _capture.Start();
        _overlay?.SetRecording();
        StartPartialTimer();
        Log($"🎤 Recording started ({DateTime.Now:HH:mm:ss})");
    }

    private DateTime _lastPartial = DateTime.MinValue;
    private string _lastPartialText = "";
    private System.Windows.Threading.DispatcherTimer? _partialTimer;
    private bool _warnedNoAudio;

    private void CheckVad(float[] chunk)
    {
        if (!_capture.IsRecording || _engine == null) return;

        // If we've been recording >2.5s and got almost no audio, the mic is blocked
        if (!_warnedNoAudio && _sessionTimer.ElapsedMilliseconds > 2500 && _capture.TotalSamples < 4000)
        {
            _warnedNoAudio = true;
            Dispatcher.BeginInvoke(() =>
            {
                Log("⚠️ No audio detected — Windows may be blocking the microphone.");
                MicStatusText.Text =
                    "No audio is reaching Taptalk. Click 'Mic Privacy' and allow microphone access, then try again.";
            });
        }

        if (!_autoStop) return;

        // Don't auto-stop in first 1.5s
        if (_sessionTimer.ElapsedMilliseconds < 1500) return;

        if (_vad.Check(_capture.GetSnapshot(), (int)_sessionTimer.ElapsedMilliseconds))
        {
            Log("🔇 Silence detected — auto-stop");
            _ = StopAndTranscribeAsync();
        }
    }

    private void StartPartialTimer()
    {
        _partialTimer?.Stop();
        _partialTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _partialTimer.Tick += async (_, _) =>
        {
            if (!_capture.IsRecording || _engine == null) return;
            var audio = _capture.GetSnapshot();
            if (audio.Length < _engine.MinSamplesForPartial) return;
            try
            {
                // Runs on UI thread — never blocks the NAudio callback thread
                var partial = await Task.Run(() => _engine!.TranscribePartial(audio));
                var cleaned = TextPostProcessor.Clean(partial);
                if (!string.IsNullOrWhiteSpace(cleaned) && cleaned != _lastPartialText)
                {
                    _lastPartialText = cleaned;
                    Log($"🎙️ {cleaned}");
                }
            }
            catch { /* partial transcription is best-effort */ }
        };
        _partialTimer.Start();
    }

    private void StopPartialTimer()
    {
        _partialTimer?.Stop();
        _partialTimer = null;
    }

    private async Task StopAndTranscribeAsync()
    {
        _isTranscribing = true;
        _recordingRequested = false;
        _lastPartialText = "";
        StopPartialTimer();
        try
        {
            _capture.Stop();
            _overlay?.SetProcessing();
            Log("⏳ Transcribing...");

            var audio = _capture.GetSnapshot();
            var ms = _sessionTimer.ElapsedMilliseconds;
            Log($"   {audio.Length / 16.0f / 1000.0f:F1}s audio captured ({audio.Length} samples)");

            try
            {
                var sw = Stopwatch.StartNew();
                var raw = await Task.Run(() => _engine!.Transcribe(audio));
                sw.Stop();
                var rtf = sw.ElapsedMilliseconds / (float)Math.Max(1, ms);
                Log($"⏱️ Inference: {sw.ElapsedMilliseconds}ms (RTF {rtf:F2}x)");

                var cleaned = TextPostProcessor.Clean(raw);
                Log($"📝 \"{cleaned}\"");

                if (!string.IsNullOrWhiteSpace(cleaned))
                    await TextInjector.InjectTextAsync(cleaned);

                _overlay?.SetDone();
                await Task.Delay(900);
            }
            catch (Exception ex)
            {
                Log($"❌ Error: {ex.Message}");
            }
            finally
            {
                _overlay?.SetIdle();
            }
        }
        finally
        {
            _isTranscribing = false;
        }
    }

    private void Log(string msg) =>
        Dispatcher.Invoke(() => LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n"));

    private void EngineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Reload engine if a model is already selected
        if (IsLoaded && ModelPathBox.Text.Length > 0 && File.Exists(ModelPathBox.Text))
            LoadEngine();
    }

    private void ModelBrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Model files (*.onnx;*.bin;*.gguf)|*.onnx;*.bin;*.gguf|All files (*.*)|*.*",
            Title = "Select ASR model"
        };
        if (dlg.ShowDialog() != true) return;

        ModelPathBox.Text = dlg.FileName;
        LoadEngine();
    }

    private void LoadEngine()
    {
        var path = ModelPathBox.Text;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            _engine?.Dispose();
            _engine = null;

            if (EngineCombo.SelectedIndex == 0)  // Parakeet GPU
            {
                var parakeet = new ParakeetEngine(path);
                if (parakeet.LoadModel(path))
                {
                    _engine = parakeet;
                    ModelStatusText.Text = $"✅ Parakeet loaded on GPU (DirectML) — {new FileInfo(path).Length / 1e6:F0}MB";
                    Log("✅ Parakeet engine ready (GPU/DirectML)");
                }
                else
                {
                    ModelStatusText.Text = "❌ Failed to load Parakeet model";
                    Log("❌ Parakeet load failed");
                }
            }
            else  // Whisper CPU
            {
                var whisper = new WhisperEngine();
                if (whisper.LoadModel(path))
                {
                    _engine = whisper;
                    ModelStatusText.Text = $"✅ Whisper loaded (CPU, AVX) — {new FileInfo(path).Length / 1e6:F0}MB";
                    Log("✅ Whisper engine ready (CPU)");
                }
                else
                {
                    ModelStatusText.Text = "❌ Failed to load whisper model";
                    Log("❌ Whisper load failed");
                }
            }
        }
        catch (Exception ex)
        {
            ModelStatusText.Text = $"❌ {ex.Message}";
            Log($"❌ Engine load error: {ex.Message}");
        }
    }

    private void AutoStopChk_Checked(object sender, RoutedEventArgs e)
    {
        _autoStop = AutoStopChk.IsChecked.GetValueOrDefault(true);
    }

    private void MicCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MicCombo.SelectedIndex < 0) return;
        _capture.DeviceIndex = MicCombo.SelectedIndex;
        // If we're mid-recording, restart with the new device
        if (_recordingRequested || _capture.IsRecording)
        {
            Log($"🎤 Switching to: {MicCombo.SelectedItem} — restarting capture");
            _capture.Stop();
            _capture.Start();
        }
        else
        {
            Log($"🎤 Microphone: {MicCombo.SelectedItem}");
        }
        MicStatusText.Text = "";
    }

    private void MicSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        // Open Windows 10/11 microphone privacy settings
        try
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"❌ Could not open mic settings: {ex.Message}");
        }
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _capture.Dispose();
        _engine?.Dispose();
        _hotKey?.Dispose();
        _tray?.Dispose();
        _overlay?.Close();
    }
}
