# Code signing policy

> **Draft.** Encspect has not applied to SignPath yet, and no release is signed. This page takes effect, and is linked from the README and the release notes, once SignPath Foundation accepts the project and the release workflow signs the Windows executables.

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What is signed

- The Windows executables of each release, `encspect.exe` (command line) and `encspect-gui.exe` (graphical interface), for `win-x64` and `win-arm64`.
- They are built by the [release workflow](../.github/workflows/release.yml) on GitHub-hosted runners, from the tagged commit of this repository, and signed only after an approver confirms the signing request.
- Nothing else is signed under this certificate. The open-source components the executables bundle (.NET, Avalonia, SkiaSharp, HarfBuzzSharp, ANGLE) are not signed by this project; their licenses are in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
- The macOS and Linux builds are not covered by this policy.

## Team roles

- **Committers and reviewers:** [Sebastian Mares (@smares)](https://github.com/smares)
- **Approvers:** [Sebastian Mares (@smares)](https://github.com/smares)

How changes are made:

- Most changes are written by AI coding agents (Claude Code) working under the maintainer's direction.
- Every change is reviewed by an independent AI review agent and must pass the full CI pipeline on every platform before it is merged into `main` under the maintainer's account.
- Signing requests are approved only by the maintainer.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

- Encspect only reads the audio files you open. It does not connect to the network, and it sends no telemetry, usage data or crash reports.
- The graphical interface stores two files on your computer and nowhere else:
  - its window settings, `settings.json`, in the user's application data folder under `Encspect`;
  - the summary cache of folders it has listed: `%LOCALAPPDATA%\Encspect` on Windows, `~/Library/Caches/Encspect` on macOS, `~/.cache/Encspect` (or under `$XDG_CACHE_HOME`) elsewhere. View, Clear Summary Cache empties it.
- The command line keeps no files.

## Installing and removing

- Encspect has no installer and changes no system settings: unpack the archive and run the executables.
- To remove it, delete the unpacked folder, and the settings and cache folders listed above.
