using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Taptalk.Core;
using Taptalk.Engine.Parakeet;
using Taptalk.Engine.Whisper;

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
    private ISttEngine? _engine;
    private HotKeyManager? _hotKey;
    private System.Windows.Forms.NotifyIcon? _tray;

    private RecordingState _state = RecordingState.Idle;
    private readonly object _stateLock = new();

    private bool _autoPaste = true;
    private readonly Stopwatch _sessionTimer = new();
    private bool _isPartialTranscribing;
    private bool _warnedNoAudio;
    private DateTime _lastTapTime = DateTime.MinValue;
    private IntPtr _recordingStartForegroundWindow = IntPtr.Zero;
    private System.Windows.Threading.DispatcherTimer? _partialTimer;
    private DateTime _lastPartial = DateTime.MinValue;
    private string _lastPartialText = "";
    private string _lastModelDirectory = "";

    public MainWindow()
    {
        InitializeComponent();
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
        MicCombo.SelectedIndex = 0;
        _capture.DeviceNumber = -1;

        // 2. Restore persistent settings (engine, model path, options)
        LoadSettings();

        // 3. Surface mic device failures safely on the UI thread
        _capture.OnError += msg =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                Log($"❌ Microphone error: {msg}");
                MicStatusText.Text = "Microphone error. Please check your Windows default microphone settings.";
                ResetToIdleState();
            });
        };

        // 4. Global hotkey
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotKey = new HotKeyManager();
        _hotKey.OnHotKeyPressed += OnMicTap;
        _hotKey.Register(hwnd);
        RefreshHotkeyStatus();

        // 6. Tray icon
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = $"Taptalk — {GetHotkeyLabel()} to record",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Settings", null, (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); });
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;

        // 7. Audio chunks arrive on the NAudio thread — watchdog only (no auto-stop), never touch UI
        _capture.OnChunk += OnAudioChunk;

        // 8. Minimize to tray on launch if requested
        if (MinimizeOnLaunchChk.IsChecked.GetValueOrDefault(false))
        {
            Hide();
            _tray.Text = "Taptalk is running in the background";
        }

        // 10. Load history
        RefreshHistoryList();

        Log("Taptalk ready. " + GetHotkeyLabel() + " to record.");
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
                WindowState = WindowState.Normal;
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
        _sessionTimer.Restart();
        _engine?.ResetSession();

        // Remember which window had focus before we started, so we can type back into it after stop.
        // Give any brief focus shift from the hotkey a moment to settle.
        _recordingStartForegroundWindow = IntPtr.Zero;
        Task.Delay(50).ContinueWith(_ =>
        {
            _recordingStartForegroundWindow = GetForegroundWindow();
            try
            {
                var sb = new System.Text.StringBuilder(256);
                if (GetWindowText(_recordingStartForegroundWindow, sb, 256) > 0)
                    DebugRecorder.Log("REC", $"Captured focus window: '{sb}'");
            }
            catch { }
        }, TaskScheduler.Default);

        try
        {
            _capture.Start();
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

    private void OnAudioChunk(float[] chunk)
    {
        // Called on the NAudio thread — NO UI access here. Pure math only.
        try
        {
            // Watchdog only: recording but almost no audio for 2.5s → likely muted/blocked mic.
            // (Auto-stop on silence was removed — recording is stopped manually via hotkey.)
            if (_state != RecordingState.Recording) return;

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
        }
        catch (Exception ex)
        {
            // A stale DataAvailable callback after Stop/Dispose is a documented NAudio
            // hazard — NEVER let an exception escape the wave thread (fatal, no hook).
            DebugRecorder.Log("ERR", $"OnAudioChunk: {ex.GetType().Name}: {ex.Message}");
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
                    // Add to persistent history
                    TranscriptionHistory.Add(cleaned);
                    Dispatcher.BeginInvoke(RefreshHistoryList);

                    // Brief pause so the overlay/UI focus change settles before injecting
                    await Task.Delay(150);

                    if (_autoPaste)
                    {
                        // Type back into the window that was focused when recording started.
                        TextInjector.InjectText(cleaned, _recordingStartForegroundWindow);
                    }
                }
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
        _sessionTimer.Reset();
    }

    #region Settings persistence

    private string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Taptalk", "settings.json");

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var settings = new Dictionary<string, object?>
            {
                ["lastModelDirectory"] = _lastModelDirectory,
                ["lastModelPath"] = ModelPathBox?.Text ?? "",
                ["engineIndex"] = EngineCombo?.SelectedIndex ?? 0,
                ["autoPaste"] = AutoPasteChk?.IsChecked ?? true,
                ["debug"] = DebugChk?.IsChecked ?? true,
                ["minimizeOnLaunch"] = MinimizeOnLaunchChk?.IsChecked ?? false,
                ["startupWithWindows"] = StartupChk?.IsChecked ?? false,
                ["hotkeyModifiers"] = GetSelectedHotkeyModifiers(),
                ["hotkeyKey"] = GetSelectedHotkeyKey(),
                ["micIndex"] = (MicCombo?.SelectedItem as MicDevice)?.Index ?? -1
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex)
        {
            DebugRecorder.Log("CFG", $"Save settings failed: {ex.Message}");
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("lastModelDirectory", out var d))
                _lastModelDirectory = d.GetString() ?? "";

            // Load model path if the file still exists; otherwise keep the directory so Browse lands there.
            if (root.TryGetProperty("lastModelPath", out var p))
            {
                var path = p.GetString() ?? "";
                if (!string.IsNullOrEmpty(path))
                {
                    ModelPathBox.Text = path;
                    if (File.Exists(path)) LoadEngine();
                }
            }

            if (root.TryGetProperty("engineIndex", out var e) && EngineCombo != null)
                EngineCombo.SelectedIndex = Math.Max(0, Math.Min(e.GetInt32(), EngineCombo.Items.Count - 1));

            if (root.TryGetProperty("autoPaste", out var ap) && AutoPasteChk != null)
                AutoPasteChk.IsChecked = ap.GetBoolean();

            if (root.TryGetProperty("debug", out var dbg) && DebugChk != null)
                DebugChk.IsChecked = dbg.GetBoolean();

            if (root.TryGetProperty("minimizeOnLaunch", out var mol) && MinimizeOnLaunchChk != null)
                MinimizeOnLaunchChk.IsChecked = mol.GetBoolean();

            if (root.TryGetProperty("startupWithWindows", out var sw) && StartupChk != null)
                StartupChk.IsChecked = sw.GetBoolean();

            // Restore hotkey
            uint mods = HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_SHIFT;
            uint key = HotKeyManager.VK_SPACE;
            if (root.TryGetProperty("hotkeyModifiers", out var hm))
            {
                var raw = hm.GetUInt32();
                if ((raw & (HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_ALT | HotKeyManager.MOD_SHIFT | HotKeyManager.MOD_WIN)) != 0)
                    mods = raw;
            }
            if (root.TryGetProperty("hotkeyKey", out var hk))
                key = hk.GetUInt32();
            SelectHotkeyInUi(mods, key);

            // Restore microphone
            if (root.TryGetProperty("micIndex", out var mi) && MicCombo != null)
            {
                int savedIndex = mi.GetInt32();
                var items = MicCombo.ItemsSource as List<MicDevice>;
                var match = items?.FirstOrDefault(x => x.Index == savedIndex);
                MicCombo.SelectedItem = match ?? items?.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            DebugRecorder.Log("CFG", $"Load settings failed: {ex.Message}");
        }
    }

    #endregion

    #region UI helpers

    private void Log(string msg)
    {
        if (LogBox == null) return; // may fire during InitializeComponent before LogBox exists
        Dispatcher.Invoke(() => LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n"));
    }

    private void AppendDebugLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string>(AppendDebugLine), line);
            return;
        }

        if (LogBox == null) return;

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

    private void RefreshHistoryList()
    {
        if (HistoryList == null) return;
        var items = TranscriptionHistory.Load();
        HistoryList.ItemsSource = items;
    }

    #endregion

    #region Event handlers

    private void DebugChk_Changed(object sender, RoutedEventArgs e)
    {
        if (DebugChk == null) return;
        bool on = DebugChk.IsChecked.GetValueOrDefault(true);
        Taptalk.Core.DebugRecorder.Instance.IsVerboseEnabled = on;
        Log("🔍 Debug mode " + (on ? "ON" : "OFF"));
        SaveSettings();
    }

    private void ClearLogBtn_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        Log("Log cleared — full history is in " + Taptalk.Core.DebugRecorder.Instance.LogFilePath);
    }

    private void ClearHistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        TranscriptionHistory.Clear();
        RefreshHistoryList();
        Log("History cleared");
    }

    private void EngineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded && ModelPathBox.Text.Length > 0 && File.Exists(ModelPathBox.Text))
        {
            LoadEngine();
            SaveSettings();
        }
    }

    private void ModelBrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Model files (*.onnx;*.bin;*.gguf)|*.onnx;*.bin;*.gguf|All files (*.*)|*.*",
            Title = "Select ASR model"
        };

        // Always restore the last model directory, even if the saved file moved.
        if (!string.IsNullOrEmpty(_lastModelDirectory) && Directory.Exists(_lastModelDirectory))
            dlg.InitialDirectory = _lastModelDirectory;

        if (dlg.ShowDialog() != true) return;

        _lastModelDirectory = Path.GetDirectoryName(dlg.FileName) ?? "";
        ModelPathBox.Text = dlg.FileName;
        SaveSettings();
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

            if (EngineCombo.SelectedIndex == 0)  // Parakeet CTC GPU
            {
                var parakeet = new ParakeetEngine(path);
                if (parakeet.LoadModel(path))
                {
                    _engine = parakeet;
                    ModelStatusText.Text = $"✅ Parakeet CTC loaded on GPU (DirectML) — {new FileInfo(path).Length / 1e6:F0}MB";
                    Log("✅ Parakeet CTC engine ready (GPU/DirectML)");
                }
                else
                {
                    ModelStatusText.Text = "❌ Failed to load Parakeet CTC model";
                    Log("❌ Parakeet CTC load failed");
                }
            }
            else if (EngineCombo.SelectedIndex == 1)  // Parakeet TDT GPU
            {
                var tdt = new TdtEngine(path);
                if (tdt.LoadModel(path))
                {
                    _engine = tdt;
                    ModelStatusText.Text = "✅ Parakeet TDT loaded on GPU (DirectML)";
                    Log("✅ Parakeet TDT engine ready (GPU/DirectML)");
                }
                else
                {
                    ModelStatusText.Text = "❌ Failed to load Parakeet TDT model — needs encoder + decoder_joint + vocab.txt in the same folder";
                    Log("❌ Parakeet TDT load failed — select the encoder-model.onnx file");
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

    private void AutoPasteChk_Checked(object sender, RoutedEventArgs e)
    {
        _autoPaste = AutoPasteChk.IsChecked.GetValueOrDefault(true);
        SaveSettings();
    }

    private void MinimizeOnLaunchChk_Checked(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void StartupChk_Checked(object sender, RoutedEventArgs e)
    {
        StartupManager.SetStartupEnabled(StartupChk.IsChecked.GetValueOrDefault(false));
        SaveSettings();
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
            System.Diagnostics.Process.Start(new ProcessStartInfo("control", "mmsys.cpl,,recording") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"❌ Could not open sound settings: {ex.Message}");
        }
    }

    private void FixMicLevelBtn_Click(object sender, RoutedEventArgs e)
    {
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
    }

    #endregion

    #region Hotkey UI

    private uint GetSelectedHotkeyModifiers()
    {
        return HotkeyModifierCombo.SelectedIndex switch
        {
            0 => HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_SHIFT,
            1 => HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_ALT,
            2 => HotKeyManager.MOD_ALT | HotKeyManager.MOD_SHIFT,
            3 => HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_ALT | HotKeyManager.MOD_SHIFT,
            _ => HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_SHIFT
        };
    }

    private uint GetSelectedHotkeyKey()
    {
        var text = HotkeyKeyBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return HotKeyManager.VK_SPACE;
        if (text.Equals("Space", StringComparison.OrdinalIgnoreCase)) return HotKeyManager.VK_SPACE;
        var c = text[0];
        var vk = HotKeyManager.ParseKeyChar(c);
        return vk != 0 ? vk : HotKeyManager.VK_SPACE;
    }

    private void SelectHotkeyInUi(uint modifiers, uint key)
    {
        if (HotkeyModifierCombo == null || HotkeyKeyBox == null) return;

        int index = (modifiers & (HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_ALT | HotKeyManager.MOD_SHIFT)) switch
        {
            HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_SHIFT => 0,
            HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_ALT => 1,
            HotKeyManager.MOD_ALT | HotKeyManager.MOD_SHIFT => 2,
            HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_ALT | HotKeyManager.MOD_SHIFT => 3,
            _ => 0
        };
        HotkeyModifierCombo.SelectedIndex = index;
        HotkeyKeyBox.Text = HotKeyManager.FormatVirtualKey(key);
    }

    private void RefreshHotkeyStatus()
    {
        if (_hotKey == null) return;
        var label = HotKeyManager.FormatHotKey(_hotKey.Modifiers, _hotKey.VirtualKey);
        HotkeyStatusText.Text = $"Active: {label}";
        if (_tray != null) _tray.Text = $"Taptalk — {label} to record";
    }

    private string GetHotkeyLabel()
    {
        if (_hotKey == null) return HotKeyManager.FormatHotKey(HotKeyManager.MOD_CONTROL | HotKeyManager.MOD_SHIFT, HotKeyManager.VK_SPACE);
        return HotKeyManager.FormatHotKey(_hotKey.Modifiers, _hotKey.VirtualKey);
    }

    private void ApplyHotkeyFromUi()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var mods = GetSelectedHotkeyModifiers();
        var key = GetSelectedHotkeyKey();
        _hotKey?.SetHotKey(hwnd, mods, key);
        RefreshHotkeyStatus();
        Log($"🎹 Hotkey set to {GetHotkeyLabel()}");
    }

    private void HotkeyCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyHotkeyFromUi();
        SaveSettings();
    }

    private void HotkeyKeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyHotkeyFromUi();
        SaveSettings();
    }

    #endregion

    // ---------- Win32 focus helpers ----------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
}
