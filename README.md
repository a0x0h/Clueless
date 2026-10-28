<div align="center">

<img src="assets/icon-256.png" width="112" alt="Clueless logo">

# Clueless

**The seamless, open-source Cluely alternative.**
*Looks clueless. Isn't.*

An invisible copilot for interviews, meetings and calls on Windows: it listens, keeps the transcript,
sees your screen, and gets you Claude's answer before the pause gets awkward.

[![Release](https://img.shields.io/github/v/release/a0x0h/Clueless?color=FF5100&label=release)](https://github.com/a0x0h/Clueless/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/a0x0h/Clueless/total?color=FF5100)](https://github.com/a0x0h/Clueless/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/a0x0h/Clueless/build.yml?branch=main)](https://github.com/a0x0h/Clueless/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11&logoColor=white)
[![Stars](https://img.shields.io/github/stars/a0x0h/Clueless?style=social)](https://github.com/a0x0h/Clueless/stargazers)

### [⬇ Download for Windows](https://github.com/a0x0h/Clueless/releases/latest)

<img src="docs/assist.png" width="420" alt="Claude's answer in the overlay"> <img src="docs/transcript.png" width="420" alt="Selecting transcript lines to ask about">

</div>

---

## Why Clueless

| | |
|---|---|
| **Invisible** | The overlay is excluded from screen shares, recordings, screenshots and the taskbar. |
| **Hears both sides** | Your mic is transcribed offline with Whisper; everyone else comes from Windows Live Captions. |
| **Knows you** | Your name, CV and instructions are given to Claude once when a session starts, so every answer is about *you*. |
| **Sees your screen** | Each question can carry a screenshot of the screen you're on (the overlay never appears in it). |
| **Clean answers** | You get Claude's final answer with its formatting (bold, lists, tables), never its thinking steps. |
| **No accounts, no keys** | It drives the Claude desktop app you're already signed in to. Nothing else leaves your PC. |
| **Everything saved** | Each session is saved as Markdown: transcript plus every question and answer. |

## Quick start

1. Install the **Claude desktop app** and sign in.
2. Download **`Clueless-x.y.z-win-x64.zip`** from [Releases](https://github.com/a0x0h/Clueless/releases/latest), unzip it anywhere and run `Clueless.exe`. Nothing to install. (Windows SmartScreen may ask once: *More info → Run anyway*.)
3. Open **Settings → Your profile** and add your name, your CV (paste it, or *Load from file…* for `.txt`, `.md` or `.docx`) and any instructions.
4. In the Claude app, open the chat you want Clueless to use. Then press **▶ Start session**.

<details>
<summary><b>Build from source</b></summary>

Run `build.cmd`. It uses the C# compiler built into Windows (no SDK) and downloads, once:
- the WebView2 SDK (Microsoft, nuget.org),
- whisper.cpp `b5454` (github.com/ggml-org) and two English Whisper models (huggingface.co/ggerganov, 56 MB + 181 MB).

`scripts\package.ps1` builds the release zip into `dist\`.
</details>

## How it works

```
 your mic ──► voice detector ──► Whisper (local) ──┐
                                                   ├──► transcript ──► Claude desktop app ──► answer in the overlay
 speakers ──► Windows Live Captions (hidden) ──────┘        ▲                │
 Meet / Teams / Zoom captions (extension, real names) ──────┘           screenshot
```

### Sessions and the warm-up

When a session starts, Clueless sends Claude one **warm-up** message: your instructions, name and CV. After that, questions carry only the conversation, so they're short and fast.

- By default the warm-up goes into **the chat that's open in the Claude app**, and that chat is reused for the whole session.
- Clueless remembers which chat already has your profile. If you start the next session in the same chat with the same profile, the warm-up is skipped.
- Prefer a clean chat every time? Turn on **Settings → New Claude chat for every session**.
- Changed your CV mid-session? Logo menu → **Give Claude my profile again**.

### What Claude sees with each question

| Action | Conversation sent |
|---|---|
| **Assist**, **What should I say?**, **Follow-up questions**, typed questions | Every line Claude hasn't seen yet, plus the **last minute again** (at least 4 lines), *including the line still being spoken*. A question split across caption lines, or not finished yet, arrives whole. |
| **Recap** | The whole transcript. |
| Selected lines | The above, plus the selected lines quoted as the focus. |

**Assist** answers the latest question the other side asked, in your voice; with nothing being asked, it tells you what's useful right now.

## Using it

**The pill** sits at the top center of your screen. Drag it, or the panel's top bar, to move everything.

- Logo → menu: settings, saved sessions, stealth, copy transcript, give Claude your profile again, quit.
- **▶ Start session** begins listening. While live, it shows a timer. **■** stops and saves.
- **Hide / Show** collapses the panel.

**Assist tab:** Claude's answers. "Viewed screen" means a screenshot was attached.

**Transcript tab:** every sentence, labeled by speaker.
- **Click** lines to select them, **Shift+click** to select a range. A bar appears: *How should I respond?* · *Explain* · or type your own question about them.
- Hover a line and press **Ask** to ask about just that line.
- Click a speaker's name to rename them.

| Shortcut | |
|---|---|
| `Ctrl+Alt+A` | Assist |
| `Ctrl+Alt+S` | What should I say? |
| `Ctrl+Alt+H` | Show / hide everything |
| `Ctrl+Enter` | Assist (in the panel) |
| `Esc` | Clear the selection, close menus |

### Speaker names

| Situation | Labels |
|---|---|
| Google Meet / Teams / Zoom in Chrome (captions on + extension) | Real names: `Alex`, `Abi` |
| Any other app or site | `You` (your mic) vs `Others` (your speakers) |

To get real names: open `chrome://extensions`, turn on **Developer mode**, **Load unpacked** → choose the `extension` folder, and turn on captions (CC) in the meeting.

Use headphones: without them the mic also hears the speakers. Clueless drops mic sentences that echo what the speakers just said, but headphones are cleaner.

## How a question reaches Claude

1. A screenshot of the screen you're working on is taken (scaled to 1920 px wide at most).
2. The Claude window is found, even when it's closed to the tray, and the open chat is used.
3. If Claude is still answering the previous question, Clueless waits for it to finish.
4. The screenshot and text are pasted and sent; focus returns to your window and your clipboard is restored.
5. The reply streams into the panel without Claude's thinking or tool steps. When it's done, it's taken from Claude's own **Copy** button, so the formatting is exactly Claude's.

**Safety check:** keys are only sent after confirming Claude's prompt box has focus. Otherwise nothing is typed and the text stays on your clipboard.

## Saved sessions

Each session is saved as Markdown in `Documents\Clueless Sessions\` (for example `2026-10-28 14.05.md`): the timestamped transcript by speaker, and every question with Claude's answer, in time order. The file is rewritten every few seconds, so nothing is lost if the PC goes down.

## Window

- See-through: only the pill and panel take clicks; everything around them clicks through.
- **Stealth** (default on): excluded from screen capture and the taskbar.
- Per-monitor DPI aware.
- One copy at a time: starting it again brings the running copy into view.
- Flags: `--no-stealth` (to take screenshots of the app), `--dev` (DevTools).

## Troubleshooting

| Problem | Fix |
|---|---|
| Your voice doesn't show up | `%APPDATA%\Clueless\mic.log` shows the mic level, detected sentences and transcriptions. Check Windows' microphone privacy settings. |
| Accents / noisy room | Settings → **Higher accuracy for my voice** (Whisper `small.en`, about 3× slower). |
| Others are labeled as you | In Live Captions settings, leave **Include microphone audio** off. |
| Questions go to the wrong chat | Open the right chat in the Claude app before you start the session, or turn on **New Claude chat for every session**. |

## Project layout

| Path | |
|---|---|
| `Clueless.cs` | The app: window and stealth, sessions and session file, Live Captions reader, offline mic channel, meeting-caption listener (`localhost:47321`), Claude-app bridge. |
| `ui/` | The interface (HTML/CSS/JS). Edit and restart; no rebuild needed. Serving `ui/` from a local web server shows it with sample data. |
| `extension/` | Chrome extension for Meet, Teams and Zoom captions with names. |
| `build.cmd` | One-step build, no SDK needed. |
| `scripts/package.ps1` | Packs the release zip. |
| `assets/` | Logo and app icon. |

Contributions are welcome, see [CONTRIBUTING.md](CONTRIBUTING.md). Changes are listed in [CHANGELOG.md](CHANGELOG.md).

Settings live in `%APPDATA%\Clueless\` (carried over from the app's former name, *Caption → Claude*).

## Responsible use

Clueless is a note-taking and thinking aid. Recording or transcribing people may require their consent where you live, and many interviews, exams and meetings have rules about assistance. Follow them.

## License

[MIT](LICENSE)
