# Encspect

A cross-platform GUI and CLI tool that reports everything technically knowable about an audio file's structure: encoder detection, bitrate behavior, bit reservoir usage, VBR headers, tags, and more. Every reported value points back to the bytes it came from.

Think EncSpot, but modern and with many more formats.

## Status

Early skeleton: core abstractions, host, CLI and tests, but no format parsers yet. The plan is in [docs/PLAN.md](docs/PLAN.md); the first milestone is MP1/MP2/MP3 analysis at the header, side-info and metadata level.

## Planned stack

C# on .NET, Avalonia UI for the GUI, a console app for the CLI, and one shared core library with a separate project per format. Windows, macOS and Linux on x64 and ARM64.

## License

[MIT](LICENSE)
