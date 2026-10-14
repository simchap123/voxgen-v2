# VoxGen v2 — native Windows dictation utility

A tiny, free tray utility: hold a hotkey, talk, release, polished text is pasted into the active window. A ground-up rebuild of v1 (Electron) as a native Windows app. **Fully local since v2.1.0 (2026-10-14):** the user brings their own Groq API key; no account, no database, no VoxGen backend, no payments. See `docs/decision-log.md`.

**Source of truth for product scope:** [VoxGen-v2-PRD.md](VoxGen-v2-PRD.md). Read it before changing behaviour; cite section numbers in PRs (e.g. "per §10.5"). When code and PRD diverge, the PRD wins unless the change has been agreed and the PRD updated in the same patch.

---

## Stack (desktop)

- **C# / .NET (latest LTS)**
- **WPF** for the UI — chosen over WinUI 3 deliberately for mature tray integration and simpler packaging at this size
- **Win32 via P/Invoke** for global hotkeys, active-window capture, clipboard, synthetic paste
- **DPAPI (`ProtectedData`)** for the user's Groq API key
- **`HttpClient` + `System.Text.Json`** for all HTTP/JSON (no SDKs)

### Sanctioned third-party packages

The default is zero packages. Anything not on this list **needs explicit approval before adding** — see PRD §6.2.

| Capability | Package | Why it's sanctioned |
|---|---|---|
| Microphone capture | **NAudio** | Hand-rolled WASAPI interop is the most bug-prone part of an app like this |
| Local history DB | **Microsoft.Data.Sqlite** | No zero-dep alternative; Microsoft-owned |
| Auto-update + rollback | **Velopack** | Self-rolled updaters are fragile and security-sensitive |

Notably **not** in the project: any cloud SDK, any audio encoding library (ship WAV), any UI/MVVM framework on top of WPF.

## Transcription (local, BYOK)

```
DictationController → GroqWhisperProvider
  → POST https://api.groq.com/openai/v1/audio/transcriptions  (multipart WAV, whisper-large-v3-turbo, Bearer <user key>)
  → [cleanup on] POST .../chat/completions  (llama-3.3-70b-versatile, same prompt the old backend used)
```

- Key: `Settings/ApiKeyStore` → DPAPI file `%APPDATA%\VoxGen\groq-key.bin`. Read per request, so saving a key applies instantly.
- No key → `MissingApiKeyException` → overlay "Add your Groq API key in Settings". First run opens Settings on the API key page.
- Cleanup failure falls back to the raw transcript. Upload cap 25 MB (~13 min) → `RecordingTooLongException`, WAV kept.
- Vercel (`voxgenflow.vercel.app`, VoxGen repo `website/`) is ONLY the marketing + download site now.

## History (local)

`History/HistoryStore` — `history.jsonl` (one JSON entry per line) + WAVs in `recordings/`, both under `%APPDATA%\VoxGen`. Toggles: `SaveTextHistoryLocal`, `SaveAudioHistoryLocal`. Settings → History lists, copies, plays, clears.

## Updates (one click)

`Updater/UpdateService` reads `api.github.com/repos/simchap123/voxgen-v2/releases/latest`, downloads the `VoxGen.exe` asset, renames the running exe to `.old`, moves the new one in, relaunches with `--after-update` (which waits for the singleton mutex). Background check 10 s after start → tray balloon; Settings → About → Update now. Dev builds (VoxGen.dll beside the exe) don't self-update.

**Releasing:** bump `AppVersion` in `App.xaml.cs` + `<Version>` in the csproj, commit, `git tag vX.Y.Z && git push origin vX.Y.Z`. `.github/workflows/release.yml` builds, tests and attaches `VoxGen.exe`. Tags must parse as a version (`v2.1.0`, `v2.1.1-preview`).

---

## Suggested folder structure (PRD §6.5)

```
/Core            /Transcription   /Auth        /Overlay
/Audio           /AICleanup       /License     /Tray
/Hotkeys         /Settings        /UI          /Installer
/WindowDetection /History         /Logging     /Updater
/Clipboard       /Backend         /Tests
```

`/Auth`, `/Backend`, `/License` were removed in v2.1.0 (fully local). Current extra folders: `/History`, `/Updater`.

---

## Hard invariants — do not break these

1. **The API key never leaves the machine except to Groq.** DPAPI-encrypted file only — never in `settings.json`, never logged.
2. **All user content is local-only.** Transcripts, audio, settings — `%APPDATA%\VoxGen`. Nothing is sent anywhere except the audio/text to Groq for the one request.
3. **Never lose a transcript.** Any failure after recording must preserve the user's words. Don't delete the temp WAV on transcription failure until the user dismisses the error. On paste failure, leave the final text on the clipboard and show a small message (PRD §5.3, §8.4, §8.8, §13).
4. **Settings reliability is non-negotiable — PRD §10.** Read the whole section before touching anything in `/Settings`. The shortlist:
   - One single source of truth; strongly-typed model; no string keys at call sites
   - UI reflects **persisted** state, never guessed defaults
   - Change order: in-memory → persist → verify write → apply to runtime
   - On write failure: roll the UI back and surface an error
   - Settings must survive restart, crash, sleep/wake, app update, installer repair
   - Every toggle has an automated test
5. **Pre-warm the mic.** Keep the selected device initialised in the background so a hotkey press starts buffering immediately. Opening the device on keypress clips the first word and the <100 ms target is unreachable (PRD §8.4, §14.1).
6. **Capture the foreground window before any VoxGen UI appears.** That handle is what restores focus and targets the paste (PRD §8.5).
7. **Mic must self-heal.** USB/webcam mics get invalidated (`0x88890004`) on unplug, sleep/wake, default-device switch. `NAudioCapture` flags `_needsReinit` and rebuilds on the next `Start()`, retrying once. Don't regress this.
8. **Sign the executable** when a certificate is available (PRD §14.3).
9. **Free.** No trial, plans, licensing or payment code.
10. **Branding is VoxGen everywhere** — installer name, tray icon, exe metadata, publisher. No framework defaults visible to the user (PRD §15).

---

## UI / brand

v1's visual language (see `C:\Users\spent\projects\VoxGen V1\`) is the brand reference:

- **Palette:** sage green primary (`hsl(152 32% 42%)`), cream background (`hsl(40 33% 96%)`), charcoal text. Light theme is the default; v2 should preserve the warm-light identity rather than defaulting to the dark+glow look most desktop utilities reach for.
- **Type:** v1 uses DM Sans. WPF equivalent: ship DM Sans as a bundled font resource. Avoid Segoe defaults.
- **Settings window:** single window, calm density, generous vertical rhythm. v1's settings page is the layout reference — sidebar + sectioned content, not a tabbed property sheet.
- **Tray overlay:** unobtrusive bottom-of-screen pill. v1's `OverlayShell.tsx` has the four states to reproduce — idle (tiny click-through waveform bars), recording (translucent pill with live waveform), error (red pill that opens settings on click), trial-expired (compact persistent card). The idle pill is click-through; the active pill is interactive.
- **Don't import v1's CSS.** It's a reference for *what the user sees*, not source to port. Rebuild idiomatically in WPF (resource dictionaries, styles, control templates).

The frontend-design skill's "AI slop test" applies to the WPF UI too: no glowing-cyan-on-dark, no glassmorphism for its own sake, no centered-everything dashboards.

---

## Data

| Concern | Where |
|---|---|
| Settings | `%APPDATA%\VoxGen\settings.json` (System.Text.Json, PRD §11) |
| Groq API key | `%APPDATA%\VoxGen\groq-key.bin` (DPAPI, CurrentUser) |
| History | `%APPDATA%\VoxGen\history.jsonl` + `recordings\*.wav` |
| Failed-transcription audio | `%APPDATA%\VoxGen\temp-audio\` |

---

## Performance targets (PRD §14.1)

- Idle RAM < 150 MB
- Cold start < 2 s
- Recording start < 100 ms (requires the mic pre-warm)
- End of speech → pasted text: 1–3 s
- Installed size < 100 MB, target < 50 MB

If a change pushes any of these the wrong way, flag it explicitly in the PR.

---

## Out of scope for v1 (PRD §2, §18, §20)

Don't add these. They're either deferred or roadmap:

- AI Prompt Mode (voice → generated content)
- Local / on-device AI models
- Team accounts, enterprise admin, cloud sync, cloud audio storage
- Prompt marketplace, mobile app, browser extension
- macOS

If a request implies any of these, surface the conflict before writing code.

---

## Build & run

The solution is scaffolded: `VoxGen.sln` → `src\VoxGen.Desktop` (WPF tray app, `net10.0-windows`) + `tests\VoxGen.Desktop.Tests` (xUnit).

```
dotnet build VoxGen.sln
dotnet test  VoxGen.sln                      # 71 tests as of 2026-10-14
dotnet run --project src\VoxGen.Desktop      # launches the tray app (no main window)
```

**SDK:** on the SimchaPentelnik machine the .NET 10 SDK is user-local at `%LOCALAPPDATA%\dotnet` (not on PATH): `$env:DOTNET_ROOT="$env:LOCALAPPDATA\dotnet"; $env:Path="$env:LOCALAPPDATA\dotnet;$env:Path"`. Logs: `%APPDATA%\VoxGen\logs`.

