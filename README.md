# Encspect

A cross-platform GUI and CLI tool that reports everything technically knowable about an audio file's structure: encoder detection, bitrate behavior, bit reservoir usage, VBR headers, tags, and more. Every reported value points back to the bytes it came from.

Think EncSpot, but modern and with many more formats.

## Status

Core abstractions, host, CLI and the tag parsers: ID3v1 (with ID3v1.1 and the Enhanced TAG), ID3v2.2, 2.3 and 2.4, APEv1 and APEv2, and Lyrics3 v1 and v2. The host finds tags by position and structure, never by signature alone (see the plan). The MPEG audio parser reads MP1, MP2 and MP3 at frame-header level: sync and resync, every header field, free format, and CRC-16 for Layers I and III. Next in the first milestone: Layer III side info, the Xing, Info, LAME and VBRI headers, and statistics. The plan is in [docs/PLAN.md](docs/PLAN.md).

## Development

```sh
dotnet build -c Release
dotnet test -c Release
dotnet run -c Release --project src/Encspect.Cli -- --offsets file.mp3
```

The CLI prints a text tree by default. `--json` writes one JSON array of reports, `--ndjson` one compact report per line (newline-delimited JSON), which stays usable line by line if a long run stops early. Reports are written as each file is analyzed.

Tag fixtures live in `tests/fixtures/tags`. `scripts/fixtures/make_tag_fixtures.py` writes the hand-built ones (CI checks they are reproducible) and `scripts/fixtures/make_external_tag_fixtures.py` the ones written by mutagen and ffmpeg. MPEG fixtures live in `tests/fixtures/mpeg`, written by `make_mpeg_fixtures.py` (hand-built, reproducible in CI) and `make_external_mpeg_fixtures.py` (LAME and TwoLAME); `crosscheck_mpeg_fixtures.py` confirms their manifests with ffprobe, madplay and mpg123. Golden reports (`*.report.json`) are compared on every test run; after an intended output change, rerun the tests with `ENCSPECT_UPDATE_GOLDEN=1` and review the diff.

## Planned stack

C# on .NET, Avalonia UI for the GUI, a console app for the CLI, and one shared core library with a separate project per format. Windows, macOS and Linux on x64 and ARM64.

## License

[MIT](LICENSE)
