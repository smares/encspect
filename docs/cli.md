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

Reports are written as each file is analyzed. All output is UTF-8 without a byte order mark, text from files included; JSON writes non-ASCII characters as they are, not as `\u` escapes. A CSV field that starts with `=`, `+`, `-` or `@` gets a leading apostrophe, so a spreadsheet does not run it as a formula; the columns the writer formats from numbers (the counts, duration, samples, bitrate, sample rate, `JOC Object Count` and the measured columns) skip the guard, as a number cannot start one. The CSV has five columns more than before, at the end: `Spectral Cutoff (Hz)`, `Sample Peak (dBFS)`, `Maximum True Peak Level (dBTP)`, `Integrated Loudness (LUFS)` and `Loudness Range (LU)`: a reader that counts columns must be changed.

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

### `--hide tags,frames,side-info,statistics,measurements`

Leaves those sections out of the text tree and JSON; it does not apply to `--csv`. Sections are recognized by the kind the parser gives a node, not by its name, so an APE item named `Frame` stays.

- **Paths in JSON** index the tree as written. With sections hidden, a finding's `path` and the paths in a stream's `nodes` count the siblings that are kept, so following them through the written `children` arrays reaches the node they name.
- **`measurements`** hides the measurement lines of the text tree and the `measurements` object of the JSON (see `--measure`); it is the one section that is not a kind of node.
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
- **Formats without a decoder:** the report is the same as without `--verify`.
  - FLAC (native and in Ogg), WavPack, Monkey's Audio, and TrueHD and MLP have decoders that compare checksums. WAV, Wave64 and AIFF have the `pcm` decoder, which stores no checksum to compare: `--verify` alone opens no PCM session, reads none of the samples (so it costs a folder of WAV files nothing) and adds nothing to the report; `--measure` decodes them.
  - `decode.no-decoder` (info) is raised only where a parser starts a decode session for a codec ID that no decoder has. Of the formats today that is TTA; WAV, Wave64 and AIFF do not raise it.
  - Formats whose parsers start no session (MPEG audio and the others) get neither the finding nor a `verification`.
  - Streams a container does not hand to a codec parser (FLAC, WavPack and TrueHD in Matroska today, `matroska.codec-not-analyzed`) get no verification either, although a decoder for the coding exists.
- **Findings:** a stored checksum that differs is a warning, so `--fail-on warning` fails the run. A decoder that fails (`decode.failed`) is an error and a bug.
- **Output:** the text tree, JSON and CSV carry the verdict (see below). Without `--verify` the tree and every other field are the same.

### `--measure [spectrum,levels,loudness,true-peak]`

Decodes, verifies and measures each file's audio, for the codings that have a decoder, in the same pass: the average spectrum and the spectral cutoff of the decoded samples with the spectrogram and waveform grids behind them, the levels: sample peak, RMS level, DC offset, clipping runs, digital silence and bit-depth usage, the loudness of ITU-R BS.1770-5 and EBU R 128 (integrated loudness, loudness range, the maxima of the momentary and short-term loudness) and the true peak (`encspect --learn measurements`, `--learn levels`, `--learn loudness`, `--learn loudness-range`, `--learn sample-peak-and-true-peak`). It implies `--verify`: the checksums over decoded audio are compared as with `--verify`, and measuring changes neither the verdict nor a `decode.*` finding.

- **Kinds:** a list after the option (`--measure spectrum,levels`, or `--measure=levels`) picks measurements by name: `spectrum`, `levels`, `loudness` and `true-peak`; without a list every kind the program has is measured. A value that is not a kind is taken for a path.
- **Cost:** about 16 to 21 ns for each sample of each channel for everything on the machine the work weights were measured on (`MeasurementWorkTests` prints them), of which the levels are about 2 ns at 16 and 24 bits (`--measure levels` alone), counted in work units of its own, apart from the decoders': past the larger of 2^35 units and the file's size times 2,048 units a byte, measuring stops with an info finding (`measure.limit`) and decoding and verification go on. The state of the measurements of one file is bounded at 64 MiB, and at most 32 channels are measured.
- **PCM files:** WAV, Wave64 and AIFF (including AIFF-C `sowt`, `in24`, `in32`, `raw `, `fl32`, `fl64`, `alaw` and `ulaw`) are decoded by the `pcm` decoder for the measurements, which read the container's samples as stored (see [wav.md](formats/wav.md#decoding)). A file of integers wider than 32 bits says `not measured` with the reason, and the stream has `decode.not-possible` (info).
- **Formats without a decoder:** the stream says why it has no figures (`not measured (no decoder for tta)`); MPEG audio and the other formats whose parsers start no decode session get no measurements at all, and so do WAV and AIFF streams that are not PCM (ADPCM, a codec parser's data, IEC 61937 bursts). PCM streams whose layout the file does not state consistently also get no session and no record, and no reason, among them: a block alignment that is not the channels times the width, data in a `wavl` list, 0 channels, 0 bits per sample, floats of other than 32 or 64 bits, A-law or mu-law of other than 8 bits, and an AIFF sample size of 0. Integers wider than 32 bits, in WAV and in AIFF, are refused with `decode.not-possible`. WavPack's DSD is not measured.
- **Text tree:** lines under the stream's first node, after the `Verified:` line.

      Measured: 1.0 s of 1 channel
        Spectral Cutoff: 16,796 Hz (a step of 59.1 dB, transition from 16,538 Hz, in 20 of 20 frames with signal)
        Sample Peak: -8.7 dBFS
        RMS Level: -20.1 dBFS
        DC Offset: 0.570796% of full scale (-44.9 dBFS)
        Clipping: none
        Digital Silence: none at the start or at the end
        Zero Samples: 6 samples
        Low Bits Unused: 0 of 16, the highest magnitude 12,090
        Integrated Loudness: -20.4 LUFS
        Loudness Range: none (the stream is shorter than 3 s)
        Maximum True Peak Level: -8.6 dBTP
        Maximum Momentary Loudness: -20.3 LUFS
        Maximum Short-Term Loudness: none (the stream is shorter than 3 s)

  - `Measured:` gives the audio measured, `(lossy stream)` after the duration where the verification says so, the channels measured where a stream has more than 32, the gaps skipped, and why measuring stopped early.
  - `Spectral Cutoff: none found (largest step 2.3 dB, at 2,003 Hz)` where no cutoff is found, and `not searched (the sample rate is below 8,000 Hz)` under 8,000 Hz.
  - The frequencies are those of the upper edge of a row of the spectrum (43 Hz wide at 44,100 Hz); the figures are measurements, never verdicts.
  - **Levels,** each with the figure of each channel in parentheses (named by its speaker where the stream states distinct ones, else `channel 1`, `channel 2`) for a stream of several channels. Levels are in dBFS, tenths with a sign, `-inf` for a channel whose samples are all 0, and a value of -0.0 is written 0.0.
    - `Sample Peak` is the highest of the channels, the most negative integer code counting as full scale (a float's full scale is 1.0); `RMS Level` covers all channels together, a full-scale square wave reading 0 dBFS (a full-scale sine reads -3.01 dBFS). `DC Offset` is the mean as a percentage of full scale (up to six decimals, signed) with its level in dBFS, the stream's being that of the channel with the largest one.
    - `Clipping` counts runs of 3 or more consecutive samples at the same extreme code (a float at or beyond full scale): the runs, the samples in them and the longest; three is a convention of this analysis. `none` where there is no run.
    - `Digital Silence` counts the sample frames in which every channel is exactly 0 and that start the measured samples without a break, and those that end them, in samples and (from a tenth of a second) seconds. `Zero Samples` counts all the sample frames in which every channel is 0, wherever they are, with the count of each channel where the channels differ; it includes the zeros of quiet audio and a sine's zero crossings, so it is a count of samples and not a stretch of silence (a minimum length for a stretch is left for later).
    - `Low Bits Unused` is the low bits that no sample of any channel has set, out of the declared bits, with the highest magnitude reached (and the count of each channel where they differ); `not defined (every sample is 0)` for silence. A float stream has `Has Only 16-Bit Values` and `Has Only 24-Bit Values` instead: whether every sample is a whole multiple of 2^-15 (2^-23) in [-1, 1).
    - No run and no silent stretch is counted across a gap in the decoded blocks.
  - **Loudness and true peak,** worded as the Broadcast Wave `bext` chunk names the loudness values a file states (these are measured, from the decoded audio; see `encspect --learn loudness`), in the order of the chunk's fields. Loudness is in LUFS and LU with one decimal, the true peak in dBTP with a sign on a positive value and the figure of each channel in parentheses for a stream of several channels.
    - `Integrated Loudness` is the gated loudness of the whole stream, `Loudness Range` the spread between the 10th and the 95th percentile of the short-term loudness (EBU Tech 3342), `Maximum Momentary Loudness` the highest loudness of 400 ms (taken every 20 ms) and `Maximum Short-Term Loudness` the highest of 3 s (every 100 ms). Where a stream has no figure the line says why: `none (the stream is shorter than 400 ms)` or `3 s` (`no stretch between gaps lasts` that long where there are gaps), or `none (every block is below the absolute gate of -70 LUFS)`; digital silence has a maximum of `-inf LUFS`.
    - `Loudness: not measured (loudness needs a sample rate of 8,000 Hz or more)` under 8,000 Hz, `(the sample rate is not stated)` at 0 Hz, and `(the channel positions are not stated)` for three or more channels without stated positions (channel weights follow the loudspeaker positions, BS.1770-5 Table 4: 1.41 for the surround positions, the LFE left out). One channel and two channels without positions are measured as mono and as left and right.
    - `Maximum True Peak Level` is measured by oversampling 4 times below 96 kHz and 2 times from 96 kHz (`; oversampled 2 times`), and from 192 kHz it is the sample peak (`; the sample peak, as the rate is 192,000 Hz or more`); it is never below the sample peak. At 0 Hz it is `not measured`.
    - No block of the loudness and no window of the true peak spans a gap in the decoded blocks.
  - `--explain` adds each line's description and topic.
- **JSON:** `measurements` in the stream, after `verification`: `status` (`complete`, `stopped`, `notPossible`, `formatChanged` or `failed`), `reason`, `samples`, `firstPosition`, `lostSamples`, `gaps`, `sampleRate`, `channels`, `channelsMeasured`, `channelLayout`, the `spectrum`, the `levels`, the `loudness` and the `truePeak`. A field is added and none changes meaning, so `schemaVersion` stays 1.
  - `spectrum` has `fftSize`, `frames`, `rowCount` (512), `binsPerRow`, `rows` (integers in tenths of a dB: `unit`, `valuesPerUnit`, `combined` and `perChannel`, `null` for a row with no power), `cutoff` and `channelCutoffs`.
  - A cutoff has `isFound`, and where found `frequency` and `transitionStart` as `{value, unit}` in whole hertz, then `stepFrequency`, `step` (`value` in dB with one decimal) and the counts `framesShowing` and `framesWithSignal`; `isSearched: false` under 8,000 Hz.
  - `levels` has `format` (`integer`, `float32` or `float64`), `bitsPerSample` (integers), and the figures, each with the stream's value and, for a stream of several channels, the same by channel:
    - `samplePeak`, `rms`, `dcOffset` (in `%`, the value placed from millionths of a percent), `dcOffsetLevel` and `zeroSamples` (in `samples`) as `{value, unit, perChannel}`, with `null` for minus infinity;
    - `clipping` (`minimumRunLength`, `runs`, `samples`, `longestRun`), `digitalSilence` (`atStart` and `atEnd`, in samples) and `bitDepth` (`lowBitsUnused`, `null` where every sample is 0, and `highestMagnitude` as an integer code; for floats `hasOnly16BitValues` and `hasOnly24BitValues`) as counts, with a `perChannel` array of objects of the same fields.
  - `loudness` has `channelWeights` (1.0, 1.41 or 0), then `integratedLoudness`, `loudnessRange` (with `low` and `high`, the two percentiles in LUFS), `maximumMomentaryLoudness` and `maximumShortTermLoudness` as `{value, unit}` (`null` for minus infinity) or, where the stream has none, `{unit, absence}` with `absence` `tooShort` or `belowAbsoluteGate`; `gatingBlocks` (`total`, `aboveAbsoluteGate`, `aboveRelativeGate`); and `shortTerm`: `unit`, `stepsPerPoint` (a power of 2), `x` (seconds since the first measured sample, at the end of the 3 s window of the first value a point covers) and `values` in LUFS, at most 1,024 points, merged in pairs as the stream grows. `notMeasured` with the reason replaces them where loudness was not measured.
  - `truePeak` has `oversampling` (4, 2 or 1) and `maximumTruePeakLevel` as `{value, unit, perChannel}` in dBTP, or `notMeasured`.
  - No number is a floating-point quotient: the row spacing is `sampleRate * binsPerRow / fftSize` hertz and the time of a column is `hopsPerColumn * hopSamples / sampleRate` seconds, from integers the report gives. Column 0 starts at sample `floor(firstPosition / hopSamples) * hopSamples`, so the time of column `c` is `(floor(firstPosition / hopSamples) * hopSamples + c * hopsPerColumn * hopSamples) / sampleRate`.
- **CSV:** the `Spectral Cutoff (Hz)` column, from the first audio stream's combined spectrum, empty where no cutoff is found, and the `Sample Peak (dBFS)` column, the highest over the channels of the first audio stream, with one decimal and empty for a stream whose samples are all 0, and (step 5) `Maximum True Peak Level (dBTP)`, `Integrated Loudness (LUFS)` and `Loudness Range (LU)` of the first audio stream, empty where the stream has no figure; all are empty for every file of an analysis without `--measure`, and `Is Verified` is filled with `--measure` too. The numeric columns are written as they are, so a negative peak reads back as `-0.3`, not `'-0.3`. Each step adds columns to the CSV (a schema change for a reader that counts columns): `Sample Peak (dBFS)` is the new column of step 2 and the three loudness columns, the last, of step 5.
- **Exit codes:** unchanged; `--fail-on` sees only the host's `measure.*` findings (see below).

### `--measure-grids`

As `--measure`, and writes the spectrogram and the waveform into the JSON, as base64 of the bytes (the text and CSV never hold them, so `--measure-grids` with either is wrong usage).

- `spectrogram`: `columns`, `rows` (512), `hopsPerColumn`, `hopSamples`, `unit`, `valueOffset` (200), `noData` (0) and `power`, the cells column by column, one byte each (the level in dB plus 200, 1 to 220; 0 is no data).
- `waveform`: `columns`, `channels`, `unit` and `minimum` and `maximum`, 16-bit little-endian sample codes channel by channel.
- A grid of 1,024 columns is 512 KiB, 683 KiB as base64.

### The findings of the measurements

Measurements raise no finding about the audio, since a figure is not a verdict; the one exception is a warning for samples that no reader can play as written.

- `measure.non-finite-samples` (warning): float samples that are NaN or infinite; they were measured as 0.
- `measure.limit` (info): measuring stopped at the budget.
- `measure.channels-limited` (info): more than 32 channels; the first 32 were measured.
- `measure.streams-limited` (info): more streams than the memory bound allows were decoded; this one is not measured.
- `measure.positions-jump` (info): a block's position went back; measuring stopped there.
- `measure.format-changed` (info): a block's channels or sample format differed from the declared output; measuring stopped there.
- `measure.failed` (error): a bug in a measurement; that stream's figures are dropped and the analysis goes on.

### The verification of a stream

A stream of a coding that declares its checksums has a verification, with or without `--verify`. FLAC declares them (frame header CRC-8, frame CRC-16 and STREAMINFO MD5); the other codings do so as their decoders land. The model also carries a verification for the streams of WAV, Wave64 and AIFF, which declare no checksum: it says only whether the audio was decoded (`notRequested`, or `decoded` with `--measure`), the front ends read it, and the text tree, the JSON and the GUI's `Verified` line leave out a verification that declares no checksum, so those reports are the same with and without it. A stream is verified when every checksum it stores was compared and holds, and at least one of them covers all of its audio (`encspect --learn verification` explains it).

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

`--only-format mpeg` (or `mp4`, `aac`, `ogg`, `musepack`, `wavpack`, `flac`, `monkeys-audio`, `tta`, `truehd`, `ac-3`, `asf`, `dts`, `dts-hd`, `matroska`, `wav`, `wave64`, `aiff`, several separated by commas) analyzes only files whose detected format starts with that name and skips the rest. Detection looks at the bytes, not the extension. A name that starts no known format is wrong usage, as is a missing list (`--only-format` followed by a file or folder).

- **Names:** the same as for `--as` (see below), so `ac3` is `ac-3`. A prefix several formats share (`a`) selects all of them. `dts` is the DTS parser's own name, so `--only-format dts` selects raw DTS files and not `.dtshd` files, which are `dts-hd`.

### `--as <format>`

`--as aac` skips format detection and analyzes every file as that format, for a file whose format detection does not find (a damaged one) or finds wrongly. The format is named like this, ignoring case, spaces, hyphens, underscores and apostrophes; one that fits no format, or several (`a`), is wrong usage, as is a missing name (`--as` followed by a file or folder).

- **The formats and the start of their names:** `mpeg` (MPEG Audio), `mp4`, `aac`, `ogg`, `musepack`, `wavpack`, `flac`, `monkeys-audio`, `tta`, `truehd`, `ac-3` (also `ac3`), `asf`, `dts`, `dts-hd`, `matroska`, `wav`, `wave64`, `aiff`.
- **Other names a format goes by:** `mp1`, `mp2`, `mp3` for MPEG Audio; `m4a`, `m4b`, `mov`, `3gp` for MP4; `adts`, `adif` for AAC; `eac3`, `ec3` for AC-3; `vorbis`, `opus`, `speex`, `spx`, `oga` for Ogg; `wma` for ASF; `mkv`, `mka`, `webm` for Matroska; `mpc` for Musepack; `wv` for WavPack; `ape` for Monkey's Audio; `thd`, `mlp`, `dolbytruehd` for TrueHD (`dolbytruehd` is "Dolby TrueHD", the name the stream's codec goes by, with its space dropped); `wave`, `rf64`, `bw64` for WAV; `w64` for Wave64; `aif`, `aifc` for AIFF. `dtshd` is the name of DTS-HD itself (ignoring the hyphen), so `--as dtshd` analyzes a file as a DTS-HD file, with its chunks; before the DTS-HD parser existed it meant DTS. `--as dts` reads the same file as a raw DTS stream.

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
  - `declaredRange` is the range the series' format defines for its values, in the series' `unit`, written after `quantity` and only where the parser sets one; a front end can shade by it so that a color means the same in every file of that kind. It has one of two shapes:
    - `{"steps": [32, 40, 48, ...]}`: the steps the format allows, strictly ascending, such as the bitrate table of an MPEG version and layer.
    - `{"minimum": 0, "maximum": 100}`: a fixed minimum and maximum, such as a percentage.
  - MPEG sets it on `Bitrate` (not on a free-format stream, whose computed bitrates fall between the steps) and on both forms of `Channel Bit Share`. The text report does not print it. A field was added, so the schema version stays 1.
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
- `Is Protected`, `true` if any stream is protected and `false` otherwise (empty for a file without a stream), `Is Verified` (see `--verify`), `Protection Scheme`, the schemes of the protected streams, and `DRM Systems`, the systems their files name; several are separated by `; `;
- `Spectral Cutoff (Hz)` (see `--measure`), empty without a cutoff, and `Sample Peak (dBFS)` (the highest over the channels of the first audio stream; empty without `--measure` and for silence), `Maximum True Peak Level (dBTP)`, `Integrated Loudness (LUFS)` and `Loudness Range (LU)` (of the first audio stream; empty without `--measure`, and where the stream has no figure).

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
