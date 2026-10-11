# Encspect

A cross-platform GUI and CLI tool that reports everything technically knowable about an audio file's structure: encoder detection, bitrate behavior, bit reservoir usage, VBR headers, tags, and more. Every reported value points back to the bytes it came from.

Think EncSpot, but modern and with many more formats.

## Status

Core abstractions, host, the CLI (text, JSON and CSV output, section filters, format filter, exit codes by severity) and the tag parsers: ID3v1 (with ID3v1.1 and the Enhanced TAG), ID3v2.2, 2.3 and 2.4, APEv1 and APEv2, and Lyrics3 v1 and v2. The host finds tags by position and structure, never by signature alone (see the plan). The MPEG audio parser reads MP1, MP2 and MP3 frames: sync and resync, every header field, free format, CRC-16, and for Layer III the side information of every granule and the bit reservoir, with each frame's bytes split into its own main data, other frames' main data and ancillary data, and for Layer II the bit allocation, scfsi and scalefactors and the MPEG-2 multichannel extension in the ancillary data. It reads the Xing, Info, LAME and VBRI headers, verifies the LAME tag and music CRCs, and names the encoder from them or from the version string LAME writes into ancillary data, with a confidence level. Without a tag, it recognizes Shine, Helix, Fraunhofer's encoders and TwoLAME from habits their encodes show, with low confidence. It detects mp3PRO and MP3 Surround from the signature their encoders put into the ancillary data of every frame, and reads Fraunhofer's Original File Length block. Each MPEG Audio region reports statistics: duration, bitrate summary and distribution, bitrate and bit reservoir fill per second, the counts a VBR header claims against the ones counted, and the gapless sample count. Next: the first milestone's independent review. The plan is in [docs/PLAN.md](docs/PLAN.md).

## Command line

```sh
encspect [options] [--] <file-or-folder>...
```

Each folder is analyzed with its subfolders, hidden files and folders included, in the order of their paths; output starts with the first file. A symbolic link to a folder is followed only when it leads outside the folder named on the command line and not above it, so every folder is analyzed once and link loops end. Anything that is not a regular file (a FIFO, a device, a socket, or a file of a kernel pseudo-file system such as `/proc` or `/sys`) is reported without being opened, so a FIFO cannot block the run. Standard input is not read: `-` is wrong usage, and a file named `-` is given as `./-`. After `--`, every argument is a file or folder, even one that starts with a dash. The output is a text tree by default; `encspect --help` lists the options.

- `--json` writes one JSON array of reports, `--ndjson` one compact report per line, which stays usable line by line if a long run stops early, and `--csv` one summary row per file: format, streams, codec, duration, average bitrate, sample rate, channel mode, encoder and its confidence, tags, and the number of errors, warnings and infos. Reports are written as each file is analyzed.
- `--offsets` adds the byte offset and length of each node, and of the node each finding points to, to the text tree. JSON always has them and CSV has none, so `--offsets` with `--json`, `--ndjson` or `--csv` is wrong usage. `--hide tags,frames,side-info,statistics` leaves those sections out of the text tree and JSON; it does not apply to `--csv`.
- `--only-format mpeg` analyzes only files whose detected format starts with that name and skips the rest; detection looks at the bytes, not the extension. A name that starts no known format is wrong usage.
- `--fail-on info|warning|error` makes the exit code 1 when a finding of that severity or worse turns up.

Exit codes: 0 success, 1 findings as `--fail-on` says, 2 wrong usage, 3 a file or folder could not be read or is not a regular file, and 4 the output could not be written, as on a full disk; the run stops there. A closed pipe, as with `| head`, is not an error. A file or folder that cannot be read, and anything that is not a regular file, still gets a report with one error finding (`file.unreadable`, `folder.unreadable` or `file.not-regular`), so JSON, NDJSON and CSV list every input, and a line on stderr.

On Linux, a file or folder whose name is not valid UTF-8 cannot be opened: .NET decodes such names lossily, so the name it lists no longer leads to the file. It is reported as unreadable.

All output is UTF-8 without a byte order mark, text from files included: JSON writes non-ASCII characters as they are, not as `\u` escapes. A CSV field that starts with `=`, `+`, `-` or `@` gets a leading apostrophe, so a spreadsheet does not run it as a formula.

## Development

```sh
dotnet build -c Release
dotnet test -c Release
dotnet run -c Release --project src/Encspect.Cli -- --offsets file.mp3
```

Tag fixtures live in `tests/fixtures/tags`. `scripts/fixtures/make_tag_fixtures.py` writes the hand-built ones (CI checks they are reproducible) and `scripts/fixtures/make_external_tag_fixtures.py` the ones written by mutagen and ffmpeg. MPEG fixtures live in `tests/fixtures/mpeg`, written by `make_mpeg_fixtures.py` (hand-built, reproducible in CI) and `make_external_mpeg_fixtures.py` (LAME, TwoLAME, Shine, ffmpeg, the Helix MP3 Encoder, which `helix_tool.py` builds, and Fraunhofer's MP3 Surround encoder, which `fhg_tool.py` downloads), Layer III side-information and bit-reservoir fixtures in `tests/fixtures/layer3` (`make_layer3_fixtures.py`), Layer II and MPEG-2 multichannel fixtures in `tests/fixtures/layer2` (`make_layer2_fixtures.py`), Xing, Info, LAME tag and VBRI fixtures in `tests/fixtures/vbr` (`make_vbr_fixtures.py`), and statistics fixtures in `tests/fixtures/stats` (`make_stats_fixtures.py`; every hand-built manifest lists its regions' statistics, computed by `stream_stats.py`); `crosscheck_mpeg_fixtures.py` confirms their manifests with ffprobe, madplay, mpg123, minimp3, mctoolamed and mp3guessenc. `conformance_mpeg_audio.py` checks `tests/conformance/mpeg2-audio.json` against the ISO/IEC 13818-4 audio conformance streams, which it downloads, since they may not be redistributed, and `check_mp3pro_samples.py` checks `tests/conformance/mp3pro-samples.json` against three mp3PRO files from the FFmpeg sample archive, downloaded for the same reason. The mp3PRO, MP3 Surround and OFL fixtures are in `tests/fixtures/layer3`, their model in `ancillary_scan.py`. minimp3 and mp3guessenc are built from the sources in `third_party` (see its README), so the checks do not depend on a download or a distribution's package version; everything still downloaded is pinned by its SHA-256 and falls back to the Wayback Machine's copy where one exists (`downloads.py`), and the weekly `sources` workflow checks every address (`check_sources.py`). Golden reports (`*.report.json`) are compared on every test run; after an intended output change, rerun the tests with `ENCSPECT_UPDATE_GOLDEN=1` and review the diff.

## Planned stack

C# on .NET, Avalonia UI for the GUI, a console app for the CLI, and one shared core library with a separate project per format. Windows, macOS and Linux on x64 and ARM64.

## License

[MIT](LICENSE)
