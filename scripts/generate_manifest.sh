#!/usr/bin/env bash
set -euo pipefail

# Local helper to generate a manifest.json for a given release asset.
#
# Example:
#   bash ./scripts/generate_manifest.sh \
#     --owner queukat --repo YummyKodik \
#     --tag v1.0.0.0 --version 1.0.0.0 \
#     --zip YummyKodik_1.0.0.0.zip --md5 <md5> \
#     --changelog-file release-notes.md \
#     --out manifest.json

OWNER=""
REPO=""
TAG=""
VERSION=""
ZIP=""
MD5=""
CHANGELOG_FILE=""
OUT="manifest.json"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --owner) OWNER="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    --tag) TAG="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --zip) ZIP="$2"; shift 2 ;;
    --md5) MD5="$2"; shift 2 ;;
    --changelog-file) CHANGELOG_FILE="$2"; shift 2 ;;
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
DESCRIPTION="Creates Jellyfin anime series cards from YummyAnime and streams episodes from Alloha, CVH, and Kodik-backed sources."
OVERVIEW="Builds a local STRM/NFO library from YummyAnime and plays episodes via Alloha, CVH, and Kodik-backed sources."
TARGET_ABI="12.0.0.0"

SOURCE_URL="https://github.com/${OWNER}/${REPO}/releases/download/${TAG}/${ZIP}"
if [[ -z "$CHANGELOG_FILE" ]]; then
  CHANGELOG_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/.github/release-notes.md"
fi
CHANGELOG="$(cat "$CHANGELOG_FILE")"
IMAGE_URL="https://raw.githubusercontent.com/${OWNER}/${REPO}/main/YummyKodik/Assets/logo.png"
TS="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

jq -n \
  --arg category "$CATEGORY" \
  --arg guid "$GUID" \
  --arg name "$NAME" \
  --arg description "$DESCRIPTION" \
  --arg owner "$OWNER" \
  --arg overview "$OVERVIEW" \
  --arg imageUrl "$IMAGE_URL" \
  --arg checksum "$MD5" \
  --arg changelog "$CHANGELOG" \
  --arg targetAbi "$TARGET_ABI" \
  --arg sourceUrl "$SOURCE_URL" \
  --arg timestamp "$TS" \
  --arg version "$VERSION" \
  '[{
    category: $category,
    guid: $guid,
    name: $name,
    description: $description,
    owner: $owner,
    overview: $overview,
    imageUrl: $imageUrl,
    versions: [{
      checksum: $checksum,
      changelog: $changelog,
      targetAbi: $targetAbi,
      sourceUrl: $sourceUrl,
      timestamp: $timestamp,
      version: $version
    }]
  }]' > "$OUT"

echo "Wrote: $OUT"
