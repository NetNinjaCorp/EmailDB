# EmailDB Documentation

## Overview

This folder contains the authoritative documentation for the EmailDB project. All specifications, architecture documents, and development guides here represent the current state of the project.

## Document Structure

### Core Documentation

- **[EmailDB_Architecture_Overview.md](./EmailDB_Architecture_Overview.md)** - Complete system architecture and design principles
- **[Block_Format_Detailed_Specification.md](./Block_Format_Detailed_Specification.md)** - Byte-level specification of the EmailDB block format
- **[Development_TODO.md](./Development_TODO.md)** - Comprehensive development roadmap and testing strategy

### Architecture Details

The `architecture/` folder contains detailed component documentation:

- **[Block_Storage.md](./architecture/Block_Storage.md)** - RawBlockManager and BlockManager details
- **[Email_Storage.md](./architecture/Email_Storage.md)** - EmailManager and ZoneTree integration
- **[Overall_Architecture.md](./architecture/Overall_Architecture.md)** - High-level system overview
- **[Payload_Encoding.md](./architecture/Payload_Encoding.md)** - Serialization architecture

## Documentation Standards

1. **Accuracy**: All documentation must reflect the current implementation
2. **Completeness**: Include all necessary details for implementation
3. **Examples**: Provide concrete examples where applicable
4. **Versioning**: Mark version numbers on specifications
5. **Updates**: Keep documentation synchronized with code changes

## Quick Links

- [Block Format Spec v1.0](./Block_Format_Detailed_Specification.md#block-structure)
- [Development Roadmap](./Development_TODO.md#mission)
- [Testing Strategy](./Development_TODO.md#testing-infrastructure-priority-high)
- [Architecture Diagram](./EmailDB_Architecture_Overview.md#system-architecture)

## Contributing

When updating documentation:
1. Ensure consistency across all documents
2. Update version numbers where applicable
3. Mark deprecated content clearly
4. Add examples for complex concepts
5. Keep formatting consistent

## Version History

- **v1.0** (Current) - Initial specification with complete block format including PayloadEncoding field
- **v0.9** - Draft specification without PayloadEncoding field (deprecated)