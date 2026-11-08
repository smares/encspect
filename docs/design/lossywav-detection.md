# lossyWAV and lossyFLAC: the marker and the low bits per span

Status: draft for the project lead, revised after its independent review. The lead resumed the work on 2026-11-08 from the paused text of the plan ([Paused and on hold](../plan.md#paused-and-on-hold)). Every claim here was checked against files lossyWAV wrote. The decisions are under [Decisions for the lead](#decisions-for-the-lead).

## Scope

- **In:**
  - what lossyWAV leaves in a file;
  - the figures that show its pattern in the decoded audio and in the coded syntax;
  - the claim that names it and its confidence;
  - the false positives;
  - determinism, memory and budget;
  - fixtures, docs and pull requests.
- **Out:**
  - any statement about quality or about whether a file is lossless. No finding, figure name, article or message says or implies that a file "is not lossless", "is lossy" or "was transcoded" (the plan's non-goal, [CLAUDE.md](../../CLAUDE.md));
  - lossyWAV's correction files as a pair (`.lwcdf.wav` merged with `.lossy.wav`): the analyzer reads each file alone;
  - TAK, LPAC, MPEG-4 ALS and WMA Lossless, which lossyWAV's README lists as codecs that profit. Encspect has no TAK, LPAC or ALS parser, and the WMA Lossless decoder is not built yet. Its figures come for free once it decodes.

## Words

One noun for each unit, used for nothing else here:

- **lossyWAV block:** the run of samples over which lossyWAV keeps one count of zeroed low bits for a channel (512 samples at 44.1 kHz).
- **span:** 32 consecutive samples of one channel, the unit of the measurement. The word is new to the outputs; `StreamMeasurer`'s internal "withheld spans" are code, not output.
- **frame:** a FLAC frame. **WavPack block:** a WavPack block. **unit:** either of the two, where a rule applies to both.
- **wasted bits:** the count a coding states for a unit (FLAC's name, RFC 9639 section 9.2, and the `wasted-bits` article; WavPack calls it Zero Bits). **Low Bits Unused:** the same count measured over decoded samples, the levels' existing figure. The two names stay because the `wasted-bits` article already maps one to the other ("counts the same thing over every sample").

## The design in short

- **The marker:**
  - lossyWAV 1.4.2 writes a `fact` chunk right after `fmt ` whose body is text: `lossyWAV 1.4.2 @ 08/11/2026 08:37:19, --quality standard`, then CR LF and two NUL bytes;
  - lossyWAV's own `--check` recognizes its output by this chunk alone;
  - the WAV parser misreads that chunk today: it takes the first four bytes as a sample length of 1,936,945,004 and warns `wav.fact-samples-mismatch`;
  - the WAV, FLAC (foreign metadata), WavPack (wrapper) and Monkey's Audio (header data) parsers read the text and make a high-confidence claim of a new kind, "Processed by", with lossyWAV's version and settings.
- **No marker in most lossyFLAC:**
  - `lossyWAV --stdout`, which lossyFLAC pipelines use, writes no `fact` chunk, but its RIFF size still counts it, so the WAV parser today reports the file as cut off;
  - `flac` keeps the chunk only with `--keep-foreign-metadata`.
- **The pattern:**
  - lossyWAV zeroes a count of low bits in each lossyWAV block, per channel by default;
  - the block is 256 samples at 22.05 and 32 kHz, 512 at 44.1 and 48 kHz, 1,024 at 88.2 and 96 kHz and 2,048 at 176.4 and 192 kHz, anchored at the first sample;
  - in a block, every sample of a channel has at least that many zero low bits.
- **The figures:**
  - a measurement of the decoded integer PCM of every lossless decoder, over spans of 32 samples, so no grid needs to be known in advance;
  - the distribution of the spans' Low Bits Unused, the changes, the grid the changes fall on, and the share of times at which the channels agree;
  - figures with a unit and a basis, beside the levels' existing Low Bits Unused, holding no verdict.
- **The coded counterpart:** the distribution and changes of FLAC's wasted bits per subframe and WavPack's Zero Bits per block. They show the grid only when the units are no longer than the lossyWAV block and divide it.
- **The claim from behavior:**
  - only in the FLAC and WavPack parsers, at low confidence, at the rates tested, where the units divide the lossyWAV block and the counts change on its grid;
  - never from the measurement, which adds figures only.

## What lossyWAV writes

### The build

- **Tool:** lossyWAV for POSIX, a port of lossyWAV 1.4.2 (`github.com/MoSal/lossywav-for-posix`, commit `c3a22be0e1d8d516e3aa1761b6822ec68c06f920`, GPL 3, pinned in [lossywav_tool.py](../../scripts/fixtures/lossywav_tool.py)). It is built with its own `Makefile.unix` by g++ 15.2.0 on ubuntu-26.04, and its `--version` prints `lossyWAV 1.4.2`.
- **Binary:** the SHA-256 is `ea4fffbb4bf2143493518acd8dc62c2350afa03e5de3a3951d80a1f5e19e5a3f`, the same in all three runs.
- **Black box only:**
  - it is built and run only on the CI runner, by [lossywav-fixtures.yml](../../.github/workflows/lossywav-fixtures.yml);
  - what was read: in the design's first session, three trees were cloned into the session's scratchpad to read their READMEs: `MoSal/lossywav-for-posix` at the pinned commit (its two READMEs, the main one and `README.lossyWAV.md`), `corrideat/lossywav` (an older C++ port) and a `lossywav/v1.4.2` tree (its README). No source file of any of them was opened, nothing was built from them, and the clones were deleted on 2026-11-08;
  - the help text (`-L`) was read from the CI runs' records (`tools/help-*.txt` in the artifact), not from a local run;
  - the files are downloaded as an artifact and read as data.
- **Only one port, one version:** every observation here is of lossyWAV 1.4.2 as the POSIX port builds it. The official Windows build of 1.4.2, which foobar2000 users run, and older versions were not examined.
  - The help text's `--linkchannels` ("revert to original single bits-to-remove value for all channels") implies that older versions linked the channels.
  - The date was seen only as `DD/MM/YYYY`; another build or locale may write it otherwise.
  - The articles and format docs therefore say "lossyWAV 1.4.2 (POSIX port)", and the text parse does not depend on the date's form (decision 9).
  - The official Windows build is attached to the Hydrogenaudio forum, which a Cloudflare challenge keeps from scripts. The Internet Archive item `lossyWAV_beta_1.4.3p` is a third-party upload of a beta with an expiry-date patch, and was not used.
- **Runs:**
  - run 42463604298 (commit 61cad4c), run 42464513495 (commit dd28265) and run 42467248396 (commit 7924841), a few minutes each;
  - the last run's artifact (13612391877, SHA-256 `8834ba9b…`, 1.27 GB) holds every case of the earlier two and the fixtures, and is kept until 2027-02-06;
  - the files are in the session's scratchpad, not in git (see [Fixtures](#fixtures)).

### Inputs and settings

- **Signals:** the deterministic signals of `flac_signal.py`:
  - tones under a tremolo for two thirds, then noise; loud noise; silence around a tone;
  - 10 s plus 123 samples each;
  - 8, 16, 24 and 32 bits; mono, stereo and six channels; 8, 22.05, 32, 44.1, 48, 88.2, 96, 176.4 and 192 kHz;
  - 16-bit audio in 24 bits;
  - one file with `LIST` and `bext` chunks.
- **Recordings,** 30 s each, pinned by SHA-256 in [make_lossywav_fixtures.py](../../scripts/fixtures/make_lossywav_fixtures.py) and registered in `check_sources.py`. Their licenses, as their sources state them:
  - Goldberg Variations, Variation 4, played by Kimiko Ishizaka, 16 bits, from the Internet Archive item `musopen-dvd-in-lossless-flac-format` ("Musopen Kickstarter Music"). The item states the Public Domain Mark 1.0; its uploader is not Musopen. The performer's own project, the Open Goldberg Variations (opengoldbergvariations.org), states that its recording is "governed by the Creative Commons Zero license". Whether the DVD's track is that recording was not checked.
  - "D'un vieux jardin", 24 bits, from the Internet Archive item `anonymous-420-remix-what-intimidate-you`, a remix by "anonymous420". The item states CC0 1.0, asserted only by its uploader. Its credits name the sample: Lili Boulanger's "D'un vieux jardin" played by Pandora Selfridge, "certified public domain by the website musopen". It is a remix with effects, so it holds another performer's recording.
  - Both are flagged for decision 4.
- **Settings:**
  - the seven presets (`-q I` to `-q X`), `-q 0` and `-q 10`;
  - at standard: `--correction`, `--linkchannels`, `--midside`, `--limit 20000`, `--scale 0.5`, `--feedback`, `--shaping fixed` and `--shaping off`;
  - a repeated run, `--check` on altered files, its own output as input, `--stdout`, input from STDIN (`-`) to STDOUT and to a file (`--stdinname`).
- **Codings of every output:** `flac -5 -b 512` (lossyFLAC) with and without `--keep-foreign-metadata`, `flac -5` (4,096-sample frames), `flac -5` with frames of the lossyWAV block above 48 kHz, `wavpack`, `wavpack --blocksize=512`, `wavpack --blocksize=512 --merge-blocks`, and `mac -c2000` (Monkey's Audio 13.44).
  - flac 1.5.0, wavpack 5.9.0 and g++ are the runner image's packages, unpinned. Their versions are in the manifest, which is enough for files committed once.

### The marker

- **The chunk:** a `fact` chunk, the second chunk, between `fmt ` and `data`.
  - Body: `lossyWAV <version> @ <DD/MM/YYYY> <HH:MM:SS>, <settings>`, CR LF, two NUL bytes.
  - The body is 53 to 85 bytes in these files, with a pad byte after an odd size.
  - The settings are lossyWAV's long names: `--quality standard`, `--quality 0`, `--quality standard --linkchannels --midside` for `--midside`, `--quality standard --scale 0.500000`. `--limit 20000` is not recorded.
  - **Settings as written, not as applied:** `-q 0` gave the same samples as economic and `-q 10` the same as insane, yet the texts differ (`--quality 0`, `--quality economic`). `--midside` gave the same samples as `--linkchannels` on the four inputs it ran on. The claim shows the text as written.
  - The correction file (`.lwcdf.wav`) carries the same text.
- **Other chunks:** a `LIST` and a `bext` chunk of the input are kept, after the `fact` chunk.
- **Not reproducible byte for byte:** the time of the run is in the text. The samples are: the same input and settings gave the same samples in all runs, and the `flac` codings without the chunk are identical.
- **lossyWAV's own check,** `--check` (exit code 255 for "already processed" on this build; the help text says 16):

  | Input | `--check` |
  |---|---|
  | the input | not marked |
  | the output | "lossyWAV FACT Chunk found. File already processed." |
  | the output without its `fact` chunk | not marked |
  | the `fact` chunk with a 4-byte sample length | not marked |
  | the text with version 9.9.9 | found |
  | the text with `LOSSYwav` | not marked |
  | the `fact` chunk after `data` | not marked |
  | the input with the output's `fact` chunk added | found |

  - It reads only a `fact` chunk before `data` whose text starts with `lossyWAV`, and does not look at the samples.
  - Given its own output as input, lossyWAV refuses: "lossyWAV FACT Chunk found. File already processed."
- **Where the marker survives:**

  | Coding | `fact` text in the file |
  |---|---|
  | `lossyWAV --stdout`, and `lossyWAV -` (STDIN) with `--stdout` | no: `fmt ` and `data` only, the same samples; see the RIFF size below |
  | `lossyWAV -` with `--stdinname piped` | yes; the file is named `pilossy.ped` (the name's last three letters taken as its extension) |
  | `flac -b 512` | no |
  | `flac -b 512 --keep-foreign-metadata` | yes, in an APPLICATION block with ID `riff` (one block per chunk: 12, 48, 68 and 8 bytes) |
  | `wavpack` | yes, in the RIFF wrapper |
  | `mac` | yes, in the WAV header data |

- **The RIFF size of `--stdout`:**
  - the output of `--stdout` (from a file or from STDIN) states a RIFF size of 1,764,596 for a file of 1,764,536 bytes: the header counts the `fact` chunk it leaves out (8 bytes of header and its 60-byte body; the overshoot is 8 plus the length of the settings text and its padding);
  - its `data` chunk is whole: the samples equal those of the file output;
  - Encspect on main reports `[Warning] wav.truncated: The RIFF chunk states 1764604 bytes, but the data ends 68 bytes short of that: the file is cut off.` The file is not cut off.
  - The overshoot is a trace of lossyWAV, not a marker: the parser does not name lossyWAV from it. PR 0 changes how it is reported (below).

### The pattern in the samples

- **lossyWAV blocks:**

  | Rate | lossyWAV block | Changes on its grid |
  |---|---|---|
  | 8 kHz | none: lossyWAV crashed (signal 11) at standard and portable after "Too much WAV information before 'data' chunk!" | |
  | 22.05 kHz | 256 | 14 at standard (under the gate), 195 at portable, all on it |
  | 32 kHz | 256 | 33 and 534, all on it |
  | 44.1 and 48 kHz | 512 | all, in every output with 20 changes or more (below) |
  | 88.2 kHz | 1,024 | 89 and 20, all on it |
  | 96 kHz | 1,024 | 93 and 292, all on it |
  | 176.4 kHz | 2,048 | 283 and 28, all on it |
  | 192 kHz | 2,048 | 308 and 298, all on it |

  - These are the 24-bit stereo signal at standard and at portable. Rates not in the table (11.025 kHz, for example) were not run; the behavior claim is limited to the rates in it.
  - The blocks are anchored at sample 0 at every rate (every change falls at phase 0).
- **On the grid:**
  - in the 95 outputs with 20 changes or more (six-channel ones among them), every change falls on the grid, with one exception: `--scale 0.5` on the Goldberg recording (22 changes, 4 on the grid), whose spans change within lossyWAV blocks;
  - that is after the two rules of the measurement below (short and quiet spans). Without them, the last span of a stream (27 samples in these files) had one more zero bit by chance in most synthetic outputs, which put one change off the grid (as low as 26 of 27, 96.3%), and `--scale 0.5` on the 16-bit signal had no grid.
- **Per channel:**
  - at the default, the channels of a block often differ: the counts agree at 47 to 52% of the times in the recordings wherever most spans have zeroed bits ("D'un vieux jardin" at every preset, the Goldberg variation at portable and extraportable);
  - with `--linkchannels` or `--midside` they agree at 100%, which the help text describes ("single bits-to-remove value for all channels").
- **How many bits, by preset,** over every span of both channels:

  | Preset | "D'un vieux jardin" (24 bits): lowest, mean, highest | Goldberg Var. 4 (16 bits): spans with zero bits, mean |
  |---|---|---|
  | insane | 5, 8.0, 11 | 0%, 0 |
  | extreme | 6, 8.9, 12 | 0%, 0 |
  | high | 7, 9.9, 13 | 0.1%, 0.00 |
  | standard | 8, 10.3, 13 | 1.7%, 0.02 |
  | economic | 8, 10.8, 14 | 14.3%, 0.15 |
  | portable | 9, 11.8, 15 | 77.8%, 1.01 |
  | extraportable | 10, 12.8, 16 | 97.7%, 2.10 |

- **Where the pattern shows and where it vanishes:**
  - On the 24-bit recording, every span keeps a floor of 5 to 10 zero bits. The levels' Low Bits Unused alone reads "8 of 24" at standard, as 16-bit audio in 24 bits would. The span figures tell them apart: 2,223 changes on a 512 grid against none.
  - On the quiet 16-bit recording at standard, 1.7% of the spans have any zero low bits (121 changes, all on the grid). At insane and extreme there are none, and nothing in the samples shows that lossyWAV ran.
  - The synthetic music signal at 44.1 kHz has only 8 to 12 changes, and no grid, at portable and extraportable, in 16 and 24 bits; the same signal at 48 kHz and 24 bits has 263 at portable. Changes need audio whose count varies; a count that stays at its ceiling does not change.
  - Loud noise at extraportable is a constant 10 bits in 16 (1 change in 10 s): Low Bits Unused 10, no span above it, no grid. It reads exactly as audio truncated to 6 bits. The help text gives the reason: lossyWAV keeps at least "minimum-bits-to-keep-static" bits (`--static`, default 6, "limited to bits-per-sample - 3"), and loud noise reaches that floor in every block. The article says so.
  - 8-bit input: no bits are removed at standard ("0.0000 bits").
- **The tones of the synthetic signal keep all their bits** at every preset; only the noise part has zeroed bits. A figure is therefore a share of the spans, never a promise about each span.

### What the coders make of it

The bitrate of the 30 s "D'un vieux jardin" before and after lossyWAV at standard, as file size times 8 over 30 s (Encspect's own Average Bitrate, over the audio only, reads 478.5 kbps where the table says 481):

| Coding | Input | Output |
|---|---|---|
| `flac -b 512` | 1,383 kbps | 481 kbps |
| `flac -b 4096` | 1,361 kbps | 519 kbps |
| `wavpack` (11,025-sample blocks) | 1,367 kbps | 579 kbps |
| `wavpack --blocksize=512` | 1,426 kbps | 549 kbps |
| `mac -c2000` | 1,348 kbps | 1,351 kbps |

- **Monkey's Audio:** the output is the same size as the input; that is all that was observed. Its figures come from the measurement only.
- **FLAC:**
  - With 512-sample frames, each subframe's wasted bits is the count of its lossyWAV block (44.1 and 48 kHz).
  - With 4,096-sample frames it is the smallest count of eight blocks.
  - With 512-sample frames at 96 kHz, a frame holds half a lossyWAV block, so the count changes only on every second frame. At 22.05 and 32 kHz a 512-sample frame holds two blocks.
  - At standard, the 512-sample codings of the 24-bit recording read wasted bits 8 to 13, the 4,096-sample ones 8 to 13 with fewer high counts, and "Subframes With Wasted Bits" is 100% in both.
- **WavPack:**
  - Each block's Zero Bits (INT32_INFO sub-block) is the smallest count over the block's channels.
  - At 512 samples, its distribution is the per-block minimum of the samples exactly: 8 bits in 64 blocks, 9 in 618, 10 in 1,234, 11 in 504, 12 in 144, 13 in 20.
  - `--merge-blocks` (wavpack 5.9.0 in CI) writes blocks of whole multiples of 512 samples: in the 3 s fixture, 37 of 512, 14 of 1,024, 13 of 1,536, 7 of 2,048 and fewer of longer, with Zero Bits 8 to 11; 514.5 kbps by Encspect's Average Bitrate against 590.0 for the default blocks.
  - The Left Shift field stays 0.

## The figures

### Where they are computed

- **The measurement, for every decoder that writes integer PCM:**
  - FLAC, WavPack, Monkey's Audio, TTA, ALAC and TrueHD, and the PCM route of WAV, Wave64 and AIFF;
  - figures on `StreamMeasurements.Levels`, beside Low Bits Unused, computed in `LevelsMeter`, which ORs every sample already.
  - The figures depend only on the samples, not on how a file was coded, so a lossyWAV output coded with Monkey's Audio or with `flac -b 4096` reads as its WAV does.
  - Float output (float WAV, and the lossy decoders the plan builds on PR #232's groundwork) has no integer low bits and gets none of these figures.
- **The FLAC and WavPack parsers, from the coded syntax, without decoding:** the distribution of wasted bits over the units and the changes between consecutive units of a channel (below).

### The measurement

- **Spans of 32 samples per channel,** anchored at position 0 of the stream (the first position the decoder could write, not the first decoded or untrimmed sample). A span's count is the number of low bits that are zero in all its samples (the trailing zeros of their OR, within the declared width), and its magnitude is the bit length of its largest sample without the sign.
  - **Why 32:** lossyWAV's blocks are multiples of 32 at every rate tested, so a span never straddles two of them in a file that was not cut.
  - **Silent spans:** all samples 0. They have no count and are counted apart.
  - **Short spans:** fewer than 32 samples, because the stream ends or a gap or withheld positions follow. They are counted in the distribution but take no part in the changes. A span ends at a gap or at withheld positions, and the next starts at the position the grid gives after it; no change is counted across a gap.
  - **Quiet spans:** a magnitude fewer than 4 bits above the count. They are counted in the distribution, counted apart as quiet, and take no part in the changes. In real audio, a span with more zero bits than its lossyWAV block has them in all 32 samples, which happens by chance only when the samples are small.
  - Trimmed and withheld positions (`trimmedSamples`, `withheldSamples` of PR #232) are not measured; `firstPosition` gives the anchor.
  - The distribution over spans needs no grid known in advance and one pass.
- **Figures** (names in Title Case as the levels write them; settled in PR 2):
  - **Low Bits Unused per Span:** for each count from 0 to the width, the number of spans, all channels together; silent spans apart, and quiet spans counted.
  - **Spans Above the Stream's Low Bits Unused:** the spans whose count is above the stream's Low Bits Unused. It is 0 for 16-bit audio in 24 bits and for truncated audio, and 98.7% for "D'un vieux jardin" at standard.
  - **Changes of Low Bits Unused:** the spans, neither silent, short nor quiet, whose count differs from the same channel's last such span.
  - **Grid of the Changes:** the largest power of two from 256 to 65,536 samples such that at least 95% of the changes fall within one span of one phase of it (`hits * 20 >= changes * 19`, in integers), given at least 20 changes; otherwise none. It is reported with the number of changes on it.
    - The phase is free, so a stream cut off the grid keeps it: 100 samples cut from the start still read 512.
    - **Why 20 and 95%:** they are this design's conventions, which the article states. After the short and quiet rules, every lossyWAV output tested with 20 changes or more has all of them on the grid (one exception, `--scale 0.5`, above), while edits that put two phases in one stream fall far below 95% (a join: no grid). 95% leaves one change in 20 for chance.
  - **Channels With Equal Low Bits Unused:** the share of the times (span positions at which no channel's span is silent) at which all channels have the same count. It is not given for mono.
- **Units:** counts are integers and the grid is in samples. The shares are printed by the writers from the counts (one decimal of a percent), as the other measurement figures are, with nothing computed in floating point.
- **Checked against the prototype:** PR 2 compares the figures with [lossywav_figures.py](../../scripts/fixtures/lossywav_figures.py) on the PCM that `tests/Encspect.PcmDump` writes for the FLAC, WavPack and Monkey's Audio fixtures.

### The FLAC and WavPack statistics

- **"Wasted Bits"** beside FLAC's existing "Subframes With Wasted Bits", and the same statistic in WavPack, whose description maps Zero Bits to it:
  - the distribution of wasted bits over the units (FLAC's subframes, WavPack's blocks);
  - the number of changes between consecutive units of each channel (WavPack's count is per block, for all its channels).
- **FLAC and mid/side:**
  - A side channel (left minus right) has at least the smaller count of the two channels. A mid channel (their sum shifted right by one) has one bit fewer.
  - The statistic counts the subframes as coded and says so in its description; it does not undo the channel assignment.
  - In the 512-sample codings of the 24-bit recording, 97% of the frames are left/right at the default and 95% with `--linkchannels`; flac chose no mid/side.
- **Monkey's Audio, TTA, ALAC and TrueHD** have no per-unit count in their syntax that matches.
  - Monkey's Audio: no per-unit count, and no gain from the zeroed bits was observed (above).
  - TTA, ALAC and TrueHD: the parsers report no per-unit count of zero low bits today. ALAC's shifted bytes (whole bytes coded apart) and TrueHD's output shifts are other mechanisms. Whether either follows lossyWAV's counts is checked in PR 3 on lossyWAV output coded with them: TTA with `libtta_tool.py`'s `tta`, ALAC with `alac_tool.py`'s `alacconvert`, and TrueHD with FFmpeg's experimental `truehd` encoder, the only one at hand. The format docs say what was found.

## The claim

### From the marker

- **Read by:**
  - the WAV parser (the `fact` chunk);
  - the FLAC parser (APPLICATION blocks with ID `riff`, which are flac's foreign metadata: each holds one RIFF chunk);
  - the WavPack parser (the RIFF wrapper);
  - the Monkey's Audio parser (the WAV header data).
- **The test:** as lenient as lossyWAV's own check. A `fact` chunk before `data` whose body starts with `lossyWAV ` is lossyWAV's.
  - The version is the text up to ` @ `, and the settings are the text after the first `, `. The date and time between them are shown as written and not parsed, so a date of another form raises nothing.
  - A body with the prefix but without ` @ ` or `, ` is a node of text with an info finding, one code per format (decision 12).
  - A `fact` chunk in a PCM file that holds neither 4 bytes nor this text keeps today's reading.
- **The WAV parser's fix (PR 0):**
  - A lossyWAV `fact` chunk states no sample length. The parser stops reading one from it, so neither `wav.fact-samples-mismatch` nor the false Container Duration (43,921.655 s for a 30 s file) appears.
  - A RIFF size past the end of the file, when the last chunk ends exactly at the end of the file, is a new warning, `wav.riff-size-past-chunks` ("The RIFF chunk states N bytes more than the file holds; its last chunk is complete."), instead of `wav.truncated` (decision 8). A chunk cut by the end of the file stays `wav.chunk-past-end` and `wav.truncated`.
  - Both stand alone, whatever the rest of this design becomes.
- **Shared code:** the text parse is about 30 lines that the four parsers need, and parsers do not reference each other (decision 3).
- **The claim:**
  - "lossyWAV 1.4.2", with the settings as its evidence and high confidence, of a role that is neither encoder nor writer (decision 1);
  - lossyWAV turned PCM into PCM before the encoder ran, and the encoder of the FLAC file is still libFLAC.
- **The correction file** carries the same text, so it gets the same claim; the article says so. The analyzer does not use file names.

### From behavior

- **Where:** the FLAC parser, and the WavPack parser, at the rates of the table above (22.05, 32, 44.1, 48, 88.2, 96, 176.4 and 192 kHz), for a stream whose units are no longer than the lossyWAV block and divide it (FLAC: fixed frames; WavPack: blocks of whole multiples of a length that divides it, as `--merge-blocks` writes).
- **What it needs:**
  - at least 20 changes of wasted bits between consecutive units of a channel;
  - at least 95% of them on units that start at one phase of the lossyWAV block's grid (with 512-sample frames at 96 kHz, every second frame);
  - at least one unit in four with wasted bits above the stream's smallest.
- **On the fixtures:** it claims the 512-sample codings of "D'un vieux jardin" at standard and of the Goldberg recording at portable, the synthetic 48 kHz signal, both 96 kHz codings and the six-channel file, and not the 4,096-sample codings or the Goldberg recording at standard (25 changes, but 17 of 862 subframes above the smallest).
- **Negative set:** the 146 FLAC fixtures in `tests/fixtures/flac` and `tests/fixtures/measurement` that `flac` decodes (236 s in all; 2 kHz to 640 kHz, 8 to 32 bits, 11 with more than two channels; the decoded MP3 and AAC fixtures among them), re-coded with `flac -5 -b 512`:
  - no claim, and no grid in the measurement;
  - at most 3 changes of wasted bits, and at most 5 changes of Low Bits Unused (`built-subframe-types.flac`). Without the quiet-span rule, a full-scale square wave reads 63.
- **The parser does not see where in a unit the count holds,** so a FLAC file from another tool that zeroes bits per block of 512 samples reads the same.
- **Confidence:** low. [architecture.md](../architecture.md)'s rule for habits asks that "the other encoders available for testing do not" show the habit. This claim names a processor, not an encoder, so the rule is read as extended to one (decision 1); and the only other sources available were the negative set above.
- **Untested sources that zero bits per block:** NICAM-decoded audio (a scale factor per block of 32 samples) and DAT long-play, both named in the review; decoded HDCD, which `wavpack --help` names beside lossyWAV for `--merge-blocks`; and the original lossyFLAC script (David Robinson's), which lossyWAV's README credits. None was available.
- **Not from the measurement:** a measurement adds figures only, never a claim, finding or word that reads as a verdict ([spectral-analysis.md](spectral-analysis.md), "Evidence, not verdicts"). The figures say what they measured; the article explains what lossyWAV's pattern looks like.

## False positives

Measured with [lossywav_figures.py](../../scripts/fixtures/lossywav_figures.py) on variants it writes from the files above (`--variants`; the 24-bit lossyWAV output is "D'un vieux jardin" at standard):

| File | Low Bits Unused | Spans above it | Changes | Grid |
|---|---|---|---|---|
| "D'un vieux jardin", lossyWAV standard | 8 | 98.7% | 2,223 | 512 (100%) |
| the same, first 100 samples cut | 8 | 98.7% | 2,227 | 512 (100%) |
| Goldberg Var. 4, lossyWAV standard | 0 | 1.7% | 121 | 512 (100%) |
| Goldberg Var. 4, lossyWAV portable | 0 | 77.8% | 2,008 | 512 (100%) |
| 16-bit audio in 24 bits | 8 | 0 | 0 | none |
| 24 bits truncated to 14, without and with TPDF dither | 10 | 0 | 0 | none |
| first half 16-bit in 24, second half 24-bit | 0 | 50% | 2 | none |
| another tool, a count per 4,096-sample block (simulated) | 8 | 98.3% | 200 | 4,096 (100%) |
| the lossyWAV output after a gain of -0.5 dB | 0 | 0 | 0 | none |
| the 16-bit lossyWAV output with TPDF dither of ±1 LSB | 0 | 0 | 0 | none |
| a fade to digital silence and back (16 bits, before lossyWAV) | 0 | 0 | 0 | none |
| every input before lossyWAV (the first runs' 15) | 0, or 8 for 16-bit in 24 | 0 | 0 | none |
| **Edits after lossyWAV:** | | | | |
| two outputs joined (standard, then portable), the join 100 samples after a grid line | 8 | 98.9% | 2,130 | none |
| the output faded to digital silence over its last 5 s | 0 | 83.3% | 1,845 | 512 (1,843) |
| faded out over 3 s, 1 s of digital silence, the output again from its start | 0 | 85.0% | 1,190 | 256 (1,188) |

- **Silence:** digital silence has no count and is counted apart. A fade to silence gives no changes in the recording tested.
- **Deliberate truncation and 16-bit audio in 24 bits:** a constant count, no changes, no grid, and no span above it.
- **Processing after lossyWAV:** a gain or a dither removes the pattern entirely. The figures then read like any audio and the marker, if kept, is the only trace. The article says so.
- **Edits after lossyWAV:**
  - a fade leaves the unfaded part's pattern, and the faded part reads 0 bits, one change per channel;
  - a join puts two phases in one stream, so no grid is found, while the changes stay;
  - two phases closer than one span (here 236 samples apart on a 512 grid, 20 samples from a 256 line) read as the next smaller grid.
- **A cut file:** the grid survives, because the phase is free.
- **A tool that zeroes bits per block on another grid** reads with its own grid. One on lossyWAV's grid would read like lossyWAV; none was available to test (see the untested sources above), and the article says so.

## Determinism, memory and budget

- **Determinism:**
  - integers only: an OR and an OR of magnitudes per sample, a trailing-zero count and a bit length per span and channel, and counters;
  - no floating point and no order that depends on anything but the data;
  - the kernel is `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` and is pinned in `MeasurementDeterminismTests` like the levels.
- **Memory per stream,** whatever its length:
  - per measured channel (at most 32): the current span's two ORs and fill, and the last count;
  - one distribution of 33 counters;
  - 2,048 phase counters for grids up to 65,536 samples.
  - About 17 KB, within the per-stream estimate that the measurer checks before it allocates.
- **Budget:**
  - charged in the measurers' own budget as part of the levels' work per sample;
  - the cost is measured in PR 2 and the levels' 5 units per integer sample are raised if it shows, with `MeasurementWorkTests` holding the time per unit.
- **The FLAC and WavPack statistics** are counters over units the parser reads already: a few per frame, no allocation per frame.

## Fixtures

- **Committed once, checked by content:**
  - lossyWAV's output carries the time of the run, so the files are committed once and never regenerated, as the Apple and WMA fixtures are;
  - the workflow makes each fixture at its committed length: lossyWAV runs on an input of that length, so each file is the bytes lossyWAV and the coder wrote, and the manifest holds its SHA-256, its figures and lossyWAV's `--check` answer (`fixtures` in `manifest.json`);
  - `scripts/fixtures/import_lossywav_fixtures.py` (PR 0 and PR 1) copies the files from the artifact, checks each against the manifest's SHA-256, and computes the figures and the behavior rule again from the committed bytes;
  - the files are committed only once the lead has signed off on this design. The artifact of run 42467248396 is kept until 2027-02-06. If it expires first, a new run makes new bytes (new times in the text) with the same samples, and the importer takes that run's manifest.
- **The set, from run 42467248396,** 4.1 MB in all (the synthetic files of the second group are the fallback for decision 4 b):

  | File | Input | lossyWAV | Size | Changes, grid |
  |---|---|---|---|---|
  | `wav/lossywav-standard.wav` | synthetic, 44.1 kHz, 16 bits, 0.5 s | standard | 89 KB | 2, none |
  | `wav/lossywav-correction.lossy.wav`, `.lwcdf.wav` | the same | `--correction` | 89 KB each | 2, none |
  | `wav/lossywav-standard-chunks.wav` (`LIST` and `bext` kept) | the same, with the chunks | standard | 89 KB | 2, none |
  | `wav/lossywav-stdout.wav` (no marker, the RIFF size) | the same | `--stdout` | 89 KB | 2, none |
  | `monkeys-audio/external-lossywav.ape` (the header data) | the same | standard | 37 KB | 2, none |
  | `flac/external-lossyflac-b512.flac`, `-b512-foreign.flac`, `-b4096.flac` | "D'un vieux jardin", 3 s | standard | 188, 188 and 199 KB | 216, 512 |
  | `wavpack/external-lossywav.wv`, `-merge-blocks.wv` | the same | standard | 221 and 193 KB | 216, 512 |
  | `flac/external-lossyflac-goldberg-standard.flac` | Goldberg, 5 s | standard | 333 KB | 25, 512 |
  | `flac/external-lossyflac-goldberg-portable.flac` | Goldberg, 3 s | portable | 174 KB | 221, 512 |
  | `flac/external-lossyflac-synthetic-b512.flac`, `-b4096.flac` | synthetic, 48 kHz, 24 bits, 3 s | portable | 238 and 208 KB | 76, 512 |
  | `wavpack/external-lossywav-synthetic-merge-blocks.wv` | the same | portable | 318 KB | 76, 512 |
  | `flac/external-lossyflac-96k-b1024.flac`, `-96k-b512.flac` | synthetic, 96 kHz, 24 bits, 3 s | portable | 354 and 378 KB | 86, 1,024 |
  | `flac/external-lossyflac-6ch.flac` (six channels, the marker) | synthetic, 48 kHz, 24 bits, six channels, 3 s | extraportable | 673 KB | 371, 512 |

  - Paths are under `tests/fixtures`. "Changes, grid" are the measurement's figures of the samples lossyWAV wrote.
  - **The change gate:** every file that is to show the pattern has at least 20 changes on the grid; all but the Goldberg recording at standard have at least 40. The Goldberg file is the quiet case at the gate's edge on purpose: 25 changes in 5 s, the measurement finds the grid, and the behavior rule does not claim it. A 2 s excerpt had 17.
  - **The marker files** are 0.5 s and below the gate on purpose: they carry the marker, and their measurement goldens record a stream without a grid.
  - lossyWAV's `--check` answered "already processed" for the lossyWAV output of every fixture.
- **Expected findings** (PR 0 and PR 1):
  - `wav/lossywav-stdout.wav`: on main `wav.truncated` (warning, 68 bytes short); after PR 0 only `wav.riff-size-past-chunks` (warning, 68 bytes, the last chunk complete), as decision 8 picks;
  - every WAV with the marker: on main `wav.fact-samples-mismatch`; after PR 0 none, and after PR 1 the claim;
  - no other finding on any fixture.
- **Built and deterministic** (no lossyWAV): the false positives above, written by `lossywav_figures.py --variants` with a fixed seed, as short excerpts in `tests/fixtures/measurement` (PR 2).
- **Goldens:** each fixture's report and its `.measure.json`, and a `DescriptionTests` and finding-coverage row for each new node and code, as for every fixture. PR 2 also changes the `.measure.json` of every integer stream (decision 10).
- **Cross-check:**
  - no tool that was checked names lossyWAV: ffprobe 6.1.1 and MediaInfo 24.01 show nothing of the `fact` text in the WAV or the FLAC file. MediaInfo 26.01 of the runner is checked in PR 1 and mapped if it does;
  - lossyWAV's own `--check` is the cross-check of the marker: the manifest holds its answer for every fixture, which the importer compares with Encspect's claim.
- **Sources:** the two recordings and the lossyWAV commit are registered in `check_sources.py` (the scheduled sources check). [development.md](../development.md) names both recordings with their licenses as stated above before anything is committed.

## Docs

- **Articles:**
  - `lossywav` (new): what lossyWAV is, the `fact` chunk and where it survives, the RIFF size of `--stdout`, the block grid by rate and the per-channel counts, the presets and the minimum bits kept, the correction file, the limits above, and that the analyzer names lossyWAV only from the marker (high) or from FLAC and WavPack units (low). It is worded as observed behavior of lossyWAV 1.4.2 (POSIX port) in Encspect's test files;
  - `low-bits-per-span` (new): the measurement's figures, the span, the silent, short and quiet spans, and the thresholds;
  - `bit-depth-usage`, `wasted-bits` and `wavpack-wrapper`: links and one sentence each.
- **Format docs:**
  - [wav.md](../formats/wav.md): the `fact` text, `wav.riff-size-past-chunks`;
  - [flac.md](../formats/flac.md): `riff` APPLICATION blocks, the statistic, the claim;
  - [wavpack.md](../formats/wavpack.md): wrapper, the statistic, claim;
  - [monkeys-audio.md](../formats/monkeys-audio.md): header data, and no coded counterpart under what is not covered;
  - [tta.md](../formats/tta.md), [alac.md](../formats/alac.md) and [truehd.md](../formats/truehd.md): the measurement only, under what is not covered.
- **Other docs:** [architecture.md](../architecture.md) (the claim's role, the rule for habits read for a processor), [cli.md](../cli.md) and [gui.md](../gui.md) (where the figures and the claim show), [development.md](../development.md) (fixtures, sources, the workflow, the cross-check), and the plan's entry, which moves from "Paused" to the development steps.

## Pull requests

0. **PR 0, the WAV parser's fix:** the `fact` chunk no longer read as a sample length when it holds lossyWAV's text, `wav.riff-size-past-chunks`, the marker and stdout fixtures with their goldens, the importer. It needs none of decisions 1 to 4 (decision 11).
1. **PR 1, the marker:**
   - the shared `fact` text reader; its nodes in WAV; the FLAC, WavPack and Monkey's Audio parsers' reading;
   - the claim in the role decision 1 picks, and its outputs (decision 13);
   - the other marker fixtures and their goldens; the `lossywav` article; the format docs.
2. **PR 2, the measurement:**
   - the figures in `LevelsMeter`, on PR #232's positions (`firstPosition`, `trimmedSamples`, `withheldSamples`), the writers (text, JSON, CSV), the GUI's levels section;
   - the determinism pin, the work test, the check against the prototype through `Encspect.PcmDump`, the built false-positive fixtures, and the measurement goldens (decision 10);
   - the `low-bits-per-span` article.
3. **PR 3, the coded statistics and the behavior claim:**
   - "Wasted Bits" in FLAC and WavPack;
   - the low-confidence claim, with the negative set run again;
   - the TTA, ALAC and TrueHD checks above, and their goldens.
- PR #232 merged on 2026-11-08 as 75a0984 (groundwork: withheld and trimmed output, determinism, the PCM tools); nothing here waits for it.
- One independent review covers the four, scoped to their diffs and this design.

## Decisions for the lead

1. **The claim's role:**
   - a) a new `ClaimRole.Processor`, shown as "Processed by" beside the encoder and writer, with its own confidence and evidence; the rule for habits in [architecture.md](../architecture.md) is extended to processors. **Recommended:** lossyWAV is neither the encoder of a FLAC, WavPack or Monkey's Audio stream nor the software that wrote the file, and this keeps the two existing roles meaning what they mean.
   - b) the encoder role in WAV only, where lossyWAV wrote the samples, and nodes without a claim elsewhere.
   - c) no claim: nodes and the figures only.
2. **A claim from behavior, without the marker:**
   - a) low confidence from FLAC and WavPack units, at the tested rates, as above. **Recommended:** most lossyFLAC carries no marker, the negative set gives no claim, and low confidence is what the rule for habits allows.
   - b) none: only the marker names lossyWAV.
3. **Where the `fact` text reader lives:**
   - a) a small type in `Core.Abstractions` that the four parsers call, added to the list in [CLAUDE.md](../../CLAUDE.md). **Recommended:** the least machinery for 30 lines.
   - b) a copy in each parser.
   - c) a host route, as `ParseTag` is: the FLAC, WavPack and Monkey's Audio parsers hand the RIFF chunks they hold to the WAV parser's reader through their context. No new type in `Core.Abstractions`, but a new route for one chunk.
4. **Real recordings in the repository:**
   - a) the 3 and 5 s excerpts above as committed fixtures, with their sources and licenses in [development.md](../development.md). They show what synthetic signals do not (the 24-bit floor, the per-channel counts, the quiet 16-bit recording). The licenses are flagged above: the Goldberg item's Public Domain Mark is the uploader's, the performer states CC0 for her Open Goldberg Variations, and whether this is that recording was not checked; the remix's CC0 is asserted only by its uploader, over another performer's Musopen recording.
   - b) synthetic signals only: the second group of the set, with the recordings downloaded by the workflow. **Recommended,** unless the lead accepts the licenses as stated; the synthetic files show the grid, the behavior claim and the merged WavPack blocks, but not the 24-bit floor or the quiet case.
5. **Spans of 32 samples for the measurement:**
   - a) one pass and no grid needed in advance, as above. **Recommended.**
   - b) blocks at the grid found, which needs a second pass or accumulators for every candidate grid and phase.
6. **The grid's thresholds and rules:** at least 20 changes, 95% of them within one span of one phase, grids from 256 to 65,536 samples; short and quiet spans (fewer than 4 bits above the count) take no part in the changes.
   - a) as proposed, stated in the article as this design's conventions. **Recommended:** every lossyWAV output tested but one reads 100%, and the negative set reads at most 5 changes.
   - b) without the quiet rule, which lets a full-scale square wave read 63 changes and loses `--scale 0.5` on the 16-bit signal.
   - c) stricter (99%), which leaves no room for a chance change in a short stream.
7. **The workflow after this design:** keep it on a `fixtures/lossywav` branch and `workflow_dispatch`, as the Apple and WMA fixture workflows are, and drop the push trigger on this design's branch. **Recommended:** the importer needs it, and so does lossyWAV's `--check` cross-check.
8. **The RIFF size of `--stdout`:**
   - a) a new warning `wav.riff-size-past-chunks` for a RIFF size past the end of the file when the last chunk is complete, as above. **Recommended:** the size is wrong, but nothing is cut off.
   - b) the same as info.
   - c) `wav.truncated` with the wording changed for this case only.
9. **Other builds of lossyWAV:** every observation is of the POSIX port.
   - a) the lead downloads the official Windows build of 1.4.2 from Hydrogenaudio by hand, and a workflow runs it on a Windows runner with the same inputs, before PR 1. **Recommended:** it is the build most lossyFLAC users run, and its `fact` text and samples may differ.
   - b) scope everything to the POSIX port, as written, and add the Windows build later.
10. **The measurement goldens:** PR 2 adds the figures to the levels of every integer stream, so it changes 76 of the 81 `.measure.json` goldens, not only the new fixtures'.
    - a) the figures in every integer stream's levels, and all 76 goldens in PR 2, reviewed by their diff. **Recommended.**
    - b) the figures only where a stream has a count above its Low Bits Unused, which keeps most goldens but makes the record's shape depend on the audio.
11. **Order:** ship PR 0 (the WAV fix and the RIFF size) first, on its own, before decisions 1 to 4. **Recommended:** it fixes a false warning on every lossyWAV file and needs none of them.
12. **Finding codes for an unparsed lossyWAV text:** one per format, `wav.fact-text-unrecognized`, `flac.riff-fact-text-unrecognized`, `wavpack.wrapper-fact-text-unrecognized` and `monkeys-audio.header-fact-text-unrecognized` (info), each with its finding-coverage row; and whether the recognized text itself, which breaks the RIFF layout of a `fact` chunk (a sample length), gets an info finding or only its description.
    - a) the four codes, and only a description for a recognized text. **Recommended:** the text is what lossyWAV writes on purpose, and a finding on every lossyWAV file would be noise.
    - b) the four codes, and an info finding for a recognized text too.
13. **"Processed by" in the outputs:** the JSON claim gets the role `processor` beside `encoder` and `writer`; the CSV gets Processor and Processor Confidence columns after Writer Confidence; the GUI shows it in the stream's summary line ("processed by lossyWAV 1.4.2") and in the claims list, with no new column in the file list.
    - a) as written. **Recommended.**
    - b) JSON and the GUI's claims list only, no CSV columns.
