- Added a second YummyAnime list with its own library folder and refresh schedule.
- Added explicit account sign-in and clearer authentication errors while preserving unsaved settings.
- Fixed Kodik and CVH playback failures with FFmpeg 8 when providers return an incorrect playlist content type.
- Improved playback startup, seeking, and selected-voice consistency in Jellyfin 12.
- Improved Alloha recovery and same-voice fallback; canceled recovery attempts can be retried immediately.
- Made the voice selector load from local library files without waiting for providers. Native version choices affect only the current playback.
- Improved refresh isolation between libraries and safe cleanup of removed releases.
- Added English settings-page text and kept long series titles available on hover.

Requires Jellyfin 12.
