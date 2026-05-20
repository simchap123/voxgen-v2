# VoxGen

A tiny native Windows dictation utility. **Hold a hotkey, talk, release — polished text is pasted into whatever app you were using.** Lives in the system tray, stays out of the way.

### **[⬇ Download VoxGen for Windows](https://github.com/simchap123/voxgen-v2/releases/latest/download/VoxGen-win-x64.zip)**  ·  [all releases](https://github.com/simchap123/voxgen-v2/releases)

> ⚠️ **Heads-up:** the preview is **not code-signed yet**, so on Windows 11 with **Smart App Control on** it will be *blocked* (not just warned). It runs on machines with Smart App Control off or on Windows 10 (click "More info → Run anyway"). Code signing is the next milestone.

> ### ⚠️ Preview release (`v2.0.0-preview`)
> This build transcribes **100% on your device** using a local Whisper model — no account, no API key, nothing leaves your computer. It's an early preview while the managed cloud backend is being built, so:
> - Accuracy is the small/fast `tiny.en` model — quick, but expect occasional mistakes (names, jargon).
> - It's **not yet code-signed**, so Windows SmartScreen will warn on first run (see install steps).
>
> See [the PRD](VoxGen-v2-PRD.md) §3.4 for what this preview is and isn't.

---

## Install

1. Download **`VoxGen-2.0.0-win-x64.zip`** from the [latest release](../../releases/latest).
2. Extract it anywhere (e.g. `C:\Program Files\VoxGen` or your Desktop).
3. Run **`VoxGen.exe`**.
   - Windows SmartScreen may show *"Windows protected your PC"* — click **More info → Run anyway**. (This goes away once the app is code-signed.)
4. On first run it downloads a ~75 MB speech model once, then works fully offline.

No .NET install required — the runtime is bundled.

## Use it

1. VoxGen runs in your **system tray** (bottom-right; check the `^` overflow).
2. Click into any text field (email, chat, editor…).
3. **Hold the hotkey, speak, release.** A small pill shows recording → transcribing, then your text is pasted where the cursor is.
4. **Default hotkey: Right Alt** (hold-to-record). Change it — and switch to tap-to-toggle — in **tray → Open Settings → General**.

## Privacy

Everything stays on your machine. In this preview, audio is transcribed locally by Whisper and never uploaded. There's no telemetry. (The future managed release transits audio to VoxGen's backend for transcription only, storing no audio or transcripts — see the PRD.)

## Build from source

Requires the **.NET 10 SDK**.

```sh
dotnet build VoxGen.sln -c Debug
dotnet test  VoxGen.sln                      # 72 tests
dotnet run   --project src/VoxGen.Desktop    # launch the tray app

# self-contained distributable (no .NET needed to run):
dotnet publish src/VoxGen.Desktop -c Release -r win-x64 --self-contained true -o dist/VoxGen-win-x64
```

## Tech

C# / .NET 10 · WPF · Win32 P/Invoke (global hotkeys, foreground-window capture, clipboard, synthetic paste) · NAudio (capture) · Whisper.net (preview-only local STT). Deliberately tiny dependency surface — see the PRD's dependency policy (§6.2).

## Status

Preview. Working: hotkey → record → local transcribe → paste, recording overlay, tray + settings (mic picker, hotkey recorder), reliable settings persistence. Not yet: managed cloud transcription + accounts, code signing, auto-update, local history UI, live/streaming dictation. Roadmap and full scope live in [`VoxGen-v2-PRD.md`](VoxGen-v2-PRD.md).

## License

VoxGen is **source-available** (not OSI open-source): you may read it and build it for personal use, but not redistribute, resell, or build a competing product from it. See [`LICENSE`](LICENSE). Bundled third-party components (NAudio, Whisper.net, whisper.cpp, the Whisper model) keep their own MIT licenses. Commercial licensing: contact VoxGen.

## Code signing

Builds are signed via the [SignPath Foundation](https://signpath.org/) free OSS program — see [`CODE_SIGNING.md`](CODE_SIGNING.md) for the build & signing process. `VoxGen.exe` is built from source by GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)) and signed in the pipeline; no maintainer signs locally.

> Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).
