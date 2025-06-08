namespace EmailDB.Console;

public class TestConfiguration
{
    public int EmailCount { get; set; }
    public int BlockSizeKB { get; set; }
    public int Seed { get; set; }
    public bool AllowAdd { get; set; }
    public bool AllowDelete { get; set; }
    public bool AllowEdit { get; set; }
    public int StepSize { get; set; }
    public bool PerformanceMode { get; set; }
    public StorageType StorageType { get; set; }
    public bool EnableHashChain { get; set; }
    public string? OutputFile { get; set; }
}

public enum StorageType
{
    Traditional,
    Hybrid,
    AppendOnly
}