# YummyKodik context for coding agents

## What refresh does today
`RefreshYummyKodikLibraryTask` builds a generated Jellyfin library under `PluginConfiguration.OutputRootPath`.

High-level flow:
1. Validate config: Yummy client id and output root; resolve Jellyfin's process-local playback gateway.
2. Build title keys from configured slugs and optional Yummy user list. When explicitly enabled, a successfully fetched complete list may remove stale plugin-managed releases; manual slugs and ambiguous/modified directories are retained.
3. For each title key:
   - Fetch Yummy anime metadata with videos.
   - Resolve season/title layout, optionally via Shikimori GraphQL.
   - Load optional Alloha API catalog additions and build a combined Yummy video catalog.
   - Resolve series/season paths.
   - Ensure series NFO and poster.
   - In per-voice mode, fetch the lightweight Kodik catalog before writing episode artifacts. When the Yummy fingerprint, Kodik catalog signature, managed hashes, and validation age all match, skip the expensive per-episode link pass and all file generation.
   - Otherwise generate Yummy-backed STRM/NFO files where Alloha/CVH coverage exists and use Kodik as an episode/translation supplement.
   - Cleanup stale episode-shaped STRM/NFO artifacts.

## Generation modes

### Single-file mode
`CreateStrmPerVoiceTranslation = false`.

Generated files look like:

```text
Series Title [shikimori-123]/Season 01/S01E01.strm
Series Title [shikimori-123]/Season 01/S01E01.nfo
```

Playback can still choose a voice at runtime through saved user preferences and provider fallback. Refresh should not create one file per voice.

### Per-voice mode
`CreateStrmPerVoiceTranslation = true`.

Generated files look like:

```text
S01E01 - AniLibria.strm
S01E01 - AniLibria.nfo
S01E01 - Dream Cast.strm
S01E01 - Dream Cast.nfo
```

Normally do not pre-skip Kodik metadata in this mode, because new translations can appear without episode-count changes. The bounded exception is a high-quality fast path: when the requested quality is above Kodik's known 720p ceiling and verified fallback state is less than 24 hours old, unchanged Yummy inputs and managed files may skip the Kodik lookup entirely. Yummy-advertised Kodik episode/voice availability is part of that input fingerprint, so a newly listed Kodik-only translation immediately forces a fresh catalog lookup without waiting for the 24-hour validation interval. At 720p or below, the catalog is still checked every refresh. A post-metadata deep skip remains available when the current stable Kodik catalog signature and every local safety check match.

Runtime sibling backfill reports only the NFO files it actually rewrites and atomically reconciles their managed state hashes. This keeps the next safe refresh from repeating title work solely because the plugin enriched its own NFO after state capture.

## Provider meanings
- Yummy: source of anime metadata and raw video entries.
- Alloha: preferred Yummy-backed playback provider when usable.
- CVH: second Yummy-backed playback provider and fallback.
- Kodik: supplemental provider when Yummy-backed coverage misses episodes/translations.
- Shikimori: layout helper to infer root series title and season number.

## Playback gateway model
Generated STRM files always call the Jellyfin API origin reachable from the Jellyfin server process and its encoder. Jellyfin is therefore the stable playback gateway: it resolves or proxies provider media while clients consume Jellyfin playback URLs. The client-facing/public server address is not a generated-STRM setting and must not be persisted as a provider endpoint or as a secret-bearing URL.

## Refresh convergence after filesystem writes
A managed refresh suppresses event-driven version merges while it writes artifacts. It then waits until Jellyfin has materialized changed STRM/NFO items and removed deleted items, applies usable NFO runtimes to library items, and runs one authoritative versions merge. The readiness wait is bounded; a timeout logs a warning and still performs a best-effort merge.

Version-link changes use the host library manager's batch persistence with `ItemUpdateType.None`, grouped by actual parent. Calling `Video.UpdateToRepositoryAsync` for this purpose also propagates title metadata to local alternate versions and can cause redundant NFO writes and refresh work. A potentially changed group is reloaded with complete metadata directly through a repository-backed item query before mutation. Removed, moved or incompletely indexed members defer that group; an unchanged group performs no writes. Host persistence and item-update events remain active.

Native `LocalAlternateVersions` remain intact and are excluded from the desired explicit `LinkedAlternateVersions`. Jellyfin deduplicates these relationships in favor of local versions; trying to add a local child again as explicitly linked causes a rewrite on every pass. An entirely local group can have an empty explicit-link list and still needs its ownership and primary-version checks.

All NFO serializers declare UTF-8, matching the generated file writer. XML written through a plain `StringBuilder` writer declares UTF-16 regardless of the XML settings and must not be used for these UTF-8 files.

## Runtime metadata
Episode NFOs carry both Jellyfin-compatible minute runtime and exact-second runtime when known. Runtime comes from Yummy/provider metadata, a conservative Kodik manifest probe, or a sibling-version fallback. Refresh-time backfill remains conservative: it fills only missing or implausibly short metadata. At playback, an exact runtime resolved from the selected provider is authoritative for the effective merged primary and may correct that item and its NFO when the difference exceeds two seconds. It is never copied to sibling voices because different translations may have different cuts.

## Voice catalog and version-selection contract
The series widget must expose the canonical union of voices actually available to the managed series, including generated Jellyfin episode versions, not only the translation list of whichever provider or episode happened to resolve the details request. Equivalent provider spellings are deduplicated by normalized voice name.

The merge and managed voice catalog enumerate physical episode versions with `GroupByPresentationUniqueKey = false`. Jellyfin 10.11 has no `IncludeOwnedItems` switch or implicit primary-version exclusion on those queries. Linked versions use `LinkedChildType.Manual` and a persisted `Path`; `ItemId` is only the optional resolution cache.

The saved and automatically chosen values returned to the widget must be resolved back to the actual canonical option id in that union. This lets aliases and punctuation variants such as `AniLibria`/`AniLibria.TV` or `РуАниме _ DEEP`/`РуАниме / DEEP` activate the visible option. The active option is marked with stronger weight, underline, background, and a check mark.

Jellyfin Web keeps hidden details pages alive inside its SPA. Widget injection must therefore target the connected, visible `.itemDetailsGroup` for the active page rather than the first matching node in the document. The bootstrap URL includes a per-build identifier so replacing the plugin DLL cannot leave clients on an older cached widget script. Translation catalog GETs also carry a request nonce. Translation option fields are accepted in both Jellyfin's emitted PascalCase (`Id`/`Name`/`Type`) and camelCase forms. A managed response that identifies the series or current choice but contains no voices is incomplete: the widget is withheld and retried at most four times with exponential backoff instead of freezing as an `Auto`-only list.

Choosing a widget voice or explicitly changing Jellyfin Web's native `Version` dropdown saves the series preference through the same `setTranslation` API. The native bridge only accepts user changes on the visible active details page and an unambiguous voice from the managed canonical catalog. Initial rendering and programmatic selection do not save a preference. Widget and native writes are serialized so rapid choices retain their order. After a choice is saved, the merge makes the matching version primary for every episode where that voice exists; unmatched episodes use the normal safe fallback. The primary items form the episode sequence, so `Next` and autoplay advance to the next episode in the selected voice instead of a sibling version of the current episode. `Auto` clears the explicit selection and restores normal preference/filter fallback.

An explicit choice queues the validated series directory as priority work, including when the same voice was already stored. The existing merge worker applies these groups before continuing its bulk pass, including groups it visited before the choice changed. This preserves the single merge owner and refresh barrier while avoiding a wait behind unrelated titles. The selection response still means saved/queued; the short current group and library enumeration may finish before the priority work starts.

Promoting an alternate clears both `PrimaryVersionId` and the scanner's `OwnerId`. Jellyfin 10.11 retains owned versions in ungrouped queries; clearing ownership still ensures a promoted version is independent. A leftover owner also invalidates the merge's no-op check so existing affected groups repair themselves. Native selection updates the widget without a success dialog that could interrupt playback.

Jellyfin can still request the media source of a linked child even after `PrimaryVersionId` is corrected. The media-source provider first maps that child back to the merged primary. An explicit `voice`/`tr` at the gateway remains authoritative for that request; merely fetching a stream does not change the series preference. The authenticated UI selection owns persistence, and the queued merge owns the primary episode sequence.

The Kodik HLS proxy forwards binary resource chunks as they arrive, including a flush to the player, rather than waiting for a complete segment. This prevents slow CDN bodies from repeatedly exceeding the player's first-byte deadline. Manifest bodies, including those identified by their content, are fully rewritten before delivery. Only complete resources up to 16 MiB enter the existing bounded cache. Transient transport failures, timeouts, HTTP 404/408, and 5xx responses retain at most three attempts before the first downstream write; a partial response is aborted rather than replayed. Upstream bodies have a 30-second idle limit and a two-minute total limit. Slow provider throughput can still cause buffering, but no longer forces a fresh download merely because the proxy withheld the first bytes.

CVH uses the same bounded `ProxyResponseBodyReader` for media proxy responses, with immediate binary writes and flushes, preserved cookies/request headers, and complete manifest rewriting. CVH metadata calls retain full-response reads; this change introduces no CVH cache or retry policy. An abandoned client response is aborted without an upstream-error log, while a failed partial upstream response is aborted rather than presented as a complete segment.

Kodik media playlists also schedule up to 120 seconds (at most 48 segments) of lookahead with two independent background workers. A player request joins an in-flight segment by receiving its retained first chunk immediately and the remaining bytes on completion; it does not start a second upstream body download. Canceling that consumer leaves the shared transfer available for another request. Seeking cancels obsolete transfers without attached consumers. Foreground requests bypass full background capacity, and unknown-length or larger-than-16-MiB bodies use direct streaming. Complete binary cache entries live for three minutes within the existing 192-MiB total limit; manifests retain their one-minute TTL. After 30 seconds without demand or an attached consumer, workers finish their current downloads and stop scheduling more.

The managed Jellyfin Web bootstrap also loads `playbackBuffer.js`. For YummyKodik HLS sources, it raises the player's buffer ceiling to 120 seconds (Jellyfin's high-bitrate browser profile may otherwise use only six seconds) and pauses startup, seeking, or recovery from an empty buffer until 60 seconds of contiguous video are ready. Short remaining tails need only the remaining duration. An overlay reports actual downloaded seconds, with a control to suspend automatic startup; manual pause remains respected. The overlay and pending automatic startup survive network errors that Jellyfin retries, including errors HLS labels fatal; actual player destroy/detach events own cleanup. While the source is stalled the overlay reports that it is waiting for data. Other HLS sources retain their configuration. This changes local playback behavior at the user's explicit request and adds no plugin configuration setting.

Preferred quality is a preference rather than an availability filter for every provider. It participates in the refresh fingerprint, but a stream or voice is retained when 1080p is requested and only a lower quality is available; playback selects the best provider quality it can resolve.

During a required deep pass, Kodik link validation is limited to episode/voice pairs not already represented by an equivalent Alloha/CVH voice. This keeps higher-quality Yummy-backed variants preferred without losing Kodik-only coverage or its lower-quality playback fallback.

Jellyfin's `PrimaryVersionId` is library-global rather than per-user, and Jellyfin/ffmpeg fetches generated gateway STRMs without a reliable user identity. Consequently, the latest explicit widget selection is a library-wide request mirrored across every provider key for the series; simultaneous per-user voice locks require a separate authenticated playback queue and are not supported.

## State file goal
A `.yummykodik.refresh-state.json` file may be written inside a series root to skip work for stable titles.

Recommended schema shape:

```json
{
  "schemaVersion": 1,
  "generationContractVersion": 4,
  "seasons": {
    "Season 01": {
      "seasonNumber": 1,
      "cleanKey": "example-slug",
      "fingerprint": "sha256:...",
      "mode": "single-file",
      "expectedAvailableEpisodes": 12,
      "coveredEpisodes": [1,2,3,4,5,6,7,8,9,10,11,12],
      "managedFiles": [
        { "relativePath": "Season 01/S01E01.strm", "sha256": "...", "kind": "strm" },
        { "relativePath": "Season 01/S01E01.nfo", "sha256": "...", "kind": "nfo" }
      ],
      "kodikCatalogSignature": "sha256:...",
      "kodikDeepValidatedAtUtc": "2026-08-11T00:00:00Z",
      "updatedAtUtc": "2026-05-22T00:00:00Z"
    }
  }
}
```

Do not store raw provider tokens or request tokens. If a secret-bearing value must affect the fingerprint, hash it first and store only the hash or include it only in the fingerprint input without serializing the raw value.

## Safe skip rules
A title may be skipped only when all checks pass:
1. State file is readable and schema/contract versions match.
2. The current season entry exists.
3. Current fingerprint equals stored fingerprint.
4. Single-file mode may skip before Kodik. Per-voice mode normally skips only after a current Kodik metadata lookup whose canonical signature matches the stored signature; the high-quality exception may pre-skip that lookup only while a complete deep validation is less than 24 hours old.
5. For single-file pre-Kodik skip, `coveredEpisodes` includes every episode from `1..expectedAvailableEpisodes`.
6. Every stored managed file exists and its hash matches.
7. The season folder contains no extra episode-shaped `.strm`/`.nfo` artifacts that the current cleanup rules would remove.
8. For either per-voice skip, `kodikDeepValidatedAtUtc` exists, is not in the future, and is less than 24 hours old. Pre-Kodik skip additionally requires preferred quality above 720p and a non-empty stored catalog signature. A changed preferred quality or normalized Yummy-advertised Kodik episode/voice availability also changes the main fingerprint.

If any check fails, run the full title refresh and rewrite state only after successful cleanup. A failed Kodik link validation does not receive a fresh deep-validation timestamp, so the next refresh retries it.
