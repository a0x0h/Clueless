# Contributing to Clueless

Thanks for helping. Clueless is small on purpose: one C# file, one web UI, no SDK.

## Build

```bat
build.cmd
```

It uses the C# compiler that ships with Windows (.NET Framework 4.x) and downloads the WebView2 SDK, whisper.cpp and the Whisper models on the first run.

## Where things live

| | |
|---|---|
| `Clueless.cs` | Host: window and stealth, captions, offline mic, Claude-app bridge, session files |
| `ui/` | The interface. Edit and restart the app, no rebuild needed. Serve `ui/` locally (for example `python -m http.server -d ui`) to work on it in a browser with sample data. |
| `extension/` | Chrome extension for Meet / Teams / Zoom captions with names |
| `scripts/package.ps1` | Builds the release zip into `dist/` |

## Guidelines

- Match the surrounding style: short comments that say *why*, no new dependencies unless they pull their weight.
- Test in a real session: a call, a video, or `CLUELESS_FEED_WAV=<16 kHz mono wav>` to play a recording through the mic path.
- Keep privacy first. Nothing should leave the PC except what is sent to the Claude app on the user's request.

## Releasing

Bump the version in `Clueless.cs` (`AssemblyVersion` and friends), add a `CHANGELOG.md` entry, then push a tag:

```bash
git tag v1.0.1 && git push origin v1.0.1
```

The Release workflow builds, packs and publishes the zip.
