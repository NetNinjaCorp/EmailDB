# EmailDB Console Test Application

## Overview

The EmailDB Console application allows you to test various storage schemes and analyze their performance and storage efficiency characteristics.

## Features

- **Deterministic Testing**: Use seed values for reproducible results
- **Multiple Storage Types**: Test Traditional, Hybrid, and AppendOnly storage
- **Size Analysis Mode**: Track storage growth at each step
- **Performance Mode**: Measure throughput and latency
- **Configurable Operations**: Enable/disable add, delete, and edit operations
- **Block Type Analysis**: See breakdown by data, index, and metadata

## Usage

```bash
# Basic usage - size analysis with 1000 emails
dotnet run --project EmailDB.Console -- --emails 1000

# Performance test mode
dotnet run --project EmailDB.Console -- --emails 10000 --performance

# Test with specific configuration
dotnet run --project EmailDB.Console -- \
  --emails 5000 \
  --block-size 1024 \
  --seed 123 \
  --allow-delete \
  --allow-edit \
  --step-size 500 \
  --storage Hybrid \
  --hash-chain \
  --output results.txt
```

## Options

| Option | Description | Default |
|--------|-------------|---------|
| `--emails` | Number of emails to generate | 1000 |
| `--block-size` | Block size in KB | 512 |
| `--seed` | Random seed for deterministic generation | 42 |
| `--allow-add` | Allow adding new emails | true |
| `--allow-delete` | Allow deleting emails | false |
| `--allow-edit` | Allow editing emails | false |
| `--step-size` | Report size every N operations | 100 |
| `--performance` | Run in performance test mode | false |
| `--storage` | Storage type (Traditional/Hybrid/AppendOnly) | Hybrid |
| `--hash-chain` | Enable hash chain for integrity | false |
| `--output` | Output file for results | (none) |

## Examples

### 1. Compare Storage Types

```bash
# Traditional storage
dotnet run --project EmailDB.Console -- --emails 1000 --storage Traditional

# Hybrid storage
dotnet run --project EmailDB.Console -- --emails 1000 --storage Hybrid

# Append-only storage
dotnet run --project EmailDB.Console -- --emails 1000 --storage AppendOnly
```

### 2. Test with Operations

```bash
# Test with all operations
dotnet run --project EmailDB.Console -- \
  --emails 1000 \
  --allow-add \
  --allow-delete \
  --allow-edit \
  --step-size 100
```

### 3. Performance Benchmarking

```bash
# Benchmark write performance
dotnet run --project EmailDB.Console -- \
  --emails 10000 \
  --performance \
  --storage Hybrid

# Benchmark with large blocks
dotnet run --project EmailDB.Console -- \
  --emails 10000 \
  --performance \
  --block-size 2048
```

### 4. Archival Testing

```bash
# Test with hash chain enabled
dotnet run --project EmailDB.Console -- \
  --emails 5000 \
  --storage Hybrid \
  --hash-chain \
  --output archive_test.txt
```

## Output Format

### Size Analysis Mode

```
EmailDB Storage Test
===================
Configuration:
  Storage Type: Hybrid
  Email Count: 1,000
  Block Size: 512 KB
  ...

Size Analysis Mode
------------------
Initial:
  Total: 4.00 KB

After 100 adds:
  Total: 523.45 KB
  Data: 501.23 KB (95.8%)
  Index: 20.11 KB (3.8%)
  Metadata: 2.11 KB (0.4%)

...

Final Analysis
--------------
Storage Efficiency: 99.6%
Overhead: 0.4%
```

### Performance Mode

```
Performance Test Mode
--------------------

Write Performance:
  Total Time: 2.34s
  Emails/sec: 4,273
  Throughput: 51.2 MB/s

Read Performance:
  Total Time: 0.89s
  Reads/sec: 1,123
  Avg Latency: 0.89ms

Search Performance:
  Total Time: 0.02s
  Searches/sec: 5,000
```

## Understanding Results

- **Storage Efficiency**: Percentage of actual email data vs total storage used
- **Data Size**: Actual email content storage
- **Index Size**: Space used by search indexes (Hybrid only)
- **Metadata Size**: Space used by structural metadata
- **Block Type Distribution**: Count of each block type (Traditional only)

## Tips

1. Use consistent seed values for comparing different configurations
2. Run performance tests multiple times and average results
3. Test with realistic email size distributions
4. Consider testing with your actual expected workload