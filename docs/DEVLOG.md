## 2026-08-13 — Parakeet "records nothing" fix: NeMo preprocessing + VAD performance + model detection

**Symptom:** User reports "everytime I record it records nothing" with Parakeet ONNX on AMD Radeon GPU.

**Root causes identified (coding-partner verified):**
1. **NeMo audio preprocessing mismatch.** Parakeet was trained with NVIDIA NeMo pipeline expecting:
   - Audio scaled to the 16-bit integer range (×32768) before STFT/Mel
   - Pre-emphasis filter (coefficient 0.97)
   - Per-feature instance normalization across time
   The old code fed [-1,1] floats with none of these steps, producing log-mel features far outside the training distribution → 100% CTC blank tokens → empty transcript.
2. **VAD O(N²) performance leak.** `CheckVadCore()` called `_capture.GetSnapshot()` on every audio callback, resampling the entire ever-growing recording buffer each time. This could starve the audio thread and make capture appear dead.
3. **Redundant audio type thrashing.** `GetSnapshot()` converted native float → 16-bit PCM bytes → float. Now reads float samples directly from `ISampleProvider`.
4. **Wrong model file selection.** README pointed to `model.onnx` (~41MB) which requires external `model.onnx.data` (~2.4GB). The self-contained file is `model.int8.onnx` (~650MB). Added size detection + warning in `LoadModel()` and updated README.
5. **Silent DirectML INT8 failure on AMD.** Added feature-tensor and logit statistics (min/max/mean/NaN) to the debug log so the user can verify whether DirectML is producing valid output.

**Fixes applied (first pass):**
- `MelScaleFeaturizer.Extract()`: scale to 32768, pre-emphasis 0.97, per-feature z-score normalization.
- `VADDetector`: added `Check(float rms, int nowMs)` overload for streaming chunks; `MainWindow.CheckVadCore()` now uses the incoming chunk instead of `GetSnapshot()`.
- `AudioCapture.GetSnapshot()`: direct float read, no short PCM roundtrip.
- `ParakeetEngine.LoadModel()`: warns if model file <100MB and missing `.data` file.
- `ParakeetEngine.Transcribe()` + `RunInferenceCore()`: logs feature tensor stats and logit min/max/NaN.
- `README.md`: explicitly instructs users to download `model.int8.onnx` and `vocab.txt`.

**Second pass — exact onnx-asr match:**
After the first build still produced all-blank frames, I checked the official reference implementation (`onnx-asr` by the model exporter). The real mismatches were:
- **Slaney mel scale** with **Slaney bandwidth normalization** (old code used HTK scale + no normalization).
- Hann window must be **400-point zero-padded to 512** (old code used a 400-point window on a 400-sample segment).
- Waveform must be **padded by 256 samples** on each side.
- Audio must stay in **[-1,1]** float range (no ×32768 scaling).
- Log zero guard value is **2^-24**.
- Feature output is flattened `[1, 80, T]` directly.
- Length input formula matches reference: `frames / 8`

**Files:** `Taptalk.Engine.Parakeet/MelScaleFeaturizer.cs`, `Taptalk.Engine.Parakeet/ParakeetEngine.cs`, `Taptalk.Core/VADDetector.cs`, `Taptalk.Core/AudioCapture.cs`, `Taptalk.WPF/MainWindow.xaml.cs`, `README.md`

## 2026-08-13b — length input bug: off by factor of 8

**Symptom:** v1.0.1 still produced 100% blank CTC output even with correct mel pipeline.

**Root cause found in user log:** `Run: 'audio_signal'=[1,80,268] length=33`. The ONNX `length` input was being divided by 8 (subsampled encoder length), but the model expects the **number of mel frames**, i.e. `raw_samples / hop_length = 42720 / 160 = 267`.

**Fix:**
- `ParakeetEngine.Transcribe` / `TranscribePartial` now pass the raw sample count through to `RunInferenceCore`.
- `length` = `rawSamples / HopLength`.
- Feature extractor now masks/zeros the trailing frame and computes instance normalization only over the valid `sample_len / hop_length` frames.
- Padding switched from reflect to constant zero to exactly match the reference `np.pad(...)` default.

**Build:** v1.0.2

## 2026-08-13c — Clipboard copy + Copy-only mode

**Symptom:** Transcription works, but text is not reliably injected into the target field and is not available on the clipboard.

**Fix:**
- Final cleaned transcription is always copied to the Windows clipboard after recording stops.
- Added a "📋 Copy only" checkbox: when checked, Taptalk skips `SendInput` keystroke injection and only copies the text to the clipboard (user pastes with `Ctrl+V`).
- Resolved `Clipboard` ambiguity between `System.Windows.Clipboard` and `System.Windows.Forms.Clipboard`.

**Build:** v1.0.3

## 2026-08-13d — Focus-aware injection + clipboard retries

**Symptom:** Text is transcribed correctly but is not pasted into the intended text field; log shows `CLIPBRD_E_CANT_OPEN` and paste goes to the wrong window.

**Fix:**
- Taptalk now records which window had focus when recording started and restores that window before typing/pasting.
- Clipboard operations retry up to 10 times (50ms backoff) to survive `CLIPBRD_E_CANT_OPEN`.
- Text shorter than 50 characters is now sent as direct `SendInput` Unicode keystrokes instead of clipboard paste (avoids clipboard contention entirely for short phrases).
- Added short delays after recording stops so overlay focus changes settle before injection.

**Build:** v1.0.4

## 2026-08-13e — Simple auto-paste (no focus management)

**Symptom:** User wants a simpler workflow: after recording, copy to clipboard and immediately paste into whatever is currently selected, without Taptalk trying to manage windows.

**Fix:**
- Removed focus-restoration logic entirely.
- Replaced "Copy only" checkbox with "📌 Auto-paste after stop" (default ON).
- Added configurable "Paste shortcut" text box (default `Ctrl+V`).
- After recording stops, Taptalk copies the transcription to clipboard and sends the configured paste shortcut to the currently active window.

**Build:** v1.0.5

## 2026-08-09 — ROOT CAUSE CONFIRMED: Windows mic level 25% (pure passthrough) → Fix Mic Level button (11th report)

**Log evidence (the smoking gun):** `Endpoint mic level: 25% (Windows Levels slider)` + `⚠️ Mic input level is only 25%`. The app's diagnostics WORKED — it read the Windows mic Levels slider and found it at 25%. Taptalk is pure passthrough (no AGC) so it hears the TRUE 25% level; Discord/other apps apply their own auto-gain and mask it. User's voice IS present (peak 0.0456, 16x above noise) but attenuated by Windows to 25%.

**Also fixed:** the per-app session-volume code threw `InvalidCastException: Unable to cast COM object ... to IMMDevice (E_NOINTERFACE)` on `AudioSessionManager` — some devices don't expose it. Now best-effort only (logs a plain line, never CRITICAL). The ENDPOINT level is the real lever anyway.

**Fixes (commit pending):**
- `AudioCapture.EndpointLevelScalar` public property (reads the Levels slider).
- `AudioCapture.RaiseEndpointVolumeToFull()` — sets `MasterVolumeLevelScalar = 1.0` (GLOBAL: affects all apps on this mic — requires user consent).
- MainWindow: after `_capture.Start()`, if level < 60% → show **`🔊 Fix Mic Level`** button + red status text explaining the issue; button prompts Yes/No (consent for the global change) then raises + hides + confirms.
- Session-volume normalization now best-effort (catch → plain log, no CRITICAL spam).

**Next-log expectations:** either the user clicks Fix Mic Level (→ `🔊 Raised Windows mic level: 25% → 100%`) or raises it manually; then AUDIO peak should jump to 0.1+ under speech and the model should decode real words.

## 2026-08-09 — Mic level -56dBFS but "other apps hear me fine": per-app session volume + endpoint diagnosis + noise gate (10th report)

**Symptom:** pipeline NOW works (FIRST CHUNK, Samples=48640, inference runs) but AvgRMS=0.0016 (-56 dBFS) on TWO mics while Discord/Voice Recorder hear the user fine. Permissions granted (42 access events).

**Research insight (web subagent + coding-partner):** "other apps fine" does NOT rule out low system mic level — Discord/Voice Recorder apply their OWN AGC/noise-suppression on top of raw WASAPI shared capture. Taptalk's WasapiCapture is PURE PASSTHROUGH. The two app-addressable causes:
1. **Per-app capture session volume** (Volume Mixer input slider) persisted LOW for Taptalk specifically — the #1 cause of "my app quiet, others fine". Windows persists it per-app.
2. **Endpoint Levels slider** low on the device (applies to ALL shared-mode apps) — masked by other apps' AGC.

**Fixes (commit pending, coding-partner verified):**
- `LogEndpointVolumeAndNormalizeSession()` in AudioCapture.Start() (after StartRecording):
  - Logs endpoint `AudioEndpointVolume.MasterVolumeLevelScalar` (the Levels slider) + warns if < 60% (never auto-raises — global side effect).
  - Deferred (150ms, Task.Run) index-based session scan; finds OUR session via `GetProcessID == (uint)Environment.ProcessId`; sets `SimpleAudioVolume.Volume = 1.0` + unmute (per-app only, safe).
- **Noise gate** in AudioNormalizer: samples below `SilenceFloor*8` (~-54 dBFS) zeroed AFTER gain — so 30x boost can't amplify noise floor into ASR hallucinations.
- NAudio 2.2.1 API: `AudioSessionControl.GetProcessID` is a uint PROPERTY; `SimpleAudioVolume.Volume/Mute` are read/write properties; use index-based Sessions loop (foreach throws on session churn).

**Next-log expectations:** `Endpoint mic level: NN% (Windows Levels slider)` + either `🔊 Fixed Taptalk per-app mic volume` (if it was the cause) or `⚠️ No dedicated WASAPI session found`. If endpoint level is low (e.g. 10%), the fix is Windows Sound → Input level (guide user; app never changes global level silently).

## 2026-08-09 — ZERO samples despite FIRST CHUNK: live WDL pipeline stalls → RAW-capture + post-conversion (9th report)

**Symptom:** after the polling-mode revert, `FIRST CHUNK: 5760 raw bytes arrived in callback` proves DataAvailable fires, but `Samples=0 | Rate=0 samples/s` — the live pipeline produced nothing for 2.4s.

**Root cause (coding-partner pattern):** the LIVE streaming pipeline (BufferedWaveProvider → WdlResamplingSampleProvider → SampleToWaveProvider16) drained on every DataAvailable never yielded on the user's machine — WDL buffers internally and the read loop stalled. WASAPI has never actually delivered samples through the live chain (earlier "recording works" reports were about the state machine; the stop-hang masked it).

**Fix (commit pending):** rewrite AudioCapture to RAW-capture + post-conversion:
- OnDataAvailable: append native bytes to a growing `_rawBytes` list (lock-protected) + compute a LIGHTWEIGHT inline RMS from raw float bytes (energy detection needs no resampling) → OnChunk for VAD.
- GetSnapshot(): deterministic FULL conversion of the whole recording: RawSourceWaveStream → ToSampleProvider → MonoDownmixSampleProvider → WdlResamplingSampleProvider(16000) → SampleToWaveProvider16 → float[]. Runs at stop + partial ticks (2-3s audio converts fast).
- TotalSamples: derived from raw byte count / native format (for the no-audio watchdog).
- Keep: polling mode, stop-hang protections (unsubscribe-first + 2s time-box + orphan + no capture-thread dispose), FIRST CHUNK + per-sec AUDIO diagnostics.

**Interface unchanged** (OnChunk float[] for VAD, GetSnapshot float[] 16kHz, TotalSamples, DeviceNumber, NoteSilence, OnError).

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
