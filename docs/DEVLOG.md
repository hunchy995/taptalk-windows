# Taptalk — Development Log

Chronological record of fixes. Latest first. Companion to the `taptalk-windows` skill.

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

**Files:** `Taptalk.Core/DebugRecorder.cs` (new), `AudioCapture.cs`, `MelScaleFeaturizer.cs`, `ParakeetEngine.cs`, `WhisperEngine.cs`, `TextInjector.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`

## 2026-08-07 — Empty transcription fixed (vocab.txt)

**Symptom:** `7.3s audio captured (116800 samples)` + `Inference: 338ms (RTF 0.05x)` but `📝 ""`.

**Root cause:** `ParakeetEngine._vocab` was never loaded — `DecodeTokens` fell back to `string.Join(" ", tokens)` (raw IDs / empty). The model also needs SentencePiece BPE decoding (`\u2581` → space).

**Fix (with coding partner):**
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

**Fix:**
- `RecordingState` enum (Idle/Recording/Transcribing), all transitions on UI thread
- VAD stop marshaled via `Dispatcher.BeginInvoke`
- Hotkey → **Ctrl+Shift+Space** + 500ms debounce
- `MicDevice` list: Index -1 = Windows default (WAVE_MAPPER)
- `AudioCapture.DeviceNumber` renamed, defaults -1, 100ms buffers

**Files:** `MainWindow.xaml.cs`, `MainWindow.xaml`, `AudioCapture.cs`, `HotKeyManager.cs`, `README.md`

## 2026-08-07 — Cross-thread crash fixed (WPF)

**Symptom:** `Microphone error: The calling thread cannot access this object because a different thread owns it.` ~0.25s after start; user's mic-privacy screenshot showed access granted (so NOT permissions).

**Root cause:** `CheckVad` read `AutoStopChk.IsChecked` (WPF control) on the NAudio thread.

**Fix:** cache `_autoStop` on UI thread; all audio-thread UI updates via `Dispatcher.BeginInvoke`.

**Files:** `MainWindow.xaml.cs`, `MainWindow.xaml`

## 2026-08-06 — Recording toggle + mic selection + privacy

**Root cause (toggle):** mic device dying instantly (Windows privacy) → `IsRecording=false` → every tap started fresh. Also stale `RecordingStopped` events from old capture instances clearing a live recording.

**Fix:** `_recordingRequested` intent flag + generation counter in AudioCapture. Added mic dropdown (default = Windows default), Mic Privacy button, no-audio watchdog.

## 2026-08-06 — Static CRT for whisper.dll

**Symptom:** `0x8007007E Unable to load DLL 'whisper.dll' or one of its dependencies` on user PC (CI runner had VS runtime, user didn't).

**Fix:** `-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded` in CI cmake.

## 2026-08-06 — CI: VS 17 generator → Ninja + msvc-dev-cmd

**Symptom:** `Generator "Visual Studio 17 2022" could not find any instance of Visual Studio` — runner now ships VS 18.

**Fix:** `ilammy/msvc-dev-cmd@v1` + `-G Ninja -DCMAKE_BUILD_TYPE=Release`. Pinned whisper.cpp to v1.9.2.

## 2026-08-06 — Engine compile fixes

- `TextPostProcessor.cs`: missing `using System.Text.RegularExpressions`
- `ParakeetEngine.cs`: ONNX 1.19 API (`CreateTensorValueFromMemory`, `GetTensorTypeAndShape`, `Run(options, dict, outputs)`), dynamic input names (`audio_signal` + `length`, NOT `input`), `using var` scoping bug (disposed tensor before Run)
- `MelScaleFeaturizer.cs`: FFT bit-reversal int→bool
- `WhisperEngine.cs`: struct layout rewritten to match whisper.cpp v1.9.2 exactly (bool→byte, by-value params, embedded vad_params)
- `Taptalk.WPF/*.cs`: WinForms/WPF type ambiguity aliases (Application, MessageBox, Point, Color, Clipboard, IDataObject, OpenFileDialog)
