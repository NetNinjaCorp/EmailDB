using System.Diagnostics;
using System.Text;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models.BlockTypes;

class Program
{
    const string filePath = "test_email_store.dat";
    const string compactedFilePath = "test_email_store_compacted.dat";

    static void Main()
    {
        // TODO: Tests need to be updated to use the new RawBlockManager API
        // StorageManager and BlockManager have been removed during refactoring.
        // The following test methods have been disabled until the new API is integrated.
        Console.WriteLine("EmailDB.Testing: Test suite pending migration to new RawBlockManager API.");
        Console.WriteLine("See StorageManager -> RawBlockManager + CacheManager + MetadataManager refactor.");
    }

    static void CleanupTestFiles()
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
        if (File.Exists(compactedFilePath))
            File.Delete(compactedFilePath);
    }
}
