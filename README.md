# Taptalk — Voice-to-Text for Windows

Port of the TapType Android app to Windows 10/11. Floating mic overlay → record → on-device transcription → text injected into the focused field.

## Engines

| Engine | Backend | Default | Notes |
|--------|---------|---------|-------|
| **Parakeet (GPU)** | ONNX Runtime + DirectML | ✅ | Uses ANY GPU (AMD/NVIDIA/Intel). Real-time streaming. |
| **Whisper (CPU)** | whisper.cpp DLL (AVX-512/AVX2) | Fallback | Built from source with CPU optimizations. |

## How to get the installer (easiest)

1. Push this repo to GitHub
2. GitHub Actions builds `Taptalk-Setup-1.0.0.exe` automatically
3. Download it from the workflow artifact (Actions tab) or a tagged Release
4. Run it — done

## Build locally (Windows)

Requires: Visual Studio 2022 (C++ + .NET 8 workload), CMake, .NET 8 SDK

```powershell
# 1. Get whisper.cpp
git clone --depth 1 https://github.com/ggml-org/whisper.cpp.git

# 2. Build whisper.dll (AVX-512 for Ryzen 9 / Zen 4+)
cd native
cmake -B build -DWHISPER_SRC_DIR="C:\path\to\whisper.cpp" -G "Visual Studio 17 2022" -A x64 -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_AVX512=ON -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_EXAMPLES=OFF
cmake --build build --config Release --target whisper
Copy-Item build\bin\Release\whisper.dll ..\Taptalk.Engine.Whisper\bin\Release\

# 3. Publish the app
cd ..
dotnet restore Taptalk.sln
dotnet publish Taptalk.WPF -c Release -r win-x64 --self-contained true -o publish

# 4. Build installer (Inno Setup)
cd installer
iscc /DAppDir="..\publish" Taptalk.iss
# → Output\Taptalk-Setup-1.0.0.exe
```

## Models

### Parakeet (default)
Download: https://huggingface.co/istupakov/parakeet-ctc-0.6b-onnx
- File: `model.onnx` (~600MB) — CTC single-file export
- **Also download `vocab.txt` from the same page** and put it in the SAME folder as `model.onnx` (the app loads it automatically — without it, transcription comes back empty)
- In the app: Settings → Engine: Parakeet → Browse → select the .onnx file

### Whisper
Download any GGML model: https://huggingface.co/ggerganov/whisper.cpp/tree/main
- `ggml-tiny.en.bin` (75MB) — fastest
- `ggml-base.en.bin` (150MB) — good balance
- In the app: Settings → Engine: Whisper → Browse → select the .bin file

## First-run setup

1. **Allow microphone access** — Windows will ask; if it didn't, open Settings → Privacy & security → Microphone → "Let desktop apps access your microphone" → On. Or click the **🔧 Mic Privacy** button in Taptalk.
2. Pick your microphone in Settings → Microphone (default = Windows default mic).
3. Download a model (see below).

## Usage

- **Ctrl+Shift+Space** — start/stop recording (push-to-talk)
- **Tap the floating mic button** — same
- Hold the button and drag to move it
- Auto-stop on silence (toggleable)
- Text is injected into whatever window has focus
- **Microphone:** pick any input device in Settings (default = Windows default mic); use **🔧 Mic Privacy** if Windows blocks access

## Project structure

```
Taptalk.sln
 ├── Taptalk.WPF             # Overlay UI, tray icon, hotkey, text injection
 ├── Taptalk.Core            # Audio capture (NAudio), TextPostProcessor, VAD
 ├── Taptalk.Engine.Parakeet # ONNX + DirectML + Mel featurizer
 ├── Taptalk.Engine.Whisper  # whisper.cpp P/Invoke bridge
 ├── native/                 # whisper.cpp CMake build
 ├── installer/              # Inno Setup script
 └── .github/workflows/      # CI → builds installer automatically
```

## Privacy

100% on-device. Audio never leaves your machine. No cloud, no account, no telemetry.
