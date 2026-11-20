- Allow compatible HLS video and audio streams to be remuxed without unnecessary video re-encoding, while preserving client playback preferences.
- Preserve selected HLS rendition bitrate and frame-rate metadata for accurate stream selection.
- Reduce CVH playback startup work by avoiding redundant provider resolution and retaining translation validation.
- Show a playback preparation indicator for managed episodes before HLS playback starts, including buffer progress and recovery; leave other Jellyfin media unaffected.
- Match the active Jellyfin theme for preparation and buffering overlays, including colors, typography, borders, and accents.
- Expand playback, CVH session, source-selection, and browser UI regression coverage.

Requires Jellyfin 12.
