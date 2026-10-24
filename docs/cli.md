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

- **Paths in JSON** index the tree as written. With sections hidden, a finding's `path` and the paths in a stream's `nodes` count the siblings that are kept, so following them through the written `children` arrays reaches the node they name.
- **A path to a hidden node,** or through one, names no node of the output: the finding has no `path` (its `offset` and `length` stay), and the entry in `nodes` is `null`.

### `--explain`

Says what each node is: its description, as the parser that read it gives it, and the background topic it belongs to.

- **Text tree:** indented under each node and wrapped at 100 columns (`[topic:mid-side-stereo]`). Each share line of a category series (see "Shares over time") is followed by the description of its category.
- **JSON:** `description` and `topic`, with a `description` for each series and, for a category series, a `description` and `topic` for it and for each of its categories.
- **CSV:** does not apply.

Without it, the output is unchanged.

### `--verify`

Decodes each file's audio while reading it and compares the checksums that cover decoded audio, for the codings that have a decoder (an MD5 over all samples, a CRC over the samples of a frame). Without it, only the checksums over coded data are compared, so a stream whose checksum over decoded audio was not compared says so.

- **Cost:** decoding runs in the same pass over the file and costs time for every sample, much more than the structure analysis alone. Each decoder counts the work it does in work units (one unit is a predictor or filter tap on one sample of one channel, about 0.6 ns on the machines it was measured on), and decoding stops with an info finding (`decode.limit`) past the larger of 2^35 units, about 21 seconds, and the file's size times 2,048 units a byte, about 1.2 seconds a megabyte. A stream that stops is verified as unknown unless a checksum compared before the stop failed, which makes it "no"; the structure analysis carries on.
- **Formats without a decoder:** the stream says so (`decode.no-decoder`, info) and the report is otherwise the same. FLAC has a decoder (native and in Ogg); the decoders of the other formats arrive with them.
- **Findings:** a stored checksum that differs is a warning, so `--fail-on warning` fails the run. A decoder that fails (`decode.failed`) is an error and a bug.
- **Output:** the text tree, JSON and CSV carry the verdict (see below). Without `--verify` the tree and every other field are the same.

### The verification of a stream

A stream of a coding that declares its checksums has a verification, with or without `--verify`. FLAC declares them (frame header CRC-8, frame CRC-16 and STREAMINFO MD5); the other codings do so as their decoders land. A stream is verified when every checksum it stores was compared and holds, and at least one of them covers all of its audio (`encspect --learn verification` explains it).

- **Text tree:** a line under the stream's first node.
  - `Verified: yes (frame header CRC-8 40 of 40, frame CRC-16 40 of 40, STREAMINFO MD5 1 of 1)`: each checksum with the values compared of those stored.
  - `Verified: no (STREAMINFO MD5 1 of 1 differ)`: a stored checksum differs.
  - `Verified: unknown (STREAMINFO MD5 not compared: decoding was not asked for)`: something stored was not compared, or nothing compared covers all of the audio.
  - `Verified: yes (lossy stream; block header CRC 120 of 120)`: the checksums vouch for the output of a lossy coding (a WavPack hybrid file without its correction file).
- **JSON:** `verification` in the stream, after `protection`:
  - `isVerified`, written where it is not null;
  - `isLossy`, written where it is true;
  - `decoding`: `notRequested`, `noDecoder`, `notPossible`, `decoded`, `stopped` or `failed`;
  - `decodedSamples`, the samples per channel the decoder decoded;
  - `checksums`, each with `name`, `covers` (`header`, `codedData` or `decodedAudio`), `coversAllAudio`, `stored`, `checked` and `failed`.
- **CSV:** the `Is Verified` column, filled only with `--verify`: `false` where a stream's verification is false, `true` where every audio stream's is true, empty otherwise. Without `--verify` it is empty for every file, so a folder export never mixes verdicts from coded data alone with empty ones.

### `--learn`

`encspect --learn` lists the background articles, and `encspect --learn mid-side-stereo` prints one as plain text: why a coding tool such as mid/side stereo or scfsi exists, how it works, what seeing it in a file means and where it is specified.

### `--only-format`

`--only-format mpeg` (or `mp4`, `aac`, `ogg`, `musepack`, `wavpack`, `flac`, `tta`, `ac-3`, `asf`, `dts`, `dts-hd`, `matroska`, several separated by commas) analyzes only files whose detected format starts with that name and skips the rest. Detection looks at the bytes, not the extension. A name that starts no known format is wrong usage, as is a missing list (`--only-format` followed by a file or folder).

- **Names:** the same as for `--as` (see below), so `ac3` is `ac-3`. A prefix several formats share (`a`) selects all of them. `dts` is the DTS parser's own name, so `--only-format dts` selects raw DTS files and not `.dtshd` files, which are `dts-hd`.

### `--as <format>`

`--as aac` skips format detection and analyzes every file as that format, for a file whose format detection does not find (a damaged one) or finds wrongly. The format is named like this, ignoring case, spaces, hyphens and underscores; one that fits no format, or several (`a`), is wrong usage, as is a missing name (`--as` followed by a file or folder).

- **The formats and the start of their names:** `mpeg` (MPEG Audio), `mp4`, `aac`, `ogg`, `musepack`, `wavpack`, `flac`, `tta`, `ac-3` (also `ac3`), `asf`, `dts`, `dts-hd`, `matroska`.
- **Other names a format goes by:** `mp1`, `mp2`, `mp3` for MPEG Audio; `m4a`, `m4b`, `mov`, `3gp` for MP4; `adts`, `adif` for AAC; `eac3`, `ec3` for AC-3; `vorbis`, `opus`, `speex`, `spx`, `oga` for Ogg; `wma` for ASF; `mkv`, `mka`, `webm` for Matroska; `mpc` for Musepack; `wv` for WavPack. `dtshd` is the name of DTS-HD itself (ignoring the hyphen), so `--as dtshd` analyzes a file as a DTS-HD file, with its chunks; before the DTS-HD parser existed it meant DTS. `--as dts` reads the same file as a raw DTS stream.

- **Tags:** found as always; only the format parser changes. The forced format's declared size decides which signatures inside the data are no tags only where the format matches the data; otherwise the detected format's does, so forcing a format that is not there leaves the tags as detection finds them.
- **Report:** says it was forced and what detection would have chosen (see "Format detection" below), in the text tree, in JSON and in the `format.forced` finding. A CSV row does not mark it: `--as` applies to the whole run, so the command line says which files were forced.
- **With `--only-format`:** wrong usage. `--only-format` selects files by the format detection finds and `--as` skips detection, so the two cannot both say which files are meant.
- **Wrong format:** a format forced on a file that is none of it gives findings (leading junk, sync lost, invalid data), never a failed parser.

### `--version`

Prints `encspect <version>` and exits with 0, for example `encspect 0.1.0+4da770c4bc662c9524f4f219682b00c7634a48fc`.

- **Same string everywhere:** it is the `version` in a JSON report's `tool` object and the version the GUI's About window shows.
- **Commit suffix:** the part after `+` names the commit the build was made from, so a bug report identifies the exact source. A build without Git metadata has no suffix.
- **Alone:** with any other argument it is wrong usage.

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
  "tool": { "name": "encspect", "version": "0.1.0+…" },
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
  - `playbackGroup` is present on streams that play at the same time (the tracks of an MP4 movie or of a Matroska Segment, the streams of one link of an Ogg chain, the streams of one ASF file, the independent substreams of an E-AC-3 file or track); streams with the same number are one presentation, and a summary counts the longest of them once (a movie with one audio track has a group of one). Streams without it, and streams of different groups, follow one another and add up.
    - An MP4 or Matroska track whose codec parser reports several streams that play together (E-AC-3 independent substreams) gives all of them its movie's or Segment's group. Several streams of a track that follow one another (a configuration change) are not grouped: no one number says that they follow each other beside the other tracks.
    - An ASF file's streams are interleaved in one Data Object and play at the same time, or are bitrate alternatives of which one plays: either way the longest counts once.
  - `encoder` and `encoderConfidence` name the software that turned the audio into the stream; `writer` and `writerConfidence` the software that put it into the file (the muxer, the container writer, a tagger). They are resolved apart, each from its own evidence, and either is left out where nothing names one. A writer that differs from the encoder is not a conflict. `encoder` holds what the stream and the tags that name an encoder show (a TSSE frame, a BUILDVER chunk, ffmpeg's `Lavc…` ID); `writer` holds what the container's layout and the tags that name a writer show (ASF attributes, MP4's ©too and ©swr, an APEv2 Encoder item, ffmpeg's `Lavf…` text).
  - `channelLayout` lists the speaker each channel feeds, in the stream's channel order as its format defines it. A channel without a speaker position, such as either program of dual channel, is `"discrete"`.
  - `channelLayoutName` is the layout's common name where it has one: `mono`, `stereo`, `quadraphonic`, `5.1`, `7.1`, `22.2` and others.
  - `hasObjectAudio` is `true` for a stream whose data carry object audio that was parsed (E-AC-3 with joint object coding); DTS:X is reported through the profile and the `HasDtsX` statistics, not through this flag, and `jocObjectCount` is the number of objects its JOC payload reconstructs (joc_num_objects, not the OAMD object_count); both are left out otherwise.
  - `kind` is `Video` or `Other` for a stream that is not audio (an ASF video stream that is protected, reported so that its protection is not lost), which then has no `sampleRate`, `channels`, `samples` or `isVbr`; it is left out for audio. The summary and the CSV take the codec, sample rate, channel mode, duration and samples from the audio streams only (empty where there is none), and the protection from all streams.
  - A track counts as encrypted unless its key information says no sample is protected and nothing in the file says otherwise; Encspect does not prove sample by sample that encrypted tracks are partly clear.
  - `isProtected` is `true` for a stream that its container protects, with `protection` (both are left out otherwise): `scheme` names it ("Common Encryption, AES-CTR (cenc)", "iTunes protection (drms)", "Microsoft Digital Rights Manager version 1"), `isEncrypted` says whether its samples are encrypted, so that the analyzer read no frames of them (false only where the key information says that no sample is protected and nothing in the file says otherwise), and `systems` lists the DRM systems the file names, from the DASH-IF registry for MP4's pssh boxes and from the specification for ASF's Advanced Content Encryption Object; a system the tables do not list is its ID, in lower case as the registry writes it (`ProtectionSystems.Text`), in both formats. Nothing is decrypted, and no key or license is read.
  - `verification` is written for a stream of a coding that stores checksums (see "The verification of a stream"); the schema version stays 1, since it adds a field.
  - `multilingualChannels` counts the MPEG-2 multilingual channels a stream carries besides its layout.
  - `series` are numbers for graphs, per frame (`axis` `frameOffset`, `x` the frame's byte offset) and per second of audio (`seconds`, `x` the start of each second). MPEG gives the bitrate and, for Layer III, the bit reservoir fill, both ways, the coded bandwidth of Layers I and II in Hz and, for streams of two channels, the channel bit share in percent (the first channel's share of the frame's audio bits). A series that belongs to a shared concept also has `concept`.
  - `quantity` names the quantity a numeric series shows, such as `Bitrate`, where the series of one stream that show it in other forms (per frame, per second of audio) carry the same text; the GUI offers them as one entry with the forms to choose from. It is left out where a series stands alone. MPEG sets it on the bitrate (`Bitrate`), the bit reservoir fill (`Reservoir Fill`), the coded bandwidth and the channel bit share.
  - `concept` is `stereoCoding`, `blockSwitching`, `codedBandwidth` or `bitDistribution`, on a numeric series or a category series, and is left out where a series has none. Front ends group graphs by it, never by name.
  - `categorySeries` holds the category of each value of each unit (a frame, packet or block) of the stream, for graphs and for the summary over the stream. It is left out when the stream has none, and a field was added, so the schema version stays 1. Each entry has:
    - `name`, the name of the tally node it is anchored to, and `valueLabel`, what one value is (`granule and channel`);
    - `valuesPerUnit` and `valueNames` for a unit of a fixed number of values (`granule 1, channel 1`, …); a variable layout writes `valueCounts`, the number of values of each unit as runs of `[count, units]`; a fixed layout of one value writes neither, and one value per unit is meant;
    - `categories`, each with its `name` and `count` (weighted, and including values past the caps), and `isReserved` where the category marks a value the specification does not allow; with `--explain` also `description` and `topic`, as the series itself has;
    - `total`, the sum of the counts, and `uncodedValues` where values with a category past 254 of a unit or 2^24 of the series were counted without a code;
    - `xSeries`, the name of the per-frame series whose `x` the units share, or `x` with the byte offset of each unit where they share none (a grouping has its source's);
    - `runs`, the codes as runs of `[code, length]`, value by value within each unit and unit by unit, `null` for a value without a category; the codes index `categories`. A steady stream is a few runs. `weights` gives runs of weights where a value stands for more than one;
    - `seconds`, the first unit of each interval of audio time (the starts are the `x` of the stream's per-second series), absent without one;
    - for a grouping of another series (long, start and stop blocks as "long"), `groupOf`, the name of its source, and `map`, the category of the group for each source category, and no runs.

    ```json
    "categorySeries": [ { "name": "Block Usage", "concept": "blockSwitching", "valueLabel": "granule and channel", "valuesPerUnit": 4,
      "valueNames": ["granule 1, channel 1", "granule 1, channel 2", "granule 2, channel 1", "granule 2, channel 2"],
      "categories": [ { "name": "Long", "count": 432 }, { "name": "Start", "count": 12 }, { "name": "Short", "count": 24 }, { "name": "Mixed", "count": 0 }, { "name": "Stop", "count": 12 }, { "name": "Reserved", "count": 0 } ],
      "total": 480, "xSeries": "Bitrate", "runs": [[0, 198], [1, 2], [2, 4], [4, 2], [0, 76], …], "seconds": [0, 40, 80] } ]
    ```
- **Detection:** `detection`, present only when other formats also match or the analysis was forced, has:
  - `format`, the format that ran, and `evidence` for it (`class`, `offset`, `frames`, `verifiedFrames` and, where the parser gives one, `note`);
  - `isForced`, and with it `detected`, the format detection would have chosen (null if it finds none);
  - `alternatives`, a list of `format` and `evidence`.
- **Findings:** `path` is the child indexes from the root to the node a finding points to. `count` is the number of cases it stands for, since findings of one kind are reported once per stream.
- **Depth:** trees are written down to 64 levels below the root. Deeper children are left out, which JSON marks with `"hasOmittedChildren": true` on the node and the text tree with a line. No parser nests that deep today; a damaged container could.
- **Schema version:** `schemaVersion` changes when a field changes meaning or type or goes away, not when one is added.

## Shares over time

A category series that has a time index and an anchor in the tree prints, after its anchor node and its children and at the anchor's depth, one line for each category that occurs, in the series' category order: the category's share of the values in each interval of audio time, in percent rounded half to even to 0.1, as the tallies round, and `-` for an interval without values.

The lines follow the anchor and its entries (the tally's own lines, not shown here):

```
      Block Usage: 480 granules
      Long Share Over Time: 100,90,80
      Start Share Over Time: 0,2.5,5
      Short Share Over Time: 0,5,10
      Stop Share Over Time: 0,2.5,5
```

- **Cost:** each interval is counted once for all categories, so the lines cost a pass over the values however many categories there are.
- **Several streams** anchored to one node label their lines with the stream's number (`Long Share Over Time (stream 2)`).
- **`--offsets`** adds the anchor's location to each line, **`--hide statistics`** hides the lines with their anchor, and **`--explain`** adds each category's description.
- **The per-frame codes** are not printed, as per-frame numeric series are not; the JSON has them.
- **CSV** has no category columns.

## CSV rows

The CSV row of a file has:

- its format and the number of streams;
- the first audio stream's codec;
- the duration and samples of the audio streams together, where streams that play at the same time (a playback group: the tracks of a movie or a Matroska Segment, the streams of one Ogg chain link, the streams of one ASF file, the independent substreams of an E-AC-3 file or track) count the longest once;
- their average bitrate, weighted by duration;
- the first audio stream's sample rate, channel mode and channel layout (`5.1 (front left, front right, front center, back left, back right, LFE)`, or only the speakers where the layout has no common name);
- the first encoder named by an audio stream and its confidence (`Encoder`, `Encoder Confidence`), and the first writer named by any stream and its confidence (a fact about the file) (`Writer`, `Writer Confidence`, the two columns after the encoder's);
- the tags, including those a container holds in its chunks;
- the number of errors, warnings and infos;
- `Has Object Audio`, `true` or `false` for a file with a stream (empty without one), and `JOC Object Count`, the most objects a stream's joint object coding reconstructs, empty where it is not known;
- `Is Protected`, `true` if any stream is protected and `false` otherwise (empty for a file without a stream), `Is Verified` (see `--verify`), `Protection Scheme`, the schemes of the protected streams, and `DRM Systems`, the systems their files name; several are separated by `; `.

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
