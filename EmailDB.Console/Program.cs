using System.CommandLine;
using System.Diagnostics;
using EmailDB.Console;

var rootCommand = new RootCommand("EmailDB Console - Test various storage schemes");

// Options
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

// Add all options to root command
rootCommand.AddOption(emailCountOption);
rootCommand.AddOption(blockSizeOption);
rootCommand.AddOption(seedOption);
rootCommand.AddOption(allowAddOption);
rootCommand.AddOption(allowDeleteOption);
rootCommand.AddOption(allowEditOption);
rootCommand.AddOption(stepSizeOption);
rootCommand.AddOption(performanceModeOption);
rootCommand.AddOption(storageTypeOption);
rootCommand.AddOption(enableHashChainOption);
rootCommand.AddOption(outputFileOption);

// Set handler
rootCommand.SetHandler(async (context) =>
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

// Execute
return await rootCommand.InvokeAsync(args);