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

## 2026-10-14 — Read-aloud voice: NVIDIA Magpie (free) as default, Groq Orpheus optional

**Decision (owner request: "use NVIDIA, find a free voice / the best voice"):** read-aloud uses
NVIDIA's hosted **Magpie TTS Multilingual** (build.nvidia.com free endpoint, function-id
`877104f7-e885-42b9-8de8-f6e4c6303969`) by default; Groq Orpheus stays selectable.

- Protocol: Riva gRPC at `grpc.nvcf.nvidia.com`, spoken directly over HTTP/2 with HttpClient and a
  hand-encoded protobuf (`NvidiaSpeech`) — no gRPC/protobuf packages added.
- Voices: Aria, Mia, Sofia, Jason, Leo, Ray (`Magpie-Multilingual.EN-US.<Name>`), 22.05 kHz PCM.
- Key: separate DPAPI file `nvidia-key.bin` (entropy `VoxGen.NvidiaApiKey.v1`).
- Considered **Chatterbox Multilingual** (also in the catalog, reachable with the key): returned
  500/502 on both gRPC and REST on 2026-10-14 — not usable yet; revisit later.
