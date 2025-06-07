#!/usr/bin/env pwsh

# Run the tests and capture output
$output = dotnet test EmailDB.UnitTests/EmailDB.UnitTests.csproj `
    --filter "FullyQualifiedName~RealisticStorageAnalysisTest" `
    --logger "console;verbosity=normal" `
    --no-build `
    -- xunit.parallelExecution=false `
    -- NUnit.ConsoleOut=1

Write-Host $output