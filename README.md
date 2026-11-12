# PhoneCam

Personal, low-latency "phone as webcam" system for Android 16 + Windows 11 without OBS.

## Structure

- `android/` — Kotlin Android app (Camera2 -> hardware `MediaCodec` H.264 -> Foreground Service + TCP client + UDP discovery + live `SET_CONFIG` + `OrientationEventListener`)
- `windows-app/` — C# WPF desktop app (.NET 10, TCP server, UDP discovery responder, Media Foundation D3D11 H.264 decoder, GPU/NV12 rotation + mirror/flip, shared memory writer, `MFCreateVirtualCamera` host, settings persistence, system tray)
- `virtualcam-dll/` — C++ COM DLL implementing `IMFMediaSourceEx` / `IMFMediaStream` for Windows 11 Frame Server (`MFCreateVirtualCamera`)
- `docs/` — [Architecture](docs/architecture.md), [Wire Protocol Spec](docs/protocol.md), and [Shared Memory Spec](docs/shared-memory.md)

## Build & Run (Phase 4)

### 1. Android App
```powershell
cd android
.\gradlew.bat assembleDebug
adb install -r app\build\outputs\apk\debug\app-debug.apk
```

### 2. Virtual Camera C++ DLL (One-Time Admin Setup)
Open an **Administrator PowerShell** window and run:
```powershell
Set-ExecutionPolicy -Scope Process Bypass
d:\Projects\personal\cam\virtualcam-dll\setup-and-register.ps1
```

### 3. Windows App
Launch [PhoneCam.exe](file:///D:/Projects/personal/cam/windows-app/PhoneCam/bin/Debug/net10.0-windows10.0.19041.0/PhoneCam.exe) (or run `dotnet run` inside `windows-app\PhoneCam`).

- **Settings Panel:** Toggle **Settings** in the top bar to adjust **Resolution** (`1080p`/`720p`/`540p`/`480p`), **FPS** (`15`/`24`/`30`/`60`), **Bitrate** (`2–16 Mbps`), **Zoom** (`1.0x–5.0x`), **Mirror Horizontal (Selfie)**, **Flip Vertical**, and **Minimize to tray on close**. Settings persist in `%APPDATA%\PhoneCam\settings.json`.
- **System Tray:** Closing the window hides it to the system tray while keeping your virtual camera active for Zoom/Meet/Teams; double-click the tray icon to restore or right-click -> **Exit** to quit.
