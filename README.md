# Encspect

[![main](https://img.shields.io/github/actions/workflow/status/smares/encspect/ci.yml?branch=main&event=push&label=main)](https://github.com/smares/encspect/actions/workflows/ci.yml?query=branch%3Amain+event%3Apush)
[![nightly](https://img.shields.io/github/actions/workflow/status/smares/encspect/ci.yml?branch=main&event=schedule&label=nightly)](https://github.com/smares/encspect/actions/workflows/ci.yml?query=branch%3Amain+event%3Aschedule)

A cross-platform GUI and CLI tool that reports everything technically knowable about an audio file's structure: encoder and writer detection, bitrate behavior, bit reservoir usage, VBR headers, tags, and more. Every reported value points back to the bytes it came from.

Think EncSpot, but modern and with many more formats.

## Status

Version 0.1.0, a pre-release in development. Download builds for Windows, macOS and Linux from the [releases page](https://github.com/smares/encspect/releases); they are not signed (see the release notes). The plan is in [docs/PLAN.md](docs/PLAN.md).

Formats read so far ([details](docs/formats/README.md)):

- MPEG audio: MP1, MP2 and MP3, with VBR headers, mp3PRO, MP3 Surround and MPEG-2 multichannel
- MP4, M4A, M4B, 3GP and QuickTime files
- AAC in MP4, ADTS and ADIF, including HE-AAC, HE-AAC v2 and xHE-AAC
- Ogg, with Speex, Vorbis and Opus
- Musepack SV4 to SV8
- AC-3 and E-AC-3, raw and in MP4, with the object audio (JOC) that Dolby Atmos adds
- DTS (core, XCh, X96, XXCH and the DTS-HD extension substreams with the lossless extension's channel sets and the DTS:X signal), raw and in MP4
- DTS-HD files (`.dtshd`)
- WavPack (lossless, hybrid and DSD), `.wv` and `.wvc`, with an optional decode that verifies the CRCs and the MD5 of the audio
- FLAC (`.flac`, and in Ogg), with an optional decode that verifies the MD5 of the audio
- Monkey's Audio (`.ape`), with an optional decode of files of versions 3.93 to 3.99 and 4.11 that verifies the CRC of every frame
- TTA (`.tta`), verified by the CRC-32s of its header, seek table and frames
- Dolby TrueHD and MLP (`.thd`, `.mlp`), with an optional decode that verifies the lossless checks of the audio
- ASF (`.wma`, `.wmv`, `.asf`) with Windows Media Audio versions 1 and 2, Professional and Voice, and Lossless at header level
- WAV (`.wav`; RIFF, RIFX, RF64 and BW64), Wave64 (`.w64`) and AIFF (`.aif`, `.aiff`, `.aifc`) with all their chunks; PCM, floating-point and compressed formats at header level, and MPEG audio, AC-3, E-AC-3 and DTS in WAV read by their parsers, also as S/PDIF bursts in the samples
- Matroska and WebM (`.mka`, `.mkv`, `.webm`) with AAC, AC-3, E-AC-3, DTS, Vorbis and Opus audio read by their parsers

Tags ([details](docs/tags/README.md)): ID3v1, ID3v2, APE, Lyrics3 and Vorbis comments.

Protection: streams that a container encrypts or locks are reported with their scheme and DRM systems, without decrypting: Common Encryption in MP4, iTunes protection and Audible entries, Windows Media DRM in ASF, and ContentEncryption in Matroska.

Phase 2, the lossy formats, is done. Next: the lossless formats, among them the frames of WMA Lossless.

## Documentation

- [Command line](docs/cli.md)
- [GUI](docs/gui.md)
- [Formats](docs/formats/README.md) and [tags](docs/tags/README.md)
- [Development](docs/development.md): building, tests, fixtures and cross-checks
- [Using Encspect from other programs](docs/library.md): the JSON output today, NuGet packages and a native library later
- [Plan](docs/PLAN.md): goals, architecture and rollout
- [Release notes](docs/releases/0.1.0.md)

## Quick start

```sh
dotnet build -c Release
dotnet run -c Release --project src/Encspect.Cli -- song.mp3
dotnet run -c Release --project src/Encspect.Cli -- --as aac damaged.bin   # skip format detection
dotnet run -c Release --project src/Encspect.Gui
```

C# on .NET, with Avalonia UI for the GUI. Windows, macOS and Linux (X11, also under XWayland; native Wayland is experimental and opt-in) on x64 and ARM64.

## License

[MIT](LICENSE)
