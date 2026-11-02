# YummyKodik

![YummyKodik wordmark](YummyKodik/Assets/wordmark.png)

[![CI](https://img.shields.io/github/actions/workflow/status/queukat/YummyKodik/ci.yml?branch=main&label=CI)](https://github.com/queukat/YummyKodik/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/queukat/YummyKodik?display_name=tag)](https://github.com/queukat/YummyKodik/releases)
[![Last Commit](https://img.shields.io/github/last-commit/queukat/YummyKodik)](https://github.com/queukat/YummyKodik/commits/main)
[![Issues](https://img.shields.io/github/issues/queukat/YummyKodik)](https://github.com/queukat/YummyKodik/issues)
![Jellyfin 12](https://img.shields.io/badge/Jellyfin-12-00A4DC)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)
![Library STRM+NFO](https://img.shields.io/badge/Library-STRM%20%2B%20NFO-orange)
![Playback HLS](https://img.shields.io/badge/Playback-HLS-2ea44f)
![Segments Intro/Outro](https://img.shields.io/badge/Segments-Intro%20%2F%20Outro-blue)

YummyKodik is a Jellyfin plugin that builds a local anime library from YummyAnime and streams episodes through Jellyfin from Alloha, CVH, and Kodik-backed sources.

## Quick Links

- [Releases](https://github.com/queukat/YummyKodik/releases)
- [Raw Jellyfin manifest](https://raw.githubusercontent.com/queukat/YummyKodik/gh-pages/manifest.json)
- [GitHub Pages manifest](https://queukat.github.io/YummyKodik/manifest.json)
- [Actions](https://github.com/queukat/YummyKodik/actions)
- [Issues](https://github.com/queukat/YummyKodik/issues)

## What It Does

- Creates a local Jellyfin TV library layout with `tvshow.nfo`, `Season XX/*.nfo`, and `Season XX/*.strm`.
- Reads anime from manually configured Yummy slugs or from two Yummy lists with separate output folders.
- Can optionally remove plugin-managed releases that disappear from a successfully fetched Yummy user list.
- Combines episode availability from three provider families under the hood: Alloha, CVH, and Kodik.
- Generates as many episode files as it can from all available provider data, instead of assuming one player has complete coverage.
- Uses the requested quality as a preference, then asks the selected provider for the best matching HLS stream.
- Falls back to another provider when an episode is missing, a selected provider has fewer episodes, or playback resolution fails.
- Resolves and caches a Kodik token automatically, with an optional manual token override.
- Adds intro/outro skip timings and can borrow them across voice translations when a track has none, so more episodes offer skip buttons in compatible Jellyfin clients.
- Injects a small Jellyfin Web series-page widget for choosing the series-wide voice translation or returning to auto mode.
- Can create one episode file per episode, or one episode file per voice translation.

## Requirements

Release `2.1.0.0` and the `main` branch target Jellyfin 12. Jellyfin 10.11 uses the separate `jellyfin-10.11` branch and release `1.2.2.0`; the features described here apply to the Jellyfin 12 release.

- Jellyfin `12`. For Jellyfin `10.11.x`, use plugin release `1.2.2.0` from the `jellyfin-10.11` branch.
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

Download `YummyKodik_<version>.zip` from [GitHub Releases](https://github.com/queukat/YummyKodik/releases). Stop Jellyfin, extract the files directly into a versioned plugin folder, then start Jellyfin again. Use the plugin directory belonging to your installation; the examples below show common Windows locations.

Windows service installation:

```powershell
$version = "2.1.0.0"
$plugins = "$env:ProgramData\Jellyfin\Server\plugins"
New-Item -ItemType Directory -Force "$plugins\YummyKodik_$version"
Expand-Archive ".\YummyKodik_$version.zip" "$plugins\YummyKodik_$version" -Force
```

Windows user installation (verify the data directory if using a custom or portable setup):

```powershell
$version = "2.1.0.0"
$plugins = "$env:LOCALAPPDATA\jellyfin\plugins"
New-Item -ItemType Directory -Force "$plugins\YummyKodik_$version"
Expand-Archive ".\YummyKodik_$version.zip" "$plugins\YummyKodik_$version" -Force
```

For Docker, stop the container and extract the ZIP into `plugins/YummyKodik_<version>` under the host directory mounted as `/config`, then start the container again. Use the actual configuration mount from your container setup.

## Configure

Enter the Yummy public token and an absolute output folder, choose slugs or a user list, and save. Add the output folder as a Jellyfin **Shows** library. Run the corresponding YummyKodik scheduled refresh, then scan that library to see the generated series.

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
- `Preferred translation filter`: preferred voice tokens separated by `|`, for example `anilibria|aniliberty|shiza`.
- `Create separate STRM for each voice translation`: changes library layout from one file per episode to one file per voice.
- `Preferred quality`: target quality, usually `720` or `1080`; providers may return the nearest available stream.
- `Minimum plugin log level`: defaults to `Warning`; set to `Information`, `Debug`, or `Trace` only when diagnosing noisy refresh/playback behavior.
- `Yummy slugs`: one Yummy slug per line for manual mode.

Optional settings:

- `Enable Kodik HTTP request debug logs`: noisy HTTP request/response diagnostics for Kodik troubleshooting.
- `Enable refresh performance diagnostics`: logs refresh stage timings and file-operation counters.
- `Use user list subscription`: pulls anime from `/users/{id}/lists/{list_id}`.
- `Удалять релизы, отсутствующие в списке Yummy`: removes stale plugin-managed seasons after a successful complete list fetch. Manual slugs and directories with unknown or modified files are preserved.
- `Yummy user ID` and the named list selector identify the Yummy list to sync. The choices match Yummy: Watching, Plan to watch, Completed, On hold, Dropped, and Favorites.
- **Sign in and use my lists**: verifies the entered Yummy login/password immediately, saves the account token, and fills your user ID. Other unsaved settings are kept in the form.
- The account status shows the last verified result and actionable authentication errors. Token-only sign-in is available under advanced account settings and clears the saved password. Password sign-in stores the password on the server for recovery if the token is rejected.
- `Refresh interval`: scheduled refresh interval in minutes.

When list authentication fails, existing local titles may still be refreshed, but the task finishes as failed with an explanation. Jellyfin records failed tasks in dashboard activity and sends its standard task-completion event to administrator sessions. The settings banner persists the last authentication result; it is not a continuous connectivity check. A list access failure is distinct from an invalid account.

If Yummy requires hCaptcha, sign-in reports the challenge; the plugin cannot complete it automatically. API address changes must be saved before using the sign-in button. The initial refresh interval applies when a task is first created; change existing schedules in Jellyfin Scheduled Tasks.

## Modes

Manual slug mode:

- Put one or more Yummy slugs in `Yummy slugs`.
- Leave `Use user list subscription` off.
- Run `Dashboard -> Scheduled Tasks -> YummyKodik: primary library refresh`.

User list mode:

- Turn on `Use user list subscription`.
- Sign in using the account button, choose the list by name, save the remaining settings, then run the corresponding Jellyfin scheduled refresh. For an accessible public list owned by another user, set its user ID manually. Saving general settings does not authenticate or start a refresh.
- Manual slugs are still honored, so you can combine both sources.
- Optionally enable stale-release deletion. Cleanup is skipped whenever the list request fails or its response is incomplete.

To use a second Jellyfin library, enable the second Yummy list, choose its name, and enter a separate absolute output folder. Add that folder to another TV-show library in Jellyfin. The two folders cannot coincide or be nested. Both lists use the same Yummy account, quality and voice settings; manual slugs go only to the primary folder. Stale-release deletion applies independently to each successfully fetched list. Disabling the second list leaves its existing files untouched. Voice preferences for the same anime remain shared across libraries.

For each generated Jellyfin library, open its library settings and disable **Missing Episode Fetcher** under **Series metadata downloaders** if TheTVDB is installed. YummyKodik already generates the available episodes. That optional fetcher adds unavailable-episode placeholders and can issue costly database queries on every episode update, especially when each voice has its own STRM file. Keep **TheTVDB** enabled if you use its regular metadata; it is a separate entry. Check this setting again when adding another library, because Jellyfin does not inherit it from the first one.

The settings page follows the Jellyfin interface language, using English or Russian; other interface languages use Russian.

Each folder has a separate scheduled task: **YummyKodik: primary library refresh** keeps the existing primary schedule, and **YummyKodik: second library refresh** defaults to once every 24 hours. Change their triggers independently in Jellyfin Scheduled Tasks. Each task refreshes only its own list, files, runtimes and episode versions. The second task does nothing while its list is disabled. The tasks share one execution gate: if one is already running, an overlapping start is skipped with a warning; stagger the triggers or run the skipped task again. Finished titles still receive safe metadata/state checks on their folder's schedule, since list membership and voice availability can change.

Single episode file mode:

- Leave `Create separate STRM for each voice translation` off.
- The plugin creates `SxxEyy.strm`.
- Playback picks a voice using the explicit series selection, then `Preferred translation filter`, then provider fallback.

Per-voice file mode:

- Turn on `Create separate STRM for each voice translation`.
- The plugin creates files like `S01E01 - AniLibria.strm`.
- Jellyfin can show voice translations as separate episode versions.

Translation widget mode:

- The plugin patches Jellyfin Web `index.html` at startup to load `seriesTranslation.js`.
- On a supported series details page, the widget shows `Авто` (Auto) and the voices present in local episode files for the whole series.
- Choosing a voice makes it the series-wide primary and keeps `Next`/autoplay on that voice where it is available.
- Choosing `Auto` clears the explicit selection and returns to automatic selection.
- No library refresh is required after changing the widget choice.

## Provider Blending

Anime providers rarely have identical coverage. One player may already have four aired episodes, another may have two, and a third may have only one or a different set of voice translations. YummyKodik is built around that reality.

During refresh, the plugin asks Yummy for provider metadata and builds a combined picture of what is currently playable. It prefers direct Yummy-backed providers first:

- `Alloha`: usually the first generated provider when Yummy exposes enough Alloha data.
- `CVH`: used alongside Alloha and as a fallback when CVH has better episode/voice coverage.
- `Kodik`: used as a fallback and supplement when Yummy provider data is incomplete or a Kodik id is the best available route.

The result is that normal setup has no provider picker to babysit. You configure the anime source and desired quality, then let the plugin do the coverage work across all three providers. If episode 4 exists only in Kodik, refresh can still create the episode file even when Alloha has fewer episodes. If playback for a generated Alloha or CVH URL fails at runtime, the stream endpoint tries neighboring providers before giving up.

For a fresh Alloha playback request with a specific selected or saved voice, the plugin first tries the same voice on Kodik and keeps Alloha as a fallback. This can select a lower resolution even when a higher quality is preferred.

Voice selection follows the same idea. The plugin first honors an explicit series-wide choice from the Jellyfin Web widget, then `Preferred translation filter`, then automatic fallback. In per-voice mode it creates separate files for available voice translations; in normal mode it keeps one episode file and resolves the best voice when you press play.

## Jellyfin Web Voice Widget

The **Приоритет озвучки** (voice priority) row appears on supported series details pages. The widget shows `Авто` plus the normalized voices already present in local STRM files for the whole series, including local Kodik versions. Opening a details page does not contact providers. New voices appear after library refresh generates their files.

An explicit choice becomes the library-wide primary voice for that series. Jellyfin `Next` and autoplay then advance by episode in the chosen voice wherever it exists, while unmatched episodes use the normal safe fallback. Because Jellyfin stores the primary version globally, simultaneous independent voice locks for different users are not supported.

Changing the voice does not require a library refresh. If the selected voice exists only in a neighboring provider, playback can move to that provider instead of silently playing another voice. Choosing `Авто` clears the explicit selection and returns to `Preferred translation filter` plus automatic fallback. Jellyfin's native version selector chooses a voice for the current playback only; it does not change the series-wide preference or the widget.

If Jellyfin Web `index.html` is not writable or not found, the widget is skipped. Playback still works through generated files, `Preferred translation filter`, and per-voice files if that mode is enabled.

## Intro And Outro Timings

YummyKodik reads opening and ending timings from Yummy/Kodik metadata and supplies them to Jellyfin as media segments. Clients that support these segments can show intro/outro skip buttons.

Timings can transfer between voice translations of the same episode. The selected voice's own usable timings take priority. When they are missing, the plugin can borrow timings from another voice. For Yummy-backed sources, it prefers a complete timing set agreed on by a strict majority of at least two distinct voices, with corresponding boundaries within three seconds. Copies of the same voice on different providers do not count as additional votes.

If the voices disagree or there is not enough evidence for a consensus, the existing provider/voice fallback still applies; borrowed timings are not guaranteed to match every cut. The plugin uses one source's timing set rather than averaging boundaries or combining different cuts. Available timings are also saved with generated episodes for local reuse. Installing an update alone does not replace media segments already stored by Jellyfin.

## Docker Notes

Use container paths in plugin settings. A Windows path such as `D:\video\YummyKodik` only works for Windows Jellyfin. In Docker, use a path inside the container, for example:

```text
/media/yummykodik
```

Mount that path from the host, then point a Jellyfin `Shows` library at the same container path. Generated stream URLs use Jellyfin's process-local gateway, so no client-facing server URL is required in plugin settings.

## Refresh And Playback

After changing slugs, user-list settings, `Output root path`, or per-voice mode:

1. Save the plugin configuration.
2. Run the primary or second-library YummyKodik refresh under `Dashboard -> Scheduled Tasks`, matching the folder you changed.
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

Use the same verification command locally and in CI (PowerShell 7, .NET 10, and Node.js):

```powershell
pwsh -File scripts/verify.ps1 -Mode Info
pwsh -File scripts/verify.ps1
pwsh -File scripts/verify.ps1 -Mode Tests -Filter KodikStreaming -NoBuild
pwsh -File scripts/verify.ps1 -FfmpegSmoke
```

`Info` reports repository state, tool paths and versions, and the local Jellyfin installation without building. The default run builds the solution and runs all C#, browser-script, and voice-normalization tests. Use `-NoBuild` only when the existing build matches the source. The optional FFmpeg check generates a short local test video, reproduces the incorrect playlist MIME failure, then verifies probing and decoding through the corrected Kodik proxy. It needs FFmpeg/ffprobe 8 or newer and does not contact media providers. Pass `-FfmpegPath` and `-FfprobePath` to check Jellyfin's bundled executables.

Create a local release ZIP on Windows:

```powershell
.\scripts\package.ps1 -Version 2.1.0.0
```

Create a local release ZIP on Linux/macOS:

```bash
bash ./scripts/package.sh 2.1.0.0
```

This produces:

- `artifacts/YummyKodik_<version>.zip`
- `artifacts/YummyKodik_<version>.zip.md5`

## Release Flow

The release workflow runs on version tags and publishes the ZIP, MD5 checksum, GitHub Release, and `gh-pages` manifest.

After updating the project version and `.github/release-notes.md` and passing verification, publish a new, unused version tag:

```bash
version='X.Y.Z.0' # Replace with the new release version.
git tag "$version"
git push origin "$version"
```

Tags with a leading `v` also work because the workflow normalizes versions.

## Notes

- Generated playback sources use HLS. In Jellyfin Web, managed HLS playback waits for up to 60 seconds of contiguous video to be buffered before starting or recovering from a stall; shorter remaining tails need only their remaining duration. The progress overlay includes an automatic-start control.
- Intro/outro skip support depends on the Jellyfin client honoring media segments.
- If you update from an older manual install, restart Jellyfin after replacing plugin files.

## Acknowledgements

Special thanks to [`YaNesyTortiK/AnimeParsers`](https://github.com/YaNesyTortiK/AnimeParsers), which helped show that building a plugin like this was possible in the first place.

## License

<!-- commercial-license-policy -->
This project is licensed for non-commercial use under the [PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0.txt).
Commercial use, resale, paid distribution, marketplace publication, SaaS hosting, or bundling into a paid product requires separate written permission from the author.
Project names, logos, package identifiers, store listings, screenshots, and other branding assets are not licensed for use in forks or redistributed builds.
