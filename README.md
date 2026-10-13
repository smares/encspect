# Encspect

A cross-platform GUI and CLI tool that reports everything technically knowable about an audio file's structure: encoder detection, bitrate behavior, bit reservoir usage, VBR headers, tags, and more. Every reported value points back to the bytes it came from.

Think EncSpot, but modern and with many more formats.

## Status

In development; there are no releases yet. The plan is in [docs/PLAN.md](docs/PLAN.md).

Formats read so far ([details](docs/formats/README.md)):

- MPEG audio: MP1, MP2 and MP3, with VBR headers, mp3PRO, MP3 Surround and MPEG-2 multichannel
- MP4, M4A, M4B, 3GP and QuickTime files
- AAC in MP4, ADTS and ADIF, including HE-AAC, HE-AAC v2 and xHE-AAC
- Ogg, with Speex, Vorbis and Opus
- Musepack SV4 to SV8
- AC-3 and E-AC-3, raw and in MP4
- WavPack (lossless, hybrid and DSD), `.wv` and `.wvc`
- ASF (`.wma`, `.wmv`, `.asf`) with Windows Media Audio versions 1 and 2 and Professional, and Lossless and Voice at header level

Tags ([details](docs/tags/README.md)): ID3v1, ID3v2, APE, Lyrics3 and Vorbis comments.

Next: DTS, then the lossless formats, among them the frames of WMA Lossless.

## Documentation

- [Command line](docs/cli.md)
- [GUI](docs/gui.md)
- [Formats](docs/formats/README.md) and [tags](docs/tags/README.md)
- [Development](docs/development.md): building, tests, fixtures and cross-checks
- [Plan](docs/PLAN.md): goals, architecture and rollout

## Quick start

```sh
dotnet build -c Release
dotnet run -c Release --project src/Encspect.Cli -- song.mp3
dotnet run -c Release --project src/Encspect.Gui
```

C# on .NET, with Avalonia UI for the GUI. Windows, macOS and Linux on x64 and ARM64.

## License

[MIT](LICENSE)
