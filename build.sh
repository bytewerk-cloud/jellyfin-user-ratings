#!/usr/bin/env bash
# Builds the plugin → artifacts/Ducks/Jellyfin.Plugin.Ducks.dll
# Uses a local .NET 10 SDK if installed, otherwise the mcr.microsoft.com/dotnet/sdk:10.0 container.
set -euo pipefail
cd "$(dirname "$0")"
ARGS=(publish src/Jellyfin.Plugin.Ducks/Jellyfin.Plugin.Ducks.csproj -c Release -o artifacts/Ducks "$@")
if command -v dotnet >/dev/null 2>&1; then
  dotnet "${ARGS[@]}"
else
  docker run --rm -v "$PWD":/src -w /src -v ducks-nuget:/root/.nuget/packages \
    mcr.microsoft.com/dotnet/sdk:10.0 dotnet "${ARGS[@]}"
fi
