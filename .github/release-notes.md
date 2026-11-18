- Fixed HLS frame-rate metadata that could expose the MPEG-TS 90 kHz clock as 90,000 FPS and produce a huge AMD AMF keyframe interval.
- Use the selected local HLS media rendition for playback instead of asking FFmpeg to probe all variants in the master playlist.
- Preserve legitimate probed FPS or an explicit HLS FRAME-RATE for that rendition; unknown frame rates remain unknown.
- Added regression tests for pinned HLS sessions, frame-rate validation, and multiple renditions.

Requires Jellyfin 12.
