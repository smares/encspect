# Encspect

A cross-platform GUI and CLI tool that reports everything technically knowable about an audio file's structure: encoder detection, bitrate behavior, bit reservoir usage, VBR headers, tags, and more. Every reported value points back to the bytes it came from.

Think EncSpot, but modern and with many more formats.

## Status

Core abstractions, host, CLI and the tag parsers: ID3v1 (with ID3v1.1 and the Enhanced TAG), ID3v2.2, 2.3 and 2.4, APEv1 and APEv2, and Lyrics3 v1 and v2. The host finds tags by position and structure, never by signature alone (see the plan). The MPEG audio parser reads MP1, MP2 and MP3 frames: sync and resync, every header field, free format, CRC-16, and for Layer III the side information of every granule and the bit reservoir, with each frame's bytes split into its own main data, other frames' main data and ancillary data, and for Layer II the bit allocation, scfsi and scalefactors and the MPEG-2 multichannel extension in the ancillary data. It reads the Xing, Info, LAME and VBRI headers, verifies the LAME tag and music CRCs, and names the encoder from them or from the version string LAME writes into ancillary data, with a confidence level. It detects mp3PRO and MP3 Surround from the signature their encoders put into the ancillary data of every frame, and reads Fraunhofer's Original File Length block. Each MPEG Audio region reports statistics: duration, bitrate summary and distribution, bitrate and bit reservoir fill per second, the counts a VBR header claims against the ones counted, and the gapless sample count. Next: the first milestone's independent review. The plan is in [docs/PLAN.md](docs/PLAN.md).

## Development

```sh
dotnet build -c Release
dotnet test -c Release
dotnet run -c Release --project src/Encspect.Cli -- --offsets file.mp3
```

The CLI prints a text tree by default. `--json` writes one JSON array of reports, `--ndjson` one compact report per line (newline-delimited JSON), which stays usable line by line if a long run stops early. Reports are written as each file is analyzed.

Tag fixtures live in `tests/fixtures/tags`. `scripts/fixtures/make_tag_fixtures.py` writes the hand-built ones (CI checks they are reproducible) and `scripts/fixtures/make_external_tag_fixtures.py` the ones written by mutagen and ffmpeg. MPEG fixtures live in `tests/fixtures/mpeg`, written by `make_mpeg_fixtures.py` (hand-built, reproducible in CI) and `make_external_mpeg_fixtures.py` (LAME, TwoLAME, ffmpeg and Fraunhofer's MP3 Surround encoder, which `fhg_tool.py` downloads), Layer III side-information and bit-reservoir fixtures in `tests/fixtures/layer3` (`make_layer3_fixtures.py`), Layer II and MPEG-2 multichannel fixtures in `tests/fixtures/layer2` (`make_layer2_fixtures.py`), Xing, Info, LAME tag and VBRI fixtures in `tests/fixtures/vbr` (`make_vbr_fixtures.py`), and statistics fixtures in `tests/fixtures/stats` (`make_stats_fixtures.py`; every hand-built manifest lists its regions' statistics, computed by `stream_stats.py`); `crosscheck_mpeg_fixtures.py` confirms their manifests with ffprobe, madplay, mpg123, minimp3, mctoolamed and mp3guessenc. `conformance_mpeg_audio.py` checks `tests/conformance/mpeg2-audio.json` against the ISO/IEC 13818-4 audio conformance streams, which it downloads, since they may not be redistributed, and `check_mp3pro_samples.py` checks `tests/conformance/mp3pro-samples.json` against three mp3PRO files from the FFmpeg sample archive, downloaded for the same reason. The mp3PRO, MP3 Surround and OFL fixtures are in `tests/fixtures/layer3`, their model in `ancillary_scan.py`. Golden reports (`*.report.json`) are compared on every test run; after an intended output change, rerun the tests with `ENCSPECT_UPDATE_GOLDEN=1` and review the diff.

## Planned stack

C# on .NET, Avalonia UI for the GUI, a console app for the CLI, and one shared core library with a separate project per format. Windows, macOS and Linux on x64 and ARM64.

## License

[MIT](LICENSE)
