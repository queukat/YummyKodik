# YummyKodik context for coding agents

## What refresh does today
`RefreshYummyKodikLibraryTask` builds a generated Jellyfin library under `PluginConfiguration.OutputRootPath`.

High-level flow:
1. Validate config: Yummy client id, output root, server base URL.
2. Build title keys from configured slugs and optional Yummy user list.
3. For each title key:
   - Fetch Yummy anime metadata with videos.
   - Resolve season/title layout, optionally via Shikimori GraphQL.
   - Load optional Alloha API catalog additions and build a combined Yummy video catalog.
   - Resolve series/season paths.
   - Ensure series NFO and poster.
   - Generate Yummy-backed STRM/NFO files where Alloha/CVH coverage exists.
   - Use Kodik as episode/translation supplement where Yummy coverage is incomplete.
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

Do not pre-skip Kodik in this mode, because new translations can appear without episode-count changes.

## Provider meanings
- Yummy: source of anime metadata and raw video entries.
- Alloha: preferred Yummy-backed playback provider when usable.
- CVH: second Yummy-backed playback provider and fallback.
- Kodik: supplemental provider when Yummy-backed coverage misses episodes/translations.
- Shikimori: layout helper to infer root series title and season number.

## State file goal
A `.yummykodik.refresh-state.json` file may be written inside a series root to skip work for stable titles.

Recommended schema shape:

```json
{
  "schemaVersion": 1,
  "generationContractVersion": 1,
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
4. Mode is single-file, or the skip is only a write-unchanged optimization inside a full per-voice refresh.
5. For single-file pre-Kodik skip, `coveredEpisodes` includes every episode from `1..expectedAvailableEpisodes`.
6. Every stored managed file exists and its hash matches.
7. The season folder contains no extra episode-shaped `.strm`/`.nfo` artifacts that the current cleanup rules would remove.

If any check fails, run the full title refresh and rewrite state only after successful cleanup.
