# PhoneCam Shared Memory IPC Layout (v1)

## File Location & Permissions
- **Path**: `C:\ProgramData\PhoneCam\frame.shm`
- **Access**: Directory & file ACL grants `FullControl` / `Modify` to `BUILTIN\Users`, `NT AUTHORITY\LOCAL SERVICE` (Windows Frame Server), `SYSTEM`, and read/write to `ALL APPLICATION PACKAGES` (`S-1-15-2-1`).
- **Why file-backed?** An unnamed `CreateFileMappingW(hFile, ...)` on a shared file path maps the same kernel pages across Session 0 (`svchost.exe` Frame Server) and Session 1 (desktop WPF app) without requiring `SeCreateGlobalPrivilege`.

## Binary Layout (Little-Endian, Native x64 Struct)

Total size: `64 + 3 * (4 * 1024 * 1024) = 12,582,976` bytes (~12 MiB).

### Header (64 bytes, offset `0..63`)
| Offset | Size | Type     | Field             | Description |
|--------|------|----------|-------------------|-------------|
| 0      | 4    | `uint32` | `magic`           | `0x50435643` (`"PCVC"`) |
| 4      | 4    | `uint32` | `version`         | `1` |
| 8      | 4    | `uint32` | `slotSize`        | `4,194,304` (4 MiB per slot; fits up to `1920x1080` NV12) |
| 12     | 4    | `uint32` | `slotCount`       | `3` (triple-buffered) |
| 16     | 8    | `int64`  | `writeSeq`        | Monotonically incremented after a slot is completely written (publish fence) |
| 24     | 4    | `int32`  | `latestSlot`      | Index (`0..2`) of most recently completed frame |
| 28     | 4    | `int32`  | `frameWidth`      | Width of frame in `latestSlot` |
| 32     | 4    | `int32`  | `frameHeight`     | Height of frame in `latestSlot` |
| 36     | 4    | `int32`  | `requestedWidth`  | Output width negotiated by Frame Server (`1280` default) |
| 40     | 4    | `int32`  | `requestedHeight` | Output height negotiated by Frame Server (`720` default) |
| 44     | 4    | `uint32` | `reserved`        | `0` |
| 48     | 8    | `int64`  | `frameTimestampUs`| Monotonic microsecond timestamp of `latestSlot` |
| 56     | 8    | `int64`  | `lastProducerTickMs` | `GetTickCount64()` / `Environment.TickCount64` from C# writer (stale check > 2000 ms) |

### Frame Slots (3 x 4 MiB, offset `64 + slot * 4,194,304`)
Each slot contains tightly packed **NV12** pixels (`width * height` bytes of Y followed immediately by `width * height / 2` bytes of interleaved UV, stride = `width`).

