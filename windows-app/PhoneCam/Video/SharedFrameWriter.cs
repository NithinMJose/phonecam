using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using PhoneCam.Logging;

namespace PhoneCam.Video;

public unsafe sealed class SharedFrameWriter : IDisposable
{
    public const uint Magic = 0x50435643; // "PCVC"
    public const uint Version = 1;
    public const int SlotSize = 4 * 1024 * 1024;
    public const int SlotCount = 3;
    public const int HeaderSize = 64;
    public const long TotalSize = HeaderSize + (long)SlotSize * SlotCount;

    public const string ShmDirectory = @"C:\ProgramData\PhoneCam";
    public const string ShmFilePath = @"C:\ProgramData\PhoneCam\frame.shm";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SharedHeader
    {
        public uint Magic;
        public uint Version;
        public uint SlotSize;
        public uint SlotCount;
        public long WriteSeq;
        public int LatestSlot;
        public int FrameWidth;
        public int FrameHeight;
        public int RequestedWidth;
        public int RequestedHeight;
        public uint Reserved;
        public long FrameTimestampUs;
        public long LastProducerTickMs;
    }

    private readonly DebugLogger _logger;
    private FileStream? _fileStream;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private byte* _basePtr;
    private int _nextSlot;
    private bool _disposed;

    public bool IsReady => _basePtr != null && !_disposed;

    public SharedFrameWriter(DebugLogger logger)
    {
        _logger = logger;
    }

    public void Initialize()
    {
        try
        {
            Directory.CreateDirectory(ShmDirectory);
            ApplySharedPermissions(ShmDirectory);

            _fileStream = new FileStream(
                ShmFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.None);

            if (_fileStream.Length != TotalSize)
            {
                _fileStream.SetLength(TotalSize);
            }

            ApplyFilePermissions(ShmFilePath);

            _mmf = MemoryMappedFile.CreateFromFile(
                _fileStream,
                mapName: null,
                capacity: TotalSize,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false);

            _accessor = _mmf.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);
            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _basePtr = ptr;

            var hdr = (SharedHeader*)_basePtr;
            hdr->Magic = Magic;
            hdr->Version = Version;
            hdr->SlotSize = SlotSize;
            hdr->SlotCount = SlotCount;
            hdr->LatestSlot = 0;
            hdr->FrameWidth = 0;
            hdr->FrameHeight = 0;
            if (hdr->RequestedWidth <= 0) hdr->RequestedWidth = 1280;
            if (hdr->RequestedHeight <= 0) hdr->RequestedHeight = 720;
            hdr->Reserved = 0;
            hdr->FrameTimestampUs = 0;
            hdr->LastProducerTickMs = 0;
            Volatile.Write(ref hdr->WriteSeq, 0);

            _logger.Log($"Shared frame memory initialized at {ShmFilePath}", force: true);
        }
        catch (Exception ex)
        {
            _logger.Log($"Shared frame memory init failed: {ex.Message}", force: true);
            Dispose();
        }
    }

    public void WriteNv12Frame(ReadOnlySpan<byte> nv12, int width, int height, long timestampUs)
    {
        if (_basePtr == null || _disposed) return;
        int expectedBytes = width * height * 3 / 2;
        if (expectedBytes <= 0 || expectedBytes > SlotSize || nv12.Length < expectedBytes) return;

        var hdr = (SharedHeader*)_basePtr;
        int slot = _nextSlot;
        _nextSlot = (_nextSlot + 1) % SlotCount;

        byte* slotPtr = _basePtr + HeaderSize + (long)slot * SlotSize;
        nv12[..expectedBytes].CopyTo(new Span<byte>(slotPtr, expectedBytes));

        hdr->FrameWidth = width;
        hdr->FrameHeight = height;
        hdr->FrameTimestampUs = timestampUs;
        Volatile.Write(ref hdr->LastProducerTickMs, Environment.TickCount64);
        Volatile.Write(ref hdr->LatestSlot, slot);
        Interlocked.Increment(ref hdr->WriteSeq);
    }

    public void ClearActiveFrame()
    {
        if (_basePtr == null || _disposed) return;
        var hdr = (SharedHeader*)_basePtr;
        Volatile.Write(ref hdr->LastProducerTickMs, 0);
    }

    private static void ApplySharedPermissions(string dirPath)
    {
        try
        {
            var dirInfo = new DirectoryInfo(dirPath);
            var security = dirInfo.GetAccessControl();
            var rights = FileSystemRights.Modify | FileSystemRights.Synchronize;
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            AddRuleIfValid(security, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), rights, inheritance);
            AddRuleIfValid(security, new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), rights, inheritance);
            AddRuleIfValid(security, new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.ReadAndExecute, inheritance);
            dirInfo.SetAccessControl(security);
        }
        catch
        {
        }
    }

    private static void ApplyFilePermissions(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var security = fileInfo.GetAccessControl();
            var rw = FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize;

            AddFileRuleIfValid(security, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), rw);
            AddFileRuleIfValid(security, new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), rw);
            AddFileRuleIfValid(security, new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.Read);
            fileInfo.SetAccessControl(security);
        }
        catch
        {
        }
    }

    private static void AddRuleIfValid(
        DirectorySecurity sec,
        IdentityReference sid,
        FileSystemRights rights,
        InheritanceFlags inheritance)
    {
        sec.AddAccessRule(new FileSystemAccessRule(
            sid, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
    }

    private static void AddFileRuleIfValid(
        FileSecurity sec,
        IdentityReference sid,
        FileSystemRights rights)
    {
        sec.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_basePtr != null)
        {
            try
            {
                var hdr = (SharedHeader*)_basePtr;
                Volatile.Write(ref hdr->LastProducerTickMs, 0);
            }
            catch { }
            _accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            _basePtr = null;
        }
        _accessor?.Dispose();
        _mmf?.Dispose();
        _fileStream?.Dispose();
    }
}

