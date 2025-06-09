using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using EmailDB.Format.Models;
using EmailDB.Format.FileManagement;

namespace EmailDB.Format.Maintenance;

public class TransactionLog : IDisposable
{
    private readonly string _logPath;
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;
    
    public TransactionLog(string databasePath)
    {
        _logPath = $"{databasePath}.txlog";
        _writer = new StreamWriter(_logPath, append: true)
        {
            AutoFlush = true
        };
        
        WriteEntry("STARTUP", "Transaction log started");
    }
    
    public void LogOperation(string operation, string details, Dictionary<string, object> metadata = null)
    {
        lock (_lock)
        {
            var entry = new TransactionEntry
            {
                Timestamp = DateTime.UtcNow,
                Operation = operation,
                Details = details,
                Metadata = metadata ?? new Dictionary<string, object>()
            };
            
            var json = JsonSerializer.Serialize(entry);
            _writer.WriteLine(json);
        }
    }
    
    public void LogBlockDeletion(long blockId, BlockType type, string reason)
    {
        LogOperation("DELETE_BLOCK", $"Deleted block {blockId} of type {type}", 
            new Dictionary<string, object>
            {
                { "blockId", blockId },
                { "blockType", type.ToString() },
                { "reason", reason }
            });
    }
    
    public void LogCompaction(CompactionResult result)
    {
        LogOperation("COMPACTION", "Database compacted",
            new Dictionary<string, object>
            {
                { "originalSize", result.OriginalSize },
                { "finalSize", result.FinalSize },
                { "spaceReclaimed", result.SpaceReclaimed },
                { "blocksDeleted", result.BlocksDeleted },
                { "duration", (result.EndTime - result.StartTime).TotalSeconds }
            });
    }
    
    public void LogIndexRebuild(string reason, bool success, string error = null)
    {
        LogOperation("INDEX_REBUILD", reason,
            new Dictionary<string, object>
            {
                { "success", success },
                { "error", error }
            });
    }
    
    public List<TransactionEntry> GetRecentEntries(int count = 100)
    {
        lock (_lock)
        {
            _writer.Flush();
            
            var entries = new List<TransactionEntry>();
            var lines = File.ReadAllLines(_logPath);
            
            for (int i = Math.Max(0, lines.Length - count); i < lines.Length; i++)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<TransactionEntry>(lines[i]);
                    if (entry != null)
                        entries.Add(entry);
                }
                catch
                {
                    // Skip malformed entries
                }
            }
            
            return entries;
        }
    }
    
    private void WriteEntry(string operation, string details)
    {
        LogOperation(operation, details);
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_lock)
            {
                WriteEntry("SHUTDOWN", "Transaction log closed");
                _writer?.Dispose();
            }
            _disposed = true;
        }
    }
}

public class TransactionEntry
{
    public DateTime Timestamp { get; set; }
    public string Operation { get; set; }
    public string Details { get; set; }
    public Dictionary<string, object> Metadata { get; set; }
}