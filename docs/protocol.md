# PhoneCam Wire Protocol Specification (v1)

## Transport
- **Protocol**: TCP over `adb reverse` (USB) or direct LAN/Hotspot IP (WiFi).
- **Default TCP Port**: `18420`
- **UDP Discovery Port**: `18421` (Probe: `PHONECAM_DISCOVER_V1`, Reply: `PHONECAM_HERE_V1|<host_name>|<tcp_port>`)
- **Role**: PC is TCP Server (`0.0.0.0:18420`); Phone is TCP Client.
- **Socket Options**: `TCP_NODELAY = true` on both sides.
- **Heartbeat / Watchdog**: PC sends `PING` every 500 ms; both sides treat > 4 s of silence as a dead link and reconnect cleanly.
- **Byte Order**: **Big-Endian (Network Byte Order)** for all multi-byte integers.

---

## Packet Framing

Every message on the wire consists of a fixed **24-byte header** followed by `payload_length` bytes.

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|          magic (0x5043)       |  version (1)  |     type      |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|             flags             |           reserved            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                           sequence                            |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                        payload_length                         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
+                   timestamp_us (int64, 8 bytes)               +
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                     payload (0 .. N bytes)                    |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

### Header Fields (24 bytes)

| Offset | Size | Type     | Field            | Description |
|--------|------|----------|------------------|-------------|
| 0      | 2    | `uint16` | `magic`          | Constant `0x5043` (`"PC"`). Disconnect/resync if mismatch. |
| 2      | 1    | `uint8`  | `version`        | Protocol version. Currently `1`. |
| 3      | 1    | `uint8`  | `type`           | Message type ID (see table below). |
| 4      | 2    | `uint16` | `flags`          | Bitmask flags (see table below). |
| 6      | 2    | `uint16` | `reserved`       | Must be `0` in v1. |
| 8      | 4    | `uint32` | `sequence`       | Monotonically increasing counter per sender. |
| 12     | 4    | `uint32` | `payload_length` | Payload size in bytes (`0` if no payload). Max `16_777_216` (16 MiB). |
| 16     | 8    | `int64`  | `timestamp_us`   | Monotonic timestamp in microseconds. |

### Flags Bitmask (`flags`)

| Bit | Value    | Name            | Meaning |
|-----|----------|-----------------|---------|
| 0   | `0x0001` | `FLAG_KEYFRAME` | `VIDEO_FRAME` contains an IDR / sync frame. |
| 1   | `0x0002` | `FLAG_EOS`      | Sender is cleanly ending the stream. |

---

## Message Types

### Phone -> PC

| Type ID | Name           | Payload Format | Description |
|---------|----------------|----------------|-------------|
| `0x01`  | `VIDEO_FRAME`  | Raw H.264 Annex-B NAL unit(s) | Encoded video access unit. `timestamp_us` is encoder/capture PTS (`elapsedRealtimeNanos / 1000`). |
| `0x02`  | `CODEC_CONFIG` | Raw H.264 Annex-B SPS + PPS | Emitted at encoder start and cached so it can be re-sent immediately on keyframe request or reconnect. |
| `0x03`  | `STREAM_INFO`  | 16-byte fixed struct (see below) | Sent immediately after connection (and whenever format changes) so the PC knows video dimensions before decoding. |
| `0x04`  | `AUDIO_FRAME`  | 4-byte audio header + raw LE PCM16 samples | Phone microphone PCM chunk: `sample_rate` (`uint16` BE), `channels` (`uint8`), `bits_per_sample` (`uint8`), followed by raw little-endian 16-bit signed PCM frames. |

#### `STREAM_INFO` (`0x03`) Payload (16 bytes, Big-Endian)

| Offset | Size | Type     | Field      | Description |
|--------|------|----------|------------|-------------|
| 0      | 4    | `uint32` | `width`    | Encoded video width in pixels (e.g., `1280` or `1920`). |
| 4      | 4    | `uint32` | `height`   | Encoded video height in pixels (e.g., `720` or `1080`). |
| 8      | 2    | `uint16` | `fps`      | Target frame rate (e.g., `30` or `60`). |
| 10     | 1    | `uint8`  | `codec`    | `1` = H.264 (AVC), `2` = H.265 (HEVC, future). |
| 11     | 1    | `uint8`  | `rotation` | Clockwise rotation hint in degrees / 90 (`0`, `1`=90°, `2`=180°, `3`=270°). |
| 12     | 4    | `uint32` | `bitrate`  | Target bitrate in bps (e.g., `6000000`). |

### PC -> Phone

| Type ID | Name               | Payload Format | Description |
|---------|--------------------|----------------|-------------|
| `0x10`  | `REQUEST_KEYFRAME` | Empty (0 bytes) | Tells Android MediaCodec to emit an immediate sync frame (`PARAMETER_KEY_REQUEST_SYNC_FRAME`) and re-send `CODEC_CONFIG` if needed. |
| `0x11`  | `PING`             | Empty (0 bytes) | `header.timestamp_us` holds PC monotonic send time (µs). Phone must reply promptly with `PONG`. |
| `0x12`  | `PONG`             | 8-byte `int64` (`phone_rx_ts_us`) | Phone echoes PC's `timestamp_us` in `header.timestamp_us` and puts its own monotonic clock (`elapsedRealtimeNanos / 1000`) in the 8-byte payload. Allows PC to compute both **RTT** and **clock offset** for end-to-end latency estimation. |
| `0x13`  | `SET_CONFIG`       | 16-byte fixed struct (see below) | Dynamic camera facing, resolution, fps, bitrate, and zoom commands. |
| `0x14`  | `AUDIO_CONTROL`    | 4-byte fixed struct | Byte 0: `enabled` (`1` = stream phone microphone, `0` = stop microphone); bytes 1..3 reserved (`0`). |

#### `SET_CONFIG` (`0x13`) Payload (16 bytes, Big-Endian)

Any numeric field set to `0` (or `0xFF` for `camera_facing`) means "leave unchanged".

| Offset | Size | Type     | Field           | Description |
|--------|------|----------|-----------------|-------------|
| 0      | 2    | `uint16` | `width`         | Requested width (`0` = unchanged). |
| 2      | 2    | `uint16` | `height`        | Requested height (`0` = unchanged). |
| 4      | 2    | `uint16` | `fps`           | Requested fps (`0` = unchanged). |
| 6      | 4    | `uint32` | `bitrate`       | Requested bitrate in bps (`0` = unchanged). |
| 10     | 1    | `uint8`  | `camera_facing` | `0` = back, `1` = front, `0xFF` = unchanged. |
| 11     | 1    | `uint8`  | `flags`         | Bit 0 (`0x01`): apply flip bits; Bit 1 (`0x02`): mirror H; Bit 2 (`0x04`): flip V. |
| 12     | 2    | `uint16` | `zoom_x100`     | Zoom factor * 100 (`0` = unchanged, e.g., `150` = 1.5x). |
| 14     | 2    | `uint16` | `reserved`      | Must be `0`. |

---

## Latency Measurement Math (Phase 1+)

1. **RTT (Network Round-Trip Time)**:
   $$\text{RTT} = t_{\text{PC,recv}} - t_{\text{PC,send}}$$
   One-way transport latency $\approx \text{RTT} / 2$.
2. **Clock Offset Estimation** (using `PING`/`PONG`):
   $$\text{offset} = t_{\text{Phone,rx}} - \left(t_{\text{PC,send}} + \frac{\text{RTT}}{2}\right)$$
   (Filtered with a minimum-RTT / exponential moving average to ignore jitter spikes.)
3. **Estimated Pipeline Latency** (Capture -> Encode -> Transport -> Decode -> Render):
   $$\text{Latency}_{\text{est}} = t_{\text{PC,rendered}} - \left(t_{\text{frame,phone}} - \text{offset}\right)$$

