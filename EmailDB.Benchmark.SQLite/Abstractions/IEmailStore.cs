namespace EmailDB.Benchmark.SQLite.Abstractions;

public interface IEmailStore : IDisposable
{
    Task InitializeAsync();
    Task InsertEmailAsync(BenchmarkEmail email);
    Task InsertEmailsBulkAsync(IReadOnlyList<BenchmarkEmail> emails);
    Task<BenchmarkEmail?> GetEmailByIdAsync(string id);
    Task<List<string>> GetEmailIdsInFolderAsync(string folderPath);
    Task<List<string>> SearchAsync(string query);
    Task DeleteEmailAsync(string id);
    Task MoveEmailAsync(string id, string sourceFolder, string targetFolder);
    long GetDatabaseSizeBytes();
}
