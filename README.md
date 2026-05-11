# YummyKodik

<!-- public-repo-status -->
> Status: Active Jellyfin plugin. Releases are published through GitHub Releases and the plugin manifest; issues are open for reproducible bugs and focused feature requests.


![YummyKodik wordmark](YummyKodik/Assets/wordmark.png)

[![CI](https://img.shields.io/github/actions/workflow/status/queukat/YummyKodik/ci.yml?branch=main&label=CI)](https://github.com/queukat/YummyKodik/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/queukat/YummyKodik?display_name=tag)](https://github.com/queukat/YummyKodik/releases)
[![Last Commit](https://img.shields.io/github/last-commit/queukat/YummyKodik)](https://github.com/queukat/YummyKodik/commits/main)
[![Issues](https://img.shields.io/github/issues/queukat/YummyKodik)](https://github.com/queukat/YummyKodik/issues)
![Jellyfin 10.11](https://img.shields.io/badge/Jellyfin-10.11-00A4DC)
![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)
![Library STRM+NFO](https://img.shields.io/badge/Library-STRM%20%2B%20NFO-orange)
![Playback HLS](https://img.shields.io/badge/Playback-HLS-2ea44f)
![Segments Intro/Outro](https://img.shields.io/badge/Segments-Intro%20%2F%20Outro-blue)

YummyKodik is a Jellyfin plugin that builds a local anime library from YummyAnime and streams episodes through Jellyfin from Alloha, CVH, and Kodik-backed sources.

## Quick Links

- Releases: `https://github.com/queukat/YummyKodik/releases`
- Raw Jellyfin manifest: `https://raw.githubusercontent.com/queukat/YummyKodik/gh-pages/manifest.json`
- GitHub Pages manifest: `https://queukat.github.io/YummyKodik/manifest.json`
- Actions: `https://github.com/queukat/YummyKodik/actions`
- Issues: `https://github.com/queukat/YummyKodik/issues`

## What It Does

- Creates a local Jellyfin TV library layout with `tvshow.nfo`, `Season XX/*.nfo`, and `Season XX/*.strm`.
- Reads anime from manually configured Yummy slugs or from a Yummy user list.
- Combines episode availability from three provider families under the hood: Alloha, CVH, and Kodik.
- Generates as many episode files as it can from all available provider data, instead of assuming one player has complete coverage.
- Uses the requested quality as a preference, then asks the selected provider for the best matching HLS stream.
- Falls back to another provider when an episode is missing, a selected provider has fewer episodes, or playback resolution fails.
- Resolves and caches a Kodik token automatically, with an optional manual token override.
- Adds intro/outro media segments from Yummy/Kodik skip timings for Jellyfin clients that support skip actions.
- Injects a small Jellyfin Web series-page widget where each user can choose a preferred voice translation or return to auto mode.
- Can create one episode file per episode, or one episode file per voice translation.

## Provider Blending

Anime providers rarely have identical coverage. One player may already have four aired episodes, another may have two, and a third may have only one or a different set of voice translations. YummyKodik is built around that reality.

During refresh, the plugin asks Yummy for provider metadata and builds a combined picture of what is currently playable. It prefers direct Yummy-backed providers first:

- `Alloha`: usually the first generated provider when Yummy exposes enough Alloha data.
- `CVH`: used alongside Alloha and as a fallback when CVH has better episode/voice coverage.
- `Kodik`: used as a fallback and supplement when Yummy provider data is incomplete or a Kodik id is the best available route.

The result is that normal setup has no provider picker to babysit. You configure the anime source and desired quality, then let the plugin do the coverage work across all three providers. If episode 4 exists only in Kodik, refresh can still create the episode file even when Alloha has fewer episodes. If playback for a generated Alloha or CVH URL fails at runtime, the stream endpoint tries neighboring providers before giving up.

Voice selection follows the same idea. The plugin first honors an explicit per-user choice from the Jellyfin Web widget, then `Preferred translation filter`, then automatic fallback. In per-voice mode it creates separate files for available voice translations; in normal mode it keeps one episode file and resolves the best voice when you press play.

## Jellyfin Web Voice Widget

At startup, the plugin tries to patch Jellyfin Web `index.html` with a small bootstrap script:

```text
/web/ConfigurationPage?name=seriesTranslation.js&v=<plugin-version>
```

That script adds an `Озвучка` row to supported series details pages. The widget asks the plugin for available voices, shows `Авто` plus the combined Alloha/CVH voice list, and saves the choice through `YummyKodik/setTranslation`.

The saved value is per Jellyfin user and per Yummy series, so different users can choose different voices for the same anime. In normal single-file mode, this is how `SxxEyy.strm` can still play a chosen voice without creating separate episode files. Changing the voice in the widget does not require a library refresh; the next playback request reads the saved preference and resolves the best matching provider.

If the selected voice exists only in a neighboring provider, playback can move from the generated provider URL to that provider instead of silently playing another voice. Choosing `Авто` clears the saved preference and returns to `Preferred translation filter` plus automatic fallback.

If Jellyfin Web `index.html` is not writable or not found, the widget is skipped. Playback still works through generated files, `Preferred translation filter`, and per-voice files if that mode is enabled.

## Requirements

- Jellyfin `10.11.x`. The plugin is built against `10.11.0` for Docker/base-image compatibility.
- A writable folder visible to the Jellyfin server for generated `.strm` and `.nfo` files.
- A Jellyfin library pointing at that generated folder, usually with content type `Shows`.
- A Yummy public token from `https://site.yummyani.me/dev/applications`.
- Optional: an Alloha API token if you want the plugin to supplement missing Alloha entries by `kp_id`/`imdb_id`.

## Install From Manifest

Use one of these repository URLs in Jellyfin:

```text
https://raw.githubusercontent.com/queukat/YummyKodik/gh-pages/manifest.json
```

```text
https://queukat.github.io/YummyKodik/manifest.json
```

Then install:

1. Open `Dashboard -> Plugins -> Repositories -> Add`.
2. Paste the manifest URL.
3. Open `Dashboard -> Plugins -> Catalog -> YummyKodik -> Install`.
4. Restart Jellyfin.
5. Open `Dashboard -> Plugins -> My Plugins -> YummyKodik -> Settings`.

## Manual ZIP Install

Download `YummyKodik_<version>.zip` from GitHub Releases and extract the files directly into a versioned plugin folder.

Windows service or tray install:

```powershell
$version = "1.1.1.0"
$plugins = "$env:ProgramData\Jellyfin\Server\plugins"
New-Item -ItemType Directory -Force "$plugins\YummyKodik_$version"
Expand-Archive ".\YummyKodik_$version.zip" "$plugins\YummyKodik_$version" -Force
```

Windows portable install:

```powershell
$version = "1.1.1.0"
$plugins = "$env:LOCALAPPDATA\jellyfin\plugins"
New-Item -ItemType Directory -Force "$plugins\YummyKodik_$version"
Expand-Archive ".\YummyKodik_$version.zip" "$plugins\YummyKodik_$version" -Force
```

Docker install by copying an already extracted package:

```powershell
$version = "1.1.1.0"
docker exec jellyfin mkdir -p /config/plugins/YummyKodik_$version
docker cp .\artifacts\package\. jellyfin:/config/plugins/YummyKodik_$version/
docker restart jellyfin
```

Docker install from a zip inside the container:

```bash
version=1.1.1.0
mkdir -p "/config/plugins/YummyKodik_$version"
unzip "YummyKodik_$version.zip" -d "/config/plugins/YummyKodik_$version"
```

If Jellyfin cached a failed manual install as `NotSupported`, remove or rename `/config/plugins/YummyKodik_<version>/meta.json` after fixing the files, then restart Jellyfin.

## Configure

Open `Dashboard -> Plugins -> My Plugins -> YummyKodik -> Settings`.

Buttons:

- `Save`: writes the current settings to the Jellyfin plugin configuration.
- `Reload`: discards unsaved edits and reloads settings from Jellyfin.
- `Cancel`: closes the settings page.

Main settings:

- `Yummy public token (X-Application)`: public token from `https://site.yummyani.me/dev/applications`.
- `Yummy API base URL`: defaults to `https://api.yani.tv`; change only if the API endpoint changes.
- `Alloha API token`: optional token for extra Alloha catalog lookup.
- `Alloha API base URL`: defaults to `https://api.alloha.tv`.
- `Output root path`: path on the Jellyfin server where generated files are written.
- `Jellyfin server base URL`: base URL inserted into generated `.strm` files.
- `Preferred translation filter`: preferred voice tokens separated by `|`, for example `anilibria|aniliberty|shiza`.
- `Create separate STRM for each voice translation`: changes library layout from one file per episode to one file per voice.
- `Preferred quality`: target quality, usually `720` or `1080`; providers may return the nearest available stream.
- `Yummy slugs`: one Yummy slug per line for manual mode.

Optional settings:

- `Enable Kodik HTTP request debug logs`: noisy HTTP request/response diagnostics for Kodik troubleshooting.
- `Enable refresh performance diagnostics`: logs refresh stage timings and file-operation counters.
- `Use user list subscription`: pulls anime from `/users/{id}/lists/{list_id}`.
- `Yummy user ID` and `Yummy list ID`: identify the Yummy list to sync.
- `Yummy access token`: bearer token for private Yummy endpoints.
- `Yummy login` and `Yummy password`: fallback login flow if no access token is provided.
- `Refresh interval`: scheduled refresh interval in minutes.

## Modes

Manual slug mode:

- Put one or more Yummy slugs in `Yummy slugs`.
- Leave `Use user list subscription` off.
- Run `Dashboard -> Scheduled Tasks -> YummyKodik library refresh`.

User list mode:

- Turn on `Use user list subscription`.
- Set `Yummy user ID`, `Yummy list ID`, and either `Yummy access token` or login/password.
- Manual slugs are still honored, so you can combine both sources.

Single episode file mode:

- Leave `Create separate STRM for each voice translation` off.
- The plugin creates `SxxEyy.strm`.
- Playback picks a voice using the per-user saved choice, then `Preferred translation filter`, then provider fallback.

Per-voice file mode:

- Turn on `Create separate STRM for each voice translation`.
- The plugin creates files like `S01E01 - AniLibria.strm`.
- Jellyfin can show voice translations as separate episode versions.

Translation widget mode:

- The plugin patches Jellyfin Web `index.html` at startup to load `seriesTranslation.js`.
- On a supported series details page, the widget shows `Auto` and the combined available Alloha/CVH voice choices.
- Choosing a voice saves a per-user, per-Yummy-series preference that normal single-file playback uses on the next play.
- Choosing `Auto` clears the saved preference and returns to automatic selection.
- No library refresh is required after changing the widget choice.

## Docker Notes

Use container paths in plugin settings. A Windows path such as `D:\video\YummyKodik` only works for Windows Jellyfin. In Docker, use a path inside the container, for example:

```text
/media/yummykodik
```

Mount that path from the host, then point a Jellyfin `Shows` library at the same container path.

`Jellyfin server base URL` must be reachable from the Jellyfin container and from playback clients. On Docker Desktop, this often works well:

```text
http://host.docker.internal:8096
```

or, if Jellyfin is published on host port `8099`:

```text
http://host.docker.internal:8099
```

A trailing slash is safe. The plugin trims it before generating paths, so this:

```text
http://host.docker.internal:8099/
```

generates:

```text
http://host.docker.internal:8099/YummyKodik/stream?...
```

not:

```text
http://host.docker.internal:8099//YummyKodik/stream?...
```

## Refresh And Playback

After changing slugs, user-list settings, `Output root path`, `Jellyfin server base URL`, or per-voice mode:

1. Save the plugin configuration.
2. Run `Dashboard -> Scheduled Tasks -> YummyKodik library refresh`.
3. Scan the Jellyfin library that points at `Output root path`.
4. Open a generated series and start playback.

Playback URL types:

- `provider=alloha`: builds an Alloha HLS session and proxies playlists/segments through `/YummyKodik/alloha-proxy`; can fall back to CVH/Kodik-compatible data when needed.
- `provider=cvh`: builds a CVH HLS session and proxies nested playlists/segments through `/YummyKodik/cvh-proxy`; can fall back to Alloha/Kodik-compatible data when needed.
- `type=...&id=...`: Kodik-backed URL mode, used as legacy playback and as a supplement when provider coverage is incomplete.

## Build And Package

Restore and build:

```powershell
dotnet restore .\YummyKodik\YummyKodik.csproj
dotnet build .\YummyKodik.sln -c Release
```

Run the lightweight regression suite:

```powershell
dotnet run --project .\YummyKodik.Tests\YummyKodik.Tests.csproj -c Release
```

Create a local release ZIP on Windows:

```powershell
.\scripts\package.ps1 -Version 1.1.1.0
```

Create a local release ZIP on Linux/macOS:

```bash
bash ./scripts/package.sh 1.1.1.0
```

This produces:

- `artifacts/YummyKodik_<version>.zip`
- `artifacts/YummyKodik_<version>.zip.md5`

## Release Flow

The release workflow runs on version tags and publishes the ZIP, MD5 checksum, GitHub Release, and `gh-pages` manifest.

Recommended tag format follows the existing release convention:

```bash
git tag 1.1.1.0
git push origin 1.1.1.0
```

Tags with a leading `v` also work because the workflow normalizes versions.

## Docker Smoke Test

The `1.1.1.0` package was smoke-tested against `jellyfin/jellyfin:10.11.0` with a single configured slug:

```text
fermerskaya-zhizn-v-inom-mire-2
```

Verified:

- The plugin loads as `Active`.
- The refresh task generates one series and five aired episode `.strm` files.
- A trailing slash in `ServerBaseUrl` does not produce a double slash in generated URLs.
- The generated Alloha stream returns an HLS master playlist.
- A nested playlist returns `200 application/vnd.apple.mpegurl`.
- The first proxied `.ts` segment returns `200 video/MP2T`.

## Notes

- Generated playback sources use HLS.
- Intro/outro skip support depends on the Jellyfin client honoring media segments.
- If you update from an older manual install, restart Jellyfin after replacing plugin files.

## Acknowledgements

Special thanks to [`YaNesyTortiK/AnimeParsers`](https://github.com/YaNesyTortiK/AnimeParsers), which helped show that building a plugin like this was possible in the first place.
## License

<!-- commercial-license-policy -->
This project is licensed for non-commercial use under the [PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/).
Commercial use, resale, paid distribution, marketplace publication, SaaS hosting, or bundling into a paid product requires separate written permission from the author.
Project names, logos, package identifiers, store listings, screenshots, and other branding assets are not licensed for use in forks or redistributed builds.
