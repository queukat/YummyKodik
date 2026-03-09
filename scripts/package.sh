#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   bash ./scripts/package.sh 1.0.0.0
#
# Outputs:
#   ./artifacts/YummyKodik_<version>.zip
#   ./artifacts/YummyKodik_<version>.zip.md5

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$ROOT/YummyKodik/YummyKodik.csproj"

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
  echo "Version is required. Example: bash ./scripts/package.sh 1.0.0.0" >&2
  exit 2
fi

mkdir -p "$ROOT/artifacts"
rm -rf "$ROOT/artifacts/publish" "$ROOT/artifacts/package"

dotnet restore "$PROJ"
dotnet publish "$PROJ" -c Release -o "$ROOT/artifacts/publish" \
  -p:Version="$VERSION" \
  -p:AssemblyVersion="$VERSION" \
  -p:FileVersion="$VERSION"

mkdir -p "$ROOT/artifacts/package"

cp "$ROOT/artifacts/publish/YummyKodik.dll" "$ROOT/artifacts/package/"

if [[ -f "$ROOT/artifacts/publish/YummyKodik.deps.json" ]]; then
  cp "$ROOT/artifacts/publish/YummyKodik.deps.json" "$ROOT/artifacts/package/"
fi

if [[ -f "$ROOT/YummyKodik/Assets/logo.png" ]]; then
  cp "$ROOT/YummyKodik/Assets/logo.png" "$ROOT/artifacts/package/"
fi

if [[ -f "$ROOT/YummyKodik/Assets/logo.svg" ]]; then
  cp "$ROOT/YummyKodik/Assets/logo.svg" "$ROOT/artifacts/package/"
fi

if [[ -f "$ROOT/artifacts/publish/HtmlAgilityPack.dll" ]]; then
  cp "$ROOT/artifacts/publish/HtmlAgilityPack.dll" "$ROOT/artifacts/package/"
fi

if [[ -f "$ROOT/artifacts/publish/YummyKodik.pdb" ]]; then
  cp "$ROOT/artifacts/publish/YummyKodik.pdb" "$ROOT/artifacts/package/"
fi

ZIP="YummyKodik_${VERSION}.zip"
( cd "$ROOT/artifacts/package" && zip -r "../${ZIP}" . )
md5sum "$ROOT/artifacts/${ZIP}" | awk '{print $1}' > "$ROOT/artifacts/${ZIP}.md5"

echo "Created: $ROOT/artifacts/${ZIP}"
echo "MD5:     $ROOT/artifacts/${ZIP}.md5"
