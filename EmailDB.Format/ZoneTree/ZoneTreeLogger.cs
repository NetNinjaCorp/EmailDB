using System;
using System.IO;
using System.Collections.Concurrent;
using System.Text;

namespace EmailDB.Format.ZoneTree;

/// <summary>
/// Global logger for ZoneTree operations to help diagnose persistence issues
/// </summary>
public static class ZoneTreeLogger
{
    private static StreamWriter? _logWriter;
    private static readonly object _lock = new object();
    private static readonly ConcurrentQueue<string> _pendingLogs = new();
    private static bool _isEnabled = false;

    public static void Initialize(string logPath)
    {
        lock (_lock)
        {
            if (_logWriter != null)
            {
                _logWriter.Dispose();
            }

            _logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
            _isEnabled = true;
            
            Log("=== ZoneTree Operations Logger Initialized ===");
            Log($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            Log($"Log Path: {logPath}");
            Log("=============================================\n");
        }
    }

    public static void Log(string message)
    {
        if (!_isEnabled) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logEntry = $"[{timestamp}] {message}";

        lock (_lock)
        {
            _logWriter?.WriteLine(logEntry);
        }

        // Also write to console for immediate visibility
        Console.WriteLine($"📋 {logEntry}");
    }

    public static void LogFileOperation(string operation, string path, string details = "")
    {
        var hash = path.GetHashCode();
        Log($"FILE_OP: {operation} | Path: {path} | Hash: {hash} | {details}");
    }

    public static void LogBlockOperation(string operation, int blockId, int size, string details = "")
    {
        Log($"BLOCK_OP: {operation} | BlockId: {blockId} | Size: {size} | {details}");
    }

    public static void LogZoneTreeOperation(string operation, string treeName, string details = "")
    {
        Log($"ZONETREE_OP: {operation} | Tree: {treeName} | {details}");
    }

    public static void LogWALOperation(string operation, long segmentId, string category, string details = "")
    {
        Log($"WAL_OP: {operation} | SegmentId: {segmentId} | Category: {category} | {details}");
    }

    public static void LogSegmentOperation(string operation, long segmentId, string category, int dataLength, string details = "")
    {
        Log($"SEGMENT_OP: {operation} | SegmentId: {segmentId} | Category: {category} | DataLength: {dataLength} | {details}");
    }

    public static void LogData(string label, byte[] data, int maxLength = 100)
    {
        if (!_isEnabled || data == null) return;

        var preview = data.Length <= maxLength 
            ? BitConverter.ToString(data).Replace("-", " ")
            : BitConverter.ToString(data, 0, maxLength).Replace("-", " ") + "...";
        
        Log($"DATA: {label} | Length: {data.Length} | Preview: {preview}");
        
        // Try to decode as UTF-8 if it looks like text
        if (data.Length > 0 && (data[0] == '{' || data[0] == '['))
        {
            try
            {
                var text = Encoding.UTF8.GetString(data);
                if (text.Length > 200)
                    text = text.Substring(0, 200) + "...";
                Log($"DATA_TEXT: {label} | {text}");
            }
            catch { }
        }
    }

    public static void Close()
    {
        lock (_lock)
        {
            if (_logWriter != null)
            {
                Log("\n=== ZoneTree Operations Logger Closed ===");
                Log($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                _logWriter.Dispose();
                _logWriter = null;
                _isEnabled = false;
            }
        }
    }
}