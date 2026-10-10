# Encspect

A cross-platform GUI and CLI tool that reports everything technically knowable about an audio file's structure: encoder detection, bitrate behavior, bit reservoir usage, VBR headers, tags, and more. Every reported value points back to the bytes it came from.

Think EncSpot, but modern and with many more formats.

## Status

Core abstractions, host, CLI and the tag parsers: ID3v1 (with ID3v1.1 and the Enhanced TAG), ID3v2.2, 2.3 and 2.4, APEv1 and APEv2, and Lyrics3 v1 and v2. The host finds tags by position and structure, never by signature alone (see the plan). The MPEG audio parser reads MP1, MP2 and MP3 frames: sync and resync, every header field, free format, CRC-16, and for Layer III the side information of every granule and the bit reservoir, with each frame's bytes split into its own main data, other frames' main data and ancillary data, and for Layer II the bit allocation, scfsi and scalefactors and the MPEG-2 multichannel extension in the ancillary data. Next in the first milestone: the Xing, Info, LAME and VBRI headers, and statistics. The plan is in [docs/PLAN.md](docs/PLAN.md).

## Development

```sh
dotnet build -c Release
dotnet test -c Release
dotnet run -c Release --project src/Encspect.Cli -- --offsets file.mp3
```

The CLI prints a text tree by default. `--json` writes one JSON array of reports, `--ndjson` one compact report per line (newline-delimited JSON), which stays usable line by line if a long run stops early. Reports are written as each file is analyzed.

Tag fixtures live in `tests/fixtures/tags`. `scripts/fixtures/make_tag_fixtures.py` writes the hand-built ones (CI checks they are reproducible) and `scripts/fixtures/make_external_tag_fixtures.py` the ones written by mutagen and ffmpeg. MPEG fixtures live in `tests/fixtures/mpeg`, written by `make_mpeg_fixtures.py` (hand-built, reproducible in CI) and `make_external_mpeg_fixtures.py` (LAME and TwoLAME), Layer III side-information and bit-reservoir fixtures in `tests/fixtures/layer3` (`make_layer3_fixtures.py`), and Layer II and MPEG-2 multichannel fixtures in `tests/fixtures/layer2` (`make_layer2_fixtures.py`); `crosscheck_mpeg_fixtures.py` confirms their manifests with ffprobe, madplay, mpg123, minimp3, mctoolamed and mp3guessenc. `conformance_mpeg_audio.py` checks `tests/conformance/mpeg2-audio.json` against the ISO/IEC 13818-4 audio conformance streams, which it downloads, since they may not be redistributed. Golden reports (`*.report.json`) are compared on every test run; after an intended output change, rerun the tests with `ENCSPECT_UPDATE_GOLDEN=1` and review the diff.

## Planned stack

C# on .NET, Avalonia UI for the GUI, a console app for the CLI, and one shared core library with a separate project per format. Windows, macOS and Linux on x64 and ARM64.

## License

[MIT](LICENSE)
