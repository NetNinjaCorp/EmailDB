using System.CommandLine;
using System.Diagnostics;
using EmailDB.Console;

var rootCommand = new RootCommand("EmailDB Console - Test various storage schemes");

// Subcommands
var testCommand = new Command("test", "Run storage tests");
var demoCommand = new Command("demo", "Run full EmailDB demo with ZoneTree indexing");
var protobufDemoCommand = new Command("demo-protobuf", "Run EmailDB demo with Protobuf serialization");
var compareCommand = new Command("compare", "Compare JSON vs Protobuf serialization");

// Options for test command
var emailCountOption = new Option<int>(
    name: "--emails",
    description: "Number of emails to generate",
    getDefaultValue: () => 1000);

var blockSizeOption = new Option<int>(
    name: "--block-size",
    description: "Block size in KB",
    getDefaultValue: () => 512);

var seedOption = new Option<int>(
    name: "--seed",
    description: "Random seed for deterministic generation",
    getDefaultValue: () => 42);

var allowAddOption = new Option<bool>(
    name: "--allow-add",
    description: "Allow adding new emails",
    getDefaultValue: () => true);

var allowDeleteOption = new Option<bool>(
    name: "--allow-delete", 
    description: "Allow deleting emails",
    getDefaultValue: () => false);

var allowEditOption = new Option<bool>(
    name: "--allow-edit",
    description: "Allow editing emails", 
    getDefaultValue: () => false);

var stepSizeOption = new Option<int>(
    name: "--step-size",
    description: "Report size every N operations",
    getDefaultValue: () => 100);

var performanceModeOption = new Option<bool>(
    name: "--performance",
    description: "Run in performance test mode",
    getDefaultValue: () => false);

var storageTypeOption = new Option<StorageType>(
    name: "--storage",
    description: "Storage type to test",
    getDefaultValue: () => StorageType.Hybrid);

var enableHashChainOption = new Option<bool>(
    name: "--hash-chain",
    description: "Enable hash chain for integrity",
    getDefaultValue: () => false);

var outputFileOption = new Option<string?>(
    name: "--output",
    description: "Output file for results (optional)");

// Add all options to test command
testCommand.AddOption(emailCountOption);
testCommand.AddOption(blockSizeOption);
testCommand.AddOption(seedOption);
testCommand.AddOption(allowAddOption);
testCommand.AddOption(allowDeleteOption);
testCommand.AddOption(allowEditOption);
testCommand.AddOption(stepSizeOption);
testCommand.AddOption(performanceModeOption);
testCommand.AddOption(storageTypeOption);
testCommand.AddOption(enableHashChainOption);
testCommand.AddOption(outputFileOption);

// Set handler for test command
testCommand.SetHandler(async (context) =>
{
    var config = new TestConfiguration
    {
        EmailCount = context.ParseResult.GetValueForOption(emailCountOption),
        BlockSizeKB = context.ParseResult.GetValueForOption(blockSizeOption),
        Seed = context.ParseResult.GetValueForOption(seedOption),
        AllowAdd = context.ParseResult.GetValueForOption(allowAddOption),
        AllowDelete = context.ParseResult.GetValueForOption(allowDeleteOption),
        AllowEdit = context.ParseResult.GetValueForOption(allowEditOption),
        StepSize = context.ParseResult.GetValueForOption(stepSizeOption),
        PerformanceMode = context.ParseResult.GetValueForOption(performanceModeOption),
        StorageType = context.ParseResult.GetValueForOption(storageTypeOption),
        EnableHashChain = context.ParseResult.GetValueForOption(enableHashChainOption),
        OutputFile = context.ParseResult.GetValueForOption(outputFileOption)
    };

    var tester = new EmailDBTester(config);
    await tester.RunAsync();
});

// Options for demo command
var dbPathOption = new Option<string>(
    name: "--path",
    description: "Database path",
    getDefaultValue: () => Path.Combine(Path.GetTempPath(), $"emaildb_demo_{Guid.NewGuid():N}"));

demoCommand.AddOption(dbPathOption);

// Set handler for demo command
demoCommand.SetHandler(async (context) =>
{
    var dbPath = context.ParseResult.GetValueForOption(dbPathOption);
    var demo = new EmailDBSimpleDemo(dbPath);
    await demo.RunDemoAsync();
});

// Options for protobuf demo command
var protobufDbPathOption = new Option<string>(
    name: "--path",
    description: "Database path",
    getDefaultValue: () => Path.Combine(Path.GetTempPath(), $"emaildb_protobuf_{Guid.NewGuid():N}"));

protobufDemoCommand.AddOption(protobufDbPathOption);

// Set handler for protobuf demo command
protobufDemoCommand.SetHandler(async (context) =>
{
    var dbPath = context.ParseResult.GetValueForOption(protobufDbPathOption);
    var demo = new EmailDBProtobufDemo(dbPath);
    await demo.RunDemoAsync();
});

// Create persistence test command
var persistenceTestCommand = new Command("test-persistence", "Run persistence tests with seeded data");

// Options for persistence test
var persistenceSeedOption = new Option<int>(
    name: "--seed",
    description: "Random seed for test data",
    getDefaultValue: () => 42);

var persistenceCountOption = new Option<int>(
    name: "--count",
    description: "Number of emails to test",
    getDefaultValue: () => 100);

var persistenceCyclesOption = new Option<int>(
    name: "--cycles",
    description: "Number of open/close cycles",
    getDefaultValue: () => 3);

persistenceTestCommand.AddOption(persistenceSeedOption);
persistenceTestCommand.AddOption(persistenceCountOption);
persistenceTestCommand.AddOption(persistenceCyclesOption);

// Set handler for persistence test command
persistenceTestCommand.SetHandler(async (context) =>
{
    var seed = context.ParseResult.GetValueForOption(persistenceSeedOption);
    var count = context.ParseResult.GetValueForOption(persistenceCountOption);
    var cycles = context.ParseResult.GetValueForOption(persistenceCyclesOption);
    
    var dbPath = Path.Combine(Path.GetTempPath(), $"emaildb_persistence_test_{seed}");
    
    // Clean up any existing test database
    if (Directory.Exists(dbPath))
    {
        Directory.Delete(dbPath, true);
    }
    
    await PersistenceTestRunner.RunAsync(dbPath, seed, count, cycles);
});

// Create working demo command
var workingDemoCommand = new Command("demo-working", "Run working persistence demo");
var workingDbPathOption = new Option<string>(
    name: "--path",
    description: "Database path",
    getDefaultValue: () => Path.Combine(Path.GetTempPath(), $"emaildb_working_{Guid.NewGuid():N}"));

workingDemoCommand.AddOption(workingDbPathOption);

// Set handler for working demo command  
workingDemoCommand.SetHandler(async (context) =>
{
    var dbPath = context.ParseResult.GetValueForOption(workingDbPathOption);
    var demo = new EmailDBWorkingPersistenceDemo(dbPath);
    await demo.RunDemoAsync();
});

// Create ZoneTree test command
var zoneTreeTestCommand = new Command("test-zonetree", "Run ZoneTree persistence test");
zoneTreeTestCommand.SetHandler(async () =>
{
    await ZoneTreePersistenceTest.RunAsync();
});

// Create metadata test command
var metadataTestCommand = new Command("test-metadata", "Run metadata store test");
metadataTestCommand.SetHandler(async () =>
{
    await MetadataStoreTest.RunAsync();
});

// Create fixed persistence test command
// var fixedTestCommand = new Command("test-fixed", "Run fixed EmailDatabase persistence test");
// fixedTestCommand.SetHandler(async () =>
// {
//     await TestFixedPersistence.RunAsync();
// });

// Add commands to root
rootCommand.AddCommand(testCommand);
rootCommand.AddCommand(demoCommand);
rootCommand.AddCommand(protobufDemoCommand);
rootCommand.AddCommand(persistenceTestCommand);
rootCommand.AddCommand(workingDemoCommand);
rootCommand.AddCommand(zoneTreeTestCommand);
rootCommand.AddCommand(metadataTestCommand);
// rootCommand.AddCommand(fixedTestCommand);

// Execute
return await rootCommand.InvokeAsync(args);