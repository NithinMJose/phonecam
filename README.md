# PhoneCam

A minimal, low-latency **phone-as-webcam** system for **Android 16 + Windows 11** with **no OBS dependency**.

- **Zero-copy capture & encode on Android:** `Camera2` renders straight into `MediaCodec`'s hardware H.264 `Surface` inside a Foreground Service (keeps streaming with the screen off).
- **USB & WiFi/Hotspot transports:** `adb reverse` over USB (`127.0.0.1:18420`) or direct TCP + UDP auto-discovery (`18421`) over WiFi/hotspot with automatic reconnects.
- **GPU decode & native Windows 11 Virtual Camera:** Media Foundation D3D11 hardware H.264 decoder feeds a triple-buffered shared-memory ring (`C:\ProgramData\PhoneCam\frame.shm`) read by a lightweight C++ COM DLL inside Windows Frame Server (`MFCreateVirtualCamera`).
- **Live controls & auto-orientation:** Back/Front switching, resolution/FPS/bitrate/zoom, horizontal mirror & vertical flip, automatic portrait/landscape rotation (with centered pillarboxing so calls never break mid-stream), settings persistence, and system tray mode.

---

## Repository Layout

- [android/](android) — Kotlin Android app (`Camera2` + `MediaCodec` + Foreground Service + TCP client + UDP discovery)
- [windows-app/](windows-app) — C# WPF desktop app (`.NET 10`, TCP server, UDP discovery responder, MF D3D11 decoder, shared memory bridge, VCam host, settings, system tray)
- [virtualcam-dll/](virtualcam-dll) — C++20 COM DLL implementing `IMFActivate` / `IMFMediaSourceEx` / `IMFMediaStream` for Windows 11 Frame Server
- [installer/](installer) — Inno Setup installer script (`PhoneCam.iss`) and release build script (`build-installer.ps1`)
- [docs/](docs) — [Architecture](docs/architecture.md), [Wire Protocol](docs/protocol.md), and [Shared Memory Layout](docs/shared-memory.md)

---

## Quick Install (Built Artifacts)

Running `installer\build-installer.ps1` produces standalone distributables in `installer\output\`:
- **`PhoneCamSetup.exe`** — Self-contained Windows 11 installer (no `.NET` or Visual Studio required on target PCs). Automatically installs `PhoneCam.exe` + `PhoneCamVirtualCam.dll`, grants Frame Server permissions, registers the COM DLL via `regsvr32`, adds a Windows Firewall rule for WiFi/Hotspot, and optionally enables minimized startup at Windows login.
- **`PhoneCam.apk`** — Android APK ready to sideload onto any Android 11+ phone.

---

## Building from Source

### Prerequisites
- **Android:** Android Studio / Android SDK (`compileSdk 37`, `platform-tools` / `adb`) + JDK 17+
- **Windows C# App:** .NET 10 SDK (`net10.0-windows10.0.19041.0`)
- **C++ Virtual Camera DLL:** Visual Studio 2022 (or Build Tools) with **Desktop development with C++** + Windows 11 SDK
- **Windows Installer:** Inno Setup 6 (`winget install JRSoftware.InnoSetup`)

### 1. Build the Installer + APK (Everything at Once)
```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```
Outputs `installer\output\PhoneCamSetup.exe` and `installer\output\PhoneCam.apk`.

### 2. Development Workflow (Individual Components)
- **Android:**
  ```powershell
  cd android
  .\gradlew.bat assembleDebug
  adb install -r app\build\outputs\apk\debug\app-debug.apk
  ```
- **C++ Virtual Camera DLL (one-time Admin registration):**
  ```powershell
  # In Administrator PowerShell:
  Set-ExecutionPolicy -Scope Process Bypass
  .\virtualcam-dll\setup-and-register.ps1
  ```
- **Windows App:**
  ```powershell
  cd windows-app\PhoneCam
  dotnet run
  ```

---

## Using PhoneCam

1. Open **PhoneCam** on Windows (`Virtual Cam: Active (PhoneCam)` appears in green).
2. Open **PhoneCam** on Android:
   - **USB mode:** Plug in USB with USB Debugging enabled (`PhoneCam.exe` automatically runs `adb reverse tcp:18420 tcp:18420`). Tap **Start Streaming**.
   - **WiFi / Hotspot mode:** Switch to **WiFi / Hotspot**, tap **Scan PC** (or enter the IP shown in Windows' status bar), and tap **Start Streaming**.
3. In **Windows Camera**, **Zoom**, **Google Meet**, **Teams**, or your browser, select **`PhoneCam (Windows Virtual Camera)`**.
4. Open **Settings** in the Windows app to adjust Resolution, FPS, Bitrate, Zoom, Mirror/Flip, Rotation offset, Tray behavior, and **Start minimized at Windows login**.

---

## Troubleshooting

- **WiFi `Scan PC` doesn't find the PC:** Windows often marks WiFi networks and phone hotspots as **Public**, blocking inbound traffic. Running `PhoneCamSetup.exe` adds the firewall rule automatically; for dev builds, allow `PhoneCam.exe` on **Private & Public** networks in Windows Defender Firewall.
- **USB mode says connection refused:** Click **USB Re-bind** in the Windows app (or ensure USB Debugging is authorized on the phone).
- **Stream pauses when phone screen is off:** Ensure the PhoneCam foreground notification is allowed and set PhoneCam battery usage to **Unrestricted** in Android App Info (especially on OEM skins like Nothing OS).
- **Removing Microsoft's built-in Phone Link Virtual Camera:** Open **Windows Settings -> Bluetooth & devices -> Mobile devices -> Manage devices**, and turn **Use as a connected camera** **OFF**.
