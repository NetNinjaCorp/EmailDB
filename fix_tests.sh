#!/bin/bash

# Fix RawBlockManager constructor calls
echo "Fixing RawBlockManager constructor calls..."
find . -name "*.cs" -type f -exec sed -i 's/new RawBlockManager(\([^,]*\), createNew: true)/new RawBlockManager(\1, createIfNotExists: true)/g' {} \;
find . -name "*.cs" -type f -exec sed -i 's/new RawBlockManager(\([^,]*\), createNew: false)/new RawBlockManager(\1, createIfNotExists: false)/g' {} \;

# Fix Result.Success to Result.IsSuccess
echo "Fixing Result.Success to Result.IsSuccess..."
find . -name "*.cs" -type f -exec sed -i 's/\.Success/.IsSuccess/g' {} \;

# Fix PayloadEncoding.Binary to PayloadEncoding.RawBytes
echo "Fixing PayloadEncoding.Binary to PayloadEncoding.RawBytes..."
find . -name "*.cs" -type f -exec sed -i 's/PayloadEncoding\.Binary/PayloadEncoding.RawBytes/g' {} \;

# Add using statement for test helpers where needed
echo "Adding using statements for test helpers..."
for file in $(find . -name "*.cs" -type f | xargs grep -l "BlockType\|SegmentContent\|MetadataContent" | xargs grep -L "using EmailDB.UnitTests.Helpers"); do
    if grep -q "using Xunit;" "$file"; then
        sed -i '/using Xunit;/i using EmailDB.UnitTests.Helpers;' "$file"
    fi
done

echo "Fixes applied!"