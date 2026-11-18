#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
sources=()
for file in "$root"/Sources/*.swift; do
  if [[ "$file" != */main.swift ]]; then sources+=("$file"); fi
done
swiftc -O -swift-version 5 "${sources[@]}" "$root/Windows/export-grokbot/main.swift" -o "$scratch/export"
"$scratch/export" "$root" "${1:-grokbot}"
