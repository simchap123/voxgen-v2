# Decision log

## 2026-10-14 — Go fully local: BYOK Groq, no Supabase, no backend

**Decision (owner request):** VoxGen runs entirely from the user's computer. No database, no
account, no VoxGen backend.

- **Transcription:** the desktop calls Groq directly
  (`https://api.groq.com/openai/v1/audio/transcriptions`, `whisper-large-v3-turbo`) with the
  user's own API key. Optional AI cleanup calls Groq chat completions (`llama-3.3-70b-versatile`)
  with the same key, using the same cleanup prompt the old backend used.
- **API key:** entered in Settings → API key, stored DPAPI-encrypted (CurrentUser) at
  `%APPDATA%\VoxGen\groq-key.bin`. Never written to `settings.json` or logs.
- **History:** transcripts appended to `%APPDATA%\VoxGen\history.jsonl`; recordings saved as WAV in
  `%APPDATA%\VoxGen\recordings\`. Both toggleable in Settings → History; Clear deletes both.
  JSON lines instead of SQLite keeps the package count at one (NAudio).
- **Removed:** `Auth/` (Supabase sign-in, session token), `Backend/` (Vercel client),
  `License/` (trial/plan cache), `SignInWindow`, `AICleanup/` placeholder, and their tests.

**Supersedes:** CLAUDE.md hard invariants #1 (no API key in the app), #7 and #9 (license/trial),
PRD §6.2/§8.6/§18.1 (managed-only), and the "BYOK out of scope" line.

**Reversibility:** the `ITranscriptionProvider` seam is kept, so a managed provider could be
restored from git history (`91b9bbc` and earlier) if a hosted tier is ever wanted again.
