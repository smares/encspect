# Detection pre-check: the start of the data before the searches

Status: draft for the project lead, not approved; nothing of it is built. Revised after an independent review: the measurements were redone in one batch, with prototypes rebuilt from this description (the first round's were deleted), on `main` at b656c90. Estimates are marked as estimates.

## Summary

- **The problem:**
  - format detection, every format parser's sniff over every file and twice per file, is 35 to 73% of the time of a NativeAOT scan of a fixture folder and 57% of a scan of all fixtures (round 1, instrumented build);
  - almost all of it is in the nine parsers that search the first 64 KiB or 1 MiB for a chain further in (DTS, WavPack, FLAC, Monkey's Audio, MPEG audio, ADTS, TrueHD, AC-3, Ogg), and they search files that are plainly another format's.
- **Two changes** (named in "Two changes, named once"):
  - **reuse:** the second pass takes the first pass's results where both see the same bytes; host-only, no contract change;
  - **the pre-check:** each searching parser also says what it finds at the start of the data without a search, and the highest class its search can give; the host runs a search only where its result could still change the report.
- **The gain, in one batch** (NativeAOT, minimum of 7 runs, against `main`):
  - **reuse alone:** all 2,502 fixtures -29%, the 2,025 fixtures under 32 KiB -26%, the folder of 300 short MP3 and AAC files -31%, the fixtures with junk in front -25%; a fixture folder -4 to -34%; one 300-second file -33% (WAV), -29% (Musepack), -13 to -17% (Vorbis, Opus, Speex), and nothing beyond the noise for the others;
  - **what the pre-check adds to reuse:** all fixtures -13 points (-38% in all), small files -11 points, the fixture folders of the containers -5 to -49 points (WavPack -35, TTA -49, AIFF -27, WAV -21, Monkey's Audio -19, Musepack -17, Matroska -14, Ogg -11), 300-second files of Vorbis, Opus, Speex, Musepack and WAV -17 to -41 points, 120 tagged MP3 files -10 points (reuse alone gives -4% there);
  - **where it adds nothing, or loses:** the folder of 300 short MP3 and AAC files (+10 points), the 120 MP3 files without tags (+14), the fixtures with junk in front (+2), and the raw streams' folders, except `dts` (-14 points): -7 to +6, `truehd` +6.
- **The guarantee:** reports stay byte-identical. The prototype's JSON and CSV reports, `--only-format` and `--as` were identical to `main`'s on every fixture, the benchmark files, a tagged folder and 26,000 junk-prefixed, damaged and limit-sized inputs (270 comparisons, none different), and the checking wrapper found no violation of the contract on any of them.
- **What it costs:** reuse alone costs about 100 lines of host code. The pre-check adds two members on `IFormatParser` whose defaults keep any parser exactly as it is, a check of the start of the data in nine parsers, a checking wrapper, three test classes and a fuzz mode.
- **Recommendation:** reuse alone first, as its own step; the pre-check only as a second step, and only if scans of containers (lossless and WAV libraries) or of tagged MP3 files matter, since on the headline scans of MP3 and AAC folders its own gain does not clear the project's bar of about 15% (open decision 1).

## What was measured

Setup: Linux x64, Xeon at 2.8 GHz, 4 vCPUs shared with another session. Every timed run under the machine lock, on one pinned core, after the load average fell below 1.5 (all 2,016 timed runs started below it; the highest was 1.47), with the variants interleaved and rotated each round; minimum of 7 runs of `encspect --csv`, seconds. "NativeAOT" is `dotnet publish src/Encspect.Cli -c Release -r linux-x64 -p:PublishAot=true` (x86-64-v2, what ships).

**One batch, six builds of three trees:** the first round compared columns from three batches, which let batch noise pass for gains. This round runs every variant in every case in the same loop:

- **`main`:** the tree at b656c90, published unchanged.
- **The prototype tree,** published once and switched by an environment variable: no switch (the prototype's overhead; it reads between -9% and +6% against `main`, which is the noise floor of this machine, so a difference of less than about 6 points is not a result), `reuse`, `pre` (the pre-check without reuse), `pre,reuse` (the design) and `pre,reuse,two` (the design with the two-byte check of "The contract change").

Inputs:

- the fixture folders, audio files only (no `.json`, `.md` or `.txt`), each as one command line; "all fixtures" is the 2,502 of them, "fixtures under 32 KiB" the 2,025 below that size, of every format; "1,000 junk bytes in front" is each fixture with 1,000 seeded random bytes before it;
- the folder of 300 files of 3 s (120 MP3, 90 ADTS and 90 MP4 files of AAC, and an empty marker file), the 300-second files of "Analysis speed of the lossy formats" in `docs/development.md`, and the 300-second lossless files of "Measured: ReadyToRun against NativeAOT";
- **a tagged folder (new):** the 120 MP3 files of the folder of 300, each with an ID3v2.4 tag in front (frames, 1 KiB of padding): 30 with nothing else, 60 with an ID3v1 tag at the end and 30 with an APEv2 tag at the end, so 90 have a trailing tag, which turns reuse off; "the same 120 MP3 files without tags" is their untagged folder; "tagged folder" adds the 145 fixtures of `tags`.

### Where the time goes (round 1)

An instrumented scratch build timed the two passes of detection (every parser's sniff and the declared end of the best), the tags, and the parse. "Detection" below is its share of the whole run, start of the process included (one run, the fastest of 3; not repeated in round 2):

- **Folders:** all fixtures 57%; under 32 KiB 49%; the 300 short MP3 and AAC files 40%; fixture folders from 22% (`stats`, 9 files) and 34 to 36% (`aac`, `adts`, `alac`, `apple`, `truehd`) to 72 and 73% (`wavpack`, `monkeys-audio`).
- **300-second files:** MP3 3 to 16%, AAC 7 to 11%, AC-3 and E-AC-3 5 to 6%, WMA 14%, Vorbis, Opus and Speex 30 to 37%, Musepack 55%; FLAC 3 to 7%, TrueHD 1 to 2%, WavPack 14 to 26%, Monkey's Audio 6 to 19%, TTA 12%, WAV 64 to 67%.
- **By parser, all fixtures:** of 1,378 ms of detection (both passes, sniffs and declared ends), the DTS sniff takes 368 ms, WavPack 158, FLAC 146, Monkey's Audio 124, MPEG audio 119, ADTS 96, TrueHD 95, AC-3 90, Ogg 75; Musepack, which looks only at the start of the data, 40 (its check for versions 4 to 6); the other parsers that look only at the start (MP4, ASF, DTS-HD, TTA, Matroska, WAV, Wave64, AIFF) 2 to 7 ms each. Pass 1 takes 729 ms, pass 2 649.
- **Why a file of another format costs so much:** a searching sniff scans up to 64 KiB (Ogg) or 1 MiB (the others) of a file that is not its format. DTS, AC-3 and ADTS read it through a 64 KiB block (`BlockCache`), allocated and filled for each sniff, WavPack through two, and FLAC, Monkey's Audio and TrueHD through a window that holds a file of up to 128 KiB whole (`SourceWindow`). On a 300-second WAV file the searches meet sync-like bytes all through the PCM samples (DTS alone 10 ms of a 50 ms run).

### Reuse alone, and what the pre-check adds

Percentages are against `main` of the same batch; the last column is the design (pre-check and reuse) against reuse alone, so it is the pre-check's own gain (negative is faster). Differences below about 6 points are noise. "Detection" is round 1's share of the run, for scale.

| Case | Files | `main` (s) | Detection (round 1) | Reuse alone | Pre-check, no reuse | Pre-check and reuse | Pre-check's own gain over reuse |
|---|---|---|---|---|---|---|---|
| **Folders and mixed scans** | | | | | | | |
| all fixtures | 2502 | 1.946 | 57% | -29% | -31% | -38% | -13% |
| fixtures under 32 KiB | 2025 | 0.955 | 49% | -26% | -24% | -34% | -11% |
| 300 short MP3 and AAC files | 301 | 0.666 | 40% | -31% | -22% | -24% | +10% |
| all fixtures, 1,000 junk bytes in front | 2502 | 1.713 | - | -25% | +22% | -23% | +2% |
| **Tagged and untagged MP3 (new)** | | | | | | | |
| the same 120 MP3 files without tags | 120 | 0.222 | - | -25% | -3% | -15% | +14% |
| 120 MP3 files with ID3v2, 90 of them also with ID3v1 or APE | 120 | 0.249 | - | -4% | -2% | -14% | -10% |
| tagged folder: those 120 and the 145 `tags` fixtures | 265 | 0.285 | - | +0% | -1% | -9% | -9% |
| **Fixture folders of the raw streams** | | | | | | | |
| `mpeg` | 164 | 0.161 | 52% | -13% | -4% | -13% | -0% |
| `layer3` | 37 | 0.042 | 46% | -11% | -13% | -17% | -7% |
| `adts` | 57 | 0.054 | 35% | -13% | -2% | -15% | -2% |
| `ac3` | 78 | 0.064 | 44% | -15% | -11% | -18% | -3% |
| `dts` | 165 | 0.101 | 53% | -20% | -16% | -32% | -14% |
| `flac` | 114 | 0.150 | 56% | -26% | -17% | -30% | -5% |
| `truehd` | 65 | 0.103 | 36% | -13% | -7% | -8% | +6% |
| **Fixture folders of the containers** | | | | | | | |
| `mp4` | 187 | 0.111 | 42% | -20% | -12% | -24% | -6% |
| `ogg` | 120 | 0.078 | 43% | -16% | -18% | -25% | -11% |
| `musepack` | 99 | 0.114 | 55% | -28% | -32% | -41% | -17% |
| `asf` | 152 | 0.204 | 37% | -17% | -14% | -21% | -5% |
| `matroska` | 189 | 0.116 | 43% | -18% | -25% | -30% | -14% |
| `wav` | 242 | 0.116 | 56% | -23% | -27% | -39% | -21% |
| `aiff` | 119 | 0.056 | 60% | -14% | -24% | -37% | -27% |
| `wavpack` | 159 | 0.184 | 72% | -32% | -51% | -56% | -35% |
| `monkeys-audio` | 113 | 0.228 | 73% | -34% | -23% | -46% | -19% |
| `tta` | 67 | 0.141 | 71% | -13% | -43% | -56% | -49% |
| **Other folders** | | | | | | | |
| `tags` | 145 | 0.070 | 51% | -10% | +10% | -3% | +7% |
| `fuzz` | 57 | 0.070 | 39% | -11% | -6% | -14% | -3% |
| `aac` | 53 | 0.071 | - | -10% | -4% | -13% | -4% |
| `layer2` | 27 | 0.040 | - | -8% | -2% | -7% | +1% |
| `alac` | 4 | 0.018 | - | -18% | -1% | -17% | +1% |
| `apple` | 31 | 0.060 | - | -14% | -12% | -13% | +2% |
| `stats` | 9 | 0.030 | - | -4% | +0% | -6% | -2% |
| `vbr` | 49 | 0.047 | - | -19% | -2% | -9% | +13% |
| **One file of 300 s (the lossy ones) or a lossless benchmark file** | | | | | | | |
| MP3 CBR 128, 300 s | 1 | 0.085 | 16% | -7% | -4% | -5% | +3% |
| MP3 CBR 128, 60 min | 1 | 0.620 | 3% | +4% | +4% | -2% | -6% |
| AAC LC, ADTS | 1 | 0.140 | 11% | -2% | +4% | +12% | +14% |
| AAC LC, MP4 | 1 | 0.145 | 11% | -2% | -6% | -8% | -7% |
| AC-3 | 1 | 0.229 | 5% | +0% | -0% | -1% | -1% |
| WMA v2 | 1 | 0.132 | 14% | -2% | -5% | -6% | -4% |
| Vorbis | 1 | 0.063 | 37% | -17% | -25% | -32% | -17% |
| Opus | 1 | 0.059 | 32% | -13% | -22% | -29% | -18% |
| Speex | 1 | 0.063 | 30% | -14% | -16% | -30% | -19% |
| Musepack SV8 | 1 | 0.028 | 55% | -29% | -38% | -47% | -25% |
| FLAC -5 | 1 | 0.213 | 7% | -2% | -1% | -1% | +1% |
| WavPack default | 1 | 0.083 | 26% | -8% | -9% | -17% | -10% |
| Monkey's Audio `-c3000` | 1 | 0.111 | 19% | -11% | -14% | -16% | -5% |
| TTA | 1 | 0.121 | 12% | +3% | -5% | -7% | -10% |
| WAV, 16-bit | 1 | 0.033 | 67% | -33% | -51% | -60% | -41% |
| TrueHD, 16-bit | 1 | 0.821 | 2% | -1% | +3% | -6% | -5% |


- **The split:** reuse alone gives most of the gain on the mixed scans: all fixtures -29% against -38% for the design, the folder of 300 short MP3 and AAC files -31% against -24%, junk in front -25% against -23%. The pre-check's own gain is on the folders of containers, which reuse cannot help much (it still sniffs everything once): WavPack -35 points, TTA -49, AIFF -27, WAV -21, Monkey's Audio -19, Musepack -17, Matroska -14, and on the 300-second Vorbis, Opus, Speex, Musepack and WAV files (-17 to -41 points).
- **Where the pre-check loses:** the folder of 300 short MP3 and AAC files (+10 points), the 120 MP3 files without tags (+14), `vbr` (+13), `truehd` (+6). Where every file is a raw stream without tags, both run each search once, and the checks of the start of the data cost a few points more than they save. The pre-check alone (no reuse) is +22% on junk in front: with junk nothing matches at the start, so every search runs in pass 1 and again in pass 2.
- **Tagged files (new):** the folder of 300 short files has no tags, so reuse applied to every file in it; a real library does not look like that. With 90 of 120 MP3 files carrying a trailing tag, reuse alone falls from -25% to -4%, and the pre-check with reuse gives -14%, 10 points more than reuse alone; on the tagged folder with the 145 `tags` fixtures (data mostly unrecognized) reuse alone gives 0% and the design -9%. The pre-check is the only one of the two that helps a tagged MP3 library, and it does not reach the bar there.
- **The bar of about 15%:** reuse alone clears it on all fixtures, the small files, the folder of short files, junk in front, every container's fixture folder but AIFF and TTA (-14% and -13%), and not on a tagged MP3 library or a single 300-second raw stream. The design clears it on the same and on the tagged MP3 library by a hair (-14%); its own gain over reuse clears it on six of the ten container folders (Matroska is at -14) and on none of the mixed scans of MP3 and AAC.
- **A single 300-second file:** reuse alone helps WAV (-33%), Musepack (-29%), Vorbis, Opus and Speex (-13 to -17%) and nothing else beyond the noise; the design adds to those and gives WavPack -17%, Monkey's Audio -16%; none of the raw streams (MP3, ADTS, AC-3, FLAC, TrueHD) moves beyond the noise of about 10%.
- **The earlier tables' oddities:**
  - the first round's "upper bound" (the winner only, sniffed once, read from a table made by `main`) was beaten in about 14 rows, because it came from a batch of its own and loaded its table in every process; it was an estimate, not a bound, and is dropped. What is left to gain is not known;
  - its gaps between batches for `tags` and `truehd` (the first round read -14% (`tags`) and -15% (`truehd`) for reuse in one batch and +1% and -6% for the design in another) are not reproduced: in one batch `tags` reads -10% (reuse), +10% (pre-check alone) and -3% (design), `truehd` -13%, -7% and -8%, so they were batch noise;
  - the first round's figures for the folder of short files (-19 and -22%) and all fixtures (-46%) read lower or higher here (-24% and -38%); batch noise of 5 to 10 points is normal on this machine.
- **The two-byte check** (the last table): inside the noise nearly everywhere; it helps `mpeg` (-13% to -20%) and `layer3` (-17% to -24%), where 0xFF passes the one-byte filters of ADTS and DTS, and `dts` (-32% to -36%), and costs nothing measurable elsewhere (`fuzz` and the folder of short files read worse by 5 to 12 points, which is the noise of those rows). It is a few lines per parser; open decision 10.

The two-byte check: the design without and with it, against `main`:

| Case | Pre-check and reuse | Plus two-byte check |
|---|---|---|
| all fixtures | -38% | -38% |
| fixtures under 32 KiB | -34% | -36% |
| 300 short MP3 and AAC files | -24% | -20% |
| all fixtures, 1,000 junk bytes in front | -23% | -23% |
| the same 120 MP3 files without tags | -15% | -18% |
| 120 MP3 files with ID3v2, 90 of them also with ID3v1 or APE | -14% | -9% |
| `mpeg` | -13% | -20% |
| `layer3` | -17% | -24% |
| `adts` | -15% | -18% |
| `dts` | -32% | -36% |
| `flac` | -30% | -31% |
| `ogg` | -25% | -25% |
| `wavpack` | -56% | -56% |
| `tags` | -3% | -2% |
| `fuzz` | -14% | -3% |

### What the bar says

The project's bar is a gain of about 15%.

- **Reuse alone clears it on:**
  - all fixtures (-29%), the small files (-26%), the folder of 300 short MP3 and AAC files (-31%), files with junk in front (-25%);
  - the fixture folders of every container but AIFF and TTA (-14%, -13%), and of `flac` (-26%), `dts` (-20%), `ac3` (-15%);
  - one 300-second file of WAV, Musepack, Vorbis, Opus and Speex.
- **Reuse alone does not clear it on:** the folders of MP3 (`mpeg` -13%, `layer3` -11%, `layer2` -8%), ADTS and AAC, `truehd`, `tags`, `fuzz`, a tagged MP3 library (-4%), and a single raw stream.
- **The pre-check with reuse clears it on** all of those, on a tagged MP3 library by a hair (-14%), on `adts` (-15%) and `fuzz` (-14%), on `mpeg` (-13%) only just, and with the two-byte check (-20%); it does not on `truehd` (-8%), `tags` (-3%) and a single raw stream.
- **The pre-check's own gain over reuse clears it on** the folders of WavPack, TTA, AIFF, WAV, Monkey's Audio and Musepack, and on one 300-second file of Vorbis, Opus, Speex, Musepack and WAV; not on any mixed scan (-13% on all fixtures, -11% on the small files, +10% on the folder of short files).

## Why every file is sniffed twice

- **Pass 1** (`Analyzer.FindContainer`): after the leading tags, every parser sniffs the data up to the end of the file, to find the best format and ask it for its declared end (`IFormatParser.DeclaredEnd`). That end must be known before the trailing tags are looked for, since a trailing candidate that starts before it is the container's own chunk (plan, "Tag detection and false positives", rule 3).
- **Pass 2** (`Analyzer.ParseData`): every parser sniffs again, on the window without the trailing tags and with `SniffContext.FirstEmbeddedTag`. Its ranking picks the parser that runs and the alternatives the report lists; `Describe` asks the best for its declared end again when there are other candidates.
- **The two can differ:** a sniff reads to the end of its window. WavPack's class 100 for a single block needs the block to end at the end of the data or at the first embedded tag, Musepack's version 4 to 6 frames must end there, MPEG audio, ADTS, AC-3 and DTS count frames up to it, and Musepack and WavPack stop at the first embedded tag. Pass 1 sees a trailing ID3v1 or APE tag as data; pass 2 does not.
- **`--only-format`** adds `Analyzer.DetectFormat`, which runs pass 1 and then the ranking again on the data without the trailing tags (never with `FirstEmbeddedTag`): four sniffs of every parser for a file that is analyzed, two for one that is skipped.
- **Is the first pass reusable?** Only where both passes see the same bytes: no trailing tag and no embedded tag, so that the window, the extension hint and `FirstEmbeddedTag` are equal. A sniff reads nothing else, so it returns the same result. Where a trailing tag shortens the window (most tagged MP3 files), both passes stay.

## Two changes, named once

- **Reuse:** pass 2 (and `DetectFormat`'s second ranking) takes the results of pass 1 where both see the same bytes, instead of sniffing again. It is host-only: no contract change, no parser touched. This document says "reuse" for it everywhere.
- **The pre-check:** the contract change below, and the host rule that skips searches with it. It works with or without reuse; "pre-check and reuse" is the design proposed.

## The contract change

Two members on `IFormatParser`, both with a default:

```csharp
// What Sniff finds at the start of the data, decided without a search: Sniff's own result there, or None
// when that result is not decided there. A format matched only at the start of the data keeps the default.
SniffResult SniffAtStart(SniffContext context) => FurtherInClass == 0 ? Sniff(context) : SniffResult.None;

// The highest class Sniff returns where SniffAtStart returns None or throws: 0 for a format matched only
// at the start of the data.
int FurtherInClass => 0;
```

The rules hold for every context the host builds (both passes, `DetectFormat`, the forced parser's own sniff), with the same window, extension hint and `FirstEmbeddedTag` for the two methods:

- **Rule 1:** where `SniffAtStart` matches, `Sniff` returns the same result, every field (class, offset, end, frames, verified frames, note).
- **Rule 2:** where `SniffAtStart` returns None, or throws, `Sniff` returns at most `FurtherInClass`.
- **Rule 3:** a match starts on a byte of the window (`Offset` at least its start and below its end), which every sniff does today and the host's argument below relies on.
- **Rule 4, exceptions:** the host treats any exception from `SniffAtStart` as None, as for `Sniff`: `EndOfStreamException` and `InvalidDataException` silently, anything else but cancellation and running out of memory as a failure with the activity "sniffing the data", the text `Sniff`'s failure has, so that `AddFailure` keeps one finding per parser. A parser may throw a bug exception from `SniffAtStart` only where `Sniff` throws one on the same context, so the pre-check never reports a `parse.failed` that `main` would not (it may stay silent where `main` reports one, see "What does change").
- **Reading:** `SniffAtStart` reads the first bytes and the chain at the start, not a search window. Where the format skips a run of zero bytes first (MPEG audio, ADTS, AC-3, DTS), the run is as unbounded as in `Sniff`: a file of zeros costs a scan of the whole window in both.
- **One bound, one name:** each parser declares `FurtherInClass` as a named constant that its `Sniff` uses for the class of a chain further in (ADTS's `Chain(50, ...)`, AC-3's and DTS's `found == start ? 92 : 50`, and so on), never a second literal. A test per searching parser (below) builds an input where `Sniff` returns exactly `FurtherInClass`, so the bound is reached and never exceeded.
- **The defaults are always correct:** a parser that overrides neither is sniffed in full every time, as now, and nothing is skipped because of it. A new parser starts that way.
- **What is a violation:** a searching parser gains only when it overrides both members. Overriding `FurtherInClass` alone is a violation unless the value is the parser's maximum class (the default `SniffAtStart` returns None, so `Sniff` may return anything at the start): `FurtherInClass => 100` is the correct way to say that a parser is never skipped. Overriding `SniffAtStart` alone is a violation unless `Sniff` never matches where `SniffAtStart` does not.
- **A trap, `DtsParser.StartsWithChain`:** it looks like the check of class 93 and is not. It caps the zero run at 1 MiB (`SniffLimit`), while `Sniff` does not (`ZeroRunEnd(cache, reader.Start, reader.End)`). With 1.5 MiB of zero bytes and then a DTS chain, `Sniff` returns class 93 and a `SniffAtStart` built on `StartsWithChain` returns None, which breaks rule 2 (`FurtherInClass` is 50) and lets the host skip DTS's search behind any winner above 50. `StartsWithChain` belongs to codec routing (`DtsCodecParser.Recognizes`); `SniffAtStart` shares `Sniff`'s own code, as the table says. The prototype's check of the DTS zero run is a mutant the contract test below catches.

What each parser declares, from the classes in the plan (as prototyped):

| Parser | `SniffAtStart` | `FurtherInClass` |
|---|---|---|
| MPEG audio | a credible chain at the start (after zero bytes): 90 | 90 (a chain anywhere in the first MiB is 90) |
| ADTS (`RawAacParser`) | the ADIF header, or a chain at the start (after zero bytes): 91 | 50 |
| AC-3 | a chain at the start (after zero bytes): 92 | 50 |
| DTS | a chain at the start (after zero bytes): 93 | 50 |
| FLAC | `fLaC`, or a chain of frames at the start: 100, 85, 80 | 50 |
| Ogg | a chain of pages from the start: 100 | 60 |
| WavPack | a header at the start that passes `TryHeader`: its sniff, 100, 90, 80 | 60 |
| Monkey's Audio | `MAC ` or `MACF` at the start: its sniff, 100, 80 | 60 |
| TrueHD | a major sync in the first access unit: its sniff, 94 | 94 (a stream whose units chain from the start to a later major sync is 94 too) |
| MP4, ASF, DTS-HD, TTA, Matroska, WAV, Wave64, AIFF, Musepack | the default: `Sniff` | 0 |

- **Code shared, not copied:** each `SniffAtStart` is the first branch of its parser's `Sniff` (the chain test at the start, with the search limit of one position), so a change to the sniff changes both; WavPack's, TrueHD's and Monkey's Audio's helpers take the position or limit as a parameter.
- **The first byte:** a check of the first byte comes before the 64 KiB block (`BlockCache`, `BlockReader`) of the four formats that skip zero bytes is built: a byte that is neither zero nor the first byte of the format's sync word ends the check (0xFF for MPEG audio and ADTS; 0x0B for AC-3; 0x7F, 0xFE, 0x1F, 0xFF, 0x64 and 0x58 for DTS). ADIF is tested before the byte, by its own signature ('A'), as in the sniff; Ogg's check looks at 'O' and Monkey's Audio's at 'M' before they start. In a first, unpinned run without the check, DTS's check cost as much as its whole sniff on the Ogg fixtures (3.9 ms over 120 files in pass 1).
- **A two-byte check:** 0xFF passes the first-byte filters of MPEG audio, ADTS and DTS on every MP3 file (an MPEG audio header starts with 0xFF), so ADTS and DTS still build a 64 KiB block there. Checking the second byte as well (MPEG audio: top three bits set; ADTS: `(b & 0xF6) == 0xF0`; DTS: one of the six sync word pairs; AC-3: 0x77) ends those checks before the block. FLAC's scanner already looks at the second byte of a 0xFF. The measurement below includes the variant.

## The host's rule

For each ranking (`Rank`), with the pass-1 results of the same call where reuse applies:

1. Every parser's `SniffAtStart`. The best of the matches, by the ranking of today (`SniffResult.CompareByStart`, then `Best`), is W.
2. For each parser whose `SniffAtStart` did not match and whose `FurtherInClass` is above 0, its `Sniff` runs, unless all of these hold:
   - W exists and its class is above the parser's `FurtherInClass`;
   - the parser is not the forced one (`--as`, `AnalysisOptions.ForcedFormat`);
   - in pass 2 only, where the report's alternatives are made: W starts at the start of the window, and its matched structure or its declared end, whichever is further, reaches the end of the window (`Math.Max(W.Extent, DeclaredEnd(W)) >= window end`).
3. In pass 2, if the best of all the results is not W, the parsers skipped in step 2 are sniffed after all.
4. **Reuse:** where pass 2 sees the same bytes as pass 1 (above) and no format is forced, it takes pass 1's results of `SniffAtStart` and of every `Sniff` that ran, and runs only the searches pass 1 skipped and pass 2 needs. W's declared end is reused only if W is the parser pass 1 asked for its declared end, the pass-1 winner; if a search outranked W in pass 1, pass 2 asks W's own.
5. The ranking, the alternatives, the ambiguity finding and the report are made from the results exactly as today.

Pass 1, `DetectFormat` and the forced pass's search for the container ask only which format wins, so they skip without the third condition of step 2.

**Reuse in `DetectFormat`:** its second ranking never sets `FirstEmbeddedTag` (it does not look for embedded tags), so the condition there is only that no trailing tag shortened the window. The ranking then equals pass 1's and is taken whole.

**Library rule:** the analyzer is a library that a front end uses on several threads at once (the GUI analyzes files in parallel on one `Analyzer`), and the core keeps no caches.

- The pass-1 results live in a local object that `FindContainer` returns and `Analyze` passes to `ParseData` as an argument; it is dropped when the call ends.
- Nothing is kept in `Analyzer` fields, and nothing across calls.
- A reuse between `DetectFormat` and a later `Analyze` of the same file would be a cache keyed by the file; it is not proposed, and the rule against caches below the front ends rules it out. A front end that calls both pays for both.

## Why the reports stay byte-identical

The report uses the ranking in four places; a skipped parser changes none of them, if rules 1 to 3 hold:

- **The winner:** a skipped parser's result has a class of at most its `FurtherInClass`, below W's (rule 2), so it ranks below W and below whatever beats W. `SniffResult.Best` lets only a candidate of the same class overtake, so it cannot either.
- **The alternatives** (pass 2): with the winner W (step 3 makes sure), a skipped candidate starts at or after the start of the window, where W starts, and before the end of the window (rule 3), which W's matched structure or declared end reaches. That is inside the bytes `Describe` gives the winner (`IsInside`, with the same declared end), so it would not be listed.
- **The ambiguity finding** (`format.ambiguous`) needs a candidate of the winner's class that ties on everything; a skipped one is of a lower class, and dropping it does not change which candidate comes second in the sorted list if one of the winner's class exists.
- **A forced format:** its own evidence comes from its own `Sniff`, which always runs; "detection would have chosen" is the winner, which does not change.
- **Reused results** are the results the sniffs would return again (same bytes, same context).
- **The order-independence guarantee** (`DetectionOrderTests`): W, the skips and the final ranking depend on the results, not on the order of the parser list; the list order still only breaks a tie in all four fields.

**What does change:** five differences, all of them only where a sniff or a `DeclaredEnd` has a bug (an exception other than `EndOfStreamException` and `InvalidDataException`), which `CLAUDE.md` calls a bug and never an acceptable result, or in the progress calls:

- **Which sniffs run:** the `parse.failed` finding of a sniff that throws a bug exception appears only if that sniff runs. (It is not a difference that a reused sniff reports once: `AddFailure` already keeps one finding per parser and activity, over the two passes.)
- **Which declared ends are asked:** `DeclaredEnd(W)` may now be called where `Describe` calls `DeclaredEnd(best)` only when more than one candidate matched (the third condition of step 2), and with reuse the pass-2 call may vanish. A buggy `DeclaredEnd` can so produce a `parse.failed` ("reading its declared end") that `main` does not, or fail to produce one that `main` does.
- **The order of failures:** step 1 runs every `SniffAtStart` before any search, so when two parsers fail, their `parse.failed` findings (all on the root, in the order the failures happened) may come in another order.
- **The `AnalysisOptions.BytesRead` progress:** skipped and reused sniffs read less, so the sequence of reported offsets differs (it stays increasing and ends with the file's length). It is observable API, though not part of the report.
- **`SniffAtStart` exceptions** (rule 4): one that `Sniff` would not throw is forbidden; one that both throw is one finding either way.

`DetectionPrecheckTests` fails when the reference report of an input holds a `parse.failed`: such an input is a bug to fix, and comparing reports across it would compare the differences above.

## Containers, raw streams, junk and tags

- **Containers matched at 95 or 100** (MP4, Ogg, ASF, Musepack SV7 and SV8, Matroska with a Segment, WAV, Wave64 and AIFF with a chunk, WavPack, Monkey's Audio, TTA, DTS-HD): W is found at the start and, in a whole file, declares an end at or past the end of the window, so both passes skip every search. A truncated container declares an end past the end of the data, which still covers it. (Musepack versions 4 to 6, class 95 with three units, is not a container: it declares no end, so pass 2 treats it as a raw stream.)
- **Containers matched at 90 or below** (a WAV, Wave64 or AIFF header whose chunk runs past the data, a Matroska without a Segment, a WavPack block the data cuts short): MPEG audio (`FurtherInClass` 90) and TrueHD (94) still run in both passes, since W is not above their class; the other seven searches are skipped, in pass 2 only where W covers the window.
- **A container with bytes after its declared end** (an appended stream, junk not taken by a tag): W does not cover the window, so pass 2 runs every search, as today, and finds the alternative after the container.
- **Raw streams** (MPEG audio, ADTS, AC-3, DTS, FLAC, TrueHD, Musepack versions 4 to 6): there is no declared end, and W's matched structure is its first 16 frames, so pass 2 runs the searches, which may find a second stream further in, the alternatives the plan describes. Pass 1 skips them. This is why a single raw stream gains little.
- **TrueHD at 94:** its search runs whenever W is 94 or below, which is every raw stream and every container matched below 95.
- **Junk before the audio:** nothing matches at the start, or only a stray match of a low class, so W is missing or ranks below the searches, and pass 1 runs everything, as today; pass 2 reuses it where the windows agree. The 352 and 1,000 bytes of `DetectionOrderTests` were part of the identity check.
- **Tags at either end:** the leading tags set where the data starts, which is where `SniffAtStart` looks; the trailing tags shorten the pass 2 window, which W's declared end then covers as before, and turn off reuse. A file of tags only has no data and is not sniffed.
- **"Analyze as":** the forced parser's `Sniff` always runs and nothing is reused; the others follow the rule, since the report names what detection would have chosen and lists the alternatives.

## False negatives: proving that no skipped parser would have mattered

A skipped parser that would have changed the report is a false negative. It can only come from a parser that breaks rule 1, 2, 3 or 4, since the host's rule is correct for any parser that keeps them (the argument above). The first round's tests (a corpus that added up to 4,096 zero bytes and 1 to 1,000 random bytes) could not back that: they never reached the limits of the searches (1 MiB for DTS, MPEG audio, ADTS, AC-3, FLAC, WavPack, Monkey's Audio and TrueHD, 64 KiB for Ogg, and the unbounded zero run), and `SniffAtStartTests` never set `FirstEmbeddedTag`, which WavPack's single-block class (`end == context.FirstEmbeddedTag`) and Musepack's old-stream sniff read. The tests below close that, and run on every pull request.

**The checking wrapper** (test code, one source file in `tests/Encspect.Tests`, linked into `tests/Encspect.Fuzz`): an `IFormatParser` that wraps a real parser and forwards `Name`, `DeclaredEnd`, `Parse` and `FurtherInClass`. Whenever the host calls its `SniffAtStart` or its `Sniff`, it calls both methods of the inner parser on that same context and checks:

- **Rule 1** (equal results where `SniffAtStart` matches), **rule 2** (`Sniff` at most `FurtherInClass` where it does not, or where it throws) and **rule 3** (a match starts on a byte of the window);
- **Rule 4**, exception parity: `SniffAtStart` throws a bug exception only where `Sniff` does;
- that the bound is reached: it counts, per parser, the contexts where `Sniff` returned exactly `FurtherInClass`, and the contract test fails if a searching parser's count is 0 over all its inputs (a bound that is never reached cannot be told from a constant that is too high).

On a violation it throws, so the host reports a `parse.failed` with the rule and the evidence, which every check that fails on `parse.failed` (the fuzzer, `DescriptionTests`, the finding tests) already catches. Because it sits inside an `Analyzer`, it sees the contexts the real host builds: both passes, `DetectFormat`, the forced parser's sniff and the contexts with `FirstEmbeddedTag`, not the contexts a test imagines.

**Three tests and a fuzz mode:**

- **`SniffAtStartTests`** (the contract): every input below through `Analyzer.Analyze` and `Analyzer.DetectFormat` with every parser wrapped, and through each parser directly on the data after the leading tags, with and without the trailing tags, with and without a `FirstEmbeddedTag`.
- **`DetectionPrecheckTests`** (the host): the same inputs through the analyzer as shipped (pre-check and reuse) and through a reference analyzer, and the JSON reports must be byte-identical. The reference hides the overrides, and so skips nothing: `new Analyzer(tags, formats.Select(p => new FullSniff(p)), codecs, decoders)`, where `FullSniff` forwards `Name`, `Sniff`, `DeclaredEnd` and `Parse` only, as `DetectionOrderTests` builds its analyzers. It also turns reuse off (below). On a sample of the inputs also with every format forced, through `DetectFormat`, and for the two halves apart (pre-check without reuse, reuse without pre-check), so a failure names the half. It fails when the reference report has a `parse.failed`.
- **The nightly run** of `DetectionPrecheckTests` (trait `Category=Nightly`) uses more seeds for the seeded copies; the pull request run uses the seeds below.
- **A fuzz mode of `tests/Encspect.Fuzz`:** the fuzzer fails on `parse.failed`, and with the pre-check most mutants of a container fixture never run the nine searching sniffs (W covers the window), so the sniffs with the most code would silently lose the coverage the fuzzer gave them. In the new mode (`--check-sniffs`) the fuzzer builds its analyzer with every parser wrapped in the checking wrapper, so each mutant runs every sniff in full and the contract check; CI alternates the modes by iteration (even: as shipped; odd: checked), so a replay by seed and iteration finds the same mode.

**Inputs** (made in memory from seeds; nothing new is committed):

- every fixture; each with 352 and 1,000 seeded random bytes in front; and seeded copies of each with 1 to 4,096 zero bytes in front, 1 to 16 random bytes in front, 1 to 3 bit flips in the first 16 bytes, 1 to 64 bytes cut from the start, 1 to 8 bit flips anywhere, a truncation, and a run of 4 to 64 bytes of 0x00 or 0xFF;
- **the limits:** zero runs of 1 MiB − 1, 1 MiB, 1 MiB + 1, 1.5 MiB and 2 MiB before two fixtures each of DTS, MPEG audio, ADTS and AC-3; random junk of 64 KiB − 1, 64 KiB and 64 KiB + 1 before two fixtures each of Ogg and of the others, and of 1 MiB − 1, 1 MiB and 1 MiB + 1 before two fixtures each of FLAC, WavPack, Monkey's Audio, TrueHD and the others; 1 MiB of 0xFF before them; 4 random bytes; a container fixture with 64 KiB, 1 MiB and 1 MiB + 1 bytes in front;
- **embedded tags:** every fixture with an ID3v2.4 tag in front whose SEEK frame points at a second tag, at a seeded offset inside the data and, for WavPack, at the end of each of the first blocks (so that `end == FirstEmbeddedTag`), and Musepack versions 4 to 6 the same;
- **trailing bytes:** an ID3v1 tag after the stream, 3,000 zero bytes after it, for each of the nine searching parsers;
- **made for the host's rule:** a container followed by another format's stream (a WAV file with AC-3, MPEG audio, DTS or FLAC frames appended, also after 5,000 zero bytes), so that pass 2 searches although W matched; and a RIFF header that declares a huge size with no chunk after it, followed by a stream (class 80, covering the window, against MPEG audio's 90), so that a search outranks W and step 3 runs.

**What the prototype showed:**

- **Identity:** the prototype (pre-check and reuse, also each half apart and with the two-byte check) was run with `--json`, `--csv`, `--only-format mpeg,flac,ogg` and `--as` for nine formats against `main`'s CLI on 15 sets: all 2,502 fixtures, the 300-second lossy and lossless benchmark files, the tagged folder, the 284 limit inputs, 3,317 inputs with embedded tags, and nine kinds of copy of the fixtures (about 22,500: 352 and 1,000 junk bytes, zero bytes, short junk, header bit flips, header cut, bit flips, truncation, a run of 0x00 or 0xFF). 270 comparisons, no difference.
- **The contract:** the checking wrapper ran in every one of them, on the build that was measured, and found no violation. It checked about 55,000 sniff contexts per searching parser. Each of the nine searching sniffs returned exactly its `FurtherInClass` somewhere (counts of contexts per parser, all sets: ADTS 852, AC-3 1,485, DTS 2,320, FLAC 1,309, MPEG audio 3,909, Monkey's Audio 918, Ogg 1,654, TrueHD 120, WavPack 1,965). Step 3 of the host's rule ran 8 times, all on the inputs made for it (2 of the limit inputs, 6 of the embedded-tag ones); on the first round's inputs it ran on none, which is why the tests need them.
- **The tests would have caught the trap and the hole.** Two mutants of the prototype, checked by the wrapper:
  - DTS's `SniffAtStart` with the zero run capped at 1 MiB, as `StartsWithChain` has it: 24 violations of rule 2, on exactly the limit inputs of 1 MiB + 1 byte of zeros and above (1 MiB − 1 and 1 MiB are fine), and none on the inputs of 1 MiB of zeros and fewer; the first round's corpus, which added up to 4,096 zero bytes, cannot see it;
  - WavPack's `SniffAtStart` ignoring `FirstEmbeddedTag`: no violation on 472 WavPack inputs whose embedded tag follows a file that declares its own end (the window then ends at the tag), and 138 violations of rule 1 on the inputs where a tag between the blocks is followed by another stream (so no format declares an end and the window keeps the tag). The embedded-tag inputs above are of that kind for this reason.

**Cost of the tests:** the checking run over all of the above (about 29,000 inputs, each through the wrapped parsers: every sniff of every parser twice) took 41 s in the NativeAOT CLI on one core, against 35 s for the plain analysis of the same inputs; the reference run is about as long again. Under `dotnet test` (JIT, ordinary parallelism, two analyzers per input and the report compared) that is an estimate of 1 to 3 minutes on a runner, not measured. If it is more, the pull request run takes a third of the seeded copies and the nightly run all of them. `DetectionOrderTests` stays as it is and keeps the list order out of the choice.

## Rollout

Each step is its own pull request with the measurements of the build that ships.

1. **Reuse (host only):** `FindContainer` returns the pass-1 results, `ParseData` takes them, and `Rank` reuses them where the bytes agree (the library rule above); `DetectionPrecheckTests` with the reference that reuses nothing, and the `InternalsVisibleTo` for the switch that turns reuse off. No parser and no contract changes, the reports stay the same, and the time gains what reuse alone gives. What shows that, and what does not:
   - **Not `scripts/check_aot.sh`:** it compares the NativeAOT and the JIT builds of the same tree, so it cannot see a change of behavior that both builds share.
   - **The goldens** (`*.report.json`, compared on every test run), and `DetectionPrecheckTests` (reuse against the reference).
   - **A corpus comparison against `main`'s CLI,** like the prototype's: both CLIs published as NativeAOT, `--json`, `--csv`, `--only-format` and `--as` over every fixture, the benchmark files, a tagged folder and the junk-prefixed, damaged and limit-sized copies of the fixtures, and the outputs compared by hash. It is a script the pull request adds under `scripts/`, run by hand for the pull request and not by CI.
   Steps 2 to 4 are only built if open decision 1 says so.
2. **The contract, the host's rule and the tests:** the two members with their defaults and rule 4's handling of exceptions, the skipping, the checking wrapper and `FullSniff`, `SniffAtStartTests`, the fuzz mode, and the nightly seeds. No parser overrides yet, so every sniff runs as in step 1. The same comparison as in step 1.
3. **The nine searching parsers** override both members: DTS, WavPack, FLAC, Monkey's Audio, MPEG audio, ADTS, TrueHD, AC-3 and Ogg, in one pull request or in two (the four that cost most first: DTS, WavPack, FLAC, Monkey's Audio). Each parser's `SniffAtStart` reuses the first branch of its own sniff, so the code is shared, not copied, and its `FurtherInClass` is the named constant its sniff uses. The same comparison and the contract test with the limit inputs.
4. **Documentation and rules:**
   - the plan's "Format detection" gets a paragraph on reuse (step 1) and, with step 2, on the pre-check and its four rules, and its first sentence ("the host picks the format from the evidence of all of them") says that the evidence of a parser the contract puts below the winner may be left out;
   - the plan's "Parser contract" bullet names the two members;
   - the comments of `IFormatParser` change: `Sniff`'s ("The host ranks the parsers' results") and `DeclaredEnd`'s (which says the host asks "the parser that sniffs best", now the best of the results it has) and the two new members';
   - `docs/development.md` gets the measured gains in "Analysis speed of the lossy formats";
   - `CLAUDE.md` gets one architecture rule naming the contract and `SniffAtStartTests` (open decision 9);
   - this document gets an "As built" section.

**Where the tests run (shard plan, `scripts/ci/check_shards.py`):** a class that no shard lists lands in `rest`, so `SniffAtStartTests` and `DetectionPrecheckTests` need no change to `shards.json` and the guard stays green. `rest` has the most room on the slowest runner: about 25 minutes (push) and 28 (nightly) on `macos-15-intel` after the rebalancing of 2026-10-25, against the limit of 60, where the other shards take 28 to 38. The estimate for the two classes is 1 to 3 minutes on every pull request (not measured; see the cost above), so `rest` stays under about 31. The nightly seeds carry the trait `Category=Nightly`, which `check_shards.py` expects of a test the pull request run leaves out; they add the same again at most. Measure the shard after the first pull request and move the classes to `corrupt-a` (28 / 31) if `rest` grows past the others. The fuzz job needs no shard: it alternates the two modes.

## Open decisions

1. **Reuse alone, or the pre-check with reuse?** Reuse alone is host-only, with no contract change, and byte-identical. Measured side by side in one batch (table above), it gains 29% on all fixtures against 38%, 26% on the small files against 34%, 31% on the folder of short MP3 and AAC files against 24%, and 25% on files with junk in front against 23%. The pre-check's own gain clears the bar of about 15% on the folders of containers (WavPack, TTA, AIFF, WAV, Monkey's Audio, Musepack) and on 300-second files of Ogg, Musepack and WAV, and does not on any mixed scan; on the folder of short MP3 and AAC files it loses 10 points, and on a tagged MP3 library it adds 10 points where reuse is off for 90 of 120 files. Recommendation: **reuse alone**, as step 1 and its own pull request, whose time it fully earns on the headline scans; build the pre-check only if lossless and WAV libraries or tagged MP3 libraries are a use case that matters, as step 2, and with all of the tests below.
2. **The names `SniffAtStart` and `FurtherInClass`:** they follow the plan's words ("a chain at the start of the data", "a chain further in"). Recommendation: keep them, if step 2 is built.
3. **TrueHD's 94 further in:** its sniff calls a stream whose access units chain from the start to a later major sync a stream from the start, which no bounded check decides, so its search runs whenever the winner is below 94, which keeps TrueHD's search running on every MP3, AC-3 and DTS file (classes 90, 92 and 93) and on every raw stream. Bounding that chain to a few units would let it drop to 60, but changes what the sniff accepts. Recommendation: keep 94; TrueHD's sniff was 7% of detection over all fixtures.
4. **The tests' switches:**
   - **Skipping:** a test-only wrapper `FullSniff` that forwards `Sniff`, `DeclaredEnd`, `Parse` and `Name` and so hides the overrides: `new Analyzer(tags, formats.Select(p => new FullSniff(p)), codecs, decoders)`, as `DetectionOrderTests` builds its analyzers. No switch in the analyzer.
   - **Reuse:** the only half that needs a switch: an internal constructor parameter of `Analyzer`, not an `AnalysisOptions` member, so that front ends cannot turn it on and the public API does not change. It needs `InternalsVisibleTo Encspect.Tests` in `Encspect.Host.csproj`, which has none today (`Encspect.Cli.csproj` has one for the same project).
   - **The reference mode turns reuse off too:** it is the analyzer with `FullSniff` wrappers and reuse off, so a bug in reuse shows. Recommendation: this.
5. **The cheaper sniffs on their own:** the DTS sniff alone is 27% of detection on all fixtures (368 of 1,378 ms), and DTS, AC-3 and ADTS allocate a 64 KiB block for every sniff, WavPack two. Making them cheaper is independent of reuse and of the pre-check and helps the raw streams, which neither helps much. Recommendation: measure it after reuse, and build it only if it clears the bar on its own.
6. **What an exception from `SniffAtStart` means (rule 4):** any exception counts as None, a bug exception is reported under the activity "sniffing the data", rule 2 also holds when it throws, and the contract test checks parity (a bug exception from `SniffAtStart` only where `Sniff` throws one). Recommendation: this; the alternative of letting an exception from `SniffAtStart` fall through to `Sniff` would make the pre-check run every sniff of a broken parser anyway and hide the bug.
7. **Fuzz coverage:** the checking wrapper as a mode of `tests/Encspect.Fuzz`, alternating with the shipped analyzer by iteration, and a nightly `DetectionPrecheckTests` with more seeds. Recommendation: this; otherwise the fuzzer stops reaching the nine searches on most mutants of container fixtures.
8. **What the tests do with a `parse.failed` in the reference run:** fail, naming the input. Such a report is a bug already, and comparing across it would compare the bug-only differences of "What does change". The alternative, skipping the input, hides the bug from this test (other tests still see it). Recommendation: fail.
9. **A `CLAUDE.md` rule,** next to the ones that name `DescriptionTests` and `DecoderWorkTests`. Proposed text: "A format parser that searches further in than the start of the data declares `SniffAtStart` and `FurtherInClass` as one named constant its sniff uses (the rules are in the plan's "Format detection"); `SniffAtStartTests` wraps every parser in the checking wrapper and fails on a break of them, on every fixture and on the limit of every search. A change to a sniff's limit or class needs an input at the new limit." Recommendation: add it with step 2; with reuse alone there is nothing to name.
10. **The two-byte check** (after the first byte, MPEG audio, ADTS, AC-3 and DTS look at the second): a few lines in four parsers; gain 7 points on `mpeg` and `layer3`, nothing measurable elsewhere. Recommendation: include it if step 2 is built; skip it otherwise.
