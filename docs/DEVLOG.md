
## 2026-08-09 — REGRESSION FIX: event-sync WASAPI captured ZERO audio → revert to polling mode

**Symptom (8th report):** after the stop-hang fix (which added `useEventSync: true`), the app no longer hangs BUT captures zero audio: `Samples=0 | Audio=0.00s | Rate=0 samples/s` over 3.37s. WASAPI opens fine (`48000Hz 32-bit 1ch IeeeFloat`), model loads, no [AUDIO] metric lines (they only log when samples > 0).

**Root cause (coding-partner confirmed):** `useEventSync: true` depends on the audio driver signaling a wait handle. AMD/Realtek/USB audio drivers often fail to signal it → `DataAvailable` never fires → zero samples. Regression timeline proved it: the previous build (`useEventSync: false`) captured audio fine; this build (event-sync) captured none.

**Fix (coding-partner verified, commit pending):**
1. **Revert to `new WasapiCapture(device, useEventSync: false)`** (polling mode — NAudio's own timer loop, wide driver compatibility).
2. **Keep the stop-hang protections** (they don't need event-sync): unsubscribe handlers BEFORE StopRecording, time-box StopRecording via Task.Run + Task.WhenAny(2s), orphan + background-dispose on timeout, OnRecordingStopped never disposes from the capture thread.
3. **Add diagnostics** to distinguish "callback never fires" vs "pipeline conversion choke" next round:
   - `[REC] FIRST CHUNK: N raw bytes arrived in callback` (logs instantly on first DataAvailable)
   - `[AUDIO] Bytes=N | produced=M this sec` OR `⚠️ Bytes=N but pipeline yielded 0 samples this interval` (once/sec)
4. BufferedWaveProvider `BufferLength = AverageBytesPerSecond * 10` (10s headroom).

**Diagnostic ladder (no audio):** (1) `FIRST CHUNK` absent = DataAvailable never fires → capture-mode/driver issue (revert event-sync, check device); (2) `FIRST CHUNK` present + `pipeline yielded 0` = conversion choke (pipeline/WDL issue); (3) no warning + bytes flowing = audio levels (check AUDIO peak/RMS).
# Taptalk — Development Log

Chronological record of fixes. Latest first. Companion to the `taptalk-windows` skill.

## 2026-08-09 — STOP HANG after WASAPI switch: capture-thread Join deadlock → event-sync + time-boxed stop

**Symptom (7th report):** after the WASAPI audio fix, recording works (levels good) but the stop shortcut AND overlay button BOTH fail — records forever until force-close.

**Root cause (coding-partner confirmed, 100% match):**
1. **UI-thread hijack:** `WasapiCapture.StopRecording()` internally does `captureThread.Join()`. If the capture thread is blocked in a native driver read, Join blocks forever → `StopAndTranscribeAsync` never reaches ResetToIdleState → `_state` stays `Transcribing` → OnMicTap early-returns on `if (_state == Transcribing)` → every tap ignored → "records forever". UI frozen → force-close.
2. **Self-join deadlock:** `WasapiCapture.RecordingStopped` fires ON THE CAPTURE THREAD; my OnRecordingStopped called Cleanup() → `_capture.Dispose()` → StopRecording() → Join FROM the capture thread = self-join deadlock.
3. **No event sync:** sleep-loop WASAPI capture is prone to blocking inside native GetBuffer if the driver stutters.

**Fix (coding-partner production pattern, integrated while keeping the public interface):**
- `new WasapiCapture(device, useEventSync: true)` — event-driven wake instead of sleep loop (reliable stop).
- `Stop()`: sever `_capture` reference FIRST, unsubscribe handlers BEFORE StopRecording (capture-thread event becomes a no-op), then time-box StopRecording via `Task.Run` + `Task.WhenAny(2s)` — NEVER blocks the UI thread; on timeout log "orphaning device" and dispose on a background task.
- `OnRecordingStopped`: NEVER disposes from the capture thread — logs + raises OnError only (unexpected stops → MainWindow resets state; next Start() creates a fresh capture).
- Fresh WasapiCapture per Start() (NAudio instances aren't restartable — confirmed pattern).

**Files:** `Taptalk.Core/AudioCapture.cs` (commit 5f9ee25)

## 2026-08-09 — STRUCTURAL AUDIO FIX: MME/WaveInEvent misreads 32-bit-float mics → WASAPI

**Symptom (6th report):** after the crash + normalization fixes, still empty transcription. User screenshot PROVED mic permission is granted (Taptalk.WPF in the mic access list, 42 requests). Other apps (Discord, etc.) hear the user fine. Log: Peak=0.0135 / RMS=0.0016 (~-56 dBFS) from EVERY mic + `Rate=14875 samples/s (target 16000)` (~7% buffer loss).

**Root cause (coding-partner structural analysis):** `WaveInEvent` uses the legacy Windows MME audio API. Modern USB mics (Xiaomi Desktop Speaker, many headsets/monitors) run natively at **32-bit IEEE float or 24-bit PCM — NOT 16-bit**. When MME is asked for 16kHz/16-bit/mono, the driver-side conversion fails SILENTLY and hands the app raw 32-bit float bytes. The app read them as 16-bit integers → normal speech (0.1f) looks like tiny garbage (~0.0135) → model sees silence. MME also drops ~7% of buffers under load (measured 14875 vs 16000).

**Fix (commit 45c7359):** rewrote `Taptalk.Core/AudioCapture.cs` from WaveInEvent → **WasapiCapture** (modern Windows audio stack):
- Resolves MMDevice via `MMDeviceEnumerator` (default endpoint or by index — DeviceNumber semantics unchanged: -1 = System Default, 0+ = enumerated device).
- `WasapiCapture` in Shared mode opens the device's **NATIVE format** (guaranteed success, logs the actual negotiated format: `[REC] WASAPI native format: 48000Hz 32-bit 2ch`).
- Pipeline: `BufferedWaveProvider` → `ToSampleProvider` → `MonoDownmixSampleProvider` (new helper, multi-channel → mono, no clipping) → `WdlResamplingSampleProvider` (→ 16kHz) → `SampleToWaveProvider16` (→ 16-bit PCM) → read into the same float buffer/OnChunk/GetSnapshot contract.
- All existing metrics logging, generation guard, OnError, NoteSilence preserved. No MainWindow/VAD changes needed (same public surface).
- **Verification in next log:** `[REC] WASAPI native format: ...` + `Rate=16000 samples/s` exactly + AUDIO Peak under speech should be 0.4-0.9 instead of 0.0135.

**Files:** `Taptalk.Core/AudioCapture.cs`, `Taptalk.Core/AudioCapture.cs` (MonoDownmixSampleProvider)

## 2026-08-09 — Empty transcription root cause: mic input near noise floor → added audio normalization

**Symptom (crash FIXED, now empty text):** after the concurrency-gate fix the app no longer crashes, but transcripts are empty. User log proved it:
`[AUDIO] Peak=0.0135 | AvgRMS=0.0016` (~-56 dBFS = near digital silence) → `[DEC] Raw frames=18 | blank=18 | collapsed tokens=0` — **the model works perfectly; it decodes silence as all-blank.** Both the Windows default mic and the Xiaomi hardware mic delivered the same tiny levels → Windows mic input level is very low.

**Fix (coding-partner verified, commit 970ce1a):**
1. **`Taptalk.Core/AudioNormalizer.cs`** (new): in-place DC-offset removal (zero-mean) + peak normalization to 0.90 target with 30x gain cap + hard clamp; `ApplyGainInPlace` for segments; `Measure()` returns peak+RMS. Below 0.0005 peak → treated as digital silence, no amplification (avoid noise hallucinations).
2. **Both engines** normalize before featurization/inference: `NormalizeForInference` copies the buffer (never mutates the session snapshot), logs raw peak/RMS + applied gain, and logs a prominent `⚠️ Mic level very low!` warning when RMS < 0.003 (~-50 dBFS).
3. **Partials use the growing session snapshot** (caller passes accumulated buffer) so the gain is stable across the session — no per-window gain pumping (partner: independent partial normalization amplifies silence windows into hallucinations).
4. **`ISttEngine.ResetSession()`** (new interface method) resets `_sessionGain` per recording; called from `StartRecording()`. WhisperEngine: no-op.
5. **UI:** `🔊 Fix Sound` button opens Windows Sound → Recording tab (`control mmsys.cpl,,recording`) so the user can raise mic input level/boost directly.
6. Partner decision: **do NOT auto-adjust OS mic levels** (would break other apps' calibration); normalize in-app + guide the user.

**Files:** `Taptalk.Core/AudioNormalizer.cs` (new), `Taptalk.Core/ISttEngine.cs`, `ParakeetEngine.cs`, `WhisperEngine.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`

## 2026-08-09 — CRASH FIX: concurrent ONNX/DirectML Run() → native 0xC0000005 (research-backed)

**Symptom (5th report, user furious):** after stopping recording the icon turns blue (processing) and the app COMPLETELY crashes — every time, no dialog. Four prior updates failed to fix it.

**Root cause (3 parallel deep-research subagents + coding-partner review):** the 1.5s partial-transcription DispatcherTimer and StopAndTranscribeAsync BOTH call `_engine.Transcribe()` via Task.Run → concurrent `_session.Run()` on the same DirectML InferenceSession. ORT's DirectML EP is NOT safe for concurrent Run() (especially with variable input shapes that force DML shader recompilation, making partials take 1-3s → a partial is almost always mid-flight at stop). Result: native access violation (0xC0000005) in dml/onnxruntime.dll — silent process death, uncatchable by managed try/catch.

**Fixes applied (commit 14f6bdf):**
1. **SemaphoreSlim _runGate (1,1) in BOTH engines** — serializes ALL Run() calls (partial + full + Dispose). Blocking Wait() is fine (on Task.Run threads).
2. **GetTensorDataAsSpan → .ToArray()** — copy logits to managed array inside the using block (span dangles after results dispose = #1 C# ORT crash class).
3. **DML SessionOptions stabilizers:** `ExecutionMode.ORT_SEQUENTIAL` + `EnableMemoryPattern=false`.
4. **Gated Dispose()** — never dispose session/context mid-Run (use-after-free).
5. **App.xaml.cs full safety net:** AppDomain.UnhandledException + TaskScheduler.UnobservedTaskException + DispatcherUnhandledException → all log to %LOCALAPPDATA%\Taptalk\Logs\crash.log; only CLIPBRD_E_CANT_OPEN is swallowed (Handled=true).
6. **CheckVad wrapped in try/catch** (NAudio DataAvailable can fire after Stop — never let it escape the wave thread).

**Files:** `ParakeetEngine.cs`, `WhisperEngine.cs`, `App.xaml.cs`, `MainWindow.xaml.cs`

## 2026-08-07 — Comprehensive in-app debugger (user request)

**Request:** "include a debugger as part of the log... comprehensive debugger that's included in the log itself whenever I start and stop recording" — stop guessing root causes.

**Design (agreed with coding partner Gemini 3.5 Flash):**
- New `Taptalk.Core/DebugRecorder.cs`: thread-safe singleton, 1000-line memory ring buffer + async file mirror to `%LOCALAPPDATA%\Taptalk\Logs\debug.log` (5MB rotation → debug.old.log). Never blocks the NAudio callback thread (queue + background writer). Stage tags: `[REC] [AUDIO] [VAD] [FEAT] [INF] [DEC] [POST] [INJ] [ERR] [SYS]` with thread IDs + ms timestamps.
- Instrumented every pipeline stage:
  - REC: device idx/name, start/stop timestamps, samples captured, measured sample rate vs 16000 target
  - AUDIO: throttled 1s summaries (samples, chunks, peak, avg RMS, silence ms)
  - VAD: silence-limit trigger with elapsed ms
  - FEAT: samples → mel frame count
  - INF: model load path/size, input/output metadata, tensor shapes, length value, inference ms
  - DEC: vocab loaded status/size, raw frames, blank count, collapsed tokens, first tokens, decoded raw text
  - POST: raw → cleaned (post-processor in/out)
  - INJ: target window title, SendInput vs clipboard, char count, text
  - ERR: full exception + stack + stage
- UI: "🔍 Debug" checkbox (default ON) filters verbose tags ([AUDIO]/[FEAT]/[INF]/[DEC]) in the LogBox; "Clear" button; LogBox capped at 1000 lines; file always records everything.

**Files:** `Taptalk.Core/DebugRecorder.cs` (new), `AudioCapture.cs`, `MelScaleFeaturizer.cs`, `ParakeetEngine.cs`, `WhisperEngine.cs`, `TextInjector.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs` (commit 9308908)

## 2026-08-07 — Empty transcription fixed (vocab.txt)

**Symptom:** `7.3s audio captured (116800 samples)` + `Inference: 338ms (RTF 0.05x)` but `📝 ""`.

**Root cause:** `ParakeetEngine._vocab` was never loaded — `DecodeTokens` fell back to `string.Join(" ", tokens)` (raw IDs / empty). The model also needs SentencePiece BPE decoding (`\u2581` → space).

**Fix (with coding partner, commit cdbf775):**
- `LoadVocabularyFromFile()` parses `"token index"` vocab.txt
- `AutoLoadSiblingVocab()` in `LoadModel()` finds vocab.txt next to model.onnx
- `DecodeTokens()` skips `<...>` special tokens, converts `\u2581` → space, collapses whitespace
- README updated: user must download vocab.txt alongside model.onnx

**Files:** `Taptalk.Engine.Parakeet/ParakeetEngine.cs`, `README.md`

## 2026-08-07 — State-machine redesign (coding-partner review)

**Symptom:** single tap wouldn't stop recording; only double-tap worked; Alt+Space unreliable.

**Root causes (agreed with Gemini 3.5 Flash):**
1. VAD auto-stop ran `StopAndTranscribeAsync` on the NAudio callback thread → WPF cross-thread crash → device died → `IsRecording=false` → every tap looked like a fresh start
2. Alt+Space is Windows' system-menu shortcut + typematic auto-repeat double-fired the hotkey

**Fix (commit da733f4):**
- `RecordingState` enum (Idle/Recording/Transcribing), all transitions on UI thread
- VAD stop marshaled via `Dispatcher.BeginInvoke`
- Hotkey → **Ctrl+Shift+Space** + 500ms debounce
- `MicDevice` list: Index -1 = Windows default (WAVE_MAPPER)
- `AudioCapture.DeviceNumber` renamed, defaults -1, 100ms buffers

**Files:** `MainWindow.xaml.cs`, `MainWindow.xaml`, `AudioCapture.cs`, `HotKeyManager.cs`, `README.md`

## 2026-08-07 — Cross-thread crash fixed (WPF)

**Symptom:** `Microphone error: The calling thread cannot access this object because a different thread owns it.` ~0.25s after start; user's mic-privacy screenshot showed access granted (so NOT permissions).

**Root cause:** `CheckVad` read `AutoStopChk.IsChecked` (WPF control) on the NAudio thread.

**Fix (commit e9e89d2):** cache `_autoStop` on UI thread; all audio-thread UI updates via `Dispatcher.BeginInvoke`.

**Files:** `MainWindow.xaml.cs`, `MainWindow.xaml`

## 2026-08-06 — Recording toggle + mic selection + privacy

**Root cause (toggle):** mic device dying instantly (Windows privacy) → `IsRecording=false` → every tap started fresh. Also stale `RecordingStopped` events from old capture instances clearing a live recording.

**Fix (commit 24bce19):** `_recordingRequested` intent flag + generation counter in AudioCapture. Added mic dropdown (default = Windows default), Mic Privacy button, no-audio watchdog.

## 2026-08-06 — Static CRT for whisper.dll

**Symptom:** `0x8007007E Unable to load DLL 'whisper.dll' or one of its dependencies` on user PC (CI runner had VS runtime, user didn't).

**Fix (commit f41a9de):** `-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded` in CI cmake.

## 2026-08-06 — CI: VS 17 generator → Ninja + msvc-dev-cmd

**Symptom:** `Generator "Visual Studio 17 2022" could not find any instance of Visual Studio` — runner now ships VS 18.

**Fix:** `ilammy/msvc-dev-cmd@v1` + `-G Ninja -DCMAKE_BUILD_TYPE=Release`. Pinned whisper.cpp to v1.9.2.

## 2026-08-06 — Engine compile fixes

- `TextPostProcessor.cs`: missing `using System.Text.RegularExpressions`
- `ParakeetEngine.cs`: ONNX 1.19 API (`CreateTensorValueFromMemory`, `GetTensorTypeAndShape`, `Run(options, dict, outputs)`), dynamic input names (`audio_signal` + `length`, NOT `input`), `using var` scoping bug (disposed tensor before Run)
- `MelScaleFeaturizer.cs`: FFT bit-reversal int→bool
- `WhisperEngine.cs`: struct layout rewritten to match whisper.cpp v1.9.2 exactly (bool→byte, by-value params, embedded vad_params)
- `Taptalk.WPF/*.cs`: WinForms/WPF type ambiguity aliases (Application, MessageBox, Point, Color, Clipboard, IDataObject, OpenFileDialog)
