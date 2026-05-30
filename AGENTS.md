# AGENTS.md — YummyKodik

## Project context
YummyKodik is a Jellyfin plugin targeting .NET 9 and Jellyfin 10.11. It generates a TV-show-like STRM/NFO library from YummyAnime metadata and streams episodes through Jellyfin using Alloha, CVH, and Kodik-backed providers.

## Project parameters
- Repository root: `C:\Users\User\RiderProjects\YummyKodik`
- Solution: `C:\Users\User\RiderProjects\YummyKodik\YummyKodik.sln`
- Plugin project: `C:\Users\User\RiderProjects\YummyKodik\YummyKodik\YummyKodik.csproj`
- Regression test project: `C:\Users\User\RiderProjects\YummyKodik\YummyKodik.Tests\YummyKodik.Tests.csproj`
- Jellyfin Windows service name: `Jellyfin`
- Local Jellyfin plugin test folder: `C:\ProgramData\Jellyfin\Server\plugins\YummyKodik_1.0.0.0`
- Preferred local publish staging folder: `C:\Users\User\RiderProjects\YummyKodik\publish\YummyKodik_1.0.0.0`
- Do not create or keep deployment backup files (`*.bak-*`, backup zip copies, or backup build folders). If rollback is needed, rebuild or use git.

## Working path journal
By default, when an agent investigates or uses important project/runtime paths, update this section with the stable paths that matter for future work. Keep it short: source roots, generated output roots, deployment folders, service names, and unusual tool paths.

- `AGENTS.md`, `docs\YUMMYKODIK_CONTEXT.md`, `docs\GLOSSARY.md`: required agent context before coding.
- `YummyKodik\Tasks\RefreshYummyKodikLibraryTask.cs`: scheduled task orchestrator.
- `YummyKodik\Tasks\Refresh\`: extracted refresh services and models.
- `YummyKodik\Tasks\RefreshStateManager.cs`: refresh state schema and safe-skip checks.
- `YummyKodik\Logging\YummyKodikLogFilter.cs`, `YummyKodik\Logging\YummyKodikLogger.cs`: plugin log-level gate and wrapper loggers for `YummyKodik.*` categories.
- `YummyKodik\Configuration\PluginConfiguration.cs`, `YummyKodik\Web\config.html`: plugin settings model and Jellyfin settings page.
- `YummyKodik\PluginServiceRegistrator.cs`: DI, named HTTP clients, hosted services, and logging filter registration.
- `YummyKodik.Tests\Program.cs`: console-style regression runner.
- `C:\ProgramData\Jellyfin\Server\plugins\YummyKodik_1.0.0.0`: manual Jellyfin plugin deployment target for local testing.
- `publish\YummyKodik_1.0.0.0`: local publish staging folder used before copying into Jellyfin.

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

For local Jellyfin plugin replacement, stop the `Jellyfin` service first, publish to the staging folder, then copy only the runtime files into the plugin folder:

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
