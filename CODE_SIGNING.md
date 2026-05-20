# Code signing

VoxGen's Windows releases are signed using a free code-signing certificate provided by the
[SignPath Foundation](https://signpath.org/), with signing operated through [SignPath.io](https://signpath.io/).

## Build & signing process

- VoxGen is built **from source by GitHub Actions**, not on any maintainer's machine — see
  [`.github/workflows/release.yml`](.github/workflows/release.yml).
- The release workflow runs on every `v*` git tag and produces a single self-contained
  **`VoxGen.exe`** (`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`).
- That artifact is submitted to **SignPath** for signing in the automated pipeline, then attached to
  the GitHub Release. No human handles the certificate or signs locally.

## Project

- **Source:** https://github.com/simchap123/voxgen-v2 (public)
- **Maintainer:** individual (solo developer)
- **License:** source-available — see [`LICENSE`](LICENSE)
- **Build system:** GitHub Actions (workflows in `.github/workflows/`)

## Privacy

VoxGen records audio only while the user holds the dictation hotkey. In the managed (default) build,
that audio is transmitted to VoxGen's backend **solely to transcribe it**, then discarded — **no audio
and no transcripts are persisted** by the backend. There is no telemetry. Optional local history, if
enabled, stays on the user's machine and is never uploaded. See the privacy policy on the website.

---

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).
