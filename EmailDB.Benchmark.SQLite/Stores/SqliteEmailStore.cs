using EmailDB.Benchmark.SQLite.Abstractions;
using Microsoft.Data.Sqlite;

namespace EmailDB.Benchmark.SQLite.Stores;

public class SqliteEmailStore : IEmailStore
{
    private readonly string _dbPath;
    private SqliteConnection _connection = null!;

    public SqliteEmailStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        await _connection.OpenAsync();

        // Performance PRAGMAs
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA cache_size=-64000;
                PRAGMA mmap_size=268435456;
                PRAGMA temp_store=MEMORY;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Create tables
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS emails (
                    id TEXT PRIMARY KEY,
                    folder_path TEXT NOT NULL,
                    subject TEXT,
                    body TEXT,
                    from_addr TEXT,
                    to_addrs TEXT,
                    cc_addrs TEXT,
                    bcc_addrs TEXT,
                    sent_date TEXT,
                    raw_content BLOB,
                    created_at TEXT DEFAULT (datetime('now'))
                );

                CREATE INDEX IF NOT EXISTS idx_emails_folder ON emails(folder_path);
                CREATE INDEX IF NOT EXISTS idx_emails_sent_date ON emails(sent_date);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Create FTS5 virtual table
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS emails_fts USING fts5(
                    subject, body, from_addr,
                    content=emails, content_rowid=rowid
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Create triggers for FTS sync
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TRIGGER IF NOT EXISTS emails_ai AFTER INSERT ON emails BEGIN
                    INSERT INTO emails_fts(rowid, subject, body, from_addr)
                    VALUES (new.rowid, new.subject, new.body, new.from_addr);
                END;

                CREATE TRIGGER IF NOT EXISTS emails_ad AFTER DELETE ON emails BEGIN
                    INSERT INTO emails_fts(emails_fts, rowid, subject, body, from_addr)
                    VALUES ('delete', old.rowid, old.subject, old.body, old.from_addr);
                END;

                CREATE TRIGGER IF NOT EXISTS emails_au AFTER UPDATE ON emails BEGIN
                    INSERT INTO emails_fts(emails_fts, rowid, subject, body, from_addr)
                    VALUES ('delete', old.rowid, old.subject, old.body, old.from_addr);
                    INSERT INTO emails_fts(rowid, subject, body, from_addr)
                    VALUES (new.rowid, new.subject, new.body, new.from_addr);
                END;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task InsertEmailAsync(BenchmarkEmail email)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO emails (id, folder_path, subject, body, from_addr, to_addrs, cc_addrs, bcc_addrs, sent_date, raw_content)
            VALUES ($id, $folder, $subject, $body, $from, $to, $cc, $bcc, $sent, $raw)
            """;
        BindEmailParams(cmd, email);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InsertEmailsBulkAsync(IReadOnlyList<BenchmarkEmail> emails)
    {
        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO emails (id, folder_path, subject, body, from_addr, to_addrs, cc_addrs, bcc_addrs, sent_date, raw_content)
            VALUES ($id, $folder, $subject, $body, $from, $to, $cc, $bcc, $sent, $raw)
            """;

        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pFolder = cmd.Parameters.Add("$folder", SqliteType.Text);
        var pSubject = cmd.Parameters.Add("$subject", SqliteType.Text);
        var pBody = cmd.Parameters.Add("$body", SqliteType.Text);
        var pFrom = cmd.Parameters.Add("$from", SqliteType.Text);
        var pTo = cmd.Parameters.Add("$to", SqliteType.Text);
        var pCc = cmd.Parameters.Add("$cc", SqliteType.Text);
        var pBcc = cmd.Parameters.Add("$bcc", SqliteType.Text);
        var pSent = cmd.Parameters.Add("$sent", SqliteType.Text);
        var pRaw = cmd.Parameters.Add("$raw", SqliteType.Blob);

        foreach (var email in emails)
        {
            pId.Value = email.Id;
            pFolder.Value = email.FolderPath;
            pSubject.Value = email.Subject;
            pBody.Value = email.Body;
            pFrom.Value = email.From;
            pTo.Value = string.Join(";", email.To);
            pCc.Value = string.Join(";", email.Cc);
            pBcc.Value = string.Join(";", email.Bcc);
            pSent.Value = email.SentDate.ToString("O");
            pRaw.Value = (object?)email.RawContent ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<BenchmarkEmail?> GetEmailByIdAsync(string id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, folder_path, subject, body, from_addr, to_addrs, cc_addrs, bcc_addrs, sent_date, raw_content FROM emails WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return ReadEmail(reader);
    }

    public async Task<List<string>> GetEmailIdsInFolderAsync(string folderPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM emails WHERE folder_path = $folder";
        cmd.Parameters.AddWithValue("$folder", folderPath);

        var ids = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<List<string>> SearchAsync(string query)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT emails.id FROM emails_fts
            JOIN emails ON emails.rowid = emails_fts.rowid
            WHERE emails_fts MATCH $query
            ORDER BY rank
            """;
        cmd.Parameters.AddWithValue("$query", query);

        var ids = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task DeleteEmailAsync(string id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM emails WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MoveEmailAsync(string id, string sourceFolder, string targetFolder)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE emails SET folder_path = $target WHERE id = $id AND folder_path = $source";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$source", sourceFolder);
        cmd.Parameters.AddWithValue("$target", targetFolder);
        await cmd.ExecuteNonQueryAsync();
    }

    public long GetDatabaseSizeBytes()
    {
        if (!File.Exists(_dbPath)) return 0;
        var size = new FileInfo(_dbPath).Length;
        // Include WAL and SHM files if they exist
        var walPath = _dbPath + "-wal";
        var shmPath = _dbPath + "-shm";
        if (File.Exists(walPath)) size += new FileInfo(walPath).Length;
        if (File.Exists(shmPath)) size += new FileInfo(shmPath).Length;
        return size;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }

    private static void BindEmailParams(SqliteCommand cmd, BenchmarkEmail email)
    {
        cmd.Parameters.AddWithValue("$id", email.Id);
        cmd.Parameters.AddWithValue("$folder", email.FolderPath);
        cmd.Parameters.AddWithValue("$subject", email.Subject);
        cmd.Parameters.AddWithValue("$body", email.Body);
        cmd.Parameters.AddWithValue("$from", email.From);
        cmd.Parameters.AddWithValue("$to", string.Join(";", email.To));
        cmd.Parameters.AddWithValue("$cc", string.Join(";", email.Cc));
        cmd.Parameters.AddWithValue("$bcc", string.Join(";", email.Bcc));
        cmd.Parameters.AddWithValue("$sent", email.SentDate.ToString("O"));
        cmd.Parameters.AddWithValue("$raw", (object?)email.RawContent ?? DBNull.Value);
    }

    private static BenchmarkEmail ReadEmail(SqliteDataReader reader)
    {
        return new BenchmarkEmail
        {
            Id = reader.GetString(0),
            FolderPath = reader.GetString(1),
            Subject = reader.IsDBNull(2) ? "" : reader.GetString(2),
            Body = reader.IsDBNull(3) ? "" : reader.GetString(3),
            From = reader.IsDBNull(4) ? "" : reader.GetString(4),
            To = reader.IsDBNull(5) ? new() : reader.GetString(5).Split(';', StringSplitOptions.RemoveEmptyEntries).ToList(),
            Cc = reader.IsDBNull(6) ? new() : reader.GetString(6).Split(';', StringSplitOptions.RemoveEmptyEntries).ToList(),
            Bcc = reader.IsDBNull(7) ? new() : reader.GetString(7).Split(';', StringSplitOptions.RemoveEmptyEntries).ToList(),
            SentDate = reader.IsDBNull(8) ? DateTime.MinValue : DateTime.Parse(reader.GetString(8)),
            RawContent = reader.IsDBNull(9) ? null : (byte[])reader[9]
        };
    }
}
