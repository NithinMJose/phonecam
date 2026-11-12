# PhoneCam System Architecture

## Overview

PhoneCam turns an Android phone into a low-latency, native Windows 11 webcam without OBS or third-party virtual camera drivers.

```
┌─────────────────────────────────────────┐
│ Android Phone (Nothing OS / Android 16) │
│                                         │
│  Camera2 API                            │
│      │ (zero-copy hardware Surface)     │
│      ▼                                  │
│  MediaCodec (H.264 Hardware Encoder)    │
│      │ (CBR, low-latency, Annex-B NALs) │
│      ▼                                  │
│  Foreground Service + TCP Client        │
└──────────────────┬──────────────────────┘
                   │
      USB: adb reverse tcp:18420 -> PC:18420
      WiFi: Direct TCP -> <PC_IP>:18420
                   │
                   ▼
┌─────────────────────────────────────────┐
│ Windows 11 PC                           │
│                                         │
│  C# WPF App (.NET)                      │
│   ├── TCP Server (receives framed H.264)│
│   ├── Media Foundation H.264 Decoder    │
│   │   (D3D11 hardware-accelerated -> NV12)
│   ├── Live Preview Window + Stats       │
│   └── Shared Memory Writer (triple-buf) │
└──────────────────┬──────────────────────┘
                   │ Named Shared Memory + Event
                   │ (cross-session / FrameServer ACL)
                   ▼
┌─────────────────────────────────────────┐
│ Windows Frame Server Service (svchost)  │
│                                         │
│  virtualcam-dll (C++ COM / MF Source)   │
│   ├── Registered via MFCreateVirtualCamera
│   └── Outputs NV12 frames to apps       │
└──────────────────┬──────────────────────┘
                   │
                   ▼
     Zoom / Google Meet / Teams / Windows Camera
```

## Key Design Decisions

### 1. Why PC = TCP Server, Phone = TCP Client?
- `adb reverse tcp:18420 tcp:18420` forwards connections **initiated by the phone** to a listener on the PC.
- Using the exact same direction over WiFi (phone connects to `<PC_IP>:18420`) means the transport layer and state machine are identical for both USB and WiFi.
- Both sockets set `TCP_NODELAY = true` so small packets (control messages, small P-frames) are sent immediately without Nagle's 40–200 ms buffering delay.

### 2. Zero-Copy Camera-to-Encoder Path on Android
- **Camera2** renders directly into `MediaCodec.createInputSurface()`.
- Image buffers never enter Kotlin/Java heap or CPU memory—the ISP/GPU hands frames straight to the hardware video encoder.
- Why **Camera2** over CameraX? Camera2 gives direct access to encoder `Surface` lifecycle, manual FPS ranges (`CONTROL_AE_TARGET_FPS_RANGE`), low-latency capture requests, and camera sensor orientation without extra abstraction layers.

### 3. Low-Latency H.264 Configuration
- **H.264 Baseline/Main Profile** (Annex-B byte stream with `00 00 00 01` start codes) for universal hardware decode support on Windows 11.
- **CBR** (`BITRATE_MODE_CBR`) to prevent bitrate spikes from choking USB/WiFi buffers.
- **Periodic Keyframes** (every 1–2 seconds) + **On-Demand Keyframe Request** (`REQUEST_KEYFRAME` packet) when the PC connects, reconnects, or drops packets.
- **Drop-Old-Frames Backpressure**: If the network stalls, old non-keyframe packets are discarded instead of queueing up seconds of latency.

### 4. Windows Decoding & Virtual Camera Split
- **C# WPF App**: Owns UI, TCP networking, hardware H.264 decoding (Media Foundation `CLSID_CMSH264DecoderMFT` with D3D11), and latency telemetry. WPF is chosen over WinUI 3 for simpler unpackaged deployment and straightforward interop with Win32/Media Foundation/system tray.
- **C++ Virtual Camera DLL**: Windows 11's `MFCreateVirtualCamera` loads the COM `IMFMediaSource` DLL inside the **Windows Frame Server** service (`svchost.exe`), *not* inside the C# app.
- **IPC**: A triple-buffered Named Shared Memory region + synchronization Event transfers NV12 frames from the C# app to the DLL with a proper Windows security descriptor (DACL) so the Frame Server service can read from the user-session producer.

## Repository Structure

```
/android          Kotlin Android project (Camera2 + MediaCodec + Foreground Service)
/windows-app      C# WPF application (TCP server, MF H.264 decoder, preview, settings)
/virtualcam-dll   C++ COM DLL implementing IMFMediaSource for MFCreateVirtualCamera
/docs             Architecture, wire protocol spec, and setup guides
```

