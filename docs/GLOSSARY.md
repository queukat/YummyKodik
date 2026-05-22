# Glossary

- **Refresh**: scheduled task that regenerates the local STRM/NFO library.
- **Key / cleanKey**: normalized Yummy slug, URL-derived slug, or numeric anime id used to fetch Yummy metadata.
- **Series root**: root folder for one Jellyfin series, for example `OutputRoot/Frieren [shikimori-52991]`.
- **Season dir**: season folder under series root, for example `Season 01`.
- **Managed artifact**: `.strm`, `.nfo`, poster, or state file created/maintained by YummyKodik.
- **Episode-shaped artifact**: file matching `SxxEyy` or `SxxEyy - Voice` with `.strm`/`.nfo`; current cleanup rules treat these as generated episode artifacts.
- **Single-file mode**: one `SxxEyy.strm` per episode; voice choice happens at playback.
- **Per-voice mode**: one `SxxEyy - Voice.strm` per voice translation.
- **Yummy-backed providers**: Alloha and CVH entries present in Yummy metadata or Alloha API enrichment.
- **Kodik supplement**: Kodik lookup/generation used when Yummy-backed providers do not cover expected episodes or translations.
- **Fingerprint**: stable SHA-256 digest of inputs that affect generated paths, URLs, expected episode files, cleanup decisions, and relevant config.
- **Generation contract version**: code-level integer bumped when generated artifact semantics change in a way that should invalidate old state.
