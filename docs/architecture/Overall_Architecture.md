# EmailDB High-Level Architecture

This document provides a high-level overview of the EmailDB system architecture. The system is designed with a layered approach, promoting separation of concerns, flexibility, and maintainability.

## Core Components

The main components of the system are:

1.  **Raw Block Manager (`RawBlockManager`):** Responsible for the low-level reading and writing of raw data blocks to the underlying storage medium (the EMDB file). It deals purely with byte streams and block locations.
2.  **Block Manager (`BlockManager`):** Sits on top of the `RawBlockManager`. It understands the structure of blocks (header, payload) and manages the encoding/decoding of block payloads. It uses a pluggable `IPayloadEncoding` interface to handle different serialization formats (e.g., Protobuf, JSON).
3.  **ZoneTree:** A specialized data structure (likely a persistent B+-tree or similar) responsible for organizing and indexing data efficiently within the EMDB file. It utilizes the `BlockManager` to store its nodes and data.
4.  **Email Manager (`EmailManager`):** Provides the high-level API for storing, retrieving, and managing email data. It interacts with `ZoneTree` to handle the persistence of email information, delegating the actual block storage details to the lower layers.

## Interaction Flow

-   The `EmailManager` receives requests to store or retrieve email data.
-   It translates these requests into operations on the `ZoneTree` (e.g., inserting key-value pairs where the value might be an email or parts of it).
-   `ZoneTree` manages its internal structure (nodes, leaves) and determines where data needs to be stored or read from.
-   When `ZoneTree` needs to persist or load its data, it interacts with the `BlockManager`.
-   The `BlockManager` takes the data (e.g., a ZoneTree node), serializes the payload using the configured `IPayloadEncoding` implementation, adds necessary block headers, and passes the raw byte block to the `RawBlockManager`.
-   The `RawBlockManager` writes the raw block to the physical EMDB file at a specific location and returns metadata (like the `BlockLocation`).
-   Reading data follows the reverse path.

## Key Design Principles

-   **Layering:** Clear separation between raw storage, block management, indexing, and application logic.
-   **Abstraction:** Higher layers depend on interfaces or abstractions of lower layers (e.g., `BlockManager` uses `RawBlockManager`, `ZoneTree` uses `BlockManager`).
-   **Flexibility:** The `IPayloadEncoding` interface allows swapping serialization formats without impacting core logic.

See the specific component documents for more details:

-   [Block Storage](./Block_Storage.md)
-   [Email Storage](./Email_Storage.md)
-   [Payload Encoding](./Payload_Encoding.md)