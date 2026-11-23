#!/usr/bin/env bash
# Builds a release zip and adds it to manifest.json (the Jellyfin plugin repository file).
# usage: scripts/package.sh <version e.g. 0.3.0.0> <download base url> [changelog]
# Output: artifacts/ducks_<version>.zip, manifest.json updated in place.
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=$1
BASE_URL=$2
CHANGELOG=${3:-"See https://github.com for release notes."}
TARGET_ABI=$(python3 -c "import re;print(re.search(r'targetAbi:\s*\"([^\"]+)\"', open('build.yaml').read()).group(1))")

rm -rf artifacts/Ducks
./build.sh -p:Version="$VERSION" -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION"

STAGE=artifacts/package
rm -rf "$STAGE" && mkdir -p "$STAGE"
cp artifacts/Ducks/Jellyfin.Plugin.Ducks.dll "$STAGE/"
python3 scripts/manifest.py meta --version "$VERSION" --target-abi "$TARGET_ABI" --changelog "$CHANGELOG" > "$STAGE/meta.json"
ZIP="artifacts/ducks_${VERSION}.zip"
rm -f "$ZIP"
(cd "$STAGE" && zip -q -X "../$(basename "$ZIP")" Jellyfin.Plugin.Ducks.dll meta.json)
CHECKSUM=$(python3 -c "import hashlib,sys;print(hashlib.md5(open(sys.argv[1],'rb').read()).hexdigest())" "$ZIP")

python3 scripts/manifest.py add --manifest manifest.json --version "$VERSION" --target-abi "$TARGET_ABI" \
  --changelog "$CHANGELOG" --url "$BASE_URL/ducks_${VERSION}.zip" --checksum "$CHECKSUM"
echo "Built $ZIP (md5 $CHECKSUM) and updated manifest.json"
