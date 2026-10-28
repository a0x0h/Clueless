# Changelog

## 1.0.1 — 2026-10-29

### Changed
- New logo and app icon.
- README: *How it works* is now diagrams (the pipeline, the session warm-up, what Claude sees, and how a question reaches Claude).

## 1.0.0 — 2026-10-28

First release as **Clueless** (formerly *Caption → Claude*).

### Added
- **Profile warm-up:** your name, CV (paste, or load `.txt` / `.md` / `.docx`) and instructions are given to Claude once per session. The chat that already has your profile is remembered and reused.
- **Multi-select in the transcript:** click lines, Shift+click for a range, then ask about them together.
- **Rich answers:** headings, nested lists, tables, quotes and code, exactly as Claude formats them.
- App icon, version info, release packaging and CI.

### Changed
- Questions go to the chat that's open in the Claude app (also when Claude is closed to the tray). A new chat per session is now an option.
- Every question carries the last minute of conversation, including the line still being spoken, so split or unfinished questions arrive whole. **Recap** sends the whole transcript.
- **Assist** answers the other side's latest question in your voice.
- Replies show only Claude's answer, not its thinking or tool steps; the final text comes from Claude's own Copy button.
- Sends wait until Claude has finished its previous reply.

### Fixed
- The hidden Live Captions window no longer flickers into view.
