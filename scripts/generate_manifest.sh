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
- Made library refresh faster while keeping external API pressure conservative. Refresh workers can process different titles in parallel, but the default limit remains 2.
- Added a single-run gate so a second refresh exits quickly instead of doing duplicate work.
- Added per-series filesystem locks so season folder migration, cleanup, STRM/NFO writes, and state writes are serialized for the same series root.
- Split the refresh flow into smaller services for title loading, metadata writing, episode generation, Kodik supplementing, state checks, and cleanup.
- Added refresh state files for safe single-file skips. State is multi-season, written atomically, and never stores raw provider tokens, cookies, bearer tokens, or secret-bearing URLs.
- Added strict skip validation. The plugin now checks fingerprints, covered episodes, managed file hashes, missing files, and stale episode-shaped artifacts before skipping a title.
- Kept per-voice mode conservative. It still runs full provider lookup so new voice translation files can appear even when episode counts do not change.
- Made corrupt, missing, stale, or ambiguous refresh state fall back to a full refresh instead of failing or skipping too aggressively.
- Improved stale artifact cleanup for generated episode STRM/NFO files, including zero-episode and future-episode cases.
- Improved legacy season folder and episode filename migration under the series-root lock.
- Fixed atomic text writes so existing read-only generated STRM/NFO files can be replaced during refresh.
- Made Shikimori layout caching safe for concurrent refresh workers.
- Made shared Kodik token and lookup paths safer under parallel refresh and runtime playback.
- Delayed Kodik client initialization until a title actually needs Kodik supplement data.
- Added refresh performance diagnostics for slow-stage and file-operation troubleshooting.
- Added a configurable Minimum plugin log level. The default is Warning, so normal refresh and playback no longer spam Jellyfin with informational YummyKodik logs.
- Fixed a logging startup regression by avoiding global ILogger replacement and filtering only YummyKodik loggers.
- Improved Alloha/CVH/Kodik fallback handling, voice matching, playlist proxy recovery, and token refresh behavior used by runtime playback.
- Added regression coverage for refresh state skips, stale artifacts, parallelism, run gate behavior, thread-safe Shikimori cache, atomic file replacement, and plugin log filtering.

After updating:
- Restart Jellyfin.
- Verify Output root path and Jellyfin server base URL.
- Leave Minimum plugin log level at Warning for quiet normal operation, or lower it to Information/Debug/Trace only while diagnosing an issue.
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
