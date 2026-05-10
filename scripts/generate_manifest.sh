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
TARGET_ABI="10.11.0.0"

SOURCE_URL="https://github.com/${OWNER}/${REPO}/releases/download/${TAG}/${ZIP}"
if [[ -n "$CHANGELOG_FILE" ]]; then
  CHANGELOG="$(cat "$CHANGELOG_FILE")"
else
  CHANGELOG="What changed:
- Added blended provider coverage across Alloha, CVH, and Kodik. Users choose the anime source and desired quality; the plugin combines all available provider data to create as many episode files as possible.
- Added provider failover: if an episode is missing from one provider, one provider has fewer episodes, or playback resolution fails, the plugin can use a neighboring provider instead.
- Added Jellyfin Web seriesTranslation.js voice widget on series pages. It shows Auto plus available voices and saves a per-user, per-series choice.
- Fixed normal single-file mode voice preferences so a choice made in the widget is shared across Alloha/CVH-backed episode URLs and can route playback to the provider that actually has the selected voice.
- Added optional per-voice STRM generation and intro/outro media segments.
- Added automatic Kodik token resolution and refresh while keeping manual token override support.
- Improved season/title layout resolution, generated file maintenance, stale artifact cleanup, translation normalization, and duplicate Kodik link deduplication.
- Fixed Docker/base-image compatibility by targeting Jellyfin 10.11.0 shared assemblies.
- Fixed Docker/server URL handling so a trailing slash in ServerBaseUrl does not create //YummyKodik playback URLs.
- Added Windows PowerShell packaging script, regression test runner, and CI coverage for the solution.

After updating:
- Restart Jellyfin.
- Verify Output root path and Jellyfin server base URL.
- In Docker, use container paths such as /media/yummykodik.
- Run Scheduled Tasks -> YummyKodik library refresh and scan the Jellyfin library."
fi
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
