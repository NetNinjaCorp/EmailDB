# Email Storage Architecture

This document describes the components responsible for managing and indexing email data within the EmailDB system: `ZoneTree` and `EmailManager`.

## 1. ZoneTree (LSM KV Store & Full-Text Search Extension)

### Responsibilities

-   Provides high-performance, persistent storage and indexing based on a **Log-Structured Merge-Tree (LSM)** architecture.
-   Offers core **Key-Value (KV) storage** capabilities (`ZoneTreeFactory<TKey, TValue>`):
    -   Efficient ordered key-value operations (Upsert, Get, Delete, Scan).
    -   Supports ACID transactions (Optimistic Concurrency Control).
    -   Configurable Write-Ahead Log (WAL) modes for balancing performance and durability.
-   Includes an extension library, **`ZoneTree.FullTextSearch`**, providing advanced search capabilities (e.g., `HashedSearchEngine`):
    -   Indexes text data for fast full-text searching.
    -   Supports complex queries (Boolean operators, facets).
    -   This likely serves the role previously described as "Vector Index" for semantic search, potentially by indexing text embeddings or using specialized text hashing.
-   Manages the logical organization of its data (KV segments, WAL files, search indices) internally.
-   **Crucially, ZoneTree does *not* write directly to files.** It uses a **pluggable storage interface** (e.g., `IRandomAccessDeviceManager`, `IFileStreamProvider`) for all its persistence needs.
-   **Our `BlockManager` will be adapted to implement this ZoneTree storage interface**, ensuring all ZoneTree data (KV segments, WAL, search indices) is ultimately stored as blocks within the EMDB file format via the `BlockManager`.

### Interface (Conceptual)

```csharp
```csharp
// Core ZoneTree KV Interface (Simplified from ZoneTreeFactory usage)
interface IZoneTree<TKey, TValue>
{
    void Upsert(TKey key, TValue value);
    bool TryGetValue(TKey key, out TValue value);
    void Delete(TKey key);
    IZoneTreeIterator<TKey, TValue> CreateIterator();
    // Transactional methods (BeginTransaction, Upsert(tx,...), etc.)
}

// ZoneTree.FullTextSearch Interface (Conceptual, based on HashedSearchEngine)
interface IZoneTreeSearchEngine<TKey>
{
    void AddRecord(TKey key, string text);
    void UpdateRecord(TKey key, string oldText, string newText);
    void DeleteRecord(TKey key);
    IEnumerable<TKey> Search(string query); // Supports advanced query language
    // Methods for facets, pagination, cancellation etc.
}

// Storage Interface ZoneTree expects (Conceptual)
interface IZoneTreeStorageProvider // Or IRandomAccessDeviceManager / IFileStreamProvider
{
    // Methods for creating/opening/reading/writing underlying storage segments/files
    // that ZoneTree uses for its WAL, Disk Segments, etc.
}

```

### Dependencies

-   **`IZoneTreeStorageProvider` (or similar):** An interface defining the low-level storage operations ZoneTree needs (e.g., creating/reading/writing segments).
-   **(Internally):** Uses serializers (`ISerializer<T>`) and comparers (`IComparer<T>`) for keys and values.

### Key Characteristics

-   **LSM-based KV Store:** High-performance key-value storage suitable for write-heavy workloads.
-   **Extensible Search:** Provides powerful full-text search capabilities via the `ZoneTree.FullTextSearch` extension.
-   **Pluggable Persistence:** Decouples its logic from the underlying storage mechanism via a defined interface. **It does not directly depend on `BlockManager`**. Instead, our implementation of its required storage interface will use the `BlockManager`.

## 2. Email Manager (`EmailManager`)

### Responsibilities

-   Provides the primary high-level API for interacting with email data (e.g., `StoreEmail`, `GetEmailById`, `SearchEmailsBySubject`).
-   Defines how email data is structured and indexed using the capabilities of `ZoneTree`. This involves leveraging:
    -   The **Core KV Store** for metadata lookups (e.g., retrieving an email by its unique ID, finding emails in a date range, storing email headers).
    -   The **`ZoneTree.FullTextSearch` extension** for semantic/content-based search capabilities (e.g., finding emails matching keywords or complex queries, potentially using text embeddings indexed by the search engine).
-   Translates high-level email operations into specific KV operations *and/or* vector search operations on the underlying `ZoneTree` instance(s).
-   Handles the logic of breaking down emails into storable parts (e.g., storing headers separately from bodies, handling large attachments) and reconstructing them upon retrieval.
-   Orchestrates transactions or atomic operations if required for email storage.

### Interface (Conceptual)

```csharp
// Assuming an Email object model
interface IEmailManager
{
    Result StoreEmail(Email email);
    Result<Email> GetEmail(EmailId id);
    Result<IEnumerable<EmailHeader>> FindEmailsBySubject(string subjectQuery);
    // Other query methods (by sender, date range, etc.)
}
```

### Dependencies

-   `IZoneTree<TKey, TValue>`: Instances for storing various email metadata and potentially content pointers.
-   `IZoneTreeSearchEngine<TKey>`: Instances for indexing email content for full-text search.

### Key Characteristics

-   **Application Logic:** Contains the business logic specific to managing emails.
-   **API Layer:** Exposes the email storage functionality to the rest of the application.
-   **Index Orchestration:** Decides *what* gets indexed (metadata, content for search) and *how* it's stored and queried using the KV and Full-Text Search capabilities of `ZoneTree`.

## Relationship

```mermaid
graph LR
    A[Application Layer] --> B(EmailManager);
    B --> C(ZoneTree Instances <br/> - KV Store <br/> - FullTextSearch);
    C -- Uses --> SI{IZoneTreeStorageProvider <br/> (Interface)};
    D(BlockManager) -- Implements --> SI;
    D --> E(RawBlockManager);
    E --> F[EMDB File];

    style SI fill:#ccffcc,stroke:#669966
```

The `EmailManager` utilizes the Key-Value and Full-Text Search capabilities provided by `ZoneTree` instance(s). `ZoneTree` itself relies on a storage abstraction (`IZoneTreeStorageProvider` or similar). Our implementation of this storage interface uses the `BlockManager` to persist all ZoneTree data (KV segments, WAL, search indices) into the EMDB file format.