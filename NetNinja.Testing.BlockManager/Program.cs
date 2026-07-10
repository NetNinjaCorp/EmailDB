// Manual block-manager smoke harness.
//
// The original harness exercised the v1 CacheManager/MetadataManager/FolderManager/
// SegmentManager pipeline, all of which were retired per ADR-016 (US-EMDB-101).
// Real coverage now lives in EmailDB.UnitTests (v3 suite). This entrypoint is kept
// as a buildable placeholder.

Console.WriteLine("NetNinja.Testing.BlockManager: v1 pipeline retired; see EmailDB.UnitTests for v3 coverage.");
