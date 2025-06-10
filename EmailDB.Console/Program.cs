using System.CommandLine;
using System.Diagnostics;
using EmailDB.Console;

var rootCommand = new RootCommand("EmailDB Console - Test various storage schemes");

// Subcommands
var testCommand = new Command("test", "Run storage tests");
var demoCommand = new Command("demo", "Run full EmailDB demo with ZoneTree indexing");

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

// Add commands to root
rootCommand.AddCommand(testCommand);
rootCommand.AddCommand(demoCommand);

// Execute
return await rootCommand.InvokeAsync(args);