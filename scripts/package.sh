#!/usr/bin/env bash
# Builds the plugin, creates the install zip and adds a version entry to manifest.json.
# Usage: scripts/package.sh <version> <github-owner/repo>
#   e.g. scripts/package.sh 1.0.0.0 marcelocerqueira/jellyfin-series-track-memory
set -euo pipefail

VERSION="${1:?version, e.g. 1.0.0.0}"
REPO="${2:?github owner/repo}"
TARGET_ABI="12.0.0.0"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/Jellyfin.Plugin.SeriesTrackMemory/Jellyfin.Plugin.SeriesTrackMemory.csproj"
OUT="$ROOT/artifacts"
ZIP_NAME="series-track-memory_${VERSION}.zip"

rm -rf "$OUT/publish" && mkdir -p "$OUT/publish"
dotnet publish "$PROJECT" -c Release -o "$OUT/publish" -p:Version="$VERSION" -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION"

# Only the plugin assembly: Jellyfin provides every referenced package at runtime.
rm -f "$OUT/$ZIP_NAME"
(cd "$OUT/publish" && zip -q "$OUT/$ZIP_NAME" Jellyfin.Plugin.SeriesTrackMemory.dll)

if command -v md5sum >/dev/null; then CHECKSUM=$(md5sum "$OUT/$ZIP_NAME" | cut -d' ' -f1); else CHECKSUM=$(md5 -q "$OUT/$ZIP_NAME"); fi

python3 - "$ROOT/manifest.json" "$VERSION" "$TARGET_ABI" "$CHECKSUM" \
  "https://github.com/$REPO/releases/download/v$VERSION/$ZIP_NAME" <<'PY'
import datetime, json, sys
path, version, abi, checksum, url = sys.argv[1:]
with open(path) as f:
    manifest = json.load(f)
versions = [v for v in manifest[0]["versions"] if v["version"] != version]
versions.insert(0, {
    "version": version,
    "changelog": f"Release {version}",
    "targetAbi": abi,
    "sourceUrl": url,
    "checksum": checksum,
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
})
manifest[0]["versions"] = versions
with open(path, "w") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
PY

echo "Created $OUT/$ZIP_NAME (md5 $CHECKSUM) and updated manifest.json"
