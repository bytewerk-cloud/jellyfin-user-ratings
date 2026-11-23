#!/usr/bin/env bash
# Runs unit tests inside the .NET 10 SDK container.
set -euo pipefail
cd "$(dirname "$0")"
docker run --rm \
  -v "$PWD":/src -w /src \
  -v ducks-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test tests/Jellyfin.Plugin.Ducks.Tests/Jellyfin.Plugin.Ducks.Tests.csproj "$@"
