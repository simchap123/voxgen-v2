# VoxGen v2 — native Windows dictation utility

A tiny tray utility: hold a hotkey, talk, release, polished text is pasted into the active window. A ground-up rebuild of v1 (Electron) as a native Windows app, paired with a VoxGen-managed transcription backend so users never see an API key.

**Source of truth for product scope:** [VoxGen-v2-PRD.md](VoxGen-v2-PRD.md). Read it before changing behaviour; cite section numbers in PRs (e.g. "per §10.5"). When code and PRD diverge, the PRD wins unless the change has been agreed and the PRD updated in the same patch.

---

## Stack (desktop)

- **C# / .NET (latest LTS)**
- **WPF** for the UI — chosen over WinUI 3 deliberately for mature tray integration and simpler packaging at this size
- **Win32 via P/Invoke** for global hotkeys, active-window capture, clipboard, synthetic paste
- **DPAPI (`ProtectedData`)** for the Supabase session token
- **`HttpClient` + `System.Text.Json`** for all HTTP/JSON (no SDKs, including Supabase)

### Sanctioned third-party packages

The default is zero packages. Anything not on this list **needs explicit approval before adding** — see PRD §6.2.

| Capability | Package | Why it's sanctioned |
|---|---|---|
| Microphone capture | **NAudio** | Hand-rolled WASAPI interop is the most bug-prone part of an app like this |
| Local history DB | **Microsoft.Data.Sqlite** | No zero-dep alternative; Microsoft-owned |
| Auto-update + rollback | **Velopack** | Self-rolled updaters are fragile and security-sensitive |

Notably **not** in the project: Supabase SDK (use REST via HttpClient), any audio encoding library (ship WAV), any UI/MVVM framework on top of WPF.

## Backend

A separate service (`/backend` codebase — **Vercel serverless functions; §21.1 resolved 2026-05-20**, reusing V1's `api/` at `C:\Users\spent\projects\VoxGen V1\api` as reference + V1's Supabase project & Groq key) that:
1. Verifies the user's Supabase session token
2. Validates subscription / trial / usage quota
3. Proxies WAV audio to Groq Whisper using **VoxGen-held keys**
4. Optionally applies AI cleanup
5. Returns the final text
6. Records usage metadata only — **no audio, no transcripts persisted**

The desktop app and the backend are two different hard problems and are built/tested separately, but **nothing ships to a real user until both are done and connected** (no BYOK fallback exists in v1).

### Transcription connection — how VoxGen stays connected to Groq Whisper

**The desktop NEVER connects to Groq directly** (hard invariant #1 — no key in the app). "Connected to Groq" means the desktop holds a live connection to the **VoxGen backend**, which holds the Groq key and proxies to Groq. The path:

```
DictationController → VoxGenManagedProvider → VoxGenBackendClient.TranscribeAsync
  → POST {VoxGenBackendBaseUrl}/v1/transcribe   (multipart WAV + Bearer <Supabase JWT>)
  → backend: verify JWT → quota → Groq whisper-large-v3-turbo @ api.groq.com/openai/v1
  → { final_text, raw_text, language, duration_ms, cleanup_applied }
```

**Three conditions must ALWAYS hold for transcription to reach Groq. If any fails, the app silently falls back to `StubTranscriptionProvider` or errors — and no Groq call happens:**

1. **Config is real, not placeholder.** `BackendConfig.VoxGenBackendBaseUrl`, `SupabaseUrl`, `SupabaseAnonKey` must be substituted (build-time). While any equals `REPLACE_AT_BUILD`, `App.BackendConfigured()` returns false → `WireBackend` is skipped → `WireDictation` uses the stub. Current values: Supabase `https://xsdngjfnsszulezxvsjd.supabase.co`; backend base = the V2 deployment (V1 ref `https://voxgenflow.vercel.app`); anon key from the Supabase dashboard. **The Groq key itself lives ONLY in the backend's Vercel env (`GROQ_API_KEY`) — never in `BackendConfig`, never in the desktop.**

2. **A live Supabase JWT is always available.** `VoxGenManagedProvider`'s `getAccessTokenAsync` delegate must return a valid, non-expired access token. The token manager refreshes **proactively** (before `SupabaseSession.ExpiresAtUtc`) via `SupabaseAuth.RefreshAsync`, and the provider retries once on a 401 (the delegate is expected to refresh on that retry). Tokens persist via DPAPI (`SessionTokenStore`). No token → `VoxGenBackendClient` throws `"Access token required"` *before* any network call. (As of 2026-05-20 `getToken` is still a stub returning the stored token or empty — wiring a real sign-in + refreshing token manager is the open work; see the auth gap below.)

3. **The backend is reachable at request time.** Per hard invariant #7, the **offline grace period covers license checks only — NOT transcription.** A transcription against an unreachable backend surfaces `BackendUnavailableException`; the handler must **keep the WAV** and show "can't reach VoxGen", never delete the recording (invariant #3).

**Backend contract (build `/backend` to match the client that already exists):**
- `POST /v1/transcribe` — `multipart/form-data` fields `audio` (WAV file), `language` (omit for auto), `cleanup_enabled` (`true`/`false`); header `Authorization: Bearer <Supabase JWT>`. Returns `{ final_text, raw_text, language, duration_ms, cleanup_applied }`.
- `GET /v1/license` — returns the `LicenseStatus` shape; same Bearer auth.
- **Auth = verify the Supabase JWT** (validate signature/`/auth/v1/user`), then derive the user and run V1's license/trial/quota logic keyed by user id. **Do NOT port V1's email-in-body trust model** (PRD §9.4).
- Map errors to the status codes the client already handles: 401 unauthenticated, 402 trial expired, 403 quota exceeded, 429 rate-limited (+`Retry-After`), 5xx unavailable.

**Status (2026-05-20):** desktop loop + `VoxGenBackendClient` + `VoxGenManagedProvider` + `SupabaseAuth` are built; the `/backend` `/v1/transcribe` endpoint, real `BackendConfig` values, and a sign-in/token-manager are the remaining gaps before Groq actually runs.

---

## Suggested folder structure (PRD §6.5)

```
/Core            /Transcription   /Auth        /Overlay
/Audio           /AICleanup       /License     /Tray
/Hotkeys         /Settings        /UI          /Installer
/WindowDetection /History         /Logging     /Updater
/Clipboard       /Backend         /Tests
```

`/Backend` here is the **client-side code that talks to the VoxGen backend.** The backend service itself lives in a separate codebase.

---

## Hard invariants — do not break these

1. **No API key of any kind in the desktop app.** No key entry UI, no key encryption, no key validation. v1 ships managed-only — see PRD §6.2, §8.6, §18.1. The `ITranscriptionProvider` interface stays so BYOK can be added later, but only `VoxGenManagedProvider` is wired up in v1.
2. **All user content is local-only.** Transcripts, audio, dictionary, writing style, app-context history, usage history — local SQLite only. **Supabase and the VoxGen backend never store them.** The backend *transits* audio for one request and records usage metadata only (PRD §6.4, §9.3).
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
7. **Offline grace period applies to license checks, not transcription.** Cache the last successful license/trial validation and allow continued use for the configured window (recommend 7–14 days, see §21.4). Transcription itself needs connectivity — show a clear "can't reach VoxGen" message and keep the recording (PRD §8.12, §13).
8. **Sign the executable and the installer.** EV certificates need a hardware token and weeks of lead time — procurement starts at the beginning of the build, not the end (PRD §14.3).
9. **Trial = 30 days OR a usage cap, whichever comes first.** The cap is an abuse ceiling, sized so a real heavy user never reaches it; never surface it in the UI (PRD §16).
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
| Transcripts, audio, dictionary, app-context, usage history | **Local SQLite** (`Microsoft.Data.Sqlite`) — see PRD §12 for tables |
| Settings | **Local JSON file** (`System.Text.Json`) — schema in PRD §11; `settings_schema_version` for forward migration |
| Supabase session token | **DPAPI-protected local file** — auth state, not a user setting |
| Account / subscription / trial / device activation / latest version | **Supabase** |
| Transcription routing + usage metering + abuse limits | **VoxGen backend** |

There are **no API key fields** in the settings schema in v1. Adding one is a scope change — talk to the user first.

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

- BYOK (bring-your-own-key)
- AI Prompt Mode (voice → generated content)
- Local / on-device AI models
- Team accounts, enterprise admin, cloud sync, cloud audio storage
- Prompt marketplace, mobile app, browser extension
- macOS

If a request implies any of these, surface the conflict before writing code.

---

## Open decisions (PRD §21)

These are unsettled at PRD time and should be resolved before the relevant code lands:

1. ~~**Backend hosting**~~ — **Resolved 2026-05-20:** Vercel serverless reusing V1's `api/` + V1's Supabase project & Groq key; auth rebuilt to verify the Supabase JWT (PRD §9.4)
2. **Trial usage cap** — size against Groq Whisper minutes-per-dollar
3. **Monthly fair-use caps** per subscription tier
4. **Offline license-check grace period** — 7–14 days recommended
5. **Code-signing certificate** — OV vs EV, procurement now
6. **Pricing-page changes** — Lifetime/BYOK plan hidden until BYOK ships

When you make progress on one, update the PRD in the same change.

---

## Build & run

The solution is scaffolded: `VoxGen.sln` → `src\VoxGen.Desktop` (WPF tray app, `net10.0-windows`) + `tests\VoxGen.Desktop.Tests` (xUnit).

```
dotnet build VoxGen.sln
dotnet test  VoxGen.sln                      # 55 tests as of 2026-05-20
dotnet run --project src\VoxGen.Desktop      # launches the tray app (no main window)
```

**SDK gotcha:** the .NET 10 SDK (10.0.300) lives at `C:\Program Files\dotnet` but is **not on PATH**. In PowerShell prepend it first: `$env:Path = "C:\Program Files\dotnet;$env:Path"`. The Bash tool can't see `dotnet` at all. Logs: `%APPDATA%\VoxGen\logs`. With `BackendConfig` unset, startup logs `Backend not configured — skipping backend init` and the app uses `StubTranscriptionProvider` (by design).

The backend lives in a separate codebase; document its commands in its own CLAUDE.md.
