# Encspect

A cross-platform GUI and CLI tool that reports everything technically knowable about an audio file's structure: encoder detection, bitrate behavior, bit reservoir usage, VBR headers, tags, and more. Every reported value points back to the bytes it came from.

Think EncSpot, but modern and with many more formats.

## Status

Core abstractions, host, the CLI (text, JSON and CSV output, section filters, format filter, exit codes by severity) and the tag parsers: ID3v1 (with ID3v1.1 and the Enhanced TAG), ID3v2.2, 2.3 and 2.4 (zlib-compressed frames included), APEv1 and APEv2, and Lyrics3 v1 and v2. The host finds tags by position and structure, never by signature alone (see the plan). The MPEG audio parser reads MP1, MP2 and MP3 frames: sync and resync, every header field, free format, CRC-16, and for Layer III the side information of every granule and the bit reservoir, with each frame's bytes split into its own main data, other frames' main data and ancillary data, and for Layer II the bit allocation, scfsi and scalefactors and the MPEG-2 multichannel extension in the ancillary data. It reads the Xing, Info, LAME and VBRI headers, verifies the LAME tag and music CRCs, and names the encoder from them or from the version string LAME writes into ancillary data, with a confidence level. Without a tag, it recognizes Shine, Helix, Fraunhofer's encoders and TwoLAME from habits their encodes show, with low confidence. It detects mp3PRO and MP3 Surround from the signature their encoders put into the ancillary data of every frame, and reads Fraunhofer's Original File Length block. Each MPEG Audio region reports statistics: duration, bitrate summary and distribution, the counts a VBR header claims against the ones counted, and the gapless sample count; each stream, which may span regions, its codec, duration, average bitrate and encoder, and the bitrate and bit reservoir fill per frame and per second for graphs. The first milestone passed its independent reviews. The GUI's first slice is built: a main window with a folder tree and a file list analyzed in the background, and a detail window with the structure tree, a hex view that marks a node's bytes and bits, the findings and export (see below). Next: the GUI's graphs and its summary cache, then FLAC, WAV, AIFF and Ogg. The plan is in [docs/PLAN.md](docs/PLAN.md).

## Command line

```sh
encspect [options] [--] <file-or-folder>...
```

Each folder is analyzed with its subfolders, hidden files and folders included, in the order of their paths; output starts with the first file. A symbolic link to a folder is followed only when it leads outside the folder named on the command line and not above it, so every folder is analyzed once and link loops end. Anything that is not a regular file (a FIFO, a device, a socket, or a file of a kernel pseudo-file system such as `/proc` or `/sys`) is reported without being opened, so a FIFO cannot block the run. Standard input is not read: `-` is wrong usage, and a file named `-` is given as `./-`. After `--`, every argument is a file or folder, even one that starts with a dash. The output is a text tree by default; `encspect --help` lists the options.

- `--json` writes one JSON array of reports, `--ndjson` one compact report per line, which stays usable line by line if a long run stops early, and `--csv` one summary row per file. Reports are written as each file is analyzed.
- `--offsets` adds the byte offset and length of each node, and of the node each finding points to, to the text tree: `[0x1A, 4 bytes]`. A field that does not cover whole bytes, such as an MPEG header or side-information field, shows its first byte, the bit within it where it starts, counted from the most significant bit (0 to 7), and its width in bits: `[0x1+3, 2 bits]` is two bits starting at the fourth bit of byte 0x1. A node whose bytes are not contiguous in the file, such as a packet that spans the pages of a container, shows the range that covers them and then each range: `[0x1A, 40 bytes in 0x1A 2 bytes, 0x40 2 bytes]` spans 40 bytes from 0x1A but holds only 0x1A to 0x1B and 0x40 to 0x41. JSON always has them (`offset` and `length`, plus `bitOffset` and `bitLength` for such a field and `ranges` for such a node) and CSV has none, so `--offsets` with `--json`, `--ndjson` or `--csv` is wrong usage. `--hide tags,frames,side-info,statistics` leaves those sections out of the text tree and JSON; it does not apply to `--csv`. Sections are recognized by the kind the parser gives a node, not by its name, so an APE item named `Frame` stays.
- A JSON report (schema version 1) is an object:

  ```json
  {
    "schemaVersion": 1,
    "tool": { "name": "encspect", "version": "1.0.0+…" },
    "source": "song.mp3",
    "root": { "name": "File", "value": "MPEG Audio", "offset": 0, "length": 4096, "children": [
      { "name": "MPEG Audio", "kind": "Stream", "value": 10, "unit": "frames", "offset": 0, "length": 4096, "children": [ … ] } ] },
    "streams": [
      { "nodes": [[0]], "codec": "MPEG-1 Layer III", "sampleRate": 44100, "channels": 2, "channelMode": "Joint stereo",
        "samples": 11520, "duration": 0.26122448979591836, "averageBitrate": 138.97, "isVbr": true, "encoder": "LAME 3.100", "encoderConfidence": "High",
        "series": [ { "name": "Bitrate", "unit": "kbps", "axis": "frameOffset", "x": [0, 417, …], "values": [128, 160, …] } ] } ],
    "findings": [
      { "severity": "Warning", "code": "mpeg.crc-mismatch", "message": "2 frames fail the CRC check, …", "offset": 421, "length": 2, "path": [0, 4, 1], "count": 2 } ]
  }
  ```

  A node has `name`, `kind` unless it is a plain detail (`Tag`, `Stream`, `Frame`, `SideInfo` or `Statistics`), `value` as a JSON string, number or boolean with its `unit` where it has one (`"value": 128, "unit": "kbps"`; a CRC or flags field is a number too), `offset`, `length`, `bitOffset` and `bitLength` for a bit field, `ranges` for a node whose bytes are not contiguous (`"ranges": [{ "offset": 26, "length": 2 }, { "offset": 64, "length": 2 }]`, with `offset` and `length` covering them from the first to the last), and `children`. `streams` lists the audio streams as the format defines them: an MPEG stream continues across a tag embedded in it and ends where the version, layer or sample rate changes; `nodes` are the paths of the nodes that hold it, `duration` is in seconds and `averageBitrate` in kbps. Each stream's `series` are numbers for graphs: per frame (`axis` `frameOffset`, `x` the frame's byte offset) and per second of audio (`seconds`, `x` the start of each second); MPEG gives the bitrate and, for Layer III, the bit reservoir fill, both ways. A finding's `path` is the child indexes from the root to the node it points to, and `count` the number of cases it stands for, as findings of one kind are reported once per stream. `schemaVersion` changes when a field changes meaning or type or goes away, not when one is added.
- The CSV row of a file has its format, the number of streams, the first stream's codec, the duration and samples of all streams together, their average bitrate weighted by duration, the first stream's sample rate and channel mode, the first encoder named and its confidence, the tags (also those a container holds in its chunks), and the number of errors, warnings and infos.
- Trees are written down to 64 levels below the root; deeper children are left out, which JSON marks with `"hasOmittedChildren": true` on the node and the text tree with a line. No parser nests that deep today; a damaged container could.
- `--only-format mpeg` analyzes only files whose detected format starts with that name and skips the rest; detection looks at the bytes, not the extension. A name that starts no known format is wrong usage.
- `--fail-on info|warning|error` makes the exit code 1 when a finding of that severity or worse turns up.

Exit codes: 0 success, 1 findings as `--fail-on` says, 2 wrong usage, 3 a file or folder could not be read or is not a regular file, and 4 the output could not be written, as on a full disk; the run stops there. A closed pipe, as with `| head`, is not an error. A parser that fails on a file (a bug, not damage in the file) leaves an error finding `parse.failed` in that file's report, and the run goes on. A file or folder that cannot be read, and anything that is not a regular file, still gets a report with one error finding (`file.unreadable`, `folder.unreadable` or `file.not-regular`), so JSON, NDJSON and CSV list every input, and a line on stderr.

On Linux, a file or folder whose name is not valid UTF-8 cannot be opened: .NET decodes such names lossily, so the name it lists no longer leads to the file. It is reported as unreadable.

All output is UTF-8 without a byte order mark, text from files included: JSON writes non-ASCII characters as they are, not as `\u` escapes. A CSV field that starts with `=`, `+`, `-` or `@` gets a leading apostrophe, so a spreadsheet does not run it as a formula.

## GUI

```sh
dotnet run -c Release --project src/Encspect.Gui [-- <file-or-folder>...]
```

The main window lists a folder's files on the right with their format, codec, duration, bitrate (VBR or CBR), sample rate, channel mode, encoder with its confidence, tags and findings by severity; columns sort by the figures behind them, so durations and bitrates sort as numbers. Choose a folder in the tree on the left (home, music, desktop and the volumes, subfolders listed when expanded), with File, Open Folder (Cmd+O on macOS, Ctrl+O elsewhere), or drop files and folders onto the window; files and folders named on the command line open the same way. Files are analyzed in the background, a few at a time, and rows fill in as they are done; Stop cancels, and choosing another folder cancels the run before it. "Include subfolders" walks the whole tree as the CLI does. Rows keep a file's summary, not its report.

Enter or a double-click opens the selected file in the detail window, which is reused for the next file and follows the list's selection while it is open; Previous and Next (Cmd or Ctrl with Up and Down) step through the list in its order, and Esc closes it. It opens centered over the main window the first time and where it was after that, also in later sessions (`settings.json` in the user's application data folder, under `Encspect`). "Open in New Window" in the list's context menu opens another detail window that stays on its file, for comparing two files. The detail window always analyzes the file again. It shows the streams at the top, the structure tree, whose rows are made only as they are shown and whose children are read only when expanded, so a stream of 100,000 frames opens at once, and a hex view read from the file as it scrolls; selecting a node marks its bytes, all its ranges where they are not contiguous, and for a field of a few bits the bits themselves, also in the bit strip below. The findings can be filtered by severity, and selecting one selects its node. Export (Cmd or Ctrl+S) writes the report as text with offsets or as JSON, as `--offsets` and `--json` would.

Light and dark mode, the accent color and the UI font follow the system, the file dialogs are the system's, and on macOS the menus are in the menu bar. The GUI is built on Avalonia; its dependencies and their licenses are listed in [third_party/README.md](third_party/README.md). Graphs and a summary cache for folder views come next.

## Development

```sh
dotnet build -c Release
dotnet test -c Release
dotnet run -c Release --project src/Encspect.Cli -- --offsets file.mp3
```

`tests/Encspect.Gui.Tests` holds the GUI's tests, which run on Avalonia's headless platform without a display (and on xUnit v3, which its headless test support needs; the other tests stay on xUnit 2). Set `ENCSPECT_SCREENSHOTS` to a folder to have them save screenshots of both windows in light and dark mode there.

Tag fixtures live in `tests/fixtures/tags`. `scripts/fixtures/make_tag_fixtures.py` writes the hand-built ones (CI checks they are reproducible) and `scripts/fixtures/make_external_tag_fixtures.py` the ones written by mutagen and ffmpeg. MPEG fixtures live in `tests/fixtures/mpeg`, written by `make_mpeg_fixtures.py` (hand-built, reproducible in CI) and `make_external_mpeg_fixtures.py` (LAME, TwoLAME, Shine, ffmpeg, the Helix MP3 Encoder, which `helix_tool.py` builds, and Fraunhofer's MP3 Surround encoder, which `fhg_tool.py` downloads), Layer III side-information and bit-reservoir fixtures in `tests/fixtures/layer3` (`make_layer3_fixtures.py`), Layer II and MPEG-2 multichannel fixtures in `tests/fixtures/layer2` (`make_layer2_fixtures.py`), Xing, Info, LAME tag and VBRI fixtures in `tests/fixtures/vbr` (`make_vbr_fixtures.py`), and statistics fixtures in `tests/fixtures/stats` (`make_stats_fixtures.py`; every hand-built manifest lists its regions' statistics and its streams' figures and series, computed by `stream_stats.py`); `crosscheck_mpeg_fixtures.py` confirms their manifests with ffprobe, madplay, mpg123, minimp3, mctoolamed and mp3guessenc. `conformance_mpeg_audio.py` checks `tests/conformance/mpeg2-audio.json` against the ISO/IEC 13818-4 audio conformance streams, which it downloads, since they may not be redistributed, and `check_mp3pro_samples.py` checks `tests/conformance/mp3pro-samples.json` against three mp3PRO files from the FFmpeg sample archive, downloaded for the same reason. The mp3PRO, MP3 Surround and OFL fixtures are in `tests/fixtures/layer3`, their model in `ancillary_scan.py`. minimp3 and mp3guessenc are built from the sources in `third_party` (see its README), so the checks do not depend on a download or a distribution's package version; everything still downloaded is pinned by its SHA-256 and falls back to the Wayback Machine's copy where one exists (`downloads.py`), and the weekly `sources` workflow checks every address (`check_sources.py`). Golden reports (`*.report.json`) are compared on every test run; after an intended output change, rerun the tests with `ENCSPECT_UPDATE_GOLDEN=1` and review the diff.

## Planned stack

C# on .NET, Avalonia UI for the GUI, a console app for the CLI, and one shared core library with a separate project per format. Windows, macOS and Linux on x64 and ARM64.

## License

[MIT](LICENSE)
