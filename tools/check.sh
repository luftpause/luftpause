#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

dotnet test runtime/tests/Luftpause.Runtime.Tests
dotnet test scheduler/tests/Luftpause.Scheduling.Tests
dotnet build tools/loadgen -c Release

if command -v cargo >/dev/null 2>&1; then
  (cd dataplane && cargo test -p dataplane)
else
  echo "cargo not found: skipping dataplane tests"
fi

docker compose build
