#!/usr/bin/env bash
# Runs all unit tests with coverage; fails if any line is uncovered.
#  1. C# plugin tests (local .NET 10 SDK, or the dotnet/sdk:10.0 container)
#  2. Web client tests (ducks.js) with Node's test runner + jsdom (local node, or the node:22 container)
set -euo pipefail
cd "$(dirname "$0")"
rm -rf artifacts/coverage
ARGS=(test tests/Jellyfin.Plugin.Ducks.Tests/Jellyfin.Plugin.Ducks.Tests.csproj
  --collect:"XPlat Code Coverage" --results-directory artifacts/coverage
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Jellyfin.Plugin.Ducks]*" "$@")
echo "== plugin tests"
if command -v dotnet >/dev/null 2>&1; then
  dotnet "${ARGS[@]}"
else
  docker run --rm -v "$PWD":/src -w /src -v ducks-nuget:/root/.nuget/packages \
    -e VSTEST_DISABLE_ARTIFACTS_DIRECTORY_VERIFICATION=1 \
    mcr.microsoft.com/dotnet/sdk:10.0 dotnet "${ARGS[@]}"
fi
python3 scripts/coverage.py artifacts/coverage/*/coverage.cobertura.xml

echo
echo "== web client tests"
if command -v node >/dev/null 2>&1; then
  (cd tests/web && npm ci --silent && npm test)
else
  docker run --rm -v "$PWD":/src -w /src/tests/web node:22 sh -c "npm ci --silent && npm test"
fi
