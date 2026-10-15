# Command line

```sh
encspect [options] [--] <file-or-folder>...
```

The output is a text tree by default; `encspect --help` lists the options.

## Inputs

- Each folder is analyzed with its subfolders, hidden files and folders included, in the order of their paths. Output starts with the first file.
- A symbolic link to a folder is followed only when it leads outside the folder named on the command line and not above it, so every folder is analyzed once and link loops end.
- Anything that is not a regular file (a FIFO, a device, a socket, or a file of a kernel pseudo-file system such as `/proc` or `/sys`) is reported without being opened, so a FIFO cannot block the run.
- Standard input is not read: `-` is wrong usage, and a file named `-` is given as `./-`. After `--`, every argument is a file or folder, even one that starts with a dash.
- On Linux, a file or folder whose name is not valid UTF-8 cannot be opened: .NET decodes such names lossily, so the name it lists no longer leads to the file. It is reported as unreadable.

## Output formats

- **Text tree:** the default.
- `--json`: one JSON array of reports.
- `--ndjson`: one compact report per line, which stays usable line by line if a long run stops early.
- `--csv`: one summary row per file.

Reports are written as each file is analyzed. All output is UTF-8 without a byte order mark, text from files included; JSON writes non-ASCII characters as they are, not as `\u` escapes. A CSV field that starts with `=`, `+`, `-` or `@` gets a leading apostrophe, so a spreadsheet does not run it as a formula.

## Options

### `--offsets`

Adds the byte offset and length of each node, and of the node each finding points to, to the text tree: `[0x1A, 4 bytes]`.

- **Bit fields:** a field that does not cover whole bytes, such as an MPEG header or side-information field, shows:
  - its first byte;
  - the bit within that byte where it starts, counted from the most significant bit (0 to 7);
  - its width in bits.

  `[0x1+3, 2 bits]` is two bits starting at the fourth bit of byte 0x1.
- **Least significant bit first:** Vorbis packs its fields from the least significant bit, so its bit fields count from there and run on into the next byte's least significant bit: `[0x1+3, 2 bits, LSB first]`. JSON adds `"bitOrder": "leastSignificantFirst"`.
- **Split nodes:** a node whose bytes are not contiguous in the file, such as a packet that spans the pages of a container, shows the range that covers them and then each range. `[0x1A, 40 bytes in 0x1A 2 bytes, 0x40 2 bytes]` spans 40 bytes from 0x1A but holds only 0x1A to 0x1B and 0x40 to 0x41.
- **Other formats:** JSON always has these (`offset` and `length`, plus `bitOffset` and `bitLength` for a bit field and `ranges` for a split node), and CSV has none. So `--offsets` with `--json`, `--ndjson` or `--csv` is wrong usage.

### `--hide tags,frames,side-info,statistics`

Leaves those sections out of the text tree and JSON; it does not apply to `--csv`. Sections are recognized by the kind the parser gives a node, not by its name, so an APE item named `Frame` stays.

### `--explain`

Says what each node is: its description, as the parser that read it gives it, and the background topic it belongs to.

- **Text tree:** indented under each node and wrapped at 100 columns (`[topic:mid-side-stereo]`).
- **JSON:** `description` and `topic`, with a `description` for each series.
- **CSV:** does not apply.

Without it, the output is unchanged.

### `--learn`

`encspect --learn` lists the background articles, and `encspect --learn mid-side-stereo` prints one as plain text: why a coding tool such as mid/side stereo or scfsi exists, how it works, what seeing it in a file means and where it is specified.

### `--only-format`

`--only-format mpeg` (or `mp4`, `aac`, `ogg`, `musepack`, `wavpack`, `ac-3`, `asf`, `dts`, `dts-hd`, several separated by commas) analyzes only files whose detected format starts with that name and skips the rest. Detection looks at the bytes, not the extension. A name that starts no known format is wrong usage, as is a missing list (`--only-format` followed by a file or folder).

- **Names:** the same as for `--as` (see below), so `ac3` is `ac-3`. A prefix several formats share (`a`) selects all of them. `dts` is the DTS parser's own name, so `--only-format dts` selects raw DTS files and not `.dtshd` files, which are `dts-hd`.

### `--as <format>`

`--as aac` skips format detection and analyzes every file as that format, for a file whose format detection does not find (a damaged one) or finds wrongly. The format is named like this, ignoring case, spaces, hyphens and underscores; one that fits no format, or several (`a`), is wrong usage, as is a missing name (`--as` followed by a file or folder).

- **The formats and the start of their names:** `mpeg` (MPEG Audio), `mp4`, `aac`, `ogg`, `musepack`, `wavpack`, `ac-3` (also `ac3`), `asf`, `dts`, `dts-hd`.
- **Other names a format goes by:** `mp1`, `mp2`, `mp3` for MPEG Audio; `m4a`, `m4b`, `mov`, `3gp` for MP4; `adts`, `adif` for AAC; `eac3`, `ec3` for AC-3; `vorbis`, `opus`, `speex`, `spx`, `oga` for Ogg; `wma` for ASF; `mpc` for Musepack; `wv` for WavPack. `dtshd` is the name of DTS-HD itself (ignoring the hyphen), so `--as dtshd` analyzes a file as a DTS-HD file, with its chunks; before the DTS-HD parser existed it meant DTS. `--as dts` reads the same file as a raw DTS stream.

- **Tags:** found as always; only the format parser changes. The forced format's declared size decides which signatures inside the data are no tags only where the format matches the data; otherwise the detected format's does, so forcing a format that is not there leaves the tags as detection finds them.
- **Report:** says it was forced and what detection would have chosen (see "Format detection" below), in the text tree, in JSON and in the `format.forced` finding. A CSV row does not mark it: `--as` applies to the whole run, so the command line says which files were forced.
- **With `--only-format`:** wrong usage. `--only-format` selects files by the format detection finds and `--as` skips detection, so the two cannot both say which files are meant.
- **Wrong format:** a format forced on a file that is none of it gives findings (leading junk, sync lost, invalid data), never a failed parser.

### `--fail-on info|warning|error`

Makes the exit code 1 when a finding of that severity or worse turns up.

## Format detection

The host picks the format from the evidence each parser finds: its class, where the matched structure starts, and how many consistent and checksum-verified frames it found (see "Format detection" in the [plan](PLAN.md)). Two things show in the report when detection is not the whole story, and only then:

- **Also matches:** other formats whose evidence is real, not a stray header-like match: the chosen format's class, or, above class 30, a frame that passes a checksum or a chain of eight frames or more. A format whose data lies inside a container's declared data (AC-3 frames in an MP4 file) is the container's payload and not listed.
- **Forced:** the analysis ran the format `--as` named.

The text tree ends with a "Format detection:" section:

```
Format detection:
  Analyzed as AAC as requested; detection would take MPEG Audio.
  Also matches: MPEG Audio (class 90, starting at 0x0, 16 consistent units, 0 checksum-verified)
```

Findings: `format.forced` (info) for a forced analysis, and `format.ambiguous` (info) when two formats match with exactly the same evidence, in which case the one the host lists first is taken.

## JSON reports

A JSON report (schema version 1) is an object:

```json
{
  "schemaVersion": 1,
  "tool": { "name": "encspect", "version": "1.0.0+…" },
  "source": "song.mp3",
  "root": { "name": "File", "value": "MPEG Audio", "offset": 0, "length": 4096, "children": [
    { "name": "MPEG Audio", "kind": "Stream", "value": 10, "unit": "frames", "offset": 0, "length": 4096, "children": [ … ] } ] },
  "streams": [
    { "nodes": [[0]], "codec": "MPEG-1 Layer III", "sampleRate": 44100, "channels": 2, "channelMode": "Joint stereo",
      "channelLayout": ["front left", "front right"], "channelLayoutName": "stereo", "samples": 11520, "duration": 0.26122448979591836, "averageBitrate": 138.97, "isVbr": true, "encoder": "LAME 3.100", "encoderConfidence": "High", "writer": "FFmpeg (Lavf60.16.100)", "writerConfidence": "Low",
      "series": [ { "name": "Bitrate", "unit": "kbps", "axis": "frameOffset", "x": [0, 417, …], "values": [128, 160, …] } ] } ],
  "findings": [
    { "severity": "Warning", "code": "mpeg.crc-mismatch", "message": "2 frames fail the CRC check, …", "offset": 421, "length": 2, "path": [0, 4, 1], "count": 2 } ]
}
```

- **Nodes** have:
  - `name`;
  - `kind`, unless the node is a plain detail (`Tag`, `Stream`, `Frame`, `SideInfo` or `Statistics`);
  - `value`, as a JSON string, number or boolean, with its `unit` where it has one (`"value": 128, "unit": "kbps"`). A CRC or flags field is a number too;
  - `offset` and `length`;
  - `bitOffset` and `bitLength` for a bit field;
  - `ranges` for a node whose bytes are not contiguous (`"ranges": [{ "offset": 26, "length": 2 }, { "offset": 64, "length": 2 }]`), with `offset` and `length` covering them from the first to the last;
  - `children`.
- **Streams** are the audio streams as the format defines them; an MPEG stream continues across a tag embedded in it and ends where the version, layer or sample rate changes.
  - `nodes` are the paths of the nodes that hold the stream.
  - `duration` is in seconds and `averageBitrate` in kbps.
  - `encoder` and `encoderConfidence` name the software that turned the audio into the stream; `writer` and `writerConfidence` the software that put it into the file (the muxer, the container writer, a tagger). They are resolved apart, each from its own evidence, and either is left out where nothing names one. A writer that differs from the encoder is not a conflict.
  - `channelLayout` lists the speaker each channel feeds, in the stream's channel order as its format defines it. A channel without a speaker position, such as either program of dual channel, is `"discrete"`.
  - `channelLayoutName` is the layout's common name where it has one: `mono`, `stereo`, `quadraphonic`, `5.1`, `7.1`, `22.2` and others.
  - `hasObjectAudio` is `true` for a stream whose data carry object audio that was parsed (E-AC-3 with joint object coding); DTS:X is reported through the profile and the `HasDtsX` statistics, not through this flag, and `jocObjectCount` is the number of objects its JOC payload reconstructs (joc_num_objects, not the OAMD object_count); both are left out otherwise.
  - `multilingualChannels` counts the MPEG-2 multilingual channels a stream carries besides its layout.
  - `series` are numbers for graphs, per frame (`axis` `frameOffset`, `x` the frame's byte offset) and per second of audio (`seconds`, `x` the start of each second). MPEG gives the bitrate and, for Layer III, the bit reservoir fill, both ways.
- **Detection:** `detection`, present only when other formats also match or the analysis was forced, has:
  - `format`, the format that ran, and `evidence` for it (`class`, `offset`, `frames`, `verifiedFrames` and, where the parser gives one, `note`);
  - `isForced`, and with it `detected`, the format detection would have chosen (null if it finds none);
  - `alternatives`, a list of `format` and `evidence`.
- **Findings:** `path` is the child indexes from the root to the node a finding points to. `count` is the number of cases it stands for, since findings of one kind are reported once per stream.
- **Depth:** trees are written down to 64 levels below the root. Deeper children are left out, which JSON marks with `"hasOmittedChildren": true` on the node and the text tree with a line. No parser nests that deep today; a damaged container could.
- **Schema version:** `schemaVersion` changes when a field changes meaning or type or goes away, not when one is added.

## CSV rows

The CSV row of a file has:

- its format and the number of streams;
- the first stream's codec;
- the duration and samples of all streams together;
- their average bitrate, weighted by duration;
- the first stream's sample rate, channel mode and channel layout (`5.1 (front left, front right, front center, back left, back right, LFE)`, or only the speakers where the layout has no common name);
- the first encoder named and its confidence (`Encoder`, `Encoder Confidence`), and the first writer named and its confidence (`Writer`, `Writer Confidence`);
- the tags, including those a container holds in its chunks;
- the number of errors, warnings and infos;
- `Has Object Audio`, `true` or `false` for a file with a stream (empty without one), and `JOC Object Count`, the most objects a stream's joint object coding reconstructs, empty where it is not known.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Findings, as `--fail-on` says |
| 2 | Wrong usage |
| 3 | A file or folder could not be read, or is not a regular file |
| 4 | The output could not be written, as on a full disk; the run stops there |

- A closed pipe, as with `| head`, is not an error.
- A parser that fails on a file (a bug, not damage in the file) leaves an error finding `parse.failed` in that file's report, and the run goes on.
- A file or folder that cannot be read, and anything that is not a regular file, still gets a report with one error finding (`file.unreadable`, `folder.unreadable` or `file.not-regular`), so JSON, NDJSON and CSV list every input. A line goes to stderr too.
