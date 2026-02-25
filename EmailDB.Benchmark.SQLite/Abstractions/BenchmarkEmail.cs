namespace EmailDB.Benchmark.SQLite.Abstractions;

public class BenchmarkEmail
{
    public string Id { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public List<string> To { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    public List<string> Bcc { get; set; } = new();
    public DateTime SentDate { get; set; }
    public byte[]? RawContent { get; set; }
}
