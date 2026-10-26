# Detection pre-check: the start of the data before the searches

Status: draft for the project lead, not approved; nothing of it is built. The measurements were made on `main` at b656c90 with a scratch prototype that is not committed. Estimates are marked as estimates.

## Summary

- **The problem:** format detection, every format parser's sniff over every file and twice per file, is 35 to 73% of the time of a NativeAOT scan of a fixture folder and 57% of a scan of all fixtures. Almost all of it is in the parsers that search the first 64 KiB or 1 MiB for a chain further in (DTS, WavPack, FLAC, Monkey's Audio, MPEG audio, ADTS, TrueHD, AC-3, Ogg), and they search files that are plainly another format's.
- **The change:** each searching parser also says what it finds at the start of the data without a search, and the highest class its search can give. The host first asks every parser about the start of the data and runs a search only where its result could still change the report; the second pass reuses the first pass's results where both passes see the same bytes.
- **The gain, measured with a prototype** (NativeAOT, minimum of 5 runs): a scan of all 2,502 fixtures takes 46% less time, the 2,025 fixtures under 32 KiB 36%, the folder of 300 short MP3 and AAC files 19% (22% in a second batch), the fixtures with junk in front 32%; the fixture folders of the containers 33 to 60%, of the raw streams 6 to 36%. One 300-second file gains 21 to 67% for Ogg, Musepack and WAV, 11 to 16% for MP4, WavPack, Monkey's Audio and TTA, and nothing beyond the noise of about 10% for a raw stream (MP3, ADTS, AC-3, DTS, FLAC, TrueHD).
- **The guarantee:** reports stay byte-identical. The prototype's JSON and CSV reports, `--only-format` and `--as` were identical to `main` on every fixture, the benchmark files and 22,515 junk-prefixed and damaged copies of the fixtures, and a check of the new contract on every sniff of those inputs found no violation.
- **What it costs:** two members on `IFormatParser` whose defaults keep any parser exactly as it is, a check of the start of the data in nine parsers, and a host rule of about 100 lines (the prototype's). The 145 files of the tags folder, whose data mostly no parser recognizes, gain nothing (+1%).

## What was measured

Setup: Linux x64, Xeon at 2.8 GHz, 4 vCPUs shared with another session; every timed run under the machine lock, on one pinned core, after the load average fell below 1.5, with the variants interleaved; minimum of 5 runs of `encspect --csv`, seconds. "NativeAOT" is `dotnet publish src/Encspect.Cli -c Release -r linux-x64 -p:PublishAot=true` (x86-64-v2, what ships), "JIT" is `dotnet build -c Release` run through `dotnet`.

Inputs:

- the fixture folders, audio files only (no `.json`, `.md` or `.txt`), each as one command line; "all fixtures" is the 2,502 of them, "fixtures under 32 KiB" the 2,025 below that size, of every format; "1,000 junk bytes in front" is each fixture with 1,000 seeded random bytes before it;
- the folder of 300 files of 3 s (120 MP3, 90 ADTS and 90 MP4 files of AAC, and an empty marker file), the 300-second files of "Analysis speed of the lossy formats" in `docs/development.md`, and the 300-second lossless files of "Measured: ReadyToRun against NativeAOT".

### Where the time goes

An instrumented scratch build timed the two passes of detection (every parser's sniff and the declared end of the best), the tags, and the parse. "Detection" below is its share of the whole run, start of the process included (one run, the fastest of 3):

- **Folders:** all fixtures 57%; under 32 KiB 49%; the 300 short MP3 and AAC files 40%; fixture folders from 22% (`stats`, 9 files) and 34 to 36% (`aac`, `adts`, `alac`, `apple`, `truehd`) to 72 and 73% (`wavpack`, `monkeys-audio`).
- **300-second files:** MP3 3 to 16%, AAC 7 to 11%, AC-3 and E-AC-3 5 to 6%, WMA 14%, Vorbis, Opus and Speex 30 to 37%, Musepack 55%; FLAC 3 to 7%, TrueHD 1 to 2%, WavPack 14 to 26%, Monkey's Audio 6 to 19%, TTA 12%, WAV 64 to 67%.
- **By parser, all fixtures:** of 1,378 ms of detection (both passes, sniffs and declared ends), the DTS sniff takes 368 ms, WavPack 158, FLAC 146, Monkey's Audio 124, MPEG audio 119, ADTS 96, TrueHD 95, AC-3 90, Ogg 75; Musepack, which looks only at the start of the data, 40 (its check for versions 4 to 6); the other parsers that look only at the start (MP4, ASF, DTS-HD, TTA, Matroska, WAV, Wave64, AIFF) 2 to 7 ms each. Pass 1 takes 729 ms, pass 2 649.
- **Why a file of another format costs so much:** a searching sniff scans up to 64 KiB (Ogg) or 1 MiB (the others) of a file that is not its format. DTS, AC-3 and ADTS read it through a 64 KiB block (`BlockCache`), allocated and filled for each sniff, WavPack through two, and FLAC, Monkey's Audio and TrueHD through a window that holds a file of up to 128 KiB whole (`SourceWindow`). On a 300-second WAV file the searches meet sync-like bytes all through the PCM samples (DTS alone 10 ms of a 50 ms run).

### Upper bounds and the prototype

| Case | Files | NativeAOT (s) | JIT (s) | Detection | Sniff once | Winner only, once | Pre-check | Pre-check and reuse |
|---|---|---|---|---|---|---|---|---|
| all fixtures | 2502 | 2.327 | 5.64 | 57% | -27% | -59% | -42% | -46% |
| fixtures under 32 KiB | 2025 | 0.954 | 4.21 | 49% | -16% | -45% | -26% | -36% |
| 300 short MP3 and AAC files | 301 | 0.621 | 2.31 | 40% | -24% | -41% | -16% | -19% |
| all fixtures, 1,000 junk bytes in front | 2502 | 1.949 | - | - | -29% | -27% | -9% | -32% |
| `mpeg` | 164 | 0.176 | 1.03 | 52% | -13% | -44% | +3% | -23% |
| `layer3` | 37 | 0.047 | 0.57 | 46% | -8% | -44% | -19% | -30% |
| `adts` | 57 | 0.050 | 0.54 | 35% | -10% | -11% | -12% | -10% |
| `ac3` | 78 | 0.065 | 0.57 | 44% | -10% | -22% | -11% | -21% |
| `dts` | 165 | 0.097 | 0.67 | 53% | -21% | -41% | -26% | -36% |
| `flac` | 114 | 0.139 | 0.78 | 56% | -23% | -43% | -17% | -28% |
| `truehd` | 65 | 0.101 | 0.63 | 36% | -15% | -31% | -10% | -6% |
| `mp4` | 187 | 0.141 | 1.14 | 42% | -18% | -36% | -33% | -37% |
| `ogg` | 120 | 0.083 | 0.79 | 43% | -10% | -28% | -31% | -33% |
| `musepack` | 99 | 0.119 | 0.68 | 55% | -23% | -57% | -36% | -49% |
| `asf` | 152 | 0.221 | 1.02 | 37% | -17% | -40% | -34% | -36% |
| `matroska` | 189 | 0.123 | 1.24 | 43% | -18% | -33% | -35% | -37% |
| `wav` | 242 | 0.131 | 0.93 | 56% | -29% | -55% | -48% | -50% |
| `aiff` | 119 | 0.057 | 0.39 | 60% | -26% | -51% | -42% | -52% |
| `wavpack` | 159 | 0.186 | 0.84 | 72% | -38% | -68% | -55% | -60% |
| `monkeys-audio` | 113 | 0.251 | 0.81 | 73% | -23% | -60% | -41% | -54% |
| `tta` | 67 | 0.134 | 0.75 | 71% | -35% | -71% | -60% | -57% |
| `tags` | 145 | 0.066 | 0.62 | 51% | -14% | -47% | +5% | +1% |
| `fuzz` | 57 | 0.071 | 1.21 | 39% | -11% | -23% | -15% | -13% |
| MP3 CBR 128 | 1 | 0.089 | 0.54 | 16% | -10% | -16% | -8% | -4% |
| MP3 CBR 128, 60 min | 1 | 0.747 | 1.84 | 3% | -2% | -1% | -3% | -12% |
| AAC LC, ADTS | 1 | 0.164 | 0.75 | 11% | -11% | -13% | +7% | +2% |
| AAC LC, MP4 | 1 | 0.159 | 0.81 | 11% | -5% | -7% | -8% | -13% |
| AC-3 | 1 | 0.250 | 0.73 | 5% | -1% | -9% | +2% | -2% |
| WMA v2 | 1 | 0.149 | 0.81 | 14% | -6% | -7% | -2% | +14% |
| Vorbis | 1 | 0.079 | 0.43 | 37% | -24% | -35% | -32% | -43% |
| Opus | 1 | 0.073 | 0.40 | 32% | -20% | -22% | -29% | -25% |
| Speex | 1 | 0.073 | 0.40 | 30% | -3% | -19% | -23% | -21% |
| Musepack SV8 | 1 | 0.039 | 0.34 | 55% | -30% | -50% | -47% | -50% |
| FLAC -5 | 1 | 0.226 | 0.55 | 7% | -5% | -6% | +0% | -4% |
| WavPack default | 1 | 0.082 | 0.38 | 26% | -7% | -17% | -12% | -16% |
| Monkey's Audio `-c3000` | 1 | 0.113 | 0.41 | 19% | -9% | -13% | -5% | -11% |
| TTA | 1 | 0.130 | 0.46 | 12% | +3% | -9% | -13% | -13% |
| WAV, 16-bit | 1 | 0.048 | 0.30 | 67% | -34% | -60% | -66% | -67% |
| TrueHD, 16-bit | 1 | 0.855 | 1.48 | 2% | -3% | +5% | +6% | +2% |

- **The columns:**
  - "Sniff once": host-only; the second pass reuses the first pass's ranking, and the declared end it already asked for, where both see the same bytes (no trailing or embedded tag). Its reports were byte-identical to `main` everywhere.
  - "Winner only, once": an upper bound for any pre-check: only the parser that wins (read from a table made by `main`) is sniffed, and once where the passes see the same bytes. Its reports lose their alternatives; it shows what is left to gain.
  - "Pre-check": the design below without step 4 (reuse); "Pre-check and reuse": the design below.
- **Three batches:** "NativeAOT", "Pre-check" and "Pre-check and reuse" were measured together; "Sniff once" and "Winner only, once" in a batch of their own against `main` again, and "JIT" in a third. Each percentage is against the `main` of its own batch. The machine was shared and busier during some batches (`main` took 2.04 to 2.33 s on all fixtures across them), and a single 300-second file varies by about 10 to 15% from batch to batch: its rows, the WMA and TrueHD ones above all, are within the noise unless the change is large. The folders vary less.
- **JIT, for reference:** the JIT build takes 1.5 to 17 times as long as NativeAOT on these runs, most of it start-up and compilation; the prototype was not measured in it.

### Reuse alone against the pre-check with reuse

The two were also measured in one batch of their own (7 runs each, minimum, against `main` in the same batch), since the table's columns come from different batches:

| Case | Reuse alone | Pre-check and reuse |
|---|---|---|
| all fixtures | -28% | -47% |
| fixtures under 32 KiB | -10% | -33% |
| 300 short MP3 and AAC files | -21% | -22% |
| all fixtures, 1,000 junk bytes in front | -26% | -26% |
| `mpeg` | -22% | -17% |
| `flac` | -29% | -24% |
| `layer3` | -3% | -23% |
| `adts` | -14% | -15% |
| `ogg` | -21% | -33% |
| MP3 CBR 128, 300 s | -10% | -6% |
| Vorbis, 300 s | -20% | -33% |

- **Where the pre-check adds:** the containers and the mixed scans. Where every file is a raw stream without tags (`mpeg`, `flac`, one MP3 file), both run each search once, and the checks of the start of the data cost a few points more than they save.

### What the bar says

The pre-check with reuse clears the project's bar of about 15% on the scans that detection dominates: all fixtures (-46%), the small files (-36%), the folder of 300 short MP3 and AAC files (-19%), files with junk in front (-32%), every container's fixture folder (-33 to -60%), and the raw streams' `mpeg`, `layer3`, `ac3`, `dts` and `flac` folders (-21 to -36%). It clears it on one 300-second file of Ogg, Musepack, WAV and WavPack (-16 to -67%). It does not on `adts`, `truehd`, `fuzz`, `tags` and some small folders not in the table (`aac` -8%, `layer2` -14%, `stats` -11%, `vbr` -12%; `alac` -22% and `apple` -16% do), nor on one raw stream, whose second pass keeps the searches the alternatives need.

## Why every file is sniffed twice

- **Pass 1** (`Analyzer.FindContainer`): after the leading tags, every parser sniffs the data up to the end of the file, to find the best format and ask it for its declared end (`IFormatParser.DeclaredEnd`). That end must be known before the trailing tags are looked for, since a trailing candidate that starts before it is the container's own chunk (plan, "Tag detection and false positives", rule 3).
- **Pass 2** (`Analyzer.ParseData`): every parser sniffs again, on the window without the trailing tags and with `SniffContext.FirstEmbeddedTag`. Its ranking picks the parser that runs and the alternatives the report lists; `Describe` asks the best for its declared end again when there are other candidates.
- **The two can differ:** a sniff reads to the end of its window. WavPack's class 100 for a single block needs the block to end at the end of the data, Musepack's version 4 to 6 frames must end there, MPEG audio, ADTS, AC-3 and DTS count frames up to it, and Musepack and WavPack stop at the first embedded tag. Pass 1 sees a trailing ID3v1 or APE tag as data; pass 2 does not.
- **`--only-format`** adds `Analyzer.DetectFormat`, which runs both passes before the analysis: four sniffs of every parser for a file that is analyzed, two for one that is skipped.
- **Is sniffing once safe?** Only where both passes see the same bytes: no trailing tag and no embedded tag, so that the window, the extension hint and `FirstEmbeddedTag` are equal; a sniff reads nothing else, so it returns the same result. The prototypes reuse exactly there and were byte-identical on all inputs. Where a trailing tag shortens the window (most tagged MP3 files), both passes stay.

## The contract change

Two members on `IFormatParser`, both with a default:

```csharp
// What Sniff finds at the start of the data, decided without a search: Sniff's own result, or None
// when that result is not decided there. A format matched only at the start of the data keeps the
// default.
SniffResult SniffAtStart(SniffContext context) => FurtherInClass == 0 ? Sniff(context) : SniffResult.None;

// The highest class Sniff returns when SniffAtStart returns None: 0 for a format matched only at the
// start of the data.
int FurtherInClass => 0;
```

- **Rule 1:** where `SniffAtStart` matches, `Sniff` returns the same result, every field (class, offset, end, frames, verified frames, note).
- **Rule 2:** where `SniffAtStart` returns None, `Sniff` returns at most `FurtherInClass`.
- **Rule 3:** a match starts on a byte of the window (`Offset` at least its start and below its end), which every sniff does today and the host's argument below relies on.
- **Cost:** `SniffAtStart` reads a bounded prefix: the first bytes, the header and chain at the start. Like the existing sniffs, it skips a run of zero bytes first where the format does (MPEG audio, ADTS, AC-3, DTS).
- **The defaults are always correct:** a parser that overrides neither is sniffed in full every time, as now, and nothing is skipped because of it. A new parser starts that way, and a searching parser gains only when it overrides both.
- **What each parser declares, from the classes in the plan** (as prototyped):

| Parser | `SniffAtStart` | `FurtherInClass` |
|---|---|---|
| MPEG audio | a credible chain at the start (after zero bytes): 90 | 90 (a chain anywhere in the first MiB is 90) |
| ADTS (`RawAacParser`) | the ADIF header, or a chain at the start: 91 | 50 |
| AC-3 | a chain at the start: 92 | 50 |
| DTS | a chain at the start: 93 | 50 |
| FLAC | `fLaC`, or a chain of frames at the start: 100, 85, 80 | 50 |
| Ogg | a chain of pages from the start: 100 | 60 |
| WavPack | `wvpk` at the start: its sniff, 100, 90, 80 | 60 |
| Monkey's Audio | `MAC ` or `MACF` at the start: its sniff, 100, 80 | 60 |
| TrueHD | a major sync in the first access unit: its sniff, 94 | 94 (a stream whose units chain from the start to a later major sync is 94 too) |
| MP4, ASF, DTS-HD, TTA, Matroska, WAV, Wave64, AIFF, Musepack | the default: `Sniff` | 0 |

- **The first byte:** the prototype's checks for MPEG audio, ADTS, AC-3 and DTS look at the first byte before they build their 64 KiB block: a byte that is neither zero nor a first byte of the format's sync word (0xFF; 0x0B; 0x7F, 0xFE, 0x1F, 0xFF, 0x64, 0x58) ends the check. In a first, unpinned run without it, DTS's check cost as much as its whole sniff on the Ogg fixtures (3.9 ms over 120 files in pass 1).

## The host's rule

For each pass (`Rank`):

1. Every parser's `SniffAtStart`. The best of the matches, by the ranking of today (`SniffResult.CompareByStart`, then `Best`), is W.
2. For each parser whose `SniffAtStart` did not match and whose `FurtherInClass` is above 0, its `Sniff` runs, unless all of these hold:
   - W exists and its class is above the parser's `FurtherInClass`;
   - the parser is not the forced one (`--as`, `AnalysisOptions.ForcedFormat`);
   - in pass 2 only, where the report's alternatives are made: W starts at the start of the window, and its matched structure or its declared end, whichever is further, reaches the end of the window (`Math.Max(W.Extent, DeclaredEnd(W)) >= window end`).
3. In pass 2, if the best of all the results is not W, the parsers skipped in step 2 are sniffed after all.
4. **Reuse:** where pass 2 sees the same bytes as pass 1 (above) and no format is forced, it takes pass 1's results of `SniffAtStart`, of every `Sniff` that ran, and of W's declared end, and runs only the searches pass 1 skipped and pass 2 needs.
5. The ranking, the alternatives, the ambiguity finding and the report are made from the results exactly as today.

Pass 1, `DetectFormat` and the forced pass's search for the container ask only which format wins, so they skip without the third condition of step 2.

## Why the reports stay byte-identical

The report uses the ranking in four places; a skipped parser changes none of them:

- **The winner:** a skipped parser's result has a class of at most its `FurtherInClass`, below W's (rule 2), so it ranks below W and below whatever beats W. `SniffResult.Best` lets only a candidate of the same class overtake, so it cannot either.
- **The alternatives** (pass 2): with the winner W (step 3 makes sure), a skipped candidate starts at or after the start of the window, where W starts, and before the end of the window (rule 3), which W's matched structure or declared end reaches. That is inside the bytes `Describe` gives the winner (`IsInside`, with the same declared end), so it would not be listed.
- **The ambiguity finding** (`format.ambiguous`) needs a candidate of the winner's class that ties on everything; a skipped one is of a lower class, and dropping it does not change which candidate comes second in the sorted list if one of the winner's class exists.
- **A forced format:** its own evidence comes from its own `Sniff`, which always runs; "detection would have chosen" is the winner, which does not change.
- **Reused results** are the results the sniffs would return again (same bytes, same context).
- **What does change:** the `parse.failed` finding of a sniff that throws a bug exception (anything but `EndOfStreamException` and `InvalidDataException`) appears only if that sniff runs, and once where results are reused. Such a finding is a bug, never an accepted result (`CLAUDE.md`), and the tests below run every sniff in full.
- **The order-independence guarantee** (`DetectionOrderTests`): W, the skips and the final ranking depend on the results, not on the order of the parser list; the list order still only breaks a tie in all four fields.

## Containers, raw streams, junk and tags

- **Containers with a declared end** (MP4, Ogg, ASF, Musepack SV7 and SV8, Matroska, WAV, Wave64, AIFF, WavPack, Monkey's Audio, TTA, DTS-HD): W is found at the start and, in a whole file, declares an end at or past the end of the window, so both passes skip every search. A truncated container declares an end past the end of the data, which still covers it.
- **A container with bytes after its declared end** (an appended stream, junk not taken by a tag): W does not cover the window, so pass 2 runs every search, as today, and finds the alternative after the container.
- **Raw streams** (MPEG audio, ADTS, AC-3, DTS, FLAC, TrueHD): there is no declared end, and W's matched structure is its first 16 frames, so pass 2 runs the searches, which may find a second stream further in, the alternatives the plan describes. Pass 1 skips them. This is why a single raw stream gains little.
- **Junk before the audio:** nothing matches at the start, or only a stray match of a low class, so W is missing or ranks below the searches, and pass 1 runs everything, as today; pass 2 reuses it where the windows agree. The 352 and 1,000 bytes of `DetectionOrderTests` were part of the identity check.
- **Tags at either end:** the leading tags set where the data starts, which is where `SniffAtStart` looks; the trailing tags shorten the pass 2 window, which W's declared end then covers as before, and turn off the reuse. A file of tags only has no data and is not sniffed.
- **"Analyze as":** the forced parser's `Sniff` always runs and nothing is reused; the others follow the rule, since the report names what detection would have chosen and lists the alternatives.

## False negatives: proving that no skipped parser would have mattered

A skipped parser that would have changed the report is a false negative. It can only come from a parser that breaks rule 1, 2 or 3, since the host's rule is correct for any parser that keeps them (the argument above). Two tests, run on every pull request:

- **`SniffAtStartTests`** (the contract): for every format parser and every input below, `SniffAtStart` and `Sniff` are both called, and the test fails where a match of `SniffAtStart` differs from `Sniff` in any field, where `Sniff` returns more than `FurtherInClass` after `SniffAtStart` returned None, or where a match starts outside the window. Inputs: every fixture; each with 352 and 1,000 seeded random bytes in front; and seeded copies of each with 1 to 4,096 zero bytes in front, 1 to 16 random bytes in front, 1 to 3 bit flips in the first 16 bytes, 1 to 64 bytes cut from the start, 1 to 8 bit flips anywhere, a truncation, and a run of 4 to 64 bytes of 0x00 or 0xFF. Both passes' windows are covered by sniffing the data after the leading tags with and without the trailing tags.
- **`DetectionPrecheckTests`** (the host): the same inputs through `Analyzer.Analyze` with the pre-check and with every sniff run in full (an internal switch of the analyzer, not public API), and the JSON reports must be byte-identical; on a sample of them also with every format forced, and through `DetectFormat`.
- **Inputs made for the rule:** a container followed by another format's stream (a WAV file with AC-3 frames appended), so that pass 2 searches although W matched; and a container header whose chunk is cut short before another stream (a WAV header followed by an MPEG audio stream, class 80 against 90), so that a search outranks W and step 3 runs. They are built in the test from fixtures, not committed.
- **The prototype passed both:** the contract check found no violation over every fixture, the benchmark files and the 22,515 copies above (9 kinds of copy of the 2,502 fixtures; the one empty fixture has no copy of the three kinds that change a byte). The JSON and CSV reports, `--only-format mpeg,flac,ogg`, and `--as` for MPEG audio, Ogg, WAV, FLAC and DTS, were identical to `main`'s on all of them, for the pre-check with and without reuse. Step 3 ran on none of them, which is why the tests need the inputs made for it.
- **Cost of the tests:** an estimate, not measured: about 25,000 inputs through every sniff twice, and the same through two analyses, in the order of one to three minutes on a runner; the copies are made in memory from seeds, nothing new is committed. `DetectionOrderTests` stays as it is and keeps the list order out of the choice.

## Rollout

Each step is its own pull request with the measurements of the build that ships.

1. **Contract and host:** the two members with their defaults, the host's rule, the reuse and the switch, and the two tests. No parser overrides yet, so every sniff runs as today, apart from the reuse: the reports stay the same (checked by the goldens and the corpus comparison of `scripts/check_aot.sh`), and the time gains what reuse alone gives ("Sniff once" above).
2. **The nine searching parsers** override both members: DTS, WavPack, FLAC, Monkey's Audio, MPEG audio, ADTS, TrueHD, AC-3 and Ogg, in one pull request or in two (the four that cost most first: DTS, WavPack, FLAC, Monkey's Audio). Each parser's `SniffAtStart` reuses the first branch of its own sniff, so the code is shared, not copied.
3. **Documentation:** the plan's "Format detection" gets a paragraph on the pre-check and its three rules; `docs/development.md` gets the measured gains in "Analysis speed of the lossy formats"; this document gets an "As built" section.

## Open decisions

1. **Build the pre-check, or only reuse the first pass?** Reuse alone is host-only, with no contract change, and byte-identical. Measured side by side, it gains 28% on all fixtures against 47%, 10% on the small files against 33%, and as much on the folder of short MP3 and AAC files and on files with junk in front; it is about 5 points ahead where every file is a raw stream without tags. Recommendation: the pre-check with reuse, which gains more on the mixed scans that detection dominates, with a contract change of two members whose defaults are safe.
2. **The names `SniffAtStart` and `FurtherInClass`:** they follow the plan's words ("a chain at the start of the data", "a chain further in"). Recommendation: keep them.
3. **TrueHD's 94 further in:** its sniff calls a stream whose access units chain from the start to a later major sync a stream from the start, which no bounded check decides, so its search runs whenever the winner is below 94 (every raw stream). Bounding that chain to a few units would let it drop to 60, but changes what the sniff accepts. Recommendation: keep 94; TrueHD's sniff was 7% of detection over all fixtures.
4. **The tests' switch:** the analyzer runs every sniff in full when an internal switch is set, which only tests use. Recommendation: an internal constructor parameter of `Analyzer`, not an `AnalysisOptions` member, so that front ends cannot turn it on and the public API does not change.
5. **The cheaper sniffs on their own:** the DTS sniff alone is 27% of detection on all fixtures (368 of 1,378 ms), and DTS, AC-3 and ADTS allocate a 64 KiB block for every sniff, WavPack two. Making them cheaper is independent of the pre-check and helps the raw streams, which the pre-check helps least. Recommendation: measure it after step 2, where it is the largest item left, and build it only if it clears the bar on its own.
