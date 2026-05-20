# VoxGen v2 — Product Requirements Document

**Status:** Draft for build kickoff
**Version:** 2.0
**Date:** 19 May 2026
**Owner:** VoxGen
**Audience:** Developer / build team

---

## 1. Summary

VoxGen v2 is a ground-up rebuild of VoxGen as a **lightweight native Windows desktop dictation app**, backed by a **VoxGen-managed transcription service**. It replaces the current Electron app.

The product does one thing extremely well: the user holds a hotkey, speaks naturally, releases, and clean polished text is pasted into whatever app they were already using.

Two principles drive this rebuild:

1. **The app must be simple** — small, fast, native, dependable. Electron was the opposite: bloated, slow, layered on a browser and plugins, unreliable. v2 is about *removing* weight, not adding it.
2. **The user must not have to set anything up.** Download, sign in, talk — anywhere. No API keys, no provider accounts, no configuration. VoxGen hosts the transcription keys on its own backend so the user never sees one.

It must feel like a tiny invisible system utility — *press, talk, paste, done.*

---

## 2. Goals and non-goals

### Goals (v1)

- Native Windows app, no Electron, small footprint.
- Reliable hotkey → record → transcribe → cleanup → paste loop.
- **Zero setup for the user**: no API key entry, ever. Sign in and dictate.
- Rock-solid settings persistence (Section 10 — non-negotiable).
- Local-only handling of all user content (transcripts, audio, history).
- A VoxGen-managed backend that holds the transcription keys, meters usage, and protects against abuse.
- 30-day free trial, no credit card required.

### Non-goals (v1) — explicitly out of scope

- **BYOK (bring-your-own-key).** Deliberately deferred — see Section 18.
- AI Prompt Mode (content generation). Deferred — see Section 18.
- Local / on-device AI models.
- Team accounts, enterprise admin, cloud transcript sync, cloud audio storage.
- Prompt marketplace, mobile app, browser extension, large dashboards.
- macOS support (roadmap).

---

## 3. Phasing strategy

### 3.1 The key fact

Because the user never supplies a key, **the app cannot transcribe a single word without the VoxGen backend.** The backend is therefore **part of v1**, not a later add-on. v1 is the desktop engine *and* the managed backend, shipped together.

### 3.2 Build order

The two pieces are still **built and tested separately** — they are two different hard problems, and mixing them makes every bug ambiguous:

- **The desktop engine** — hotkeys, audio capture, active-window detection, paste, settings reliability, the un-Electron rebuild.
- **The managed backend** — authentication, real API keys, metering, rate limiting, billing/license.

The engine is built first and proven end to end. While building it, transcription is pointed at a **private developer key in a local dev config** (or a stub backend). This key is internal scaffolding only — it is never shipped, never seen by a user. The backend is built in parallel.

**The two must converge before any public release.** There is no BYOK fallback, so nothing ships to a real user until both the engine and the backend are done and connected.

### 3.3 What this means for launch

| | Original plan | This plan |
|---|---|---|
| v1 | BYOK desktop engine | Desktop engine **+** managed backend, together |
| v1.5 | Managed proxy | (merged into v1) |
| Later | — | BYOK option, AI Prompt Mode, streaming, dictionary, macOS |

The public launch waits until the engine and backend are both ready. This is a deliberate, accepted tradeoff: a slightly later launch in exchange for the "download and it just works" experience being real on day one.

---

## 4. Target user

A professional who works by voice all day — drafting emails, Slack messages, notes, code comments. They want dictation that works in *every* app with no per-app and no first-run setup, produces clean text (no filler words, correct punctuation), and stays out of the way. They value privacy and a fast native feel. The benchmark experience is WhisperFlow-style: install, sign in, immediately dictate anywhere.

---

## 5. Product principles

1. **Invisible utility.** Lives in the tray. No window unless the user opens settings.
2. **Zero setup.** No keys, no provider accounts, no configuration to start.
3. **Never lose a transcript.** Any failure after recording must preserve the user's words.
4. **Local by default.** User content stays on the user's machine. The cloud handles licensing and transcription routing only — it stores no user content.
5. **One source of truth.** Especially for settings — no duplicate state, no guessed defaults.
6. **Small and fast.** Every dependency and background process must justify itself.

---

## 6. Technical architecture

### 6.1 Stack — desktop app

- **Language / runtime:** C# / .NET (latest LTS).
- **UI framework:** **WPF.** Chosen over WinUI 3 deliberately — for a tray utility of this size, WPF has more mature tray integration, simpler packaging, and fewer deployment rough edges. The size and memory targets in Section 14 are comfortably achievable with WPF.
- **OS integration:** Win32 APIs via P/Invoke for global hotkeys, active-window detection, clipboard, and synthetic paste.

### 6.2 Dependency policy

The default is **zero third-party packages.** "Simple" means small and reliable — not literally zero libraries. Electron's problem was shipping an entire browser and dozens of uncontrolled plugins; it was never "it used libraries." A few small, trusted, native libraries are the opposite of that — they *reduce* risk, because hand-rolling them would mean more code to own and more bugs.

Everything below is sanctioned. **Any package not on this list requires explicit approval before use.**

| Capability | Source | Type |
|---|---|---|
| HTTP (VoxGen backend, Supabase REST) | `HttpClient` | Built into .NET |
| JSON (settings file) | `System.Text.Json` | Built into .NET |
| Global hotkeys, window detection, clipboard, paste | Win32 P/Invoke | OS API |
| Secure local storage (session token) | DPAPI (`ProtectedData`) | Built into .NET |
| Microphone capture | **NAudio** | Sanctioned exception |
| Local history database | **Microsoft.Data.Sqlite** | Sanctioned exception (Microsoft-owned) |
| Auto-update with rollback | **Velopack** | Sanctioned exception |

Rationale for the three exceptions: native audio capture means hand-written WASAPI interop, the most bug-prone part of an app like this; SQLite has no zero-dependency equivalent in .NET; a self-rolled updater with rollback is fragile, security-sensitive code. The Supabase SDK is **not** used — Supabase is accessed via its REST API with `HttpClient`.

Note: with no BYOK in v1, the app **stores no API key of any kind.** There is no key-entry screen, no key encryption, no key validation in the client. This makes the app smaller and simpler than a BYOK design.

### 6.3 Transcription provider abstraction

All transcription goes behind a single interface. In v1 there is exactly one implementation — the managed backend — but the interface is kept so BYOK can be added later (Section 18) without reworking the app.

```csharp
public interface ITranscriptionProvider
{
    Task<TranscriptionResult> TranscribeAsync(AudioClip audio, CancellationToken ct);
}

// v1 implementation:
//   VoxGenManagedProvider — sends audio to the VoxGen backend with the user's session token
// later (optional):
//   GroqDirectProvider / OpenAiDirectProvider — BYOK
```

The same pattern applies to AI cleanup via an `ICleanupProvider` interface. Nothing else in the app — recording, hotkeys, paste, history, overlay — knows which provider is in use.

### 6.4 Data architecture

| Concern | Where it lives |
|---|---|
| Transcripts, audio, dictionary, writing style, app-context history, usage history | **Local machine only** |
| Account, subscription / license state, device activation, trial state, latest app version | **Supabase** |
| Transcription routing (audio in → text out), usage metering, abuse limits | **VoxGen backend** |

Hard rules:

- **Supabase never stores** transcripts, audio, prompts, clipboard contents, app-context history, writing style, or user history.
- **The VoxGen backend *transits* audio** to fulfil a transcription request, then returns text. It **persists no audio and no transcripts** — it stores only usage metadata (e.g. minutes transcribed per user, timestamps) needed for metering and abuse protection. Transit for processing is not storage.

### 6.5 Suggested folder structure — desktop app

```
/Core            /Transcription   /Auth        /Overlay
/Audio           /AICleanup       /License     /Tray
/Hotkeys         /Settings        /UI          /Installer
/WindowDetection /History         /Logging     /Updater
/Clipboard       /Backend         /Tests
```

`/Backend` holds the client-side code that talks to the VoxGen backend. The backend service itself is a separate codebase (Section 9).

---

## 7. Core workflow

1. App launches and runs in the system tray.
2. User is in any app (Gmail, ChatGPT, Cursor, VS Code, Notepad, Slack, etc.).
3. User holds the hotkey, or double-taps it (toggle mode).
4. VoxGen captures the active window/app handle **before recording starts**.
5. VoxGen begins recording from the selected microphone (start latency < 100 ms).
6. User releases the hotkey, or taps again, to stop.
7. Audio is sent to the **VoxGen backend** with the user's session token.
8. The backend validates the account/trial, checks usage limits, transcribes via VoxGen-held keys, optionally applies AI cleanup, and returns the final text.
9. VoxGen restores focus to the originally captured window.
10. VoxGen pastes the final text into that app.

---

## 8. Functional requirements — v1 desktop app

### 8.1 Tray application
Runs in the system tray with a branded VoxGen icon. Tray menu: open settings, pause/resume, recent history, quit. No main window appears on launch.

### 8.2 Onboarding
First run: a minimal sign-in / account-creation screen, then straight to use. No key entry, no provider setup. The 30-day trial begins automatically on account creation.

### 8.3 Hotkey system
- Global hotkey registered via Win32, works while any app is focused.
- **Hold-to-record** mode and **double-tap toggle** mode.
- User-configurable hotkey and mode in settings.
- Graceful handling if the chosen hotkey is already claimed by another app.

### 8.4 Audio pipeline
- Records from the user-selected microphone via NAudio.
- **Pre-warm the device**: keep the selected microphone initialized in the background so a hotkey press starts buffering immediately. The sub-100 ms target is not achievable if the device is opened on keypress — the first word will clip.
- Write temp audio safely to disk.
- Send audio to the backend as **WAV** (avoids an audio-encoding dependency; the WAV header is trivial to write).
- On transcription failure, do **not** delete the recording until the user dismisses the error.
- Delete temp audio after a successful paste, unless local audio history is enabled.

### 8.5 Active window detection
Capture the foreground window handle the instant recording starts, before any VoxGen UI takes focus. Used to restore focus and target the paste.

### 8.6 Transcription
All transcription goes through `VoxGenManagedProvider` → the VoxGen backend (Sections 6.3, 9). The app holds no key.

### 8.7 AI cleanup
Optional, toggleable. Removes filler words, fixes grammar and punctuation, formats the text. Performed server-side by the backend as part of the transcription request. When off, the raw transcript is used.

### 8.8 Paste pipeline
- Restore focus to the captured window after transcription.
- Paste the final text (clipboard + synthetic Ctrl+V).
- If paste fails, leave the final text on the clipboard and show a small non-blocking message.
- **The final transcript is never lost**, regardless of paste outcome.

### 8.9 Settings window
A single settings window covering: microphone selector, hotkey selector and mode, AI cleanup on/off, start-on-boot, overlay on/off, language, theme, history controls, and clear-history. Account/trial status is shown here too. See Section 10 for the reliability requirements that govern all of it.

### 8.10 Recording overlay
A small, unobtrusive on-screen overlay shown while recording and processing. Toggleable.

### 8.11 Local history
- Local-only transcription history, browsable in the app.
- Optional local-only audio history, **default OFF**.
- A "clear all local history" action that takes effect immediately.

### 8.12 Account, license, trial, device activation
- Supabase-backed sign-in and account creation.
- Subscription / license validation.
- 30-day trial state tracked per account (Section 16).
- Device activation tied to the account.
- **Offline grace period:** cache the last successful license/trial validation with a timestamp and allow continued use for a defined offline window (recommend 7–14 days — confirm in Section 21). A network blip or a backend outage must never lock out a valid user mid-sentence. Note: transcription itself still requires connectivity (the backend does the work); the grace period governs *license checks*, not the transcription call.

### 8.13 Installer and updater
See Sections 14 and 15.

---

## 9. Functional requirements — v1 managed backend

A separate service. It is the only thing that holds transcription keys.

### 9.1 Responsibilities
- Authenticate each request using the user's Supabase session token.
- Validate account state: active subscription **or** valid trial.
- Enforce usage limits and rate limits (Section 16).
- Proxy audio to the transcription provider (Groq Whisper) using **VoxGen-owned keys**.
- Optionally apply AI cleanup, then return the final text.
- Record usage metadata for metering and abuse detection.

### 9.2 Request flow
```
Desktop app ──(session token + WAV audio)──▶ VoxGen backend
   backend: verify token → check subscription/trial → check usage quota
          → transcribe via VoxGen keys → optional cleanup → record usage
   backend ──(final text)──▶ Desktop app
```

### 9.3 Hard constraints
- Persists **no audio and no transcripts** — usage metadata only.
- VoxGen API keys are stored only in backend secret storage, never sent to the client.
- Must fail **carefully**: a failed license check must not silently grant free unlimited use, and must not lock out a valid paying user (respect the offline grace period on the client side).

### 9.4 Hosting decision (resolved 2026-05-20)
**Vercel serverless functions (TypeScript / `@vercel/node`) + reuse of V1's existing Supabase Postgres project**, mirroring V1's `api/` layout (`C:\Users\spent\projects\VoxGen V1\api`). V1's proxy mechanics, Supabase table shape (`users`, `user_licenses`, `license_types`, `usage_logs`), per-user rate limiting, and Stripe flow are the reference. The Groq key is reused from V1 (Vercel env `GROQ_API_KEY`).

**One deliberate departure from V1:** auth. V1 trusts a plaintext email in the request body (`validateUser(email)`); V2 instead **verifies the Supabase session JWT** (per §9.2 and §8.6), derives the user from the verified token, then applies the same license/trial/quota checks. Do not port V1's email-in-body trust model.

Supabase Edge Functions were the other candidate but were not chosen — V1's code is already TS/Vercel, so reuse minimises porting.

---

## 10. Settings and boolean reliability — critical, non-negotiable

Settings bugs are the most common and most damaging failure mode for an app like this. The following are hard requirements.

### Failures that must never happen
- Toggle visually ON but internally OFF (or the reverse).
- Setting saves but does not apply; or applies but does not save.
- App restart, crash, sleep/wake, update, or installer repair resets settings.
- Default values overriding saved user settings.
- Stale cached state overriding persisted state.
- Multiple sources of truth.
- An async race condition changing a setting incorrectly.
- UI updating while the local write silently fails.

### Architecture rules
1. **One single source of truth** for settings.
2. A **strongly typed** settings model — no loose dictionaries or string keys at the call site.
3. Load settings **once** at startup from the authoritative local store.
4. The UI reflects **persisted** state, never guessed or default state.
5. Every setting change must, in order: update in-memory state → persist locally → verify the write succeeded → apply to runtime behavior.
6. If the write fails: **roll the UI back** and show an error.
7. No duplicate settings files.
8. No hidden fallback defaults after first-run setup.
9. Settings must survive restart, crash, sleep/wake, app update, and installer repair.
10. Log every settings read/write failure.
11. Automated tests for **every** toggle and boolean setting.

---

## 11. Settings schema

Stored as a local config file (`System.Text.Json` serialized). The Supabase session token is stored separately and protected with DPAPI — it is auth state, not a user setting. There are **no API key fields** in v1.

| Key | Notes |
|---|---|
| `selected_microphone_id` | Stable device ID (authoritative) |
| `selected_microphone_name` | For display only |
| `hotkey_mode` | `hold` \| `toggle` |
| `hotkey_value` | Key combination |
| `cleanup_enabled` | bool |
| `save_text_history_local` | bool |
| `save_audio_history_local` | bool, default `false` |
| `use_local_history_for_ai` | bool |
| `startup_on_boot` | bool |
| `overlay_enabled` | bool |
| `language` | transcription language |
| `theme` | UI theme |
| `app_version` | last-run version |
| `settings_schema_version` | for safe migrations |

`settings_schema_version` lets future versions migrate the file forward without resetting user choices.

---

## 12. Local data model

Local history in SQLite (`Microsoft.Data.Sqlite`). **None of these tables sync to Supabase or the VoxGen backend.**

| Table | Purpose |
|---|---|
| `transcription_history` | Past transcripts (if text history enabled) |
| `audio_history` | Past recordings (if audio history enabled) |
| `local_dictionary` | Personal vocabulary (used more heavily later) |
| `app_context_rules` | Per-app behavior (later) |
| `usage_events_local` | Local-only usage events |
| `settings_audit_log` | Settings read/write events and failures |

---

## 13. Error handling

Every error below must be handled gracefully — never a freeze, never a silent crash, never a lost transcript, and the app stays usable. All errors are logged locally.

| Error | Expected behavior |
|---|---|
| No microphone found | Clear message, prompt to connect/select a device |
| Microphone permission denied | Explain and link to Windows privacy settings |
| Selected microphone disconnected | Detect, message, fall back gracefully |
| Not signed in / session expired | Prompt the user to sign in again |
| Trial expired | Clear message + upgrade path; local history preserved (Section 16) |
| Usage limit reached | Clear message explaining the limit and how to continue |
| Transcription timeout / failure | Message, **keep the recording** until the user dismisses |
| AI cleanup failure | Fall back to the raw transcript, paste it anyway |
| Paste failure | Leave final text on the clipboard, show a small message |
| Backend / license check unreachable | Apply the offline grace period for license checks (Section 8.12); transcription itself needs connectivity — show a clear "can't reach VoxGen" message and keep the recording |
| Internet unavailable | Clear message; recording is preserved for retry |

---

## 14. Non-functional requirements

### 14.1 Performance targets
- Idle RAM under 150 MB.
- Cold start under 2 seconds.
- Recording start under 100 ms (requires the pre-warm in Section 8.4).
- End of speech to pasted text: 1–3 seconds.
- Installed size under 100 MB, target under 50 MB.
- No heavy background processes.

### 14.2 Security and privacy
- No API key in the app — there is nothing to extract.
- The Supabase session token is stored with DPAPI.
- No transcript, audio, or history is ever uploaded to Supabase.
- The VoxGen backend stores no audio and no transcripts (Section 9.3).
- No hidden telemetry, no transcript analytics, no clipboard analytics.
- A clear, public privacy policy.
- The user can clear all local history instantly.

### 14.3 Installer and updater
- Branded VoxGen installer (`VoxGen Setup.exe`).
- **Signed executable and signed installer.** This requires an OV or EV code-signing certificate. EV certificates require a hardware token and provisioning can take days to weeks — **start certificate procurement at the beginning of the build, not the end.** Without signing, Windows SmartScreen will deter users from installing.
- App versioning, auto-update, and rollback on a failed update (via Velopack).
- Updates and installer repair must **preserve all user settings** — they must not reset toggles.

---

## 15. Branding

Everything the user sees says **VoxGen** — no Electron branding, no default framework icons.

- App name: VoxGen
- Installer: `VoxGen Setup.exe`
- Tray icon, taskbar icon, settings window icon — branded
- Executable metadata: VoxGen
- Publisher / company name: VoxGen

---

## 16. Trial, metering, and abuse protection

Because VoxGen hosts the transcription keys, **every transcription costs VoxGen money.** The trial and metering rules below are day-one requirements, not later additions.

### 16.1 The 30-day trial
- 30 days, **no credit card required** — download, sign in, dictate.
- The trial is **30 days OR a usage cap, whichever comes first.** Time alone does not cap spend; without a usage ceiling a single trial account could be scripted to drain the API budget.
- The usage cap is an **abuse ceiling, not a feature limit**: set generously enough that a real person dictating heavily all day for a month never reaches it, low enough that a scripted or shared account hits a wall. A genuine trial user should never see it.
- The trial is tied to the account and to device activation so a simple reinstall does not grant an endless new trial. (Not perfectly airtight — it stops the casual reset.)

### 16.2 At trial end (day 31 or cap reached)
- The app does not break or appear broken.
- It clearly states the trial is over and shows the upgrade path.
- **All local history is preserved** — the user keeps their data; they simply cannot transcribe until they subscribe.

### 16.3 Ongoing metering and limits (all users)
- Per-user usage metering (minutes transcribed) recorded by the backend.
- A monthly fair-use cap per subscription tier.
- Per-user rate limiting.
- Anomalous-volume detection (e.g. a scripted hotkey draining usage).

### 16.4 What the user sees
- A quiet "X days left" indicator during the trial is fine.
- The usage cap is **not** surfaced to normal users — a real user never approaches it, and showing it would make the product feel stingy when it isn't.

---

## 17. Business and pricing alignment

Since v1 ships with hosted keys and no BYOK, the marketing site (voxgen.app) must match:

- v1 sells the **managed (subscription) plans** — the "no setup, managed keys" experience the Pro plan already describes.
- The **Lifetime / BYOK** plan currently on the site should be **removed or hidden** until BYOK ships as a later version (Section 18).
- The site's **AI Prompt Mode** section describes a feature not in v1 scope (Section 18) — the live site must not advertise it as available until it ships.
- The 30-day-trial messaging on the site is correct and stays.

---

## 18. Scope decisions

### 18.1 BYOK — deferred
Bring-your-own-key is **not in v1.** The entire point of v1 is zero setup: download, sign in, talk. Adding a key-entry path in v1 would dilute that. BYOK is a candidate for a later version, for power users who prefer to run on their own key. The `ITranscriptionProvider` interface (Section 6.3) keeps that door open without rework.

### 18.2 AI Prompt Mode — deferred
The current site markets "AI Prompt Mode" (double-tap, speak an instruction, generate a full email/code/summary). It is **intentionally not in v1 scope**, to keep the v1 engine focused. Strong candidate for a later version. Until it ships, the site must not advertise it.

---

## 19. Success criteria

### v1 acceptance test
v1 passes if, end to end:
1. The user installs VoxGen from the branded installer.
2. The user creates an account / signs in. The 30-day trial starts automatically.
3. **No API key or provider setup is required at any point.**
4. The user selects a microphone.
5. The user opens Gmail.
6. The user holds the hotkey and speaks naturally.
7. The user releases the hotkey.
8. VoxGen transcribes (via the backend), cleans, and pastes the text into Gmail within a few seconds.
9. **Settings remain correct after closing and reopening the app** — and after a restart, a crash, a sleep/wake cycle, and an app update.

### Quality bars
- All performance targets in Section 14.1 are met.
- Every error in Section 13 is handled without a freeze, crash, or lost transcript.
- Automated tests cover every toggle and boolean setting.
- A scripted/abusive account hits the usage limits without affecting other users.

---

## 20. Roadmap

| Version | Scope |
|---|---|
| **v1** | Native Windows dictation engine **+** VoxGen-managed backend, shipped together. Hosted keys only, no setup. 30-day metered trial. AI cleanup. Local history. Supabase auth/license/activation/version. |
| **Later** | BYOK option, AI Prompt Mode, realtime/streaming transcription, personal dictionary, local writing-style learning, per-app cleanup behavior, improved overlay |
| **Later still** | macOS version, advanced AI voice commands, local semantic memory; team/enterprise only if demand warrants |

---

## 21. Open decisions

1. ~~**Backend hosting** — Supabase Edge Functions vs. a dedicated service.~~ **Resolved 2026-05-20:** Vercel serverless (TypeScript) reusing V1's `api/` as reference + V1's existing Supabase project and Groq key; auth rebuilt to verify the Supabase JWT rather than V1's email-in-body. See §9.4.
2. **Trial usage cap** — set the abuse ceiling (needs Groq Whisper minutes-per-dollar to size it sensibly).
3. **Monthly fair-use caps** — define per subscription tier.
4. **Offline license-check grace period** — confirm the window (recommend 7–14 days).
5. **Code-signing certificate** — choose OV vs. EV and begin procurement now (lead time).
6. **Pricing-page changes** — confirm the Lifetime/BYOK plan is removed/hidden for the v1 launch.
