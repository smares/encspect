# lossyWAV and lossyFLAC: the marker and the per-block low bits

Status: draft for the project lead. The lead resumed it on 2026-11-08 from the paused text of the plan ([Paused and on hold](../plan.md#paused-and-on-hold)). Every claim here was checked against files lossyWAV wrote. The decisions are under [Decisions for the lead](#decisions-for-the-lead).

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

## The design in short

- **The marker:**
  - lossyWAV 1.4.2 writes a `fact` chunk right after `fmt ` whose body is text: `lossyWAV 1.4.2 @ 08/11/2026 08:37:19, --quality standard`, then CR LF and two NUL bytes;
  - lossyWAV's own `--check` recognizes its output by this chunk alone;
  - the WAV parser misreads that chunk today: it takes the first four bytes as a sample length of 1,936,945,004 and warns `wav.fact-samples-mismatch`;
  - the WAV, FLAC (foreign metadata), WavPack (wrapper) and Monkey's Audio (header data) parsers read the text and make a high-confidence claim of a new kind, "Processed by", with lossyWAV's version and settings.
- **No marker in most lossyFLAC:**
  - `lossyWAV --stdout`, which lossyFLAC pipelines use, writes no `fact` chunk;
  - `flac` keeps one only with `--keep-foreign-metadata`.
- **The pattern:**
  - lossyWAV zeroes a count of low bits in each block of 512 samples (1,024 at 96 kHz, 2,048 at 192 kHz), per channel by default;
  - the blocks are anchored at the first sample, and the count changes from block to block;
  - in a block, every sample of a channel has at least that many zero low bits.
- **The figures:**
  - a measurement of the decoded integer PCM of every lossless decoder;
  - computed over blocks of 32 samples, so no grid needs to be known in advance;
  - the distribution of each block's low bits unused, the changes, the block grid the changes fall on, and the share of times at which the channels agree.
  - These are figures with a unit and a basis, beside the levels' existing Low Bits Unused, and they hold no verdict.
- **The coded counterpart:**
  - FLAC's wasted bits per subframe and WavPack's Zero Bits per block, the same count over the coder's own blocks;
  - they show the grid only when the coder's blocks are 512 samples or merged from 512-sample blocks.
- **The claim from behavior:**
  - only in the FLAC and WavPack parsers, at low confidence, where the coded blocks are 512 samples and the counts change on them;
  - never from the measurement, which adds figures only.

## What lossyWAV writes

### The build

- **Tool:** lossyWAV for POSIX, a port of lossyWAV 1.4.2 (`github.com/MoSal/lossywav-for-posix`, commit `c3a22be0e1d8d516e3aa1761b6822ec68c06f920`, GPL 3). It is built with its own `Makefile.unix` by g++ 15.2.0 on ubuntu-26.04, and its `--version` prints `lossyWAV 1.4.2`.
- **Binary:** the SHA-256 is `ea4fffbb4bf2143493518acd8dc62c2350afa03e5de3a3951d80a1f5e19e5a3f`, the same in both runs.
- **Black box only:**
  - it is built and run only on the CI runner, by [lossywav-fixtures.yml](../../.github/workflows/lossywav-fixtures.yml);
  - its source was not read, and only its README and its help text (`-L`) were;
  - the files are downloaded as an artifact and read as data.
- **Other builds:**
  - the official Windows build is attached to the Hydrogenaudio forum, which a Cloudflare challenge keeps from scripts;
  - the Internet Archive item `lossyWAV_beta_1.4.3p` is a third-party upload of a beta with an expiry-date patch, and was not used.
- **Runs:**
  - run 42463604298 (commit 61cad4c) and run 42464513495 (commit dd28265), a few minutes each;
  - artifact SHA-256 `a6b5006a…` and `92b2ec11…`;
  - the files are in the session's scratchpad, not in git (see [Fixtures](#fixtures)).

### Inputs and settings

- **Signals:** the deterministic signals of `flac_signal.py`:
  - tones under a tremolo, then noise; loud noise; silence around a tone;
  - 10 s plus 123 samples each;
  - 8, 16, 24 and 32 bits; mono, stereo and six channels; 44.1, 48, 96 and 192 kHz;
  - 16-bit audio in 24 bits;
  - one file with `LIST` and `bext` chunks.
- **Recordings,** 30 s each, pinned by SHA-256 in [make_lossywav_fixtures.py](../../scripts/fixtures/make_lossywav_fixtures.py):
  - Kimiko Ishizaka's Goldberg Variations, Variation 4 (Musopen, public domain mark, 16 bits);
  - Anonymous420's "D'un vieux jardin" (CC0, 24 bits).
- **Settings:**
  - the seven presets (`-q I` to `-q X`), `-q 0` and `-q 10`;
  - at standard: `--correction`, `--linkchannels`, `--midside`, `--limit 20000`, `--scale 0.5`, `--feedback`, `--shaping fixed` and `--shaping off`;
  - a repeated run, `--check` on altered files, its own output as input, and `--stdout`.
- **Codings of every output:** `flac -5 -b 512` (lossyFLAC) with and without `--keep-foreign-metadata`, `flac -5` (4,096-sample blocks), `wavpack` and `wavpack --blocksize=512`, and `mac -c2000` (Monkey's Audio 13.44).
  - flac 1.5.0 and wavpack 5.9.0 are Ubuntu 26.04's packages.
  - `wavpack --blocksize=512 --merge-blocks` was run here with wavpack 5.6.0 on one output.

### The marker

- **The chunk:** a `fact` chunk, the second chunk, between `fmt ` and `data`.
  - Body: `lossyWAV <version> @ <DD/MM/YYYY> <HH:MM:SS>, <settings>`, CR LF, two NUL bytes.
  - The body is 53 to 85 bytes in these files, with a pad byte after an odd size.
  - The settings are lossyWAV's long names: `--quality standard`, `--quality 0`, `--quality standard --linkchannels --midside` for `--midside`, `--quality standard --scale 0.500000`. `--limit 20000` is not recorded.
  - The correction file (`.lwcdf.wav`) carries the same text.
- **Other chunks:** a `LIST` and a `bext` chunk of the input are kept, after the `fact` chunk.
- **Not reproducible byte for byte:** the time of the run is in the text. The samples are: the same input and settings gave the same samples in both runs, and the `flac` codings without the chunk are identical.
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
  | `lossyWAV --stdout` | no: `fmt ` and `data` only, the same samples |
  | `flac -b 512` | no |
  | `flac -b 512 --keep-foreign-metadata` | yes, in an APPLICATION block with ID `riff` (one block per chunk: 12, 48, 68 and 8 bytes) |
  | `wavpack` | yes, in the RIFF wrapper |
  | `mac` | yes, in the WAV header data |

### The pattern in the samples

- **Blocks:**
  - 512 samples at 44.1 and 48 kHz, 1,024 at 96 kHz and 2,048 at 192 kHz, anchored at the first sample;
  - the changes of the count fall on that grid: 100% in the recordings and 96 to 100% in the synthetic signals;
  - the few changes off the grid are at the quiet ends of the tremolo, where a block of 32 samples has more zero bits by chance.
- **Per channel:**
  - at the default, the channels of a block often differ: the counts agree at 48 to 52% of the times in the recordings;
  - with `--linkchannels` or `--midside` they agree at 100%, which the help text describes ("single bits-to-remove value for all channels").
- **How many bits, by preset,** over every 32-sample block of both channels:

  | Preset | "D'un vieux jardin" (24 bits): lowest, mean, highest | Goldberg Var. 4 (16 bits): blocks with zero bits, mean |
  |---|---|---|
  | insane | 5, 8.0, 11 | 0%, 0 |
  | extreme | 6, 8.9, 12 | 0%, 0 |
  | high | 7, 9.9, 13 | 0.1%, 0.00 |
  | standard | 8, 10.3, 13 | 1.7%, 0.02 |
  | economic | 8, 10.8, 14 | 14.3%, 0.15 |
  | portable | 9, 11.8, 15 | 77.8%, 1.01 |
  | extraportable | 10, 12.8, 16 | 97.7%, 2.10 |

- **What follows for detection:**
  - On the 24-bit recording, every block keeps a floor of 5 to 10 zero bits. The levels' Low Bits Unused alone reads "8 of 24" at standard, as 16-bit audio in 24 bits would. The per-block figures tell them apart: 2,223 changes on a 512 grid against none.
  - On the quiet 16-bit recording at standard, 1.7% of the blocks have any zero low bits (121 changes, all on the grid). At insane and extreme there are none, and nothing in the samples shows that lossyWAV ran.
  - On loud noise at portable and extraportable, the count is almost constant (10 bits, 1 to 9 changes). The figures read like truncated audio, and no grid is found.
  - 8-bit input: no bits are removed at standard ("0.0000 bits").
- **The tones of the synthetic signal keep all their bits** at every preset; only the noise part has zeroed bits. A figure is therefore a share of the blocks, never a promise about each block.

### What the coders make of it

The bitrate of the 30 s "D'un vieux jardin" before and after lossyWAV at standard:

| Coding | Input | Output |
|---|---|---|
| `flac -b 512` | 1,383 kbps | 481 kbps |
| `flac -b 4096` | 1,361 kbps | 519 kbps |
| `wavpack` (11,025-sample blocks) | 1,367 kbps | 579 kbps |
| `wavpack --blocksize=512` | 1,426 kbps | 549 kbps |
| `wavpack --blocksize=512 --merge-blocks` | | 510 kbps |
| `mac -c2000` | 1,348 kbps | 1,351 kbps |

- **Monkey's Audio** codes the zeroed bits as it codes any bits: the output is the same size as the input. Its figures come from the measurement only.
- **FLAC:**
  - Each subframe's wasted bits is the count of its block when the frames are 512 samples.
  - With 4,096-sample frames it is the smallest count of eight blocks.
  - At standard, the 512-sample codings of the 24-bit recording read wasted bits 8 to 13, the 4,096-sample ones 8 to 13 with fewer high counts, and "Subframes With Wasted Bits" is 100% in both.
- **WavPack:**
  - Each block's Zero Bits (INT32_INFO sub-block) is the smallest count over the block's channels.
  - At 512 samples, its distribution is the per-block minimum of the samples exactly: 8 bits in 64 blocks, 9 in 618, 10 in 1,234, 11 in 504, 12 in 144, 13 in 20.
  - `--merge-blocks` writes blocks of whole multiples of 512 samples (498 of 512, 193 of 1,024, 106 of 1,536 and so on).
  - The Left Shift field stays 0.

## The figures

### Where they are computed

- **The measurement, for every decoder that writes integer PCM:**
  - FLAC, WavPack, Monkey's Audio, TTA, ALAC and TrueHD, and the PCM route of WAV, Wave64 and AIFF;
  - figures on `StreamMeasurements.Levels`, beside Low Bits Unused, computed in `LevelsMeter`, which ORs every sample already.
  - The figures depend only on the samples, not on how a file was coded, so a lossyWAV output coded with Monkey's Audio or with `flac -b 4096` reads as its WAV does.
  - Float output (the lossy decoders of PR #232, float WAV) has no integer low bits and gets none of these figures.
- **The FLAC and WavPack parsers, from the coded syntax, without decoding:**
  - the distribution of the counts the coding states (FLAC's wasted bits per subframe, WavPack's Zero Bits per block) and the changes between consecutive units of a channel;
  - one statistic under one name in both, since it is the same concept.
  - They show the lossyWAV grid only when the units are 512 samples, or multiples of them as `--merge-blocks` writes.

### The measurement

- **Blocks of 32 samples per channel,** anchored at the stream's first sample. A block's count is the number of low bits that are zero in all its samples (the trailing zeros of their OR, within the declared width). A block whose samples are all 0 has no count and is counted apart.
  - **Why 32:** lossyWAV's blocks are multiples of 32 at every rate tested, so a block of 32 never straddles two of them in a file that was not cut.
  - In real audio, a block of 32 samples that has one more zero bit than its lossyWAV block has it in all 32 samples. That happens by chance at a rate of about 2^-32, which leaves only near-silent blocks.
  - The distribution over blocks of 32 samples needs no grid known in advance and one pass.
- **Figures (names to be settled in PR 2; one concept, "low bits unused", as the levels already name it):**
  - **Low Bits Unused per Block:** for each count from 0 to the width, the number of blocks of 32 samples, all channels together. The writers show the shares. The silent blocks are counted apart.
  - **Blocks With More Low Bits Unused Than the Stream:** the share of blocks whose count is above the stream's Low Bits Unused. It is 0 for 16-bit audio in 24 bits and for truncated audio, and 98.7% for "D'un vieux jardin" at standard.
  - **Changes of Low Bits Unused:** the blocks whose count differs from the same channel's last block with a count.
  - **Block Grid:** the largest power of two from 256 to 65,536 samples such that at least 95% of the changes fall within one block of 32 samples of one phase of it, given at least 20 changes; otherwise none. It is reported with the number of changes on it.
    - The phase is free, so a stream cut off the grid keeps it: 100 samples cut from the start still read 512.
    - Thresholds of 20 and 95% are this design's conventions, which the article states.
  - **Channels With Equal Low Bits Unused:** the share of the times (blocks with a count in every channel) at which all channels have the same count. It is not given for mono.
- **Units:** counts are integers and the grid is in samples. The shares are printed by the writers from the counts (one decimal of a percent), as the other measurement figures are, with nothing computed in floating point.

### The FLAC and WavPack statistics

- **"Low Bits Zeroed by the Coding"** (name to be settled; FLAC's "Subframes With Wasted Bits" stays):
  - the distribution of FLAC's wasted bits per subframe and of WavPack's Zero Bits per block;
  - the number of changes between consecutive frames or blocks of each channel.
- **FLAC and mid/side:**
  - A side channel (left minus right) has at least the smaller count of the two channels. A mid channel (their sum shifted right by one) has one bit fewer.
  - The statistic counts the subframes as coded and says so in its description; it does not undo the channel assignment.
  - In the 512-sample codings of the 24-bit recording, 97% of the frames are left/right at the default and 95% with `--linkchannels`; flac chose no mid/side.
- **Monkey's Audio, TTA, ALAC and TrueHD** have no per-block count in their syntax that matches.
  - Monkey's Audio gains nothing from zeroed bits (above).
  - TTA, ALAC and TrueHD: the parsers report no per-unit count of zero low bits today. ALAC's shifted bytes (whole bytes coded apart) and TrueHD's output shifts are other mechanisms; whether either follows lossyWAV's counts is checked in PR 3 on lossyWAV output coded with them, and the format docs say what was found.

## The claim

### From the marker

- **Read by:**
  - the WAV parser (the `fact` chunk);
  - the FLAC parser (APPLICATION blocks with ID `riff`, which are flac's foreign metadata: each holds one RIFF chunk);
  - the WavPack parser (the RIFF wrapper);
  - the Monkey's Audio parser (the WAV header data).
- **The test:** it is what lossyWAV's own check accepts. A `fact` chunk before `data` whose body starts with `lossyWAV ` is read as `lossyWAV <version> @ <date> <time>, <settings>`; the version, the time as written and the settings become nodes.
  - A body that starts that way but does not parse is a node of text with an info finding, `wav.fact-text-unrecognized`.
  - A `fact` chunk in a PCM file that holds neither 4 bytes nor this text keeps today's reading.
- **The WAV parser's fix:**
  - A lossyWAV `fact` chunk states no sample length. The parser stops reading one from it, so neither `wav.fact-samples-mismatch` nor the false Container Duration (43,921.655 s for a 30 s file) appears.
  - This is a fix for every lossyWAV file, whatever the rest of this design becomes.
- **Shared code:**
  - the text parse is about 30 lines that the four parsers need;
  - parsers do not reference each other, so it goes into `Core.Abstractions` (decision 3).
- **The claim:**
  - "lossyWAV 1.4.2", with the settings as its evidence and high confidence, of a role that is neither encoder nor writer (decision 1);
  - lossyWAV turned PCM into PCM before the encoder ran, and the encoder of the FLAC file is still libFLAC.
- **The correction file** carries the same text, so it reads the same. The analyzer does not use file names.

### From behavior

- **Where:**
  - the FLAC parser, for a stream whose frames are 512 samples (1,024 above 48 kHz, 2,048 above 96 kHz);
  - the WavPack parser, for blocks of whole multiples of that length.
- **What it needs:** at least 20 changes of the stated count between consecutive units, and more than half of the units with a count above the stream's smallest.
  - The parser does not see where in a unit the count holds, so a 512-sample FLAC file from another tool that zeroes bits per block reads the same.
  - **Confidence:** low, by [architecture.md](../architecture.md)'s rule for habits, given that no other tool that zeroes bits per block was available to test (decision 2).
- **Not from the measurement:** a measurement adds figures only, never a claim, finding or word that reads as a verdict ([spectral-analysis.md](spectral-analysis.md), "Evidence, not verdicts"). The figures say what they measured; the article explains what lossyWAV's pattern looks like.

## False positives

Measured with [lossywav_figures.py](../../scripts/fixtures/lossywav_figures.py) on variants it writes from the files above (the 24-bit lossyWAV output is "D'un vieux jardin" at standard):

| File | Low Bits Unused | Blocks above it | Changes | Grid |
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
| a fade to digital silence and back (16 bits) | 0 | 0 | 0 | none |
| every input before lossyWAV | 0, or 8 for 16-bit in 24 | 0 | 0 or 1 | none |

- **Silence:** digital silence has no count and is counted apart. A fade to silence gives no changes in the recording tested, and the synthetic tremolo gave 1 change in 10 s.
- **Deliberate truncation and 16-bit audio in 24 bits:** a constant count, no changes, no grid, and "Blocks above it" at 0.
- **Processing after lossyWAV:** a gain or a dither removes the pattern entirely. The figures then read like any audio and the marker, if kept, is the only trace. The article says so.
- **A cut file:** the grid survives, because the phase is free.
- **A tool that zeroes bits per block on another grid** reads with its own grid. One on a 512 grid would read like lossyWAV; none was available to test, and the article says so.
- **Not tested:**
  - decoded HDCD, which `wavpack --help` names beside lossyWAV for `--merge-blocks`;
  - the original lossyFLAC script (David Robinson's), which lossyWAV's README credits;
  - TAK's own lossyWAV-like mode, if any.

## Determinism, memory and budget

- **Determinism:**
  - integers only: an OR per sample (which the levels do already), a trailing-zero count per block of 32 samples and channel, and counters;
  - no floating point and no order that depends on anything but the data;
  - the kernel is `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` and is pinned in `MeasurementDeterminismTests` like the levels.
- **Memory per stream,** whatever its length:
  - per measured channel (at most 32): the current block's OR and fill, and the last count;
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
  - `scripts/fixtures/import_lossywav_fixtures.py` (PR 1) takes the files from the workflow's artifact and checks each against the manifest;
  - the workflow moves to a `fixtures/lossywav` branch and to `workflow_dispatch`.
- **The set,** 2 to 5 s each, about 2 MB in all:
  - `tests/fixtures/wav/lossywav-standard.wav` and `lossywav-standard.lwcdf.wav` (the marker, the correction file), `lossywav-standard-chunks.wav` (`LIST` and `bext` kept), `lossywav-stdout.wav` (no marker), `lossywav-extraportable-6ch.wav`;
  - `tests/fixtures/flac/external-lossyflac-b512.flac` (no marker: the behavior claim), `external-lossyflac-b512-foreign.flac` (the marker in a `riff` APPLICATION block), `external-lossyflac-b4096.flac` (no claim, the figures from the measurement), `external-lossyflac-96k.flac` (the 1,024 grid);
  - `tests/fixtures/wavpack/external-lossywav-merge-blocks.wv` and `external-lossywav.wv` (the wrapper);
  - `tests/fixtures/monkeys-audio/external-lossywav.ape` (the header data; no coded counterpart);
  - the recordings: 5 s excerpts of "D'un vieux jardin" (CC0) and of Goldberg Variation 4 (public domain mark) at standard and portable, as lossyFLAC (decision 4).
- **Built and deterministic** (no lossyWAV): the false positives above, written by `lossywav_figures.py --variants` with a fixed seed, in `tests/fixtures/measurement`.
- **Goldens:** each fixture's report and its `.measure.json`, and a `DescriptionTests` and finding-coverage row for each new node and code, as for every fixture.
- **Cross-check:**
  - no tool that was checked names lossyWAV: ffprobe 6.1.1 and MediaInfo 24.01 show nothing of the `fact` text in the WAV or the FLAC file. MediaInfo 26.01 of the runner is checked in PR 1 and mapped if it does;
  - lossyWAV's own `--check` is the cross-check of the marker: the fixture workflow runs it on every WAV fixture and the manifest holds its answer, which the importer compares with Encspect's claim.

## Docs

- **Articles:**
  - `lossywav` (new): what lossyWAV is, the `fact` chunk and where it survives, the block grid and the per-channel counts, the presets, the limits above, and that the analyzer names lossyWAV only from the marker (high) or from 512-sample FLAC or WavPack units (low). It is worded as observed behavior of lossyWAV 1.4.2 in Encspect's test files;
  - `low-bits-per-block` (new): the measurement's figures and thresholds;
  - `bit-depth-usage`, `wasted-bits` and `wavpack-wrapper`: links and one sentence each.
- **Format docs:**
  - [wav.md](../formats/wav.md): the `fact` text;
  - [flac.md](../formats/flac.md): `riff` APPLICATION blocks, the statistic, the claim;
  - [wavpack.md](../formats/wavpack.md): wrapper, Zero Bits statistic, claim;
  - [monkeys-audio.md](../formats/monkeys-audio.md): header data, and no coded counterpart under what is not covered;
  - [tta.md](../formats/tta.md), [alac.md](../formats/alac.md) and [truehd.md](../formats/truehd.md): the measurement only, under what is not covered.
- **Other docs:** [architecture.md](../architecture.md) (the claim's role), [cli.md](../cli.md) and [gui.md](../gui.md) (where the figures and the claim show), [development.md](../development.md) (fixtures, the workflow, the cross-check), and the plan's entry, which moves from "Paused" to the development steps.

## Pull requests

1. **PR 1, the marker:**
   - the shared `fact` text reader; the WAV parser's fix and its nodes; the FLAC, WavPack and Monkey's Audio parsers' reading;
   - the claim in the role decision 1 picks;
   - the importer, the marker fixtures and their goldens; the `lossywav` article; the format docs.
   - It needs nothing from PR #232.
2. **PR 2, the measurement:**
   - the figures in `LevelsMeter`, the writers (text, JSON, CSV), the GUI's levels section;
   - the determinism pin, the work test, the built false-positive fixtures and the measurement goldens of PR 1's fixtures;
   - the `low-bits-per-block` article.
   - It touches `StreamMeasurements` and the measurement writers, which PR #232 also changes (the withheld counts), so it lands after #232 to avoid a conflict. Nothing else of #232 matters here: lossy decoders write floats, which get none of these figures.
3. **PR 3, the coded statistics and the behavior claim:**
   - the FLAC and WavPack statistic under one name;
   - the low-confidence claim from 512-sample units;
   - their goldens.
- One independent review covers the three, scoped to their diffs and this design.

## Decisions for the lead

1. **The claim's role:**
   - a) a new `ClaimRole.Processor`, shown as "Processed by" beside the encoder and writer, with its own confidence and evidence. **Recommended:** lossyWAV is neither the encoder of a FLAC, WavPack or Monkey's Audio stream nor the software that wrote the file, and this keeps the two existing roles meaning what they mean.
   - b) the encoder role in WAV only, where lossyWAV wrote the samples, and nodes without a claim elsewhere.
   - c) no claim: nodes and the figures only.
2. **A claim from behavior, without the marker:**
   - a) low confidence from 512-sample FLAC and WavPack units, as above. **Recommended:** most lossyFLAC carries no marker, and low confidence is what the rule for habits allows.
   - b) none: only the marker names lossyWAV.
3. **Where the `fact` text reader lives:**
   - a) a small type in `Core.Abstractions` that the four parsers call, added to the list in [CLAUDE.md](../../CLAUDE.md). **Recommended.**
   - b) a copy in each parser.
4. **Real recordings in the repository:**
   - a) 5 s excerpts of the CC0 and the public-domain-mark recordings as committed fixtures, with their sources in [development.md](../development.md). **Recommended:** they show what synthetic signals do not (the 24-bit floor, the per-channel counts, the quiet 16-bit recording that keeps almost every bit).
   - b) synthetic signals only, with the recordings downloaded by the workflow.
5. **Blocks of 32 samples for the measurement:**
   - a) one pass and no grid needed in advance, as above. **Recommended.**
   - b) blocks at the grid found, which needs a second pass or accumulators for every candidate grid and phase.
6. **The grid's thresholds:** at least 20 changes, 95% of them within one block of 32 samples of one phase, grids from 256 to 65,536 samples.
   - a) as proposed, stated in the article as this design's conventions. **Recommended.**
   - b) stricter (99%), which the synthetic signals' near-silent blocks fail (96 to 99% on the grid).
7. **The workflow after this design:** keep it on a `fixtures/lossywav` branch and `workflow_dispatch`, as the Apple and WMA fixture workflows are. **Recommended:** the importer needs it, and so does lossyWAV's `--check` cross-check.
