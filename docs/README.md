# EmailDB Documentation

## Overview
This directory contains all documentation for the EmailDB project, organized by topic.

## Documentation Structure

### 📁 [format/](format/)
Technical documentation about the EmailDB file format and architecture:
- [Hybrid Storage Architecture](format/Hybrid_Storage_Architecture.md) - Core architecture overview
- [Append-Only Block Design](format/AppendOnly_Block_Design.md) - Block storage implementation
- [Hash Chain Archival System](format/HashChain_Archival_System.md) - Cryptographic integrity
- [Block Storage](format/Block_Storage.md) - Original block format specification
- [Email Storage](format/Email_Storage.md) - Email data organization
- [Overall Architecture](format/Overall_Architecture.md) - System design
- [Payload Encoding](format/Payload_Encoding.md) - Data encoding schemes

### 📁 [guides/](guides/)
User guides and tutorials:
- [Migration Guide](MIGRATION_GUIDE.md) - Upgrading from traditional to hybrid format

## Quick Navigation

### For Developers
1. Start with [Hybrid Storage Architecture](format/Hybrid_Storage_Architecture.md)
2. Understand [Append-Only Block Design](format/AppendOnly_Block_Design.md)
3. Learn about [Hash Chain integration](format/HashChain_Archival_System.md)

### For Users
1. See the main [README](../README.md) for quick start
2. Follow the [Migration Guide](MIGRATION_GUIDE.md) if upgrading

### For Contributors
1. Review the architecture documentation
2. Understand the test structure in [Solution Structure](../SOLUTION_STRUCTURE.md)
3. Check the [Test Consolidation Plan](../TEST_CONSOLIDATION_PLAN.md)

## Key Concepts

### Storage Efficiency
The new hybrid architecture achieves 99.6% storage efficiency by:
- Packing multiple emails into blocks
- Using append-only writes
- Separating indexes from data

### Performance
- Write speed: 50+ MB/s
- Search speed: 50,000+ queries/second
- Read latency: < 0.1ms

### Data Integrity
- Cryptographic hash chains
- Immutable blocks
- Existence proofs

## Architecture Diagram

```
┌─────────────────────┐     ┌──────────────────┐
│  HybridEmailStore   │────▶│  ZoneTree Indexes │
│  (High-Level API)   │     │  - MessageId      │
└──────────┬──────────┘     │  - Folder         │
           │                │  - Full-Text      │
           ▼                └──────────────────┘
┌─────────────────────┐
│ AppendOnlyBlockStore│     ┌──────────────────┐
│ (Data Storage)      │────▶│  Hash Chain      │
└─────────────────────┘     │  (Integrity)     │
                            └──────────────────┘
```