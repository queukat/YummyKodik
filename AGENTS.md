# AGENTS.md — YummyKodik

## Project context
This `jellyfin-10.11` branch targets .NET 9 and Jellyfin 10.11. It generates a TV-show-like STRM/NFO library from YummyAnime metadata and streams episodes through Jellyfin using Alloha, CVH, and Kodik-backed providers. Release `1.2.1.0` targets Jellyfin 10.11 with current features. The default `main` branch targets Jellyfin 12 with release `2.0.0.0`.

## Project parameters
- Repository root: `C:\Users\User\RiderProjects\YummyKodik-jellyfin-10.11`
- Solution: `C:\Users\User\RiderProjects\YummyKodik-jellyfin-10.11\YummyKodik.sln`
- Plugin project: `C:\Users\User\RiderProjects\YummyKodik-jellyfin-10.11\YummyKodik\YummyKodik.csproj`
- Regression test project: `C:\Users\User\RiderProjects\YummyKodik-jellyfin-10.11\YummyKodik.Tests\YummyKodik.Tests.csproj`
- Jellyfin Windows service name: `Jellyfin`
- Local Jellyfin plugin test folder: `C:\ProgramData\Jellyfin\Server\plugins\YummyKodik_1.2.0.0`
- Preferred local publish staging folder: `C:\Users\User\RiderProjects\YummyKodik\publish\YummyKodik_1.0.0.0`
- Do not create or keep deployment backup files (`*.bak-*`, backup zip copies, or backup build folders). If rollback is needed, rebuild or use git.

## Working path journal
By default, when an agent investigates or uses important project/runtime paths, update this section with the stable paths that matter for future work. Keep it short: source roots, generated output roots, deployment folders, service names, and unusual tool paths.

- `AGENTS.md`, `docs\YUMMYKODIK_CONTEXT.md`, `docs\GLOSSARY.md`: required agent context before coding.
- `YummyKodik\Tasks\RefreshYummyKodikLibraryTask.cs`: scheduled task orchestrator.
- `YummyKodik\Tasks\Refresh\`: extracted refresh services and models.
- `YummyKodik\Tasks\Refresh\RefreshPerformanceMetrics.cs`, `YummyKodik\Tasks\Refresh\RefreshRunMetrics.cs`: opt-in per-title and run-level refresh summaries, including phase durations, skip reasons, Kodik HTTP/cache counts, and file proof/change counts.
- `YummyKodik\Tasks\RefreshStateManager.cs`, `YummyKodik\Tasks\Refresh\RefreshStateService.cs`: refresh state schema, quality-aware fingerprints, and layered safe skip; per-voice runs normally fetch current Kodik metadata, while a preferred quality above Kodik's known 720p ceiling may skip that lookup for up to 24 hours when prior validation and managed files remain proven.
- `YummyKodik\Tasks\Refresh\StaleReleaseCleanupService.cs`: opt-in, state-proven cleanup for releases removed from a successfully fetched Yummy user list; manual slugs and unproven filesystem content must be retained.
- `YummyKodik\Logging\YummyKodikLogFilter.cs`, `YummyKodik\Logging\YummyKodikLogger.cs`: plugin log-level gate and wrapper loggers for `YummyKodik.*` categories.
- `YummyKodik\Configuration\PluginConfiguration.cs`, `YummyKodik\Web\config.html`: plugin settings model and Jellyfin settings page.
- `YummyKodik\PluginServiceRegistrator.cs`: DI, named HTTP clients, hosted services, and logging filter registration.
- `YummyKodik\Media\InternalJellyfinUrlProvider.cs`: process-local Jellyfin gateway origin used by generated STRM files and runtime media sources; client-facing network addresses are intentionally not configurable.
- `YummyKodik\Media\YummyKodikMediaSourceProvider.cs`, `YummyKodik\Media\MediaRunTimePolicy.cs`: playback resolves an arbitrary linked child back to the merged primary before creating media sources; missing/short runtimes are backfilled conservatively, while an exact selected-provider runtime may correct only that primary item and its NFO when the mismatch exceeds two seconds.
- `YummyKodik\Util\NfoBuilder.cs`, `YummyKodik\Tasks\Refresh\EpisodeArtifactWriter.cs`, `YummyKodik\Tasks\Refresh\EpisodeRuntimeBackfillService.cs`, `YummyKodik\Yummy\YummyEpisodeRuntimeResolver.cs`, `YummyKodik\Tasks\Refresh\KodikSupplementService.cs`: generated episode NFOs carry minute and exact-second runtimes before playback; missing voice runtimes inherit a sibling episode version, backfilled NFO hashes are reconciled into refresh state, Yummy runtimes use cross-provider metadata fallback, and missing Kodik runtimes are probed once and then reused from NFO.
- `YummyKodik\Alloha\AllohaPlaybackService.cs`, `YummyKodik\Alloha\AllohaWebSocketStreamTokenResolver.cs`, `YummyKodik\Alloha\AllohaHeadlessStreamTokenResolver.cs`: Alloha browserless playback, master manifest download, dynamic `Accepts-Controls` token resolution, proxy resource refresh, and session-scoped two-minute segment prefetch.
- `YummyKodik\Kodik\KodikPlaybackService.cs`, `YummyKodik\Kodik\KodikPlaybackService.Prefetch.cs`: session-scoped Kodik HLS proxy, immediate first bytes and two background workers fetching up to 120 seconds ahead. Foreground requests join pending segment downloads; only complete resources enter the bounded cache. Manifests are rewritten before delivery; retries stop before any downstream write, with 30-second idle and two-minute total body limits.
- `YummyKodik\Web\playbackBuffer.js`: managed Web bootstrap for YummyKodik HLS sources; raises the browser buffer ceiling to 120 seconds and waits for 60 seconds of contiguous video before startup or stall recovery, with actual buffer progress and an auto-start pause control. This behavior change was explicitly requested for local playback.
- `YummyKodik\Cvh\CvhClient.cs`, `YummyKodik\Util\ProxyResponseBodyReader.cs`: CVH proxy and the shared CVH/Kodik bounded body reader. CVH sends binary bytes immediately while preserving session cookies and request headers; manifests remain fully rewritten. Metadata calls retain full-response reads. The Web buffer indicator survives recoverable HLS network errors and is removed on actual player teardown.
- `YummyKodik\Media\YummyKodikMediaSegmentProvider.cs`: Jellyfin skip-timing provider; Yummy video catalog is cached with stale fallback/backoff so temporary Yummy outages do not warning-spam.
- `YummyKodik\Versioning\YummyKodikPostRefreshMergeBarrier.cs`: post-refresh Jellyfin readiness barrier; waits for changed STRM episode items to settle, then runs one authoritative versions merge while event-driven merge passes are batch-suppressed.
- `YummyKodik\Api\YummyKodikStreamController.cs`, `YummyKodik\Web\seriesTranslation.js`, `YummyKodik\Web\JellyfinWebSeriesTranslationBootstrapHostedService.cs`, `YummyKodik\Versioning\YummyKodikEpisodeVersionsMergeHostedService.cs`: the widget voice catalog is the normalized union of managed-version and provider voices; injection targets only the active visible Jellyfin details page, uses per-build/static and per-request/API cache keys, and retries a managed partial catalog four times with bounded backoff; explicit widget and native Version-dropdown choices share the canonical preference API and become the series-wide primary so Jellyfin Next/autoplay advances by episode in that voice. `scripts\test-series-translation.cjs` covers the native selection bridge, hidden SPA pages, and ordered saves.
- `.yummykodik.refresh-state.json`: generated refresh state now also stores per-STRM media segments copied from the best available OP/ED timings for the episode, so Jellyfin segment generation can read local timings before hitting Yummy.
- `YummyKodik.Tests\Program.cs`: console-style regression runner.
- `YummyKodik\Util\NfoBuilder.cs`, `YummyKodik.Tests\NfoEncodingTests.cs`: UTF-8 XML declaration must match generated UTF-8 bytes; test parsing bytes, not only strings. Version merging uses host `ILibraryManager.UpdateItemsAsync` with `None` for links only, reloading complete current group records before writes; the Video metadata wrapper recursively saves local alternates. Jellyfin 10.11 uses string PrimaryVersionId and path-based Manual linked children; ungrouped queries include all versions.
- `C:\ProgramData\Jellyfin\Server\plugins\YummyKodik_1.2.0.0`: current local Jellyfin 12 test target; the DLL may carry a newer local test version than the folder/meta version.
- `publish\YummyKodik_1.0.0.0`: local publish staging folder used before copying into Jellyfin.
- `scripts\Deploy-LocalJellyfinPlugin.ps1`: standard self-elevating local deployment script; selects the highest installed `YummyKodik_<version>` directory, stops Jellyfin, replaces only the four runtime files from staging, hash-verifies them, and starts the service without creating backups.

## Hard constraints
- Do not add new UI settings and do not change `PluginConfiguration` unless the task explicitly says so.
- Do not change generated STRM URL formats, Jellyfin endpoints, the `Series/Season XX` layout, or playback behavior.
- Do not store provider tokens, cookies, bearer tokens, or raw secret-bearing URLs in generated state files.
- Preserve current single-file and per-voice mode semantics.
- Prefer safe behavior over aggressive skipping. If local state is absent, corrupt, incomplete, stale, or ambiguous, run the full refresh for that title.
- Keep external API pressure conservative. The default parallelism for refresh must remain `2` unless explicitly changed.

## Build and test commands
Use these commands from the repository root:

```powershell
dotnet run --project .\YummyKodik.Tests\YummyKodik.Tests.csproj -c Release
dotnet build .\YummyKodik.sln -c Release
```

The tests are currently a console-style regression runner, not xUnit/NUnit. Many tests use reflection to reach internal/private helpers.

The local service currently runs Jellyfin 12: do not deploy this branch to it. For replacement on a Jellyfin 10.11 host, stop the `Jellyfin` service first, publish to the staging folder, then copy only the runtime files into the plugin folder:

```powershell
dotnet publish .\YummyKodik\YummyKodik.csproj -c Release -o .\publish\YummyKodik_1.0.0.0
Copy-Item -LiteralPath @(
  '.\publish\YummyKodik_1.0.0.0\YummyKodik.dll',
  '.\publish\YummyKodik_1.0.0.0\YummyKodik.deps.json',
  '.\publish\YummyKodik_1.0.0.0\YummyKodik.pdb',
  '.\publish\YummyKodik_1.0.0.0\HtmlAgilityPack.dll'
) -Destination 'C:\ProgramData\Jellyfin\Server\plugins\YummyKodik_1.0.0.0' -Force
```

Do not overwrite `AllohaApiToken.txt` or `meta.json` during local replacement. `logo.png`/`logo.svg` rarely need copying; `logo.svg` may produce Windows access-denied noise even when already identical. Verify deployment by comparing SHA256 hashes and checking `YummyKodik.dll` assembly version.

## Coding conventions
- C# nullable annotations are enabled. Keep new code nullable-safe.
- Use `IHttpClientFactory` named clients instead of `new HttpClient()` in plugin services/tasks.
- Use atomic writes for generated files and state files.
- Use `ConfigureAwait(false)` in async library/plugin code where the surrounding code already does.
- Avoid broad catch blocks unless the current code path intentionally degrades to a safe fallback.
- Keep logs actionable and prefix refresh logs with `[YummyKodik]` where applicable.
- Plugin log noise is controlled by `PluginConfiguration.MinimumLogLevel`, default `Warning`.
- Do not rely only on Jellyfin/Microsoft `LoggerFilterOptions` for plugin log suppression; Jellyfin may still emit plugin `INF` logs. Use `YummyKodikLogger` wrappers as the primary gate before messages reach the host logger.
- Register the log filter as a category-specific rule for `YummyKodik`, not as a broad provider/category predicate. This is only a secondary defense; the wrapper logger is the important part.
- Never register `YummyKodikLogger<>` as global `ILogger<>` in DI. It creates a circular dependency for host services such as `ApplicationLifetime` and prevents Jellyfin from starting.
- `YummyKodikLogFilter.ConfigurationProvider` is assigned in `Plugin` construction so tests can exercise the filter without loading Jellyfin runtime assemblies.

## Refresh-specific invariants
- Only one refresh run should perform work at a time. A second run should log and exit quickly.
- Parallel refresh workers may process different titles, but file operations for the same `seriesRoot` must be serialized.
- State-based skip is allowed only when the generated local files can be proven to match the current inputs.
- Per-voice mode must not pre-skip Kodik lookup at 720p or below because new translation files may appear without episode-count changes. Above Kodik's known 720p ceiling, a verified unchanged state may defer the metadata lookup only until the 24-hour deep-validation interval expires.
- Per-voice mode may skip deep Kodik link validation only after a current catalog lookup matches the stored canonical signature, managed hashes and cleanup scope match, and the last complete deep validation is less than 24 hours old. An incomplete link pass must not refresh that timestamp. The high-quality pre-Kodik fingerprint must include normalized episode/voice availability from Yummy player-id 4 entries so newly advertised Kodik-only coverage forces an immediate lookup.
- Preferred quality is part of the refresh fingerprint but is never a provider-availability filter; keep a stream/voice and fall back to the best available lower resolution.
- Deep Kodik link validation should cover only episode/voice pairs still missing after Yummy-backed generation; equivalent Alloha/CVH voices remain preferred and Kodik-only coverage remains eligible.
- Single-file pre-Kodik skip is safe only when the state proves all currently expected episodes are already represented and all managed files still exist and match the state.
- A state file in a series root must support multiple seasons; do not overwrite season 1 state when refreshing season 2.

## Risky files
- `YummyKodik/Tasks/RefreshYummyKodikLibraryTask.cs`: main refresh flow; currently large and mostly private helpers.
- `YummyKodik/Tasks/SeasonDirectoryMaintenance.cs`: legacy season folder/file migrations; must be protected by series-root lock.
- `YummyKodik/Tasks/EpisodeArtifactMaintenance.cs`: cleanup rules for episode-shaped STRM/NFO artifacts.
- `YummyKodik/Shikimori/ShikimoriGraphQlClient.cs`: current cache must be made thread-safe before refresh parallelism.
- `YummyKodik/Kodik/KodikClient.cs`: shared client has mutable decode cache; make it safe before sharing under parallel workers.
- `YummyKodik/Kodik/KodikTitleResolver.cs`: currently creates its own `HttpClient`; route through DI/named Kodik HTTP client or KodikClient.
- `YummyKodik/Logging/YummyKodikLogFilter.cs`, `YummyKodik/Logging/YummyKodikLogger.cs`: a wrong registration/wrapper shape can silently fail to suppress noisy `Information` logs in Jellyfin.
- `YummyKodik/Web/config.html`: settings page must load/save any new `PluginConfiguration` property explicitly.

## Review checklist before finishing
- No new `new HttpClient()` in refresh-related code.
- No secrets written into `.yummykodik.refresh-state.json`.
- State file write is atomic.
- Corrupt/missing state causes full refresh, not failure.
- Progress reporting is safe under parallel workers.
- Default plugin logs suppress `Information` for `YummyKodik.Plugin` and `YummyKodik.*` categories; tests should cover both a real `LoggerFactory` rule and the `YummyKodikLogger` wrapper path.
- Tests cover state skip, stale extra artifacts, parallelism limit, thread-safe Shikimori cache, and run gate.

## Release notes
- Keep public release notes and changelogs in English, consistent with the README and previous releases, regardless of the conversation language.
- `.github/release-notes.md` is the single source for the Jellyfin changelog and GitHub release notes.
- Write only user-visible changes in plain language. Omit implementation details, internal counters, test counts, benchmark reports and engineering disclaimers. State required Jellyfin compatibility.
