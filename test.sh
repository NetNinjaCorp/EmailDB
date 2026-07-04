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
set -euo pipefail
cd "$(dirname "$0")"

export DOTNET_ROLL_FORWARD=LatestMajor

DEFAULT_V3_FILTER='WriterLock|DurableStream|DirectoryFsync|BlockManagerFsync|BlockManagerV3|Superblock|Ulid|BlockSerializer|BlockCompressor|RuntimeBlockOffsetMap|ForwardScan|FindLastValidBlock'

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
