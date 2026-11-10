# lossyWAV and lossyFLAC: the marker and the low bits per span

Status: signed off by the project lead on 2026-11-08, after its independent review and fix review. The lead resumed the work on 2026-11-08 from the paused text of the plan ([Paused and on hold](../plan.md#paused-and-on-hold)). Every claim here was checked against files lossyWAV wrote. The lead's decisions are under [The lead's decisions](#the-leads-decisions); where the text below names a decision, it is that one.

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
- **Two builds of one version, which agree:** the observations here are of lossyWAV 1.4.2 as the POSIX port builds it, and the official Windows build of 1.4.2, which foobar2000 users run, gives the same results on the same inputs (below, run 42470575652). Older versions were not examined.
  - **The comparison** ([compare_lossywav_builds.py](../../scripts/fixtures/compare_lossywav_builds.py)), as data, of the Linux job's output with the Windows job's (the 64-bit `lossyWAV.exe`, SHA-256 `2022215759a1e48c43989a56f47011cdb1194c6a4926dea7f5033cbfa0d90334`), on inputs identical byte for byte (21 inputs):
    - **samples:** the `data` chunk of every output is identical, in all 121 runs (123 files, correction files among them) and in the 19 fixture runs; so are the 48 WAVs both jobs kept, compared byte for byte;
    - **zeroed bits and grid:** being the same samples, the same per span and channel; the prototype's figures (distribution, Low Bits Unused, changes, grid, changes on it, thin spans, times with equal counts) were computed on both sides for the 12 kept outputs and the 19 fixture outputs, and are equal;
    - **`fact` text:** identical apart from the time, the date in the same `DD/MM/YYYY` form, in all 123 files;
    - **RIFF sizes and chunks:** the same file sizes, RIFF sizes and chunk order in every output; the `--stdout` and STDIN outputs are identical byte for byte, the RIFF overshoot of 68 bytes included; `--stdinname piped` writes `pilossy.ped` on both;
    - **`--check` and the refusal of its own output:** the same answers on the 8 probes. The exit code for "already processed" is −1 on both, which POSIX reports as 255 and Windows as 4294967295;
    - **8 kHz:** both crash at standard and portable (signal 11 on Linux, 0xC0000005, an access violation, on Windows);
    - **help text:** the same, apart from line endings.
  - The help text's `--linkchannels` ("revert to original single bits-to-remove value for all channels") implies that older versions linked the channels.
  - The date was seen only as `DD/MM/YYYY`, on two runners; another locale or version may write it otherwise.
  - The articles and format docs therefore say "lossyWAV 1.4.2", and the text parse does not depend on the date's form.
  - The official Windows build is attached to the Hydrogenaudio forum, which a Cloudflare challenge keeps from scripts. The Internet Archive item `lossyWAV_beta_1.4.3p` is a third-party upload of a beta with an expiry-date patch, and was not used.
  - **The Windows job (decision 9), run on 2026-11-08 (run 42470575652, commit 4bb86de):** the lead downloaded the official build by hand into the private repository (`lossywav/lossyWAV_v1.4.2.zip`, 955,107 bytes, SHA-256 `d5c1a9e17c9569b8fd30c28254c4ff7a7a7213783c7a55f448003cd3e720ffb1`; it holds `bin/32-Bit/lossyWAV.exe`, `bin/64-Bit/lossyWAV.exe`, `fftw.txt` and `gpl.txt`). `lossywav_tool.py` pins it (`WINDOWS`), fetches it through `downloads.private` with `ENCSPECT_PRIVATE_TOKEN`, and `check_sources.py` watches it. The workflow's `windows` job (windows-2025) runs only when the workflow is started by hand: it takes the Linux job's inputs byte for byte (the `lossywav-inputs` artifact) and runs the 64-bit exe through `make_lossywav_fixtures.py --inputs`: every case, probe and fixture input through lossyWAV alone, no codings. Its manifest gives each output's SHA-256, the SHA-256 of its `data` chunk and its `fact` text, to compare with the Linux run's. Until the file is in the private repository, the job fails at its fetch; nothing else waits on it.
  - Its result is the comparison above: the builds agree, so PR 1's `lossywav` article and PR 3's claim from behavior no longer wait on it.
- **Runs:**
  - runs 42463604298 (commit 61cad4c), 42464513495 (dd28265), 42467248396 (7924841), 42468751181 (c33b9ac) and 42469204989 (55ba334), the run of record, 10 to 25 minutes each; wherever two runs were compared (the flac codings of the kept cases, 48 of 48 between the third run and the run of record), the samples were the same;
  - the run of record's artifacts hold every case of the earlier runs (`lossywav-fixtures`, 13613590629, 1.27 GB) and the files to commit with the manifest (`lossywav-fixtures-to-commit`, 13613675366), kept until 2027-02-06;
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
  - By decision 4, neither is committed: they are inputs of the observations here, and the article states only what the synthetic fixtures back.
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
- **lossyWAV's own check,** `--check` (exit code −1 for "already processed" on both builds, reported as 255 on Linux and 4294967295 on Windows; the help text says 16):

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
  | 8 kHz | none: lossyWAV crashed at standard and portable on both builds (signal 11; 0xC0000005 on Windows) after "Too much WAV information before 'data' chunk!" | |
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
  - that is after the two rules of the measurement below (short and thin spans). Without them, the last span of a stream (27 samples in these files) had one more zero bit by chance in most synthetic outputs, which put one change off the grid (as low as 26 of 27, 96.3%), and `--scale 0.5` on the 16-bit signal had no grid.
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

- **Spans of 32 samples per channel,** anchored at position 0 of the stream (the first position the decoder could write, not the first decoded or untrimmed sample). A span's count is the number of low bits that are zero in all its samples (the trailing zeros of their OR, within the declared width), and its magnitude is the largest number of bits its samples need beside the sign bit: the bit length of v for v ≥ 0 and of −v − 1 for v < 0 (so full-scale negative, −2^(n−1), needs n − 1).
  - **Why 32:** lossyWAV's blocks are multiples of 32 at every rate tested, so a span never straddles two of them in a file that was not cut.
  - **Silent spans:** all samples 0. They have no count and are counted apart.
  - **Short spans:** fewer than 32 samples, because the stream ends or a gap or withheld positions follow. They are counted in the distribution but take no part in the changes. A span ends at a gap or at withheld positions. After it, the samples up to the next stream position that is a multiple of 32 form a short span, so every full span keeps its place on the grid of 32; no change is counted across a gap.
  - **Thin spans:** a magnitude fewer than 4 bits above the count, that is, fewer than 4 significant bits above the zeroed ones. They are counted in the distribution, counted apart as thin, and take no part in the changes.
    - What it catches: small samples, where a span has one more zero bit than its lossyWAV block by chance; and spans whose samples are a constant power of two or full-scale negative, whose magnitude equals their count. The second is why a full-scale square wave drops from 63 changes to none. It is named after what it tests, not "quiet", since full-scale spans are thin.
    - What it removes on lossyWAV's output: 0 or 1 of 82,688 spans in the recordings at every preset, up to 828 of 82,399 in the synthetic files, and 4,235 of 25,529 in the 8-bit output, from which lossyWAV removed no bits. So lossyWAV kept at least 4 bits above the removed ones at every preset tested, and the rule hid no change there.
    - It only removes changes, so it cannot make a grid from nothing; it raises the share on a grid by dropping changes off it.
    - **Not tested:** lowered minimum bits kept (`--dynamic` down to 1.0, `--static` down to 3), where spans just above the count could become thin and the pattern could vanish.
  - Trimmed and withheld positions (`trimmedSamples`, `withheldSamples` of PR #232) are not measured; `firstPosition` gives the anchor.
  - The distribution over spans needs no grid known in advance and one pass.
- **Figures** (names in Title Case as the levels write them; as built in PR 2):
  - **Low Bits Unused per Span:** for each count from 0 to the width, the number of spans, all channels together; silent spans apart, and thin spans counted.
  - **Spans Above the Stream's Low Bits Unused:** the spans whose count is above the stream's Low Bits Unused. It is 0 for 16-bit audio in 24 bits and for truncated audio, and 98.7% for "D'un vieux jardin" at standard.
  - **Changes of Low Bits Unused:** the spans, neither silent, short nor thin, whose count differs from the same channel's last such span.
  - **Grid of the Changes:** the largest power of two from 256 to 65,536 samples such that at least 95% of the moments fall within one span of the phase with the most moments (`hits * 20 >= moments * 19`, in integers), given at least 20 moments; otherwise none. It is reported with the number of moments on it. A **moment** is a span position at which some channel's count changes, however many channels change there (amendment of decision 6, below). The phase is the one with the most moments, not the best window of three spans; a window centered between two phases is not tried (a conservative choice, the same in the analyzer, the prototype and the test model).
    - The phase is free, so a stream cut off the grid keeps it: 100 samples cut from the start still read 512.
    - **The smallest grid is 256 samples.** The 22.05 and 32 kHz blocks sit exactly at that edge. A lossyWAV block shorter than 256 (perhaps at 11.025 or 16 kHz, which were not run) would read no grid; the smallest grid is lowered only with fixtures that need it.
    - **Why 20 and 95%:** they are this design's conventions, which the article states. After the short and thin rules, every lossyWAV output tested with 20 changes or more has all of them on the grid (one exception, `--scale 0.5`, above), while edits that put two phases in one stream fall far below 95% (a join: no grid). 95% leaves one change in 20 for chance.
  - **Channels with Equal Low Bits Unused:** the share of the times (span positions at which no channel's span is silent) at which all channels have the same count. It is not given for mono.
- **Units:** counts are integers and the grid is in samples. The shares are printed by the writers from the counts (one decimal of a percent), as the other measurement figures are, with nothing computed in floating point.
- **Checked against the prototype:** [crosscheck_lossywav_figures.py](../../scripts/fixtures/crosscheck_lossywav_figures.py) compares the figures with [lossywav_figures.py](../../scripts/fixtures/lossywav_figures.py) on the PCM that `tests/Encspect.PcmDump` writes for the FLAC, WavPack and Monkey's Audio fixtures: 43 streams on 2026-11-08, with no difference.

### The FLAC and WavPack statistics

- **"Wasted Bits"** beside FLAC's existing "Subframes With Wasted Bits", and the same statistic in WavPack, whose description maps Zero Bits to it:
  - the distribution of wasted bits over the units (FLAC's subframes, WavPack's blocks);
  - the number of changes between consecutive units of each channel (WavPack's count is per block, for all its channels).
- **FLAC and mid/side:**
  - A side channel (left minus right) has at least the smaller count of the two channels. A mid channel (their sum shifted right by one) has one bit fewer.
  - The statistic counts the subframes as coded and says so in its description; it does not undo the channel assignment.
  - The distribution lists every subframe as coded. The changes, and the share of subframes above the smallest count, take only the subframes of a source channel coded as itself (left in left/right and left/side, right in left/right and side/right; every channel of a frame of one or three or more channels), so that switching between mid/side and another assignment is no change. `external-ffmpeg-16-in-24-mid-side-b512.flac` (16-bit audio in 24 bits, FFmpeg's FLAC encoder with `-frame_size 512`, mid/side frames among the others) is in the negative set for this: before the rule it read 212 changes on a grid of 512 and a low-confidence claim.
  - In the 512-sample codings of the 24-bit recording, 97% of the frames are left/right at the default and 95% with `--linkchannels`; flac chose no mid/side.
- **Monkey's Audio, TTA, ALAC and TrueHD** have no per-unit count in their syntax that matches.
  - Monkey's Audio: no per-unit count, and no gain from the zeroed bits was observed (above).
  - TTA, ALAC and TrueHD: the parsers report no per-unit count of zero low bits today. ALAC's shifted bytes (whole bytes coded apart) and TrueHD's output shifts are other mechanisms. Whether either follows lossyWAV's counts is checked in PR 3 on lossyWAV output coded with them: TTA with `libtta_tool.py`'s `tta`, ALAC with `alac_tool.py`'s `alacconvert`, and TrueHD with FFmpeg's experimental `truehd` encoder, the only one at hand. The format docs say what was found.
  - **Found in PR 3** (`check_lossywav_coders.py`, on the committed outputs of the −70 dBFS and −100 dBFS noise-floor signals, before and after lossyWAV, in bytes):
    - Committed as fixtures (PR 3): `external-libtta-24-stereo-lossywav.tta`, `external-alacconvert-24-stereo-lossywav.m4a` and `external-truehd-stereo-44k-24-lossywav.thd`, all from `external-lossyflac-floor-44k.flac`'s samples, with manifests and goldens.
    - TTA (libtta-c 2.3): 148,475 and 104,736 before; 148,563 and 104,894 after. No gain, and TTA had no decoder yet, so no measurement either (it has one since; [tta.md](../formats/tta.md#decoding)).
    - ALAC: Apple's `alacconvert` 154,278 and 124,725 before, 154,474 and 124,636 after; FFmpeg's encoder 150,944 and 126,115 before, 151,026 and 126,254 after. Every element of a 24-bit stream states one shifted byte, whatever the audio. ALAC has no decoder yet either.
    - TrueHD (FFmpeg's experimental encoder, the only one at hand): 170,392 and 126,636 before, 83,016 and 88,736 after. The output shifts of the restart headers, 0 before, read 6 to 7 (at −70 dBFS) and 0 to 4 (at −100 dBFS) after, stated about every 1,400 samples: they follow the zeroed bits, not lossyWAV's grid, and the measurement reads the pattern from the decoded samples (68 and 66 changes on 512).

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
  - A RIFF size past the end of the file, when the last chunk ends exactly at the end of the file, is a new warning, `wav.riff-size-past-chunks`, instead of `wav.truncated` (decision 8). Its message: "The RIFF chunk states N bytes more than the chunks in the file hold; the file may have lost chunks after its last, or the size counts chunks that were never written." It does not say the file is whole: a file cut exactly at a chunk boundary (a trailing `LIST` or `id3 ` chunk lost) meets the same condition.
  - A chunk cut by the end of the file stays `wav.chunk-past-end` and `wav.truncated`. The 8 committed goldens with `wav.truncated` are all cut inside a chunk, so they do not change.
  - Wave64 (`Wave64.cs`) raises `wav.truncated` the same way for its riff chunk's size, so the same rule and code apply there (one concept, one code).
  - Fixtures: besides `lossywav-stdout.wav`, `make_wav_fixtures.py` builds a WAV and a Wave64 file cut exactly after a chunk (a trailing `LIST` chunk dropped), for the finding-coverage rows, and `crosscheck_wav_fixtures.py` gets their expectations.
  - Both stand alone, whatever the rest of this design becomes.
- **Shared code:** the text parse is about 30 lines that the four parsers need, and parsers do not reference each other (decision 3).
- **The claim:**
  - "lossyWAV 1.4.2", with the settings as its evidence and high confidence, of a role that is neither encoder nor writer (decision 1);
  - lossyWAV turned PCM into PCM before the encoder ran, and the encoder of the FLAC file is still libFLAC.
- **The correction file** carries the same text, so it gets the same claim; the article says so. The analyzer does not use file names.

### From behavior

- **Where:** the FLAC parser, and the WavPack parser, at the rates of the table above (22.05, 32, 44.1, 48, 88.2, 96, 176.4 and 192 kHz), for a stream whose units are no longer than the lossyWAV block and divide it (FLAC: fixed frames; WavPack: blocks of whole multiples of a length that divides it, as `--merge-blocks` writes).
- **What it needs:**
  - at least 20 moments: distinct unit positions at which some channel's count changes (decision 6's amendment, carried over: eight channels changing at the two ends of one silent passage are 2 moments, not 16 changes);
  - the greatest common divisor of the distances between the changes' positions (in samples) equal to the lossyWAV block at that rate: every change lies on the block's grid, and not all on a coarser one. A tool that zeroes bits per block of 4,096 samples, coded in frames of 512, has every change on 4,096, so its divisor is 4,096 and not 512;
  - more than half of the compared units (those that have a count, mid and side subframes of FLAC left out) above the stream's smallest.
  - **Silence:** a unit whose samples are all zero (a FLAC constant subframe of 0) has no count: it is listed apart as "Silent", is left out of the distribution, the changes and the smallest count, and leaves the channel's previous count in place, as in the measurement. libFLAC states 0 wasted bits for such a subframe, so without this rule 16-bit audio in 24 bits with a few silent passages read as changes at the frame boundaries and was claimed (found in review). WavPack marks silence in the block header: a block whose maximum magnitude is 0 (and with no Zero Bits, not float) is taken as silent in the same sense; the format document would also allow samples of one bit under it, but wavpack 5.6.0 and FFmpeg's encoder write it for blocks of zeros (found in the fix review). The shares of the statistic, and the evidence's share above the smallest count, are over the units that have a count; the silent ones are listed as a count alone.
  - **Why the divisor replaced the 95% at one phase:** with the divisor equal to the block all the changes lie at one phase of it, so the divisor is the stricter test (100%, not 95%) and the phase counters are dropped. The 95% allowed stray changes; it added nothing at 88.2 to 192 kHz either, where the fixtures have 65 to 77 changes, all on the grid. The distances, not the positions from the first sample, are used, so a cut file keeps the grid.
  - A stream whose units do not divide the block (4,096-sample frames, WavPack's default blocks) has changes only at multiples of its units, so its divisor is not the block and it makes no claim; no separate rule for the units is needed.
- **How the share was set:** from the fixtures. The noise-floor files have 94 to 99% of their subframes above the smallest, the recordings at standard and portable 91 to 99%, and the Goldberg recording at standard 2%. The synthetic signal of the first fixtures keeps every bit of its tones, so about half its subframes sit at the smallest: `synthetic-b512` has 274 of 564 above it, `96k-b1024` 281 of 564 and `96k-b512` 560 of 1,126, and none of the three is claimed, as run 42467248396's manifest records. Between two revisions of this design the share was one in four, which claimed them; it is back to more than half, which the manifest was computed with. The noise-floor fixtures, whose floor reaches every subframe, are claimed under it. The negative set does not depend on the share: it has at most 3 changes, against a gate of 20.
- **The reference is the importer's recomputation** from the committed bytes, not the artifact's manifest, if the rule changes again.
- **On the fixtures:** it claims the 512-sample codings of the −70 dBFS noise-floor signal, every −100 dBFS noise-floor file (22.05 to 192 kHz, frames of the lossyWAV block) and the six-channel file. It does not claim the 4,096-sample codings, the 16-bit noise-floor files (no bit removed) or the three synthetic files above. In the recordings, which are not committed, it claims "D'un vieux jardin" at standard and the Goldberg recording at portable, and not the Goldberg recording at standard (25 changes, but 17 of 862 subframes above the smallest).
- **Negative set:** the 146 FLAC fixtures in `tests/fixtures/flac` and `tests/fixtures/measurement` that `flac` decodes (236 s in all; 2 kHz to 640 kHz, 8 to 32 bits, 11 with more than two channels; the decoded MP3 and AAC fixtures among them), re-coded with `flac -5 -b 512`:
  - no claim, and no grid in the measurement;
  - at most 3 changes of wasted bits, and at most 5 changes of Low Bits Unused (`built-subframe-types.flac`). Without the thin-span rule, a full-scale square wave reads 63.
  - **Run again in PR 3** (`crosscheck_lossywav_behavior.py`): the 157 FLAC fixtures that are not lossyWAV's output (146 when the design was written, 11 more since; the simulated other-4096 file and the 16-bit audio in 24 bits with mid/side frames among them), codings of each: each file in frames of its rate's lossyWAV block and in frames of 512, and labeled 44.1 kHz where its rate was not a tested one: no claim and at most 3 changes of wasted bits in all but one file, and the analyzer's changes equal `flac -a`'s in every file. The simulated `synthetic-lowbits-other-4096.flac`, which zeroes bits per block of 4,096 samples, has 58 changes when coded in frames of 512, all at multiples of 4,096: its divisor is 4,096, not 512, so it is not named (an earlier form of the rule, 95% of the changes at one phase, named it). It is the one file allowed more than 3 changes, by construction.
- **The parser does not see where in a unit the count holds,** so a FLAC file from another tool that zeroes bits per block of exactly lossyWAV's length reads the same. A tool with longer blocks does not (its divisor is longer).
- **Confidence:** low. [architecture.md](../architecture.md)'s rule for habits asks that "the other encoders available for testing do not" show the habit. This claim names a processor, not an encoder, so the rule is read as extended to one (decision 1); and the only other sources available were the negative set above.
- **Untested sources that zero bits per block:** NICAM-decoded audio (a scale factor per block of 32 samples) and DAT long-play, both named in the review; decoded HDCD, which `wavpack --help` names beside lossyWAV for `--merge-blocks`; and the original lossyFLAC script (David Robinson's), which lossyWAV's README credits. None was available.
- **As built (PR 3):**
  - `WastedBitsTally` in `Core.Abstractions` is shared by the FLAC and WavPack parsers (one concept, one name: "Wasted Bits", with "N Bits" entries and "Changes"; the unit is subframes in FLAC and blocks in WavPack, whose description maps Zero Bits to it). It keeps the distribution, the previous count of each channel, the changes, and the greatest common divisor of the distances between the changes' positions.
  - **The grid rule** is that divisor (see above). It admits fixed FLAC frames of the block or a divisor of it and WavPack `--merge-blocks` (multiples of 512), and not 4,096-sample frames or WavPack's default 11,025-sample blocks. Every lossyFLAC fixture claimed before is still claimed.
  - **WavPack in Matroska:** the codec route reads the Zero Bits of each packet's blocks, takes the samples of the packets before as the position (the block index is stripped) and the block's place in the packet as the channel, and makes the same claim; `matroska/external-mkvmerge-wavpack-lossywav.mka` (a file of WavPack blocks of lossyWAV output with no marker, muxed by mkvmerge) reads as the `.wv` file does, 31 changes on 512. The Matroska route has no marker route, so the claim from behavior is the only processor claim there.
  - **WavPack:** one count per block (INT32_INFO Zero Bits, 0 without the sub-block), tracked per place in the block set, at the block's index as its position; DSD blocks and blocks without a main bitstream are not counted. `external-lossywav-floor-44k-merge-blocks.wv` (the samples of `external-lossyflac-floor-44k.flac` with no `fact` chunk, so the low-confidence claim alone) reads 31 changes on 512. The two `--merge-blocks` fixtures with the marker read 36 and 30; the synthetic one has 54.1% of its blocks above the smallest, so the share passes narrowly there (it carries the marker, so nothing hangs on it).
  - **The statistic shows only where some unit has wasted bits;** "Subframes With Wasted Bits" is reported for every stream, as before. This keeps the goldens of the streams without any unchanged.
  - **The claim** is `EncoderClaim.IsBehavior`, "lossyWAV" at low confidence, with the rule's figures as its evidence text. `ProcessorDetection` lets it add evidence to a marker claim ("lossyWAV 1.4.2") of the same software, so the six-channel and `-b512-foreign` files show both pieces of evidence, and the importer's `flacUnits.claim` is checked for every lossyFLAC file (`LossyWavBehaviorTests`).
- **Not from the measurement:** a measurement adds figures only, never a claim, finding or word that reads as a verdict ([spectral-analysis.md](spectral-analysis.md), "Evidence, not verdicts"). The figures say what they measured; the article explains what lossyWAV's pattern looks like.

## False positives

- **Amendment of decision 6 (the lead, 2026-11-08, after the review):** the grid's gate counts moments, not changes. It needs at least 20 distinct span positions at which some channel changes, and the 95% is measured over those positions. Without it, one switch of bit usage at one time in 20 or more channels (an edit, a mix of 16-bit and 24-bit sources) was 20 or more changes at one phase and read as a grid of 65,536 samples with 100% on it. The fixtures have 2 to 6 channels, so their grids do not change; `Changes` still counts every channel's change, and `MeasurementSpanTests` holds 20, 24 and 32 channels with one switch to no grid.
- **A-law audio** is the one non-lossyWAV fixture with 20 or more changes (`wav/external-alaw-mono-8000.wav` expanded to 16 bits, `synthetic-lowbits-alaw-8000`: 3 bits in 37 spans, 4 in 25 and 6 in 1, 40 changes, no grid): A-law's step grows with the level, so the count follows the level of each span. µ-law reads none. PR 3's negative set takes A-law expanded to 16 bits and coded with FLAC frames of 512: the frames' wasted bits change only at frame boundaries and must not be claimed. As built, `synthetic-lowbits-alaw-b512-44100.flac` (the A-law audio repeated to one second, labeled 44.1 kHz) has no change of wasted bits at all in its frames, so it stops at the gate of 20 moments; `synthetic-lowbits-alaw-b64-44100.flac`, the same audio in frames of 64, has 111 changes on a grid of 64 and reaches the divisor test, which rejects it. The reviewer's two inputs for silence are built as `synthetic-lowbits-silence-16-in-24.flac` and `-silence-8ch-16-in-24.flac`.

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
  - the cost was measured in PR 2: the levels with the spans take 1.2 to 1.6 times the FLAC reference's time per unit on the work test's signals (at most 2 allowed), so the 5 units per integer sample stay, with `MeasurementWorkTests` holding the time per unit.
  - **Memory as built:** 33 counters and 2,048 phase counters of 8 bytes (16.6 KB) and 1,032 bytes of codes (two arrays of 516) for one piece's spans, beside 12 bytes a channel.
- **The FLAC and WavPack statistics** are counters over units the parser reads already: a few per frame, no allocation per frame.

## Fixtures

- **Committed once, checked by content:**
  - lossyWAV's output carries the time of the run, so the files are committed once and never regenerated, as the Apple and WMA fixtures are;
  - the workflow makes each fixture at its committed length: lossyWAV runs on an input of that length, so each file is the bytes lossyWAV and the coder wrote, and the manifest holds its SHA-256, its figures and lossyWAV's `--check` answer (`fixtures` in `manifest.json`);
  - `scripts/fixtures/import_lossywav_fixtures.py` (PR 0 and PR 1) copies the files from the artifact, checks each against the manifest's SHA-256, and computes the figures and the behavior rule again from the committed bytes; that recomputation is the reference;
  - **the run of record** is run 42469204989 (commit 55ba334): its artifact `lossywav-fixtures-to-commit` (13613675366, 4.0 MB, digest `sha256:0b9b1512…`) holds the files and the manifest, and is kept until 2027-02-06. If it expires before PR 0 and PR 1 import it, a new run makes new bytes (new times in the text) with the same samples, and the importer takes that run's manifest.
- **The noise-floor signal (decision 4):** `floor_signal` in [make_lossywav_fixtures.py](../../scripts/fixtures/make_lossywav_fixtures.py).
  - The tones of `flac_signal.py`'s music signal (300 Hz, and 1,100 Hz plus 300 Hz per channel, under a 3 Hz tremolo) for the whole length, at −20 dBFS in 24 bits.
  - Over a TPDF noise floor of its own in each channel, from one fixed linear congruential generator per channel, peaking at −100 dBFS (`floor-*`) or −70 dBFS (`floor-44k-high`).
  - The floor is what lets lossyWAV remove bits from the tones, as a recording's noise does; the pure tones of the first synthetic signal keep every bit. The independent floors make the channels' counts differ.
  - **What it reproduces:** at −70 dBFS and standard, every span has 4 to 9 zeroed bits (Low Bits Unused 4 with 68 changes on a 512 grid, against none for 16-bit audio in 24 bits), and the channels agree at 40.5% of the times, as the recordings' 47 to 52%. At −100 dBFS it has 0 to 6 zeroed bits and changes on the grid at every rate.
  - **What it does not:** in 16 bits (tones at −30 dBFS over a floor of 1.5 or 3 LSB, `floor-44k-16bit-quiet` and `floor-44k-16bit`) lossyWAV removed no bit at standard in 2, 3 or 5 s. The quiet 16-bit case of the Goldberg recording (1.7% of spans with zero bits, 25 changes in 5 s) has no synthetic counterpart; it stays an observation of this design, not of the article.
- **The set, from the run of record,** 24 files, 4.3 MB in all:

  | File | Input | lossyWAV | Size | Changes, grid | Behavior claim |
  |---|---|---|---|---|---|
  | `wav/lossywav-standard.wav`, `.lwcdf.wav` (the output and its correction file, one run) | synthetic, 44.1 kHz, 16 bits, 0.5 s | `--correction` | 89 KB each | 2, none | |
  | `wav/lossywav-standard-chunks.wav` (`LIST` and `bext` kept) | the same, with the chunks | standard | 89 KB | 2, none | |
  | `wav/lossywav-stdout.wav` (no marker, the RIFF size) | the same | `--stdout` | 89 KB | 2, none | |
  | `monkeys-audio/external-lossywav.ape` (the header data) | the same | standard | 37 KB | 2, none | |
  | `flac/external-lossyflac-b512.flac`, `-b512-foreign.flac` (the marker), `-b4096.flac` | noise floor at −70 dBFS, 44.1 kHz, 24 bits, 1 s | standard | 71, 71 and 77 KB | 68, 512 | yes, yes, no |
  | `wavpack/external-lossywav.wv` (the wrapper), `-merge-blocks.wv` | the same | standard | 87 and 70 KB | 68, 512 | |
  | `flac/external-lossyflac-floor-22k.flac`, `-32k` | noise floor at −100 dBFS, 24 bits, 1 s, frames of 256 | standard | 45 and 57 KB | 77 and 103, 256 | yes |
  | `flac/external-lossyflac-floor-44k.flac` | the same at 44.1 kHz, frames of 512 | standard | 74 KB | 66, 512 | yes |
  | `flac/external-lossyflac-floor-88k.flac` | the same at 88.2 kHz, frames of 1,024 | standard | 137 KB | 69, 1,024 | yes |
  | `flac/external-lossyflac-floor-176k.flac`, `-192k` | the same at 176.4 and 192 kHz, frames of 2,048 | standard | 262 and 288 KB | 65 and 77, 2,048 | yes |
  | `flac/external-lossyflac-floor-44k-16bit.flac`, `-16bit-quiet.flac` | 16 bits, tones at −30 dBFS, floors at −78 and −84 dBFS, 5 s | standard | 251 and 221 KB | 0, none (no bit removed) | no |
  | `flac/external-lossyflac-synthetic-b512.flac`, `-b4096.flac` | synthetic, 48 kHz, 24 bits, 3 s | portable | 238 and 208 KB | 76, 512 | no (274 of 564 above the smallest) |
  | `wavpack/external-lossywav-synthetic-merge-blocks.wv` | the same | portable | 318 KB | 76, 512 | |
  | `flac/external-lossyflac-96k-b1024.flac`, `-96k-b512.flac` | synthetic, 96 kHz, 24 bits, 3 s | portable | 354 and 378 KB | 86, 1,024 | no (281 of 564, 560 of 1,126) |
  | `flac/external-lossyflac-6ch.flac` (six channels, the marker) | synthetic, 48 kHz, 24 bits, six channels, 3 s | extraportable | 673 KB | 371, 512 | yes |

  - Paths are under `tests/fixtures`. "Changes, grid" are the measurement's figures of the samples lossyWAV wrote; "Behavior claim" is the FLAC rule of [From behavior](#from-behavior) on the file as coded, as the manifest records it.
  - **The change gate:** every file that is to show the pattern has at least 40 changes on its grid, twice the gate. The noise-floor files are each the shortest of 1, 2, 3 and 5 s that does so (1 s in every case; the manifest records the lengths tried).
  - **The rates the article may state** are those of the noise-floor files and the 48 and 96 kHz synthetic files: 22.05, 32, 44.1, 48, 88.2, 96, 176.4 and 192 kHz, the same as the behavior claim's.
  - **The marker files** are 0.5 s and below the gate on purpose: they carry the marker, and their measurement goldens record a stream without a grid.
  - lossyWAV's `--check` answered "already processed" for the lossyWAV output of every fixture.
- **Expected findings** (PR 0 and PR 1):
  - `wav/lossywav-stdout.wav`: on main `wav.truncated` (warning, 68 bytes short); after PR 0 only `wav.riff-size-past-chunks` (warning, 68 bytes);
  - every WAV with the marker: on main `wav.fact-samples-mismatch`; after PR 0 none, and after PR 1 the claim;
  - no other finding on any fixture.
- **Built and deterministic** (no lossyWAV): the false positives above, as short excerpts in `tests/fixtures/measurement` (PR 2, as built): `make_measurement_fixtures.py` writes 13 `synthetic-lowbits-*` files (A-law and the dithered output among them) from the noise-floor fixtures and signal (decision 4) with the figures each is built to have, worked out by `lossywav_figures.py`; [development.md](../development.md#measurements) lists them. The table above was measured on the recordings.
- **Goldens:** each fixture's report and its `.measure.json`, and a `DescriptionTests` and finding-coverage row for each new node and code, as for every fixture. PR 2 also changes the `.measure.json` of every integer stream (decision 10).
- **Cross-check:**
  - no tool that was checked names lossyWAV: ffprobe 6.1.1 and MediaInfo 24.01 show nothing of the `fact` text in the WAV or the FLAC file. MediaInfo 26.01 of the runner is checked in PR 1 and mapped if it does;
  - lossyWAV's own `--check` is the cross-check of the marker: the manifest holds its answer for the WAV each fixture was made from. The importer compares it with Encspect's high-confidence claim only for the files that keep the chunk: the WAVs but `lossywav-stdout.wav`, `-b512-foreign.flac`, the six-channel FLAC, the `.wv` files and the `.ape`. The other FLAC files hold no marker;
  - the Windows job's manifest was compared with the Linux one ([The build](#the-build)): the official Windows build and the POSIX port agree on every case.
- **Sources:** the two recordings, the lossyWAV commit and the Windows build are registered in `check_sources.py` (the scheduled sources check). The recordings are inputs only; [development.md](../development.md) names them as inputs of the fixture workflow, with their licenses as stated above.

## Docs

- **Articles:**
  - `lossywav` (new): what lossyWAV is, the `fact` chunk and where it survives, the RIFF size of `--stdout`, the block grid by rate and the per-channel counts, the presets and the minimum bits kept, the correction file, the limits above, and that the analyzer names lossyWAV only from the marker (high) or from FLAC and WavPack units (low). It is worded as observed behavior of lossyWAV 1.4.2 in Encspect's test files (the POSIX port and the official Windows build, which agree), and states only what the committed fixtures back (the rule for encoder observations in [CLAUDE.md](../../CLAUDE.md)): the block length at the rates of the noise-floor fixtures, the per-channel counts, the marker and where it survives. The preset table, the recordings' figures and the rates without a fixture stay in this design;
  - `low-bits-per-span` (new): the measurement's figures, the span, the silent, short and thin spans, and the thresholds;
  - `bit-depth-usage`, `wasted-bits` and `wavpack-wrapper`: links and one sentence each.
- **Format docs:**
  - [wav.md](../formats/wav.md): the `fact` text, `wav.riff-size-past-chunks`;
  - [flac.md](../formats/flac.md): `riff` APPLICATION blocks, the statistic, the claim;
  - [wavpack.md](../formats/wavpack.md): wrapper, the statistic, claim;
  - [monkeys-audio.md](../formats/monkeys-audio.md): header data, and no coded counterpart under what is not covered;
  - [tta.md](../formats/tta.md), [alac.md](../formats/alac.md) and [truehd.md](../formats/truehd.md): the measurement only, under what is not covered.
- **Other docs:** [architecture.md](../architecture.md) (the claim's role, the rule for habits read for a processor), [cli.md](../cli.md) and [gui.md](../gui.md) (where the figures and the claim show), [development.md](../development.md) (fixtures, sources, the workflow, the cross-check), and the plan's entry, which moves from "Paused" to the development steps.

## Pull requests

0. **PR 0, the WAV parser's fix:** the `fact` chunk no longer read as a sample length when it holds lossyWAV's text; `wav.riff-size-past-chunks` in WAV and Wave64, with the built chunk-boundary fixtures and their cross-check expectations; the marker and stdout fixtures with their goldens; the importer. It needs none of decisions 1 to 4 (decision 11).
1. **PR 1, the marker:**
   - the shared `fact` text reader; its nodes in WAV; the FLAC, WavPack and Monkey's Audio parsers' reading;
   - the claim in the Processor role (decision 1) and its outputs (decision 13);
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

## The lead's decisions

Decided by the project lead on 2026-11-08, after the independent review and its fix review:

1. **The claim's role:** a new `ClaimRole.Processor`, shown as "Processed by" beside the encoder and writer, with its own confidence and evidence. The rule for habits in [architecture.md](../architecture.md) is extended to processors, in PR 1.
2. **A claim from behavior, without the marker:** yes, at low confidence, from FLAC and WavPack units at the tested rates, with the rule of [From behavior](#from-behavior) (more than half of the units above the smallest, set from the fixtures).
3. **The `fact` text reader:** a small type in `Core.Abstractions` that the four parsers call, added to the list in [CLAUDE.md](../../CLAUDE.md). Not a copy per parser, and not a host route.
4. **No recordings in the repository:** synthetic signals only, plus the noise-floor signal (tones over a TPDF floor of their own in each channel, [Fixtures](#fixtures)). The two recordings stay inputs of this design's observations, downloaded by the workflow; none of their output is committed, and the articles state only what the committed fixtures back.
5. **Spans of 32 samples** for the measurement, in one pass.
6. **The grid's thresholds and rules** as proposed: at least 20 changes (amended below: moments), 95% of them within one span of one phase (for the measurement's grid; the claim from behavior uses the divisor of the changes' distances, PR 3), grids from 256 to 65,536 samples, and short and thin spans out of the changes, with the rule named after what it tests ("thin", not "quiet") and the magnitude defined as in [The measurement](#the-measurement).
7. **The workflow** moves to a `fixtures/lossywav` branch with `workflow_dispatch` only, as the Apple and WMA fixture workflows are; the push trigger on this design's branch was removed before the design merged.
8. **The RIFF size of `--stdout`:** the warning `wav.riff-size-past-chunks`, with the message of [From the marker](#from-the-marker), and the same rule in Wave64.
9. **The official Windows build:** the lead downloads lossyWAV 1.4.2 for Windows by hand, and the workflow's Windows job runs it on the Linux job's inputs ([The build](#the-build)). Only PR 1's `lossywav` article and PR 3's claim from behavior wait for it; PR 0 and PR 2 do not.
   - **Result (2026-11-08, run 42470575652):** the two builds agree on the samples, the zeroed bits and grid, the `fact` text apart from its time, the RIFF sizes (`--stdout` included), `--check`'s answers and the prototype's figures, in every case compared; the only differences are how each system reports the same exit codes and the help text's line endings. Nothing waits on it any more.
10. **The measurement goldens:** the figures in every integer stream's levels, and all 76 changed `.measure.json` goldens in PR 2, reviewed by their diff.
11. **Order:** PR 0 (the WAV fix and the RIFF size) ships first, on its own.
12. **Finding codes:** `wav.fact-text-unrecognized`, `flac.riff-fact-text-unrecognized`, `wavpack.wrapper-fact-text-unrecognized` and `monkeys-audio.header-fact-text-unrecognized` (info), each with its finding-coverage row; a recognized lossyWAV text gets a description, not a finding.
13. **"Processed by" in the outputs:** the JSON role `processor`; CSV columns Processor and Processor Confidence after Writer Confidence; the GUI shows it in the stream's summary line and in the claims list, with no new column in the file list.
