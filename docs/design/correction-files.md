# Correction files

Status: draft for the lead, 2026-11-18. Milestone 0.4, item 3 of the [plan](../plan.md#04-free-decoders-and-routes-complete), "The correction file": the `.wvc` of a WavPack hybrid `.wv` and the `.ofc` of an OptimFROG DualStream `.ofs`, found beside the input by the host, checked against their main file, and reported in the same words for both formats. Claims about the code were checked at d7781f99f (main) and, for WavPack 1 to 3, on the branches `feat/wavpack-legacy-parser` and `feat/wavpack-legacy-decoder`; libwavpack is 5.6.0 as `wavpack_tool.py` pins it. The decisions are collected under [Decisions for the lead](#decisions-for-the-lead).

## Scope

- **In:**
  - host access to a companion file, opened by the host and never by a parser, from the file system beside the input or from a caller that has none;
  - the companion's own tree, findings and stream in the main file's report;
  - checking the pair against each other (WavPack 4 and 5, WavPack 3 and OptimFROG), and decoding a WavPack pair with its correction applied;
  - the stream's pairing status, verification and lossless wording in each case, the findings, the front ends, the fixtures and the cross-checks.
- **Out:** the coding class on the coding line (item 6, which reads the pairing status this item adds); WavPack correction data in Matroska BlockAdditions ([Not covered](#not-covered)); decoding OptimFROG, which has no public description.

## What exists today

- **WavPack 4 and 5** ([wavpack.md](../formats/wavpack.md#hybrid-mode-and-correction-files)):
  - a hybrid `.wv` is "WavPack hybrid"; a `.wvc` opened alone is "WavPack correction data" with `wavpack.correction-file` (Info), and its blocks, block checksums and sub-blocks are read as a file's;
  - `IsCorrectionFileCreated` is true where the configuration block says the encoder wrote a correction file (`hybrid-b4-correction.wv`: true; `hybrid-b4-md5.wv`, written without `-c`: false);
  - the decoder decodes a hybrid file without its `.wvc` to the lossy audio (`DecodeSetup.IsLossy`, so the verification says "lossy stream"), compares the block header CRCs of that audio, and does not compare the MD5 of the lossless audio (`wavpack.md5-needs-correction-file`). A correction bitstream sub-block is read and checked by `BlockContents`, never used. A `.wvc` alone is `NotPossible` to decode;
  - `crosscheck_wavpack_fixtures.py` already copies each pair to `x.wv` and `x.wvc` in a work folder and holds `wvunpack -v` to "lossless" with "(+.wvc)" (`check_pair`).
- **WavPack 3** (on the legacy branches, [wavpack-legacy.md](wavpack-legacy.md)):
  - a 3.4 hybrid file written with `-c` equals the file written without it byte for byte, so nothing in the main file says a `.wvc` exists;
  - the `.wvc` is the second bitstream alone, with no header or signature, so it is not recognized alone;
  - the decoder decodes the lossy audio and compares `crc`; `crc2`, the check of the audio with the correction, is left with `wavpack.legacy-crc2-needs-correction-file` (Info). The correction paths of `unpack3.c` (`WVC_FLAG`) are not built.
- **OptimFROG** ([optimfrog.md](../formats/optimfrog.md), [tak-optimfrog-headers.md](tak-optimfrog-headers.md#optimfrog-dualstream)):
  - an `.ofs` is "OptimFROG" with a lossy-part claim (low from the data blocks' reserved bytes, medium with the `.ofs` extension); `IsLossy` stays false, and the claim waits for this item to be confirmed;
  - an `.ofs` is the same file with and without `--correction`;
  - an `.ofc` is a run of `CORR` blocks, one for each data block of its `.ofs`, each starting with the IEEE CRC-32 of the rest of its data; nothing else of its layout is known. Alone it is "OptimFROG correction data" with `optimfrog.correction-file` (Info).
- **The host** reads one file per report: `Analyzer.AnalyzeFile(path)` opens it, `Analyze(source, stream)` takes a stream from a caller, and nothing below the front ends knows a folder. The command line never reads standard input ([cli.md](../cli.md#inputs)).

## The host API

### How a companion is found

- **The parser asks, the host opens.** A format parser that reads a stream which may have a correction file asks its context for it by the companion's extension:
  - `ParseContext.OpenCompanion(string extension)` returns a `Companion` (in `Core.Abstractions`): a status (`Found`, `Missing`, `Unchecked`), the companion's source name, a reason where it was not found or not looked for, and, where found, a `ParseContext` of its own (reader and root) through which the parser builds the companion's nodes, findings and stream;
  - the WavPack parser asks for `wvc` when its stream has a hybrid block (version 4 and 5) or a header with `bits` and the new high flag (version 3), as libwavpack does; the OptimFROG parser asks for `ofc` when it asserts the lossy-part claim, at any confidence;
  - a parser asks once per stream; a codec route (WavPack in Matroska) gets `Unchecked`, since a container's track has no file beside it.
- **The name:** the input's name without its last extension, a dot and the companion's extension, in the folder of the input: `x.wv` to `x.wvc`, `x.ofs` to `x.ofc`, and a hybrid file named `x.bin` to `x.wvc`.
  - The extension is written in the case of the input's (`X.WV` to `X.WVC`), lower case otherwise; one name is tried, so on a case-sensitive file system `x.WV` beside `x.wvc` is missing.
  - libwavpack appends `c` to the whole name (`wvunpack.c` 1237-1238, `open_filename.c` 331), which gives the same name for `.wv` and none for `.ofs`; decision 3.
- **`CompanionSource`** (public, in the host) is where the host looks, set in `AnalysisOptions.Companions`:
  - `CompanionSource.Beside(path)`: the file system, the default of `AnalyzeFile(path)`. The companion is opened as the input is (`FileType.Open`: a FIFO, device or folder is not opened), read as a stream, never loaded whole, and closed with the analysis;
  - `CompanionSource.From(Func<string, Stream?>)`: a caller with no file system, or one that knows the pair under other names (a test, a web page given two files), returns a readable, seekable stream for the extension asked, or null where there is none. The host owns and disposes the stream;
  - `CompanionSource.None`: nothing is looked for. It is the default of `Analyze(source, stream)`, so a stream or standard input reports the pair as unchecked, as the plan asks.
- **Errors:** a companion that exists and cannot be opened or read (`IOException`, `UnauthorizedAccessException`, not a regular file, a stream that cannot seek) is `Unchecked` with `correction.unreadable`; any other exception from a caller's function passes through `Analyze`, as one from a PCM sink does.
- **Reading:** the host prepares the companion as it prepares a file (tags at both ends, the main parser's declared end), so the companion's tree in a pair equals the tree it gets alone. Reads check the analysis's cancellation token; `BytesRead` stays the main file's progress.
- **Budgets:** a companion keeps the caps of a file of its own (WavPack's 4,096 detailed blocks and 16 MiB of kept data, OptimFROG's 1,048,576 blocks), so a pair costs at most two files' memory. The bytes of the correction units count in the stream's decoding allowance (`--verify`), at the coding's units per byte.

### The report

- **A report of its own, inside the main file's** (decision 1): `Report.Companions` lists `CompanionReport(Role, Report)`, role `correction`, each with its source, its root ("Correction File"), its findings and its streams (the correction data stream, with `IsCorrectionFile`).
  - Its nodes have offsets in the companion's file, so the hex view and `--offsets` stay true; nothing in the main tree points into the companion.
  - A finding is placed in the report whose tree holds its node. The companion's own damage (a block checksum that fails, a cut block) is reported there with the codes it gets alone; `wavpack.correction-file` and `optimfrog.correction-file`, which say the file was opened alone, are left out.
  - The companion has no detection of its own: the main file's parser read it as its correction file.
- **The pairing, on the main stream:** `StreamInfo.Correction`, a `CorrectionPairing` (in `Core.Abstractions`) the parser sets:
  - `Status`: `Consistent`, `Inconsistent`, `Missing`, `NotWritten` (the WavPack 4 configuration block says the encoder wrote none, and none was found), `Unchecked`;
  - `Source`, the companion's name; `Basis`, what the check rests on (`Structure`, or `DecodedAudio` for WavPack 3); `Problems`, each a kind, a count and the main tree's node it concerns; `Reason` for `Unchecked`.
  - The host words all of it and adds the findings, so both formats read the same.
- **Findings** (host codes, each pointing to the main stream's node, each kind of problem once with its count):
  - `correction.missing` (Info): no companion beside the file; the stream is reported and decoded as its lossy part. Where WavPack's configuration block says the encoder wrote one, the message says so;
  - `correction.inconsistent` (Warning), once for each problem kind: the companion holds no correction data of this format (`NotCorrectionData`, also an empty file), states another stream (`StreamDiffers`: total samples, first block index, rate, channels or sample format), lacks correction blocks the main blocks call for (`BlocksMissing`), has blocks no main block matches (`BlocksUnmatched`), has correction blocks whose coded-data checksums fail (`ChecksumsFail`), or is cut (`Cut`);
  - `correction.unchecked` (Info): not looked for, with the reason (no folder: the input is a stream; the caller passed none; a version 3 correction file, which only decoding compares, decoded without `--verify`);
  - `correction.unreadable` (Warning): found and not readable, with the error.
  - `NotWritten` and `Consistent` raise none.
- **The wording**, the same for both formats, with the status in the JSON word:
  - text: a line under the stream, `Correction file: consistent (x.wvc)`, `inconsistent (x.wvc: 3 correction blocks missing)`, `missing (x.wvc)`, `none written`, `unchecked (no folder to look in: the input is a stream)`;
  - JSON: `correction` in the stream (`status`, `source`, `basis`, `problems`, `reason`), `companions` in the report (each `role` and the fields of a report), and `withCorrection` in the verification where it is true; fields are added, so the schema version stays 1;
  - CSV: `Correction File`, the first audio stream's status, after `Lossy Part Confidence`, beside the claim the pair confirms; empty for a stream without the concept;
  - the companion's correction data stream is not counted in the file's duration, samples or bitrate.

### Verification and lossless wording, case by case

| Case | Pairing | Lossless wording | Verification |
|---|---|---|---|
| Pair consistent, not decoded | `consistent` | lossless with its correction file | WavPack: "unknown (block header CRC and MD5 not compared: decoding was not asked for)", the block checksums of both files compared; OptimFROG: the CRC-32s of both files, "yes (with correction file; …)" where it stores no MD5 |
| Pair consistent, decoded (WavPack) | `consistent` | lossless with its correction file | "yes (with correction file; block checksum …, correction block checksum …, block header CRC …, MD5 1 of 1)", `IsLossy` false |
| Companion missing | `missing` | lossy without its correction file | as today: "yes (lossy stream; …)", the MD5 or `crc2` not compared |
| Companion inconsistent | `inconsistent` | lossy without its correction file | decoded as [Inconsistent pairs](#inconsistent-pairs) says; never `withCorrection` unless a correction block was applied |
| Unchecked | `unchecked` | lossy without its correction file | as for a missing companion |

- **"Lossless with its correction file"** is said only of a consistent pair, which is what the plan asks; until item 6 adds the coding line, it is the text of the pairing line and the GUI's streams line.
- **OptimFROG's basis is stated:** its consistency rests on block counts and CRC-32s alone, since no decoder exists; the report says the two files belong together and are intact, not that the decoded pair equals the source (`Basis` and the article say so).
- **A consistent pair confirms OptimFROG's lossy-part claim:** the claim is raised to high confidence with the companion as its evidence (decision 7). A missing or unchecked companion leaves it as it is.

## WavPack

### Version 4 and 5: what "consistent" means

The parser walks the companion in step with the main file, as libwavpack's `read_wvc_block` and `match_wvc_header` do (`open_utils.c` 941-1053), and builds the companion's nodes as its own walk of the `.wvc` would.

- **Matching:** each main audio block with samples and the hybrid flag takes the next companion block with the same block index, the same sample count and the same flags; a companion block whose index lies before the main block's is skipped and counted (`BlocksUnmatched`), one after it leaves the main block without correction (`BlocksMissing`). Initial and final flags of a multichannel set follow libwavpack's rule. A companion that states another stream (`StreamDiffers`) is not matched block by block, since every block would differ for that one cause; its checksums and cuts are still counted.
- **The structure check, without decoding:**
  - the companion is correction data (`IsCorrectionFile`: audio blocks with a correction bitstream and no main bitstream), else `NotCorrectionData`;
  - the same stream: header version, total samples, first block index, and the rate, channels and sample format the flags state (`StreamDiffers`);
  - every hybrid main block matched and every companion block used (`BlocksMissing`, `BlocksUnmatched`);
  - every companion block's block checksum holds, as `WavpackVerifySingleBlock` requires before libwavpack uses a block (`ChecksumsFail`); no companion block is cut (`Cut`).
- **With decoding** (`--verify`, "Decode Audio"): the decoder applies the correction (`unpack.c`'s hybrid lossless path, the correction bitstream and the noise shaping it needs), compares each companion block header's CRC, which is the CRC of the lossless audio (libwavpack copies the `.wvc` header over the main one, `open_utils.c` 1043-1044), the extended CRC where the correction carries the extra bits, and the MD5 of the lossless audio.
  - The main blocks' header CRC, the CRC of the lossy audio, is not compared in a pair: libwavpack does not compute it there, and computing it would be a second decode.
  - A pair that the structure check passes and the decoded CRCs fail (a `.wvc` of another encode with the same blocks) stays `consistent` by structure and is verified "no": the structure cannot tell the two apart (decision 2).
- **What the decoder gains:** `DecodeSetup.HasCorrection` (and `IsLossy` false), and `CodedFrame.Correction`, the matching companion block as mapped bytes of the companion's file, valid during the call; the decoder's findings point to the main block's node.
- **None written:** where the configuration block says no correction file was written and none is found, the status is `NotWritten` and nothing is reported; a companion found anyway is checked as any other, as libwavpack would use it.

### Version 3

- **The companion has no structure:** the parser can only check that it is not empty. The pairing's basis is `DecodedAudio`:
  - without decoding it is `unchecked` ("a version 3 correction file has no header; only decoding with it compares it");
  - with decoding, the legacy decoder reads the correction bitstream (`unpack3.c`'s `WVC_FLAG` paths) and compares `crc2` (`unpack3.c` 1173); the host makes the pairing `consistent` where `crc2` holds and the correction bitstream did not end before the samples, `inconsistent` otherwise.
- **Its tree:** one node, "Correction Bitstream", over all the companion's bytes, the same with and without decoding; the bytes the decoder did not reach are its finding (`wavpack.legacy-correction-unread`), since decoding never changes the parser's nodes.
- `wavpack.legacy-crc2-needs-correction-file` stays for a missing or unchecked companion and is not raised in a pair.

### Inconsistent pairs

- **The companion belongs** (it is correction data of the same stream: no `NotCorrectionData` or `StreamDiffers`): it is used block by block where a block matches and its checksum holds, and the other blocks are decoded lossy, as libwavpack does (decision 6). The verification is `withCorrection`, the MD5 is not compared, and the blocks decoded without correction count as libwavpack counts them (`crc_errors`), which the cross-check holds to `WavpackGetNumErrors`.
- **It does not belong:** it is not used; the stream is decoded as its lossy part, `IsLossy` true, as with no companion.

## OptimFROG

- **The check,** on headers and checksums only, as the plan says:
  - the companion is a chain of `CORR` blocks (`NotCorrectionData` otherwise, also for an `.ofr` or an empty file);
  - one `CORR` block for each data block of the `.ofs` (`BlocksMissing`, `BlocksUnmatched` by the difference);
  - every `CORR` CRC-32 holds (`ChecksumsFail`), no block is cut and nothing follows the chain (`Cut`).
- **What it cannot see:** a `CORR` block holds no sample count, rate or position that is known, so an `.ofc` of another encode with as many blocks passes. The design of 0.2 hoped to compare rate, channels, sample type and each block's samples; the fixtures showed the `.ofc` stores none of them that can be read. The hand-built fixtures pin this limit, and the article says it.
- **Verification:** the tallies "block CRC-32" and "correction block CRC-32" (coded data); the MD5 stays declared and not compared. `withCorrection` is true for a consistent pair.

## Front ends

- **The command line:**
  - `encspect x.wv` reports the pair; `encspect x.wvc` reports the correction file alone, as today (its finding says to analyze the main file for the pair);
  - a folder walk analyzes every file, so `x.wvc` also gets its own report (decision 4). The pair is analyzed once, from the main file; the companion's own report costs one structure pass, and a correction file alone is never decoded;
  - `--fail-on` counts the companions' findings; `--offsets` shows each tree's own offsets.
- **The GUI:**
  - the file list keeps one row per file; the main row's codec tooltip gives the pairing line, and its finding counts include the companion's;
  - the detail window's streams line shows the pairing; the structure tree has a second top-level row, "Correction File" with its file name, and the hex view shows the file of the selected node's tree, named in its header;
  - **the summary cache:** a main file's key also holds each companion the analysis looked for: its path and, where found, the size, last-write time, file ID and first and last 64 KiB; where missing, that it was absent. A `.wvc` added, removed or changed beside the file then analyzes it again. The report lists the lookups (`Report.Companions` and the pairing's `Source`), so the GUI needs no knowledge of the formats.
  - "Decode Audio" decodes the pair.
- **Library users:** `CompanionSource` in [library.md](../library.md#a-library-others-could-use)'s public API list; a page or program with two files passes them with `CompanionSource.From`.

## Fixtures

### What exists

- **WavPack 4 and 5** (wavpack 5.6.0, `tests/fixtures/wavpack`): 9 pairs named `external-<case>.wv` and `external-<case>-wvc.wvc`: `hybrid-b4-correction`, `hybrid-mono-b3-correction`, `hybrid-24-b8-correction`, `hybrid-32-b12-correction`, `hybrid-float-b8-correction` (the extra bits in the `.wvc`), `hybrid-51-b3-correction` (sets of three blocks), `hybrid-b4-maximum` (`-cc`), `hybrid-b4-left-right`, `hybrid-b4-correction-md5-pair` (`-c -m`: the MD5 is compared). `hybrid-b4-correction-md5.wv` was written with `-c -m` and its `.wvc` was not kept: a missing companion with an MD5. `hybrid-b4-md5.wv` and the other hybrid files without `-c` are `NotWritten`.
- **WavPack 3** (WavPack 3.4 for DOS, on the legacy branches): 3 pairs, `external-wv340-hybrid-320-correction`, `-320-24-correction` and `-6-correction` with `...-wvc.wvc`; libwavpack decodes each pair to the input.
- **OptimFROG** (5.100): 5 pairs, `external-ofs-<case>.ofs` with `external-ofs-<case>-correction-data.ofc` for `quality0`, `bitrate400`, `blocks-quality3` (three blocks), `md5-correction` (an `.ofs` with an MD5 block) and `mono-quality3`. `quality3`, `mode-best` and `lossywav` have none.
- **Naming** (decision 5): the companions keep their names, so their manifests and goldens keep their stems. A main file's manifest names its companion (`"companion": "external-hybrid-b4-correction-wvc.wvc"`), and the golden and description tests analyze it with `CompanionSource.From` over that file; a main file without the field is analyzed with `CompanionSource.None`, so no golden changes by accident. The file-system lookup is tested on copies in a temporary folder under real names (`x.wv` and `x.wvc`, `X.WV` and `X.WVC`, a folder named `x.wvc`, a FIFO).

### Hand-built (`make_wavpack_fixtures.py`, `make_optimfrog_fixtures.py`, from the committed files)

- **WavPack 4:** a `.wvc` with a block checksum broken; one block removed; one block repeated; cut in its last block; empty; the `.wvc` of `hybrid-mono-b3-correction` beside `hybrid-b4-correction.wv` (another stream); the `.wvc` of `hybrid-b4-maximum` beside `hybrid-b4-correction.wv` (another encode: its blocks have the same index and samples but other flags, so libwavpack's `match_wvc_header` uses none of them and the structure check says so; a pair whose flags agree and whose audio differs is what only decoding tells apart); a lossless `.wv` given as the companion; the `.wvc` of `hybrid-b4-correction` beside `hybrid-b4-md5.wv` (`NotWritten`, a companion found anyway).
- **WavPack 3:** a `.wvc` cut short, a `.wvc` with a flipped bit (`crc2` fails), the `.wvc` of `hybrid-6-correction` beside `hybrid-320-correction.wv`, an empty one and one with 16 bytes after its bitstream (`built-legacy-pair-*`).
- **OptimFROG:** an `.ofc` with a `CORR` CRC-32 that fails, cut in a block, with one block fewer and one more than `blocks-quality3.ofs` has, bytes after the chain, empty, an `.ofr` given as the companion; the `.ofc` of `blocks-quality3` beside `quality0.ofs` (three blocks for one: inconsistent) and of `quality0` beside `bitrate400.ofs` (one block each: consistent, the limit above).

## Cross-checks

- **WavPack 4:** `check_pair` runs on every pair the manifests name, hand-built ones included, with `wvunpack -v`; `wavpack_tool.py`'s program opens each pair with `OPEN_WVC` and records `WavpackGetMode` (`MODE_WVC`), the samples, the MD5 of the samples and `WavpackGetNumErrors` in `crosscheck.pair`. The analyzer's verdict and failed counts are held to them as `crosscheck.decode` is today.
- **WavPack 3:** the legacy program does the same with its `.wvc`; 3.4's `WVUNPACK.EXE` under DOSBox is the black-box reference of the pair's audio, recorded once with the fixtures.
- **OptimFROG:** `crosscheck_optimfrog_fixtures.py` runs `ofs --decode` on each pair, hand-built ones included, with the `.ofc` beside it (and with it absent) and records the exit code and whether the output equals the input. It is a black box: what `ofs` accepts is recorded, and an inconsistent pair that `ofs` decodes to the input fails the run until explained.

## Docs and articles

- [wavpack.md](../formats/wavpack.md) and [optimfrog.md](../formats/optimfrog.md): "Hybrid mode and correction files" and "The lossy part of a DualStream" rewritten for the pair, the "Not covered" lines removed; [architecture.md](../architecture.md): a section "Companion files" under the parser contract; [library.md](../library.md), [cli.md](../cli.md) (the line, JSON, CSV, folder walks) and [gui.md](../gui.md) (the tree, the hex view, the cache key); [development.md](../development.md): the pairs, the manifest field and the cross-checks.
- **Article:** `correction-files`, shared by both formats: what a correction file is, how the host finds it, what "consistent" rests on for each format and what it cannot show, and the words of the report. `wavpack-hybrid-mode` and `optimfrog-blocks` link to it.

## Not covered

- **WavPack correction data in Matroska** (BlockAdditions with BlockAddIDType 1, which the WavPack parser does not read today, [wavpack.md](../formats/wavpack.md#not-covered)): the same concept in one file, owed by the one-concept rule as a queued follow-up (decision 8).
- Looking for the main file from a correction file opened alone.
- Any other name than the one rule above, and a correction file in another folder, unless a caller passes it.
- Decoding OptimFROG, so its pair is never compared on audio.

## Decisions for the lead

Decided by the lead, 2026-11-18: every recommendation below.

1. **The report's shape.** (a) The companion is a report of its own inside the main file's (`Report.Companions`), read in one pass by the main file's parser; (b) the companion as nodes of the main tree, with offsets in another file; (c) two separate reports joined only by the pairing, the companion read twice. Recommended: (a); offsets stay true for the hex view and `--offsets`, the companion's tree equals its tree alone, and WavPack's decoder needs both files in one pass anyway.
2. **What "consistent" rests on.** (a) The structure and the coded-data checksums of both files (WavPack 4 and OptimFROG), with the decoded-audio checksums in the verification, and decoding for WavPack 3, which has no structure; (b) for WavPack, decoding always, so a pair is unchecked without `--verify`. Recommended: (a); the pairing then reads the same with or without decoding for both formats, the plan's OptimFROG rule is the same rule, and a `.wvc` of another encode still shows as "Verified: no".
3. **The name rule.** (a) The name without its last extension plus the companion's extension, in the input's case, one name tried; (b) libwavpack's rule, the whole name plus `c`, and `.ofs` to `.ofc` apart. Recommended: (a); one rule for both formats, the same name as libwavpack's for `x.wv`, and a hybrid file under another extension is still paired.
4. **The companion in folder walks and file lists.** (a) Every file keeps its own row and report; the pair is analyzed once, from the main file; (b) a companion that a main file's report holds gets no row of its own, which needs the walk to order pairs or the front ends to defer correction files (`x.ofc` sorts before `x.ofs`). Recommended: (a); nothing is hidden by name, and the cost is one structure pass over the companion.
5. **The fixtures' names.** (a) Keep them; the main file's manifest names its companion and the tests pass it through `CompanionSource.From`, with real names tested on copies; (b) rename the 13 committed companions to `x.wvc` and `x.ofc` and key their manifests by the whole file name. Recommended: (a); no churn in manifests, goldens and scripts, and the explicit companion is the API a library user needs anyway.
6. **An inconsistent pair whose companion belongs.** (a) Use the correction where a block matches and holds, decode the rest lossy, as libwavpack does; (b) use no correction at all once anything is inconsistent. Recommended: (a); the cross-check can hold the analyzer to libwavpack's pass, and one damaged `.wvc` block does not throw away the rest.
7. **OptimFROG's lossy-part claim with a consistent `.ofc`.** (a) Raised to high confidence, with the companion as evidence; (b) left as the bytes and the extension make it. Recommended: (a); a chain of `CORR` blocks that matches is stronger evidence than the reserved bytes, and the claim's own evidence says what it rests on.
8. **WavPack correction data in Matroska.** (a) Queued for 0.5 under the one-concept rule; (b) "Not planned" with the trigger "a user brings such a file". Recommended: (a); the words, findings and decoder path are the ones this item builds, and only the hand-off of the BlockAdditions is new.

## Pull requests, in order

1. **This design,** signed off.
2. **The host and OptimFROG:** `CompanionSource`, `AnalysisOptions.Companions`, `ParseContext.OpenCompanion`, `Report.Companions`, `CorrectionPairing` and the `correction.*` findings, the writers (text, JSON, CSV), the OptimFROG pair check and claim, the manifest field, the hand-built OptimFROG pairs, goldens, finding coverage, the cross-check, docs and the article. OptimFROG first: no decoder, so the API is proven on the smallest consumer.
3. **The GUI:** the second tree, the hex view per file, the streams line, the summary cache key, headless tests.
4. **WavPack 4 and 5, structure:** the walk in step, the pairing, the hand-built pairs, goldens, cross-check records.
5. **WavPack 4 and 5, decoding** (done, #361): the hybrid lossless path of `unpack.c`, `CodedFrame.Correction`, the CRCs and MD5 with the correction, the work units and `DecoderWorkTests`, the pair cross-check against libwavpack's pass. As built:
   - **The feed:** the parser asks the host for the `.wvc` when the first hybrid block is read, not at the end of the walk, and walks it then (`Pairing.Open`), so that `CorrectionCursor` can hand the decoder each main block's correction block (`CodedFrame.Correction`, bytes of the correction file valid during the call) by `read_wvc_block`'s rule; the pairing is checked once the main file's blocks are read, as before.
   - **`DecodeSetup.HasCorrection`** is true where the correction file holds correction data of the first block's stream; the decoder says after `Finish` whether it applied any block (`ICorrectionDecoder`), and the host reports `WithCorrection` and `IsLossy` from that: a correction file applied to no block is the lossy part, as the design's table says for an inconsistent pair.
   - **The decoder** reads the correction block's sub-blocks after the main block's, as `unpack_init` does (the shaping weights and the correction and extra bitstreams are in the correction block), decodes sample by sample (`get_word` with the correction bitstream, the passes, the shaping of the error) and compares the correction block header's CRC and the extra bits' CRC. A hybrid block without a correction block is the lossy decode; the MD5 is then not compared and `wavpack.md5-needs-correction-file` counts the blocks.
   - **Work:** 16 a byte of the correction bitstream and 112 a sample of a channel beyond the passes (`WorkPerCorrectedSample`); `DecoderWorkTests` has six pair cases (`wavpack-pair-*`), which read 0.66 to 1.30 times the FLAC reference (48 left the 32-bit case at 2.02 under load).
   - **Models and cross-check:** the fixtures' manifests take the pair's audio from libwavpack's pass (`wavpack_model.libwavpack_pair`), `crosscheck.pair` holds its MD5 and `crosscheck_wavpack_fixtures.py` holds the analyzer's audio to `wvunpack --raw` with the `.wvc`. Departure: libwavpack mutes a block whose CRC fails; the analyzer keeps its audio (`PAIR_AUDIO_DIFFERS`, `built-pair-set-block-removed`).
   - **Blocks without a correction block** count as failed block header CRCs (`wavpack.correction-block-missing`), as the design says libwavpack counts them, and the model holds the count to `crosscheck.pair.errors`; a correction file the pairing says is another stream's is not used by the decoder (`built-pair-first-block-removed`). The correction file's checksums count in the verification only where its blocks are used. The bytes of the correction blocks count in the decoding allowance.
   - **Correction-file blocks without audio** are taken as libwavpack takes them (any block): they never match and are skipped or leave the main block without a correction.
6. **WavPack 3** (done, #TBD): the correction paths of `unpack3.c`, `crc2`, the `DecodedAudio` basis, the hand-built pairs. As built:
   - **The ask:** `Legacy.Summarize` asks the host for `wvc` where `AsksForCorrection` holds (version 3, a bit limit or target, the new high flag, as `unpack3_open.c` decides) and the decoder does not refuse the file; the `.wvc` is handed to the decoder only where it holds a byte (`LegacyDecodeInfo.CorrectionReader`); an empty one is `NotCorrectionData` and the stream decodes lossy, with `wavpack.legacy-crc2-needs-correction-file`.
   - **The feed:** the correction bitstream comes with each frame as `CodedFrame.Correction`, in the share of the file the main bitstream's progress is (at most 4 MiB a frame), and in frames of its own after the last main frame where a sample still needs it; the decoder keeps what it has not read, waits for more where a sample needs it, and charges the bytes (16 units each) and 24 units a sample of a channel (`WorkPerCorrectedSample`).
   - **The decoder** (`LegacyCorrection.cs`, `LegacyWords.GetWord4`, the two hybrid loops of `LegacyModes.cs`) follows the `WVC_FLAG` paths: the correction word after `get_word4`, added after the passes (before the joint-stereo step), the noise-shaped error carried, no clamp, the second CRC compared. A pair's verification is the tally "correction CRC", `WithCorrection` true and `IsLossy` false; `wavpack.legacy-crc2-mismatch` and `wavpack.legacy-correction-ended` (the correction bitstream read as far as its end, then as 0xFF bytes, `bs_read3`) are the decoder's findings.
   - **The verdict is the parser's,** not the host's: `Legacy.Pair` reads the verification the decoder ended (`DecodedAudio`: consistent where the correction CRC holds and the bitstream lasted, `ChecksumsFail` or `Cut` pointing to the header's Correction CRC field otherwise, unchecked where decoding was not asked for or did not compare it, with the reason of the design). The host words the two problems for the basis (`CorrectionFindings`).
   - **The tree:** one node, "Correction Bitstream", over all of the companion's bytes, the same with and without decoding, and a stream "WavPack correction data" (`IsCorrectionFile`). The bytes the decoder did not reach are its finding (`wavpack.legacy-correction-unread`, Info, with the count, pointing to the header's Correction CRC field), not a second node: CLAUDE.md's rule is that decoding never changes the parser's nodes, which the first text of this section (a second node "where the decoder found the end") broke.
   - **The two nits the correction-files review left for this step:** N5, a WavPack 1 to 3 file given as the `.wvc` of a version 4 file is read for its tree with no stream, decoding or companion of its own (`Legacy.Parse`'s `isCompanion`); N7, `CompanionSession.Open` answers `Unchecked` to a context over a companion.
   - **Fixtures:** the 7 encoder-written pairs (3.4 and 3.97) name their `.wvc` in their manifests (`companion`) and decode to the original decoder's audio with the correction file; 5 hand-built pairs, `built-legacy-pair-{cut,bit-flipped,other-stream,empty,trailing-bytes}`, from `make_wavpack_legacy_fixtures.py`, whose manifests hold the errors libwavpack counts (`pair_errors`) and the tests hold the verdict to them. The manifests of every version 3 hybrid file with the new high flag gain `correction.unchecked`, since the golden and manifest tests analyze without decoding.
   - **Not exercised by a file:** a mono pair and a pair with noise shaping (the pairs are stereo at 44.1 kHz; the shaping starts at 64 kHz).
   - **Plan:** the item stays open for the GUI.
