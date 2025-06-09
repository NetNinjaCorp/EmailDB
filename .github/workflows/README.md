# GitHub Actions Workflows

This directory contains GitHub Actions workflows for the EmailDB project.

## Workflows

### 1. Tests (`tests.yml`)
Main test workflow that runs on every push and pull request.
- Runs on multiple OS (Ubuntu, Windows, macOS)
- Tests with multiple .NET versions (8.0, 9.0)
- Generates test reports and code coverage
- Runs all phase tests separately
- Includes integration and performance tests

**Status Badge:**
```markdown
[![Tests](https://github.com/YOUR_USERNAME/EmailDB/actions/workflows/tests.yml/badge.svg)](https://github.com/YOUR_USERNAME/EmailDB/actions/workflows/tests.yml)
```

### 2. PR Check (`pr-check.yml`)
Quick validation for pull requests.
- Runs only on Ubuntu with .NET 9.0
- Executes phase tests with minimal output
- Provides quick feedback on PR status
- Uses caching for faster builds

**Status Badge:**
```markdown
[![PR Check](https://github.com/YOUR_USERNAME/EmailDB/actions/workflows/pr-check.yml/badge.svg)](https://github.com/YOUR_USERNAME/EmailDB/actions/workflows/pr-check.yml)
```

### 3. Nightly Tests (`nightly-tests.yml`)
Comprehensive testing that runs nightly.
- Stress and endurance tests
- Large dataset tests
- Memory usage tests
- Corruption recovery tests
- Real world scenario tests
- Full test suite on multiple platforms

**Status Badge:**
```markdown
[![Nightly Tests](https://github.com/YOUR_USERNAME/EmailDB/actions/workflows/nightly-tests.yml/badge.svg)](https://github.com/YOUR_USERNAME/EmailDB/actions/workflows/nightly-tests.yml)
```

## Test Categories

The workflows filter tests using these patterns:

- **Phase Tests**: `Phase1ComponentTests`, `Phase2ComponentTests`, etc.
- **Integration Tests**: `E2E`, `Integration`
- **Performance Tests**: `Performance`, `Benchmark`
- **Stress Tests**: `Stress`, `Endurance`, `LargeDataset`
- **Recovery Tests**: `Corruption`, `Recovery`
- **Memory Tests**: `MemoryUsage`

## Local Testing

To run the same tests locally:

```bash
# All tests
dotnet test

# Phase tests only
dotnet test --filter "FullyQualifiedName~Phase"

# Specific phase
dotnet test --filter "FullyQualifiedName~Phase1ComponentTests"

# Integration tests
dotnet test --filter "FullyQualifiedName~E2E|FullyQualifiedName~Integration"

# With coverage
dotnet test --collect:"XPlat Code Coverage"
```

## Workflow Triggers

- **tests.yml**: Push to main/develop, PRs, manual trigger
- **pr-check.yml**: Pull requests only
- **nightly-tests.yml**: Daily at 2 AM UTC, manual trigger

## Required Secrets

No secrets are currently required for these workflows.

## Customization

Replace `YOUR_USERNAME` in the badge URLs with your actual GitHub username or organization name.