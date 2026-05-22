# YummyKodik architecture refactor plan

## Recommended order

1. Apply follow-up safety/performance fixes for refresh acceleration.
2. Refactor refresh architecture only.
3. After refresh is stable, consider a separate playback/controller refactor.

## Do not combine in one task

- Refresh task extraction.
- Stream controller extraction.
- Alloha playback service rewrite.
- Test runner migration to xUnit/NUnit.
- Public config/UI changes.

## Why

The refresh task already changed concurrency and state semantics. Large unrelated refactors would make regressions hard to isolate.
