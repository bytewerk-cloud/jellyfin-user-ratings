#!/usr/bin/env bash
# Builds the plugin inside the .NET 10 SDK container (no local SDK needed).
# Output: artifacts/Ducks/Jellyfin.Plugin.Ducks.dll
set -euo pipefail
cd "$(dirname "$0")"
docker run --rm \
  -v "$PWD":/src -w /src \
  -v ducks-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish src/Jellyfin.Plugin.Ducks/Jellyfin.Plugin.Ducks.csproj -c Release -o artifacts/Ducks "$@"
