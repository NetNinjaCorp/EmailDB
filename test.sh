#!/usr/bin/env bash
# Run EmailDB unit tests with the required runtime roll-forward.
# Only the .NET 11 preview runtime is installed; tests target net9.0.
#
# Usage:
#   ./test.sh                    # run the default v3 test filter
#   ./test.sh <filter>           # run tests matching FullyQualifiedName~<filter>
#   ./test.sh 'A|B'              # multiple fragments, OR'd together
#   ./test.sh --all              # run the entire suite (WARNING: legacy BTree
#                                # stress tests make this take 25+ minutes)
#
# Examples:
#   ./test.sh WriterLock
#   ./test.sh 'ForwardScan|BlockManagerV3'
#
# Stress tests (excluded from the default filter, run explicitly):
#   ./test.sh MillionEntryModelStress    # US-EMDB-68: randomized insert/delete
#       stress of the COW B+-tree to 1M+ live entries against a reference
#       model (Category=Stress). Takes ~2.5 minutes and appends a ~3.2 GB
#       store to /dev/shm (deleted afterwards).
set -euo pipefail
cd "$(dirname "$0")"

export DOTNET_ROLL_FORWARD=LatestMajor

DEFAULT_V3_FILTER='WriterLock|DurableStream|DirectoryFsync|BlockManagerFsync|BlockManagerV3|Superblock|Ulid|BlockSerializer|BlockCompressor|RuntimeBlockOffsetMap|ForwardScan|FindLastValidBlock|BTreeNodeDeserialization|BTreeNodeContentHash|CowBTree|IndexRootSerialization|WalBufferedIndex|WalCheckpointFence|WalSerialization|WalWriter|WalReplayer|BlockLocationIndex|LocationIndexCheckpoint|CompositeBlockIdResolver|LocationIndexRegenerator|CheckpointSerialization|CheckpointWriterReader|CleanOpen|DirtyOpen|DisasterOpen|OpenComplexity|VerificationErrorTaxonomy|VerificationErrorDistinctness|PerFailureHandler|DamagedRangeOffset|ReferencedDataLoss|Section13Contract|AesGcmBlockCipher|EpochDekProvider|V3PasswordKeyDerivation|V3KeyVerificationToken|EncryptionBootstrap|RotateKeyV3EpochBound|ChangePasswordV3|EncryptionPolicyWritePath|ReadPathDecryptTaxonomy'

if [[ "${1:-}" == "--all" ]]; then
    exec dotnet test EmailDB.UnitTests "${@:2}"
fi

raw_filter="${1:-$DEFAULT_V3_FILTER}"
shift || true

# Expand 'A|B' into FullyQualifiedName~A|FullyQualifiedName~B
filter=""
IFS='|' read -ra parts <<< "$raw_filter"
for p in "${parts[@]}"; do
    [[ -n "$filter" ]] && filter+="|"
    filter+="FullyQualifiedName~${p}"
done

exec dotnet test EmailDB.UnitTests --filter "$filter" "$@"
