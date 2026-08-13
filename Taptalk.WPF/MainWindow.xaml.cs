using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Taptalk.Core;
using Taptalk.Engine.Parakeet;
using Taptalk.Engine.Whisper;
using Clipboard = System.Windows.Clipboard; // disambiguate from System.Windows.Forms.Clipboard

namespace Taptalk.WPF;

public enum RecordingState
{
    Idle,
    Recording,
    Transcribing
}

public class MicDevice
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}

public partial class MainWindow : Window
{
    private readonly AudioCapture _capture = new();
    private readonly VADDetector _vad;
    private ISttEngine? _engine;
    private HotKeyManager? _hotKey;
    private OverlayWindow? _overlay;
    private System.Windows.Forms.NotifyIcon? _tray;

    private RecordingState _state = RecordingState.Idle;
    private readonly object _stateLock = new();

    private bool _autoStop = true;
    private bool _autoPaste = true;
    private readonly Stopwatch _sessionTimer = new();
    private bool _isPartialTranscribing;
    private bool _warnedNoAudio;
    private DateTime _lastTapTime = DateTime.MinValue;

    private DateTime _lastPartial = DateTime.MinValue;
    private string _lastPartialText = "";
    private System.Windows.Threading.DispatcherTimer? _partialTimer;

    public MainWindow()
    {
        InitializeComponent();
        _vad = new VADDetector(_capture);
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Wire DebugRecorder → LogBox (thread-safe via dispatcher)
        Taptalk.Core.DebugRecorder.Instance.OnLogAdded += AppendDebugLine;
        Taptalk.Core.DebugRecorder.Instance.IsVerboseEnabled = DebugChk.IsChecked.GetValueOrDefault(true);
        Log("🔍 Debug mode " + (Taptalk.Core.DebugRecorder.Instance.IsVerboseEnabled ? "ON" : "OFF") +
            " — full log also at " + Taptalk.Core.DebugRecorder.Instance.LogFilePath);

        // 1. Populate microphones with a "System Default" option (index -1 = WAVE_MAPPER)
        var devices = new List<MicDevice>
        {
            new() { Index = -1, Name = "System Default Microphone" }
        };
        var hardwareMics = AudioCapture.EnumerateDevices();
        for (int i = 0; i < hardwareMics.Count; i++)
        {
            devices.Add(new MicDevice { Index = i, Name = hardwareMics[i] });
        }

        MicCombo.ItemsSource = devices;
        MicCombo.SelectedIndex = 0; // Default to Windows Default Mic
        _capture.DeviceNumber = -1;

        MicCombo.SelectionChanged += OnMicSelectionChanged;

        // 2. Surface mic device failures safely on the UI thread
        _capture.OnError += msg =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                Log($"❌ Microphone error: {msg}");
                MicStatusText.Text = "Microphone error. Please check your Windows default microphone settings.";
                ResetToIdleState();
            });
        };

        // 3. Overlay window
        _overlay = new OverlayWindow();
        _overlay.OnTap += OnMicTap;
        _overlay.OnDragEnd += () => { };
        _overlay.Show();

        // 4. Global hotkey — Ctrl+Shift+Space (Alt+Space conflicts with the Windows system menu)
        _hotKey = new HotKeyManager();
        _hotKey.OnHotKeyPressed += OnMicTap;
        _hotKey.Register(new System.Windows.Interop.WindowInteropHelper(this).Handle);

        // 5. Tray icon
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Taptalk — Ctrl+Shift+Space to record",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Settings", null, (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); });
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;

        // 6. Audio chunks arrive on the NAudio thread — only do VAD math here, never touch UI
        _capture.OnChunk += Chunk => CheckVad(Chunk);

        Log("Taptalk ready. Press Ctrl+Shift+Space or tap the overlay mic to record.");
    }

    /// <summary>Parse a shortcut string like "Ctrl+D", "Ctrl+V", "Ctrl+Shift+V" into virtual-key codes.</summary>
    private (bool ctrl, bool shift, ushort vKey) ParsePasteShortcut()
    {
        var text = PasteShortcutBox?.Text ?? "Ctrl+V";
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool ctrl = false, shift = false;
        ushort vKey = 0x56; // default V

        foreach (var part in parts)
        {
            var p = part.ToUpperInvariant();
            if (p == "CTRL") { ctrl = true; continue; }
            if (p == "SHIFT") { shift = true; continue; }
            if (p == "ALT") continue; // not supported yet
            if (p.Length == 1)
            {
                // Letter/digit → virtual key code
                var c = p[0];
                if (c >= 'A' && c <= 'Z') vKey = (ushort)(0x41 + (c - 'A'));
                else if (c >= '0' && c <= '9') vKey = (ushort)(0x30 + (c - '0'));
            }
        }

        return (ctrl, shift, vKey);
    }

    private void OnMicSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MicCombo.SelectedItem is MicDevice selected)
        {
            _capture.DeviceNumber = selected.Index;
            Log($"🎤 Microphone input selected: {selected.Name}");
            MicStatusText.Text = "";
        }
    }

    private void OnMicTap()
    {
        // Enforce UI-thread execution (hotkey WM_HOTKEY arrives on the UI thread already, but be safe)
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnMicTap));
            return;
        }

        // Debounce keyboard auto-repeat / double-fire (Windows typematic repeat fires WM_HOTKEY repeatedly)
        var now = DateTime.UtcNow;
        if ((now - _lastTapTime).TotalMilliseconds < 500)
            return;
        _lastTapTime = now;

        lock (_stateLock)
        {
            if (_state == RecordingState.Transcribing)
                return; // ignore inputs during transcription

            if (_engine == null || !_engine.IsLoaded)
            {
                Log("⚠️ No model loaded — click Browse and select a model file first.");
                Show();
                Activate();
                return;
            }

            if (_state == RecordingState.Recording)
            {
                // Reject stop within 500ms of recording start (typematic auto-repeat protection)
                if (_sessionTimer.ElapsedMilliseconds < 500)
                    return;
                _ = StopAndTranscribeAsync();
            }
            else if (_state == RecordingState.Idle)
            {
                StartRecording();
            }
        }
    }

    private void StartRecording()
    {
        _state = RecordingState.Recording;
        _warnedNoAudio = false;
        _vad.Reset();
        _sessionTimer.Restart();
        _engine?.ResetSession(); // fresh session gain for this recording

        try
        {
            _capture.Start();
            _overlay?.SetRecording();
            StartPartialTimer();
            Log($"🎤 Recording started ({DateTime.Now:HH:mm:ss})");

            // Show the Fix Mic Level button if the Windows mic level is low (< 60%)
            Dispatcher.BeginInvoke(() =>
            {
                var lvl = _capture.EndpointLevelScalar;
                if (lvl < 0.6f)
                {
                    FixMicLevelBtn.Visibility = System.Windows.Visibility.Visible;
                    if (MicStatusText != null)
                        MicStatusText.Text = $"Windows mic level is {lvl:P0} — click '🔊 Fix Mic Level' to raise it to 100%.";
                }
                else
                {
                    FixMicLevelBtn.Visibility = System.Windows.Visibility.Collapsed;
                }
            });
        }
        catch (Exception ex)
        {
            Log($"❌ Failed to start recording: {ex.Message}");
            ResetToIdleState();
        }
    }

    private void CheckVad(float[] chunk)
    {
        // Called on the NAudio thread — NO UI access here. Pure math only.
        try
        {
            CheckVadCore(chunk);
        }
        catch (Exception ex)
        {
            // A stale DataAvailable callback after Stop/Dispose is a documented NAudio
            // hazard — NEVER let an exception escape the wave thread (fatal, no hook).
            DebugRecorder.Log("ERR", $"CheckVad: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CheckVadCore(float[] chunk)
    {
        if (_state != RecordingState.Recording) return;

        // Watchdog: recording but almost no audio for 2.5s → likely muted/blocked mic.
        // chunk[] is the latest native-format samples; compute its peak cheaply for diagnostics.
        float peak = 0f;
        foreach (var v in chunk) { var a = MathF.Abs(v); if (a > peak) peak = a; }

        if (!_warnedNoAudio && _sessionTimer.ElapsedMilliseconds > 2500 && (peak < 0.001f || _capture.TotalSamples < 4000))
        {
            _warnedNoAudio = true;
            Dispatcher.BeginInvoke(() =>
            {
                Log("⚠️ No audio detected — check your physical microphone mute button or Windows mic level.");
                MicStatusText.Text = "No audio data is reaching Taptalk. Verify your mic input levels.";
            });
        }

        if (!_autoStop) return;
        if (_sessionTimer.ElapsedMilliseconds < 1500) return;

        // Use the incoming native-rate chunk for VAD — never resample the whole growing
        // buffer here. That was an O(N^2) performance leak that could starve the audio thread.
        var rms = _vad.GetRMS(chunk);
        if (_vad.Check(rms, (int)_sessionTimer.ElapsedMilliseconds))
        {
            DebugRecorder.Log("VAD", $"Silence limit reached at {_sessionTimer.ElapsedMilliseconds}ms — auto-stop");
            // Marshal the stop back to the UI thread — NEVER stop NAudio from its own callback
            Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (_stateLock)
                {
                    if (_state == RecordingState.Recording)
                    {
                        Log("🔇 Silence detected — auto-stop");
                        _ = StopAndTranscribeAsync();
                    }
                }
            }));
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
            if (_state != RecordingState.Recording || _engine == null || _isPartialTranscribing) return;

            var audio = _capture.GetSnapshot();
            if (audio.Length < _engine.MinSamplesForPartial) return;

            _isPartialTranscribing = true;
            try
            {
                var partial = await Task.Run(() => _engine!.TranscribePartial(audio));
                var cleaned = TextPostProcessor.Clean(partial);
                if (!string.IsNullOrWhiteSpace(cleaned) && cleaned != _lastPartialText)
                {
                    _lastPartialText = cleaned;
                    Log($"🎙️ {cleaned}");
                }
            }
            catch { }
            finally
            {
                _isPartialTranscribing = false;
            }
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
        _state = RecordingState.Transcribing;
        _lastPartialText = "";
        StopPartialTimer();
        DebugRecorder.Log("REC", "State → Transcribing");

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
                DebugRecorder.Log("POST", $"Raw=\"{raw}\" → Cleaned=\"{cleaned}\"");
                Log($"📝 \"{cleaned}\"");

                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    // Brief pause so the overlay/UI focus change settles before injecting
                    await Task.Delay(150);

                    if (_autoPaste)
                    {
                        // Type directly into the active field — never depend on the clipboard being free.
                        await TextInjector.InjectTextAsync(cleaned);
                    }

                    // Also copy to clipboard as a backup, but do not block on clipboard failures.
                    _ = SetClipboardTextAsync(cleaned);
                }

                _overlay?.SetDone();
                await Task.Delay(900);
            }
            catch (Exception ex)
            {
                Log($"❌ Processing Error: {ex.Message}");
            }
        }
        finally
        {
            ResetToIdleState();
        }
    }

    private void ResetToIdleState()
    {
        _state = RecordingState.Idle;
        _overlay?.SetIdle();
        _sessionTimer.Reset();
    }

    /// <summary>Set clipboard text from a background thread without touching STA UI clipboard directly. Retries if busy.</summary>
    private async Task SetClipboardTextAsync(string text)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    DebugRecorder.Log("INJ", $"Copied to clipboard (attempt {attempt + 1}): \"{text}\"");
                    return;
                }
                catch (Exception ex)
                {
                    DebugRecorder.Log("INJ", $"Clipboard attempt {attempt + 1} failed: {ex.Message}");
                    await Task.Delay(50);
                }
            }
            DebugRecorder.Log("INJ", "Clipboard copy failed after 10 attempts");
        });
    }

    private void Log(string msg)
    {
        if (LogBox == null) return; // may fire during InitializeComponent before LogBox exists
        Dispatcher.Invoke(() => LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n"));
    }

    /// <summary>Called from DebugRecorder (any thread) — marshals to UI, filters verbose tags, caps length.</summary>
    private void AppendDebugLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string>(AppendDebugLine), line);
            return;
        }

        if (LogBox == null) return; // during InitializeComponent, LogBox not built yet

        // Filter verbose tags when debug checkbox is off
        if (!DebugChk.IsChecked.GetValueOrDefault(true))
        {
            if (line.Contains("[AUDIO]") || line.Contains("[FEAT]") ||
                line.Contains("[INF]") || line.Contains("[DEC]"))
                return;
        }

        LogBox.AppendText(line + "\n");

        // Cap the LogBox to the last ~1000 lines to avoid unbounded growth
        if (LogBox.LineCount > 1000)
        {
            int firstBreak = LogBox.Text.IndexOf('\n');
            if (firstBreak >= 0)
                LogBox.Text = LogBox.Text.Substring(firstBreak + 1);
        }
        LogBox.ScrollToEnd();
    }

    private void DebugChk_Changed(object sender, RoutedEventArgs e)
    {
        if (DebugChk == null) return;
        bool on = DebugChk.IsChecked.GetValueOrDefault(true);
        Taptalk.Core.DebugRecorder.Instance.IsVerboseEnabled = on;
        Log("🔍 Debug mode " + (on ? "ON" : "OFF"));
    }

    private void ClearLogBtn_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        Log("Log cleared — full history is in " + Taptalk.Core.DebugRecorder.Instance.LogFilePath);
    }

    // ---------- Settings UI handlers (unchanged) ----------

    private void EngineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
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

    private void AutoPasteChk_Checked(object sender, RoutedEventArgs e)
    {
        _autoPaste = AutoPasteChk.IsChecked.GetValueOrDefault(true);
    }

    private void PasteShortcutBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // During InitializeComponent the TextBox can fire TextChanged before the hint label is constructed.
        if (PasteShortcutHint == null)
            return;

        // Just validate and update the hint
        var (_, _, vKey) = ParsePasteShortcut();
        if (vKey == 0)
        {
            PasteShortcutHint.Text = "Invalid shortcut (use Ctrl+Letter or Ctrl+Shift+Letter)";
            PasteShortcutHint.Foreground = System.Windows.Media.Brushes.Crimson;
        }
        else
        {
            PasteShortcutHint.Text = $"Paste shortcut after recording";
            PasteShortcutHint.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    private void MicSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"❌ Could not open mic settings: {ex.Message}");
        }
    }

    private void SoundSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Opens the Windows Sound panel → Recording tab (mic input level / boost)
            System.Diagnostics.Process.Start(new ProcessStartInfo("control", "mmsys.cpl,,recording") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"❌ Could not open sound settings: {ex.Message}");
        }
    }

    private void FixMicLevelBtn_Click(object sender, RoutedEventArgs e)
    {
        // Raising the Windows mic level affects EVERY app using this mic — get explicit consent
        var before = _capture.EndpointLevelScalar;
        var confirm = System.Windows.MessageBox.Show(
            $"Your microphone level is {before:P0} in Windows. This is why Taptalk hears almost nothing.\n\n" +
            "Raise it to 100%? (This also affects other apps using this microphone.)",
            "Fix Microphone Level",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        _capture.RaiseEndpointVolumeToFull();
        FixMicLevelBtn.Visibility = System.Windows.Visibility.Collapsed;
        if (MicStatusText != null)
            MicStatusText.Text = "✅ Mic level raised to 100%. Record again and it should work!";
        Log("🔊 Mic level raised to 100%");
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _hotKey?.Unregister();
        _tray?.Dispose();
        _capture.Dispose();
        _engine?.Dispose();
        _overlay?.Close();
    }
}
