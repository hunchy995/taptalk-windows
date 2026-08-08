using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Taptalk.Core;

/// <summary>
/// Thread-safe debug logger for the whole audio→transcribe→inject pipeline.
/// - Ring buffer in memory (latest 1000 lines) for the UI
/// - Async mirror to %LOCALAPPDATA%\Taptalk\Logs\debug.log (rotates at 5MB → debug.old.log)
/// - Stage tags: [REC] [AUDIO] [VAD] [FEAT] [INF] [DEC] [POST] [INJ] [ERR] [SYS]
/// - Verbose filtering: when IsVerboseEnabled=false, UI hides [AUDIO]/[FEAT]/[INF]/[DEC];
///   the FILE always records everything.
/// NEVER blocks the NAudio callback thread: Log() only enqueues + signals; a single
/// background writer drains the queue. Safe to call from any thread.
/// </summary>
public sealed class DebugRecorder : IDisposable
{
    private static readonly Lazy<DebugRecorder> _instance = new(() => new DebugRecorder());
    public static DebugRecorder Instance => _instance.Value;

    private const int MaxMemoryLines = 1000;
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    private readonly List<string> _memoryLog = new();
    private readonly object _lock = new();
    private readonly ConcurrentQueue<string> _fileQueue = new();
    private readonly AutoResetEvent _enqueueEvent = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _fileWriterTask;

    private readonly string _logDirectory;
    private readonly string _logFilePath;
    private readonly string _backupFilePath;

    /// <summary>Raised on every line (any thread). UI subscribes and marshals to the dispatcher.</summary>
    public event Action<string>? OnLogAdded;

    /// <summary>When false, the UI filters verbose tags but the file still gets everything.</summary>
    public bool IsVerboseEnabled { get; set; } = true;

    public string LogFilePath => _logFilePath;

    private DebugRecorder()
    {
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Taptalk", "Logs");
        _logFilePath = Path.Combine(_logDirectory, "debug.log");
        _backupFilePath = Path.Combine(_logDirectory, "debug.old.log");

        try
        {
            if (!Directory.Exists(_logDirectory))
                Directory.CreateDirectory(_logDirectory);
        }
        catch { }

        _fileWriterTask = Task.Run(ProcessFileQueueAsync);

        // 🔴 Do NOT call static Log() here — it accesses Instance, which re-enters the
        // Lazy<T> while the constructor is still running → InvalidOperationException
        // ("ValueFactory attempted to access the Value property of this instance").
        // This crashed the app at startup when DebugChk.IsChecked fired DebugChk_Changed.
        AppendLine(Format("SYS", $"Debugger initialized. Log file: {_logFilePath}"));
    }

    private static string Format(string tag, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var threadId = Environment.CurrentManagedThreadId;
        return $"[{timestamp}][T{threadId:00}][{tag}] {message}";
    }

    /// <summary>Core append — used by static Log() and by the constructor (no Instance access).</summary>
    private void AppendLine(string line)
    {
        lock (_lock)
        {
            _memoryLog.Add(line);
            if (_memoryLog.Count > MaxMemoryLines)
                _memoryLog.RemoveAt(0);
        }

        _fileQueue.Enqueue(line);
        _enqueueEvent.Set();

        try { OnLogAdded?.Invoke(line); }
        catch { }
    }

    /// <summary>Log a message with a stage tag. Thread-safe; never blocks the caller.</summary>
    public static void Log(string tag, string message)
    {
        var instance = Instance;
        instance.AppendLine(Format(tag, message));
    }

    /// <summary>Log an exception with full stack trace at a stage.</summary>
    public static void Error(string tag, string stage, Exception ex)
    {
        Log($"ERR-{tag}", $"CRITICAL ERROR in {stage}: {ex.GetType().Name}: {ex.Message}");
        Log($"ERR-{tag}", $"Stack: {ex.StackTrace}");
        if (ex.InnerException != null)
            Log($"ERR-{tag}", $"Inner: {ex.InnerException.Message}");
    }

    /// <summary>All buffered lines for the UI (unfiltered).</summary>
    public static string DumpMemoryLogs()
    {
        var instance = Instance;
        lock (instance._lock)
            return string.Join(Environment.NewLine, instance._memoryLog);
    }

    /// <summary>Latest buffered lines that match the current verbosity filter (for UI refresh).</summary>
    public static string DumpFiltered()
    {
        var instance = Instance;
        lock (instance._lock)
        {
            var sb = new StringBuilder();
            foreach (var line in instance._memoryLog)
            {
                if (instance.IsVerboseEnabled || !IsVerboseTag(line))
                    sb.AppendLine(line);
            }
            return sb.ToString();
        }
    }

    private static bool IsVerboseTag(string line)
    {
        return line.Contains("[AUDIO]") || line.Contains("[FEAT]") ||
               line.Contains("[INF]") || line.Contains("[DEC]");
    }

    private void ProcessFileQueueAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            _enqueueEvent.WaitOne(500);
            while (_fileQueue.TryDequeue(out var line))
            {
                WriteToFile(line);
            }
        }
    }

    private void WriteToFile(string line)
    {
        try
        {
            if (File.Exists(_logFilePath))
            {
                var fi = new FileInfo(_logFilePath);
                if (fi.Length > MaxFileSizeBytes)
                    RotateLogs();
            }

            using var sw = new StreamWriter(_logFilePath, append: true, encoding: Encoding.UTF8);
            sw.WriteLine(line);
        }
        catch { /* logging must never crash the app */ }
    }

    private void RotateLogs()
    {
        try
        {
            if (File.Exists(_backupFilePath))
                File.Delete(_backupFilePath);
            File.Move(_logFilePath, _backupFilePath);
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _enqueueEvent.Set();
        try { _fileWriterTask.Wait(500); } catch { }
        _enqueueEvent.Dispose();
        _cts.Dispose();
    }
}
