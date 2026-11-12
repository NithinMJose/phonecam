using System.Collections.Concurrent;
using System.IO;

namespace PhoneCam.Logging;

public sealed class DebugLogger
{
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly object _fileLock = new();
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), "phonecam-debug.log");
    private const int MaxEntries = 200;

    public bool Enabled { get; set; }

    public event Action? Updated;

    public void Log(string message, bool force = false)
    {
        if (!Enabled && !force) return;
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        _entries.Enqueue(line);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _)) { }
        lock (_fileLock)
        {
            try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
        }
        Updated?.Invoke();
    }

    public string GetAllText() => string.Join(Environment.NewLine, _entries);

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
        Updated?.Invoke();
    }
}
