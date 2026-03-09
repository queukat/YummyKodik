#!/usr/bin/env bash
set -euo pipefail

# Local helper to generate a manifest.json for a given release asset.
#
# Example:
#   bash ./scripts/generate_manifest.sh \
#     --owner queukat --repo YummyKodik \
#     --tag v1.0.0.0 --version 1.0.0.0 \
#     --zip YummyKodik_1.0.0.0.zip --md5 <md5> \
#     --out manifest.json

OWNER=""
REPO=""
TAG=""
VERSION=""
ZIP=""
MD5=""
OUT="manifest.json"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --owner) OWNER="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    --tag) TAG="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --zip) ZIP="$2"; shift 2 ;;
    --md5) MD5="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$OWNER" || -z "$REPO" || -z "$TAG" || -z "$VERSION" || -z "$ZIP" || -z "$MD5" ]]; then
  echo "Missing required args." >&2
  exit 2
fi

GUID="6801ee3f-27f2-4d4e-ab37-e569c025b7c5"
NAME="YummyKodik"
CATEGORY="General"
DESCRIPTION="Creates Jellyfin anime series cards from YummyAnime and streams episodes from Kodik."
OVERVIEW="Builds a local STRM/NFO library from YummyAnime and plays episodes via Kodik."
TARGET_ABI="10.11.0.0"

SOURCE_URL="https://github.com/${OWNER}/${REPO}/releases/download/${TAG}/${ZIP}"
CHANGELOG="https://github.com/${OWNER}/${REPO}/releases/tag/${TAG}"
IMAGE_URL="https://raw.githubusercontent.com/${OWNER}/${REPO}/main/YummyKodik/Assets/logo.png"
TS="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

cat > "$OUT" <<JSON
[
  {
    "category": "${CATEGORY}",
    "guid": "${GUID}",
    "name": "${NAME}",
    "description": "${DESCRIPTION}",
    "owner": "${OWNER}",
    "overview": "${OVERVIEW}",
    "imageUrl": "${IMAGE_URL}",
    "versions": [
      {
        "checksum": "${MD5}",
        "changelog": "${CHANGELOG}",
        "targetAbi": "${TARGET_ABI}",
        "sourceUrl": "${SOURCE_URL}",
        "timestamp": "${TS}",
        "version": "${VERSION}"
      }
    ]
  }
]
JSON

echo "Wrote: $OUT"
