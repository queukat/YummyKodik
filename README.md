# YummyKodik

![YummyKodik wordmark](YummyKodik/Assets/wordmark.svg)

![Jellyfin 10.11](https://img.shields.io/badge/Jellyfin-10.11-00A4DC)
![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)
![Library STRM+NFO](https://img.shields.io/badge/Library-STRM%20%2B%20NFO-orange)
![Playback HLS](https://img.shields.io/badge/Playback-HLS-2ea44f)
![Segments Intro/Outro](https://img.shields.io/badge/Segments-Intro%20%2F%20Outro-blue)
![Releases GitHub Actions](https://img.shields.io/badge/Releases-GitHub%20Actions-black)

Jellyfin plugin that builds a local anime library from YummyAnime and plays episodes from Kodik through generated `.strm` files.

## What It Does

- Creates a local `Series/Season 01/*.strm` and `*.nfo` structure from YummyAnime slugs or a Yummy user list.
- Streams episodes through Jellyfin using the plugin proxy endpoint instead of hardcoding upstream Kodik links into metadata.
- Supports preferred translations, per-user translation choice, and optional `one voice translation = one file` library mode.
- Exposes intro and outro media segments for Jellyfin skip actions.
- Generates installable release ZIPs and a Jellyfin repository manifest for GitHub Pages / `gh-pages`.

## Install

### Option A: Jellyfin plugin repository via GitHub Pages

1. Enable GitHub Pages for this repository:
   - `Settings -> Pages`
   - Source: `Deploy from a branch`
   - Branch: `gh-pages` / folder `/(root)`
2. In Jellyfin open:
   - `Dashboard -> Plugins -> Repositories -> Add`
3. Add the repository URL:
   - `https://queukat.github.io/YummyKodik/manifest.json`

Then install:
- `Dashboard -> Plugins -> Catalog -> YummyKodik -> Install`
- Restart Jellyfin

### Option B: raw GitHub manifest

Use:
- `https://raw.githubusercontent.com/queukat/YummyKodik/gh-pages/manifest.json`

### Option C: manual ZIP install

1. Download `YummyKodik_<version>.zip` from GitHub Releases.
2. Extract it into the Jellyfin plugins directory:
   - Linux: `/var/lib/jellyfin/plugins/`
   - Windows (service/tray): `%ProgramData%\Jellyfin\Server\plugins`
   - Windows (portable): `%UserProfile%\AppData\Local\jellyfin\plugins`
3. Restart Jellyfin.

## Configure

Open:
- `Dashboard -> Plugins -> My Plugins -> YummyKodik -> Settings`

Main settings:
- `Yummy public token (X-Application)`: get it from `https://yummyani.me/dev/applications`
- `Output root path`: folder where the plugin writes the local library
- `Jellyfin server base URL`: externally reachable Jellyfin base URL used to build playback URLs
- `Preferred translation filter`: substring tokens separated by `|`, for example `anilibria|aniliberty`
- `Preferred quality`: `360`, `480`, `720`, `1080`
- `Slugs`: one Yummy slug per line

Optional settings:
- `Use user list subscription`: pulls entries from `/users/{id}/lists/{list_id}`
- `Create STRM per voice translation`: creates one episode file per voice translation
- `Enable Kodik HTTP debug logs`: useful only for troubleshooting

After changing slugs or list settings run:
- `Dashboard -> Scheduled Tasks -> YummyKodik library refresh`

If you changed playback URL format or enabled per-translation `.strm` mode, also refresh the library so Jellyfin re-reads the generated files.

## Build

Restore and build:

```bash
dotnet restore ./YummyKodik/YummyKodik.csproj
dotnet build ./YummyKodik/YummyKodik.csproj -c Release
```

Create a local release ZIP:

```bash
bash ./scripts/package.sh 1.0.0.0
```

This produces:
- `artifacts/YummyKodik_<version>.zip`
- `artifacts/YummyKodik_<version>.zip.md5`

## Release Flow

The repository includes GitHub Actions workflows for CI and release publishing.

- `CI` builds the plugin on pushes and pull requests.
- `Release` runs on version tags such as `v1.0.0.0`.
- The release workflow publishes the plugin ZIP and MD5 checksum to GitHub Releases.
- GitHub release notes are generated automatically.
- `manifest.json` is updated on `gh-pages`, so Jellyfin repository installs keep working.

Recommended tag format:

```bash
git tag v1.0.0.0
git push origin v1.0.0.0
```

## Notes

- The plugin targets Jellyfin `10.11.x`.
- Generated playback sources use HLS by default.
- Intro/outro skip support depends on the Jellyfin client honoring media segments and skip actions.
