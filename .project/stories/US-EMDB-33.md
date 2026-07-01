---
acceptance_criteria:
- EmailManager.AddEmailAsync stores email and indexes it in B+-tree
- EmailManager.GetEmailAsync retrieves email via B+-tree lookup
- EmailManager.DeleteEmailAsync removes from index and marks content outdated
- BTreeIndex reads upper nodes from CacheManager on subsequent lookups
- ZoneTree NuGet dependency removed from .csproj
- ZoneTree/*.cs files removed or archived
- iStorageManager interface updated for B+-tree operations
- 'End-to-end test: add 1000 emails then retrieve each by ID'
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-33
points: 8
priority: must
status: done
tags: []
title: Integrate B+-tree index with EmailManager and CacheManager
updated: '2026-02-24'
---

As a storage engine developer, I want the B+-tree index wired into the existing manager stack so that EmailManager can store and retrieve emails through the index.

**Scope**:
- Create BTreeIndex class exposing: InsertAsync, LookupAsync, DeleteAsync, ContainsAsync, CountAsync, RangeQueryAsync, FlushAsync, VerifyIntegrityAsync
- Wire BTreeIndex into the manager initialization chain: RawBlockManager → CacheManager → BTreeIndex → EmailManager
- EmailManager.AddEmailAsync: write email content block → get BlockLocation → BTreeIndex.InsertAsync(hashedId, location)
- EmailManager.GetEmailAsync: BTreeIndex.LookupAsync(hashedId) → BlockLocation → read email content block
- EmailManager.DeleteEmailAsync: BTreeIndex.DeleteAsync(hashedId) + mark content block outdated
- B+-tree node reads go through CacheManager (upper internal nodes stay hot)
- Add BlockType.EmailContent for email data blocks (distinct from index blocks)
- Update iStorageManager interface to reflect B+-tree-backed operations
- Remove ZoneTree.FullTextSearch NuGet dependency from EmailDB.Format.csproj
- Delete or archive the 6 commented-out ZoneTree/*.cs files

**Architecture**:
```
IStorageManager
  └─ EmailManager
       ├─ BTreeIndex (lookup by EmailHashedID)
       │    └─ RawBlockManager (block I/O)
       │    └─ CacheManager (node caching)
       └─ RawBlockManager (email content blocks)
```