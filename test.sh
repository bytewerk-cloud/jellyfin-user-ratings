#!/usr/bin/env bash
# Runs unit tests (inside the .NET 10 SDK container) with line/branch coverage.
# Prints per-class coverage for the plugin and fails if any line is uncovered.
set -euo pipefail
cd "$(dirname "$0")"
rm -rf artifacts/coverage
docker run --rm \
  -v "$PWD":/src -w /src \
  -v ducks-nuget:/root/.nuget/packages \
  -e VSTEST_DISABLE_ARTIFACTS_DIRECTORY_VERIFICATION=1 \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test tests/Jellyfin.Plugin.Ducks.Tests/Jellyfin.Plugin.Ducks.Tests.csproj \
    --collect:"XPlat Code Coverage" --results-directory artifacts/coverage \
    -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Jellyfin.Plugin.Ducks]*" "$@"
python3 scripts/coverage.py artifacts/coverage/*/coverage.cobertura.xml
