# AGENTS.md — YummyKodik

## Project context
YummyKodik is a Jellyfin plugin targeting .NET 9 and Jellyfin 10.11. It generates a TV-show-like STRM/NFO library from YummyAnime metadata and streams episodes through Jellyfin using Alloha, CVH, and Kodik-backed providers.

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

## Coding conventions
- C# nullable annotations are enabled. Keep new code nullable-safe.
- Use `IHttpClientFactory` named clients instead of `new HttpClient()` in plugin services/tasks.
- Use atomic writes for generated files and state files.
- Use `ConfigureAwait(false)` in async library/plugin code where the surrounding code already does.
- Avoid broad catch blocks unless the current code path intentionally degrades to a safe fallback.
- Keep logs actionable and prefix refresh logs with `[YummyKodik]` where applicable.

## Refresh-specific invariants
- Only one refresh run should perform work at a time. A second run should log and exit quickly.
- Parallel refresh workers may process different titles, but file operations for the same `seriesRoot` must be serialized.
- State-based skip is allowed only when the generated local files can be proven to match the current inputs.
- Per-voice mode must not pre-skip Kodik lookup because new translation files may appear even when episode count is unchanged.
- Single-file pre-Kodik skip is safe only when the state proves all currently expected episodes are already represented and all managed files still exist and match the state.
- A state file in a series root must support multiple seasons; do not overwrite season 1 state when refreshing season 2.

## Risky files
- `YummyKodik/Tasks/RefreshYummyKodikLibraryTask.cs`: main refresh flow; currently large and mostly private helpers.
- `YummyKodik/Tasks/SeasonDirectoryMaintenance.cs`: legacy season folder/file migrations; must be protected by series-root lock.
- `YummyKodik/Tasks/EpisodeArtifactMaintenance.cs`: cleanup rules for episode-shaped STRM/NFO artifacts.
- `YummyKodik/Shikimori/ShikimoriGraphQlClient.cs`: current cache must be made thread-safe before refresh parallelism.
- `YummyKodik/Kodik/KodikClient.cs`: shared client has mutable decode cache; make it safe before sharing under parallel workers.
- `YummyKodik/Kodik/KodikTitleResolver.cs`: currently creates its own `HttpClient`; route through DI/named Kodik HTTP client or KodikClient.

## Review checklist before finishing
- No new `new HttpClient()` in refresh-related code.
- No secrets written into `.yummykodik.refresh-state.json`.
- State file write is atomic.
- Corrupt/missing state causes full refresh, not failure.
- Progress reporting is safe under parallel workers.
- Tests cover state skip, stale extra artifacts, parallelism limit, thread-safe Shikimori cache, and run gate.
