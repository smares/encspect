# Cross-checks against the formats' official decoders

Status: draft (2026-11-01), not reviewed and not decided; nothing in it is built. It designs step 1 of "Phase 3, continued" in the [plan](../PLAN.md).

- For the formats where FFmpeg is the only reference so far, the decoder of the format's owner, or its reference implementation, becomes the reference for sample counts, durations and the handling of damage. FFmpeg stays a second cross-check, and each known difference from it is listed per file.
- Every decoder is used as a black box, as the WMA check against Microsoft's decoder does (`.github/workflows/windows-wma-decode.yml`, "WMA decoding by Microsoft's decoder" in [development.md](../development.md)). That check is the model; the lessons of its two reviews are rules here (see [Harness rules](#harness-rules)).
- The analyzer does not change in the first steps. A difference that would change counts, findings or goldens goes to the project lead first (see [Differences and decisions](#differences-and-decisions)).

## Scope

- **In:**
  - Opus and Vorbis in Ogg: libopus and libvorbis, through Xiph's `opusdec` and `oggdec` and through a small harness that feeds the decoders packet by packet;
  - TTA: `tta -d` of libtta-c 2.3, the format authors' own program, which `libtta_tool.py` already builds;
  - AAC, HE-AAC, HE-AAC v2 and xHE-AAC, raw (ADTS, ADIF) and in MP4: Fraunhofer's FDK AAC decoder on Linux, Core Audio on macOS and the Media Foundation AAC decoder on Windows;
  - AC-3 and E-AC-3, raw and in MP4: Core Audio, and Media Foundation's Dolby audio decoder where a runner image has it (documentation says the server images do not, see [Windows](#windows));
  - a probe run that settles every open question below before anything else is built.
- **Not possible:** DTS and DTS-HD, Dolby TrueHD and MLP. Media Foundation's list of supported formats has no decoder for them, and no Apple documentation read for this design lists one in Core Audio. The probe's inventory of every registered decoder on every Windows and macOS image confirms this before the plan's line is settled (open question 1); FFmpeg stays their reference.
- **Out:** see [Out of scope](#out-of-scope).

The analyzer has no lossy decoder yet, so a cross-check compares the analyzer's parsed figures (counts, durations, priming and padding, which frames it calls damaged) with the decoders' output lengths and error behavior. No PCM is compared.

## The design in short

- One record format and one comparison script for every decoder and platform. Each file gets one JSON record per decoder, written by a child process of its own with a timeout, so a crash or a stall is recorded and the run goes on.
- Two paths per decoder where the decoder allows it, as in the WMA check:
  - **the reader path:** the owner's own file-level program or API (`opusdec`, `oggdec`, `tta -d`, Core Audio's `ExtAudioFile`, Media Foundation's Source Reader), which also applies the owner's trimming;
  - **the direct path:** the decoder itself, fed one packet, frame or access unit at a time, in file order, by a minimal reader of the harness's own, so that damaged files reach the decoder and every unit has a result.
- Each decoder figure is compared with the analyzer's figure at the same stage: a count before trimming with the analyzer's coded total, a count after trimming with its trimmed total. The harness computes no trimming of its own.
- The reference figure follows a fixed rule per format: the reader path's total only where it reached the end of the stream without an error, a stall or a stop; else the direct path's total; never a partial total.
- The decoders run on GitHub's runners: Linux in the cross-check jobs' image (`ubuntu-26.04`), macOS on `macos-15`, Windows on `windows-2025`. The comparison always runs on Linux, with the FFmpeg that `tool_versions.py` pins, so every platform's FFmpeg column comes from one version.
- The reference figures and the known differences are committed per fixture folder, so CI can hold the analyzer to them without a macOS or Windows runner.

## What the analyzer reports that a decoder can confirm

The figures below are read from the committed goldens; the names are the report's.

### Opus

- Stream: `samples` and `duration` after the pre-skip and the end trimming (72,000 samples for `external-opusenc-96.opus`).
- Nodes: "Pre-skip" (312), "Decoded Samples" (72,960: what the packets' TOC bytes give, before trimming), "End Trimming" (648), "Last Granule Position" (72,312), "Samples".
- Damage: the packet rules of RFC 6716 (`opus.packet-r1` to `-r7`, `opus.self-delimiting`), the granule cases (`opus.start-offset`, `opus.start-negative`, `opus.end-trimming-early`, `opus.end-trimming-large`, `opus.pre-skip-beyond`, `opus.granule-beyond-packets`), and Ogg's page findings (CRC, gaps, missing beginning- or end-of-stream pages).
- Today: `crosscheck_ogg_fixtures.py` already requires `opusdec`'s written samples to equal the manifest's on the fixtures oggz validates; mapping families 2, 3 and 255 are skipped (libopusfile 0.12 refuses them), and the four damaged Opus fixtures are not compared with any decoder.

### Vorbis

- Stream: `samples` and `duration` after trimming (66,150 for `external-oggenc-q5.ogg`).
- Nodes: "Decoded Samples" (67,136: the overlap of the blocks), "Start Trimming", "End Trimming" (986), "Last Granule Position"; the start trimming is capped at the first two blocks and the end trimming at the last block, and more is a finding.
- Damage: setup and identification header findings, packet errors, granule errors, Ogg's page findings.
- Today: `oggdec` must write the manifest's samples on the valid fixtures; `vorbis-packet-errors.ogg` skips it because `oggdec` writes 3,072 samples fewer, a long block for each of its three packets that code no block. That is the first known difference this design hands to the project lead. The six damaged Vorbis fixtures are not compared with any decoder.

### TTA

- Stream: `samples` (the samples of the frames read whole) and `duration`; no priming or padding.
- Damage: `tta.header-crc-mismatch`, `tta.seek-table-crc-mismatch`, `tta.frame-crc-mismatch`, `tta.frame-cut`, `tta.frames-missing`, `tta.frame-too-small`, the header field findings, `tta.audio-format-encrypted`.
- Today: `tta -d` decodes only the encoder-written files, inside the generator; ffmpeg is the cross-check's decoder. The 41 hand-built files hold bytes in place of coded samples, so a decoder's output samples mean nothing there, but its count, CRC verdicts and refusals do.

### AAC

- Stream: `samples` and `duration` count every access unit at the output rate: 1,024 per unit, 2,048 with SBR (24,576 for `apple-lc-m4a-cbr-st44.m4a`, which holds 22,050 samples of audio).
- MP4 nodes: "Encoder Delay" and "Padding" on the stream (resolved), and their sources: "Encoder Delay (iTunSMPB)", "Samples (iTunSMPB)", "Padding (iTunSMPB)", "Encoder Delay (Edit List)", "Samples (Edit List)", "Padding (Edit List)", "Media Time", "Segment Duration", "Roll Distance". iTunSMPB and the media timescale count at the core rate for HE-AAC (`fdkaac-he.m4a`: 2,048 + 33,075 + 717 = 35,840, half of the stream's 71,680).
- Findings on gapless data: an edit list and iTunSMPB that disagree, an iTunSMPB whose total is not the track's duration.
- Damage: ADTS CRC failures, cut and truncated frames, `aac.blocks-missing`, resync gaps, MP4 sample table findings (`mp4.samples-overlap`, samples outside the file), and the SBR and parametric stereo rules (`aac.sbr-data-sparse`, `aac.ps-without-mono-core`, `aac.sbr-without-channel-element`), which decide whether the output rate doubles.
- Today: ffmpeg must decode every access unit without an error, faad prints the configuration and the ADTS duration, and faad2's trace confirms every raw data block's syntax. No decoder's output length is compared.

### AC-3 and E-AC-3

- Stream: `samples` and `duration` from the frames: 1,536 per AC-3 frame, 256 per E-AC-3 audio block; one stream per configuration, and the independent substreams of one file in one playback group.
- Damage: `ac3.crc1-mismatch`, `ac3.crc2-mismatch`, `ac3.frame-truncated`, `ac3.block-overrun`, `ac3.dependent-excess`, `ac3.streams-limited`; frames that break are still counted (`crc-damaged.ac3`: 12,288 samples with both CRC findings).
- Today: ffmpeg decodes each packet alone and must refuse exactly the damaged frames, and refuses every independent substream but 0; liba52 confirms the block ends. No decoder's output length is compared.

## The decoders and how each is driven

"Documented" marks a fact from public documentation; "probe" marks one the first run must confirm (see [Open questions and the probe](#open-questions-and-the-probe)).

| Format | Decoder | Runner | Reader path | Direct path |
| --- | --- | --- | --- | --- |
| Opus | libopus, libopusfile | `ubuntu-26.04` | `opusdec --rate 48000` (opus-tools 0.2) | harness: libogg pages, `opus_multistream_decode` per packet; the projection decoder for family 3 |
| Vorbis | libvorbis, libvorbisfile | `ubuntu-26.04` | `oggdec -R` (vorbis-tools 1.4.3) | harness: libogg pages, `vorbis_synthesis` and `vorbis_synthesis_pcmout` per packet |
| TTA | libtta-c 2.3 | `ubuntu-26.04` | `tta -d` | none (see decision 6) |
| AAC | FDK AAC 2.0.2 | `ubuntu-26.04` | none | harness: `aacDecoder_Fill` and `aacDecoder_DecodeFrame`, ADTS and LOAS by the library's transport, raw access units of MP4 and ADIF from the harness's reader |
| AAC | Core Audio | `macos-15` | `ExtAudioFile` on the file | `AudioConverter` fed the access units with their magic cookie |
| AAC | Media Foundation AAC decoder | `windows-2025` | Source Reader (MPEG-4 File Source, ADTS File Source) | the AAC decoder MFT fed one access unit per sample |
| AC-3, E-AC-3 | Core Audio | `macos-15` | `ExtAudioFile` (probe: which file types open) | `AudioConverter` with `kAudioFormatAC3` or `kAudioFormatEnhancedAC3`, fed frames |
| AC-3, E-AC-3 | Media Foundation Dolby audio decoder | only where the probe finds it | Source Reader | the MFT, unlocked for field of use |

### Linux

- **libopus and libvorbis** come from Ubuntu 26.04's packages, as `opusdec` and `oggdec` do now. `tool_versions.py` asserts `opus-tools` 0.2 and `ogginfo` 1.4.3 already; the libraries (`libopus0`, `libopusfile0`, `libvorbis0a`, `libvorbisfile3`, `libogg0`) join `PACKAGES` with the versions the probe records.
- **The Ogg harness** is a C program in `scripts/fixtures/` built with gcc against those packages' headers (libogg, libopus, libvorbis: BSD licenses). Its page and packet reading is libogg's, which drops a page that fails its CRC; that is what Xiph's own tools see, and the record says which pages it dropped.
- **libtta-c's `tta -d`**, built by `libtta_tool.py` from the pinned archive. Its usage text is the only documentation used, as for the encoder.
- **FDK AAC:** `fdk_tool.py` already builds libfdk-aac 2.0.2 from its pinned commit, because Ubuntu's libfdk-aac2 lacks SBR and parametric stereo, and `fdk_crc.c` already decodes ADTS files with it to list the CRC errors. The harness grows from `fdk_crc.c`: per access unit the error code, the samples written, and `CStreamInfo` (sample rate, frame size, channels, object types, `outputDelay`). The FDK repository's own `aac-dec.c` reads only ADTS and LOAS and stops at the first failed decode, so it is not used. Documented in the library's header `aacdecoder_lib.h`: its transport takes ADTS, LOAS and raw access units with a configuration (`TT_MP4_ADTS`, `TT_MP4_LOAS`, `TT_MP4_RAW`). Probe: which object types 2.0.2 decodes (USAC, Main, LTP, SSR, ELD).
- The FDK library is under Fraunhofer's own license, libtta-c under the LGPL, libopus and libvorbis under BSD licenses: all are test tools built in CI, linked only into the harnesses, never into Encspect, and never redistributed.

### macOS

- A C program against AudioToolbox, compiled with `clang -framework AudioToolbox` on the runner, as `apple-aac-fixtures.yml` already runs `afconvert` there. C rather than a .NET probe: AudioToolbox is a C API, and a JSON Lines record written and flushed per unit needs no lock (decision 4).
- Documented: AudioToolbox has the format constants `kAudioFormatMPEG4AAC`, the HE-AAC variants, `kAudioFormatAC3` and `kAudioFormatEnhancedAC3`, and decodes through `AudioConverter`; `ExtAudioFile` reads a file through `AudioFile`. Probe: which of these `kAudioFormatProperty_DecodeFormatIDs` lists on `macos-15` (xHE-AAC as `kAudioFormatMPEGD_USAC` above all; `aac.md` records that `afconvert` refuses to encode it there, which says nothing about decoding), which file types `kAudioFileGlobalInfo_ReadableTypes` lists (raw `.ac3` and `.ec3`, ADTS, M4A with `ac-3` and `ec-3` entries), and whether `ExtAudioFile` trims iTunSMPB's priming and remainder.
- The decoders are Apple's. The plan calls the AC-3 and E-AC-3 decoders of Core Audio and Media Foundation Dolby's; both are implementations under Dolby's license, and neither vendor documents whose code they run. They are the closest to the owner's decoder a runner offers.
- `macos-15` is arm64. The probe runs `macos-15-intel` too; the matrix keeps it only if a count differs between them.

### Windows

- The Media Foundation AAC decoder (Learn, "AAC Decoder"; `MSAudDecMFT.dll`) is documented for servers from Windows Server 2008 R2, so `windows-2025` should have it. Documented limits, which become expected differences: no Main, SSR or LTP; no ADIF; frames with coupling channel elements skipped; no 960-sample frames; at most 6 channels and 48 kHz; one raw data block per ADTS frame.
- **xHE-AAC on Windows:** Windows 11 22H2 added xHE-AAC decoding, an implementation Fraunhofer licensed to Microsoft (Fraunhofer's announcement of 2022-10-20). It is therefore no independent witness to FDK for USAC, and whether Windows Server 2025 has it is open (probe).
- **The Dolby audio decoder** (Learn, "Dolby Audio Decoder"; `Msauddecmft.dll`) lists "Minimum supported server: None supported", and Microsoft's codec page says Windows 11 24H2 is the first version without the inbox AC-3 codec. `windows-2025` is build 26100, the 24H2 code base, and `windows-11-arm` runs Windows 11 build 26200. So no GitHub-hosted Windows image is expected to have it; the probe's inventory settles it on `windows-2022`, `windows-2025` and `windows-11-arm`. Documented for where it exists: only independent substream 0 is decoded and dependent substreams are skipped, up to 7.1, and the MFT must be unlocked for field of use. Installing Dolby's decoder from the Microsoft Store on a runner is not pursued: its name ("Dolby Digital Plus decoder for PC OEMs") says it is meant for PC makers, and its terms were not examined.
- **The probe project:** `scripts/fixtures/windows/WmaDecodeProbe` already has the child processes, the stall budget, the version record and the locks. It grows an MFT path (`IMFTransform::ProcessInput`, `ProcessOutput`, drain) and a reader per format (ADTS frames, MP4 access units), and is renamed once the WMA records it writes are unchanged on the same image (decision 3).

## What is compared

- **Totals, stage by stage:**
  - reader-path totals of Opus and Vorbis with the stream's `samples` (after trimming), direct-path totals with "Decoded Samples";
  - `tta -d` with `samples`;
  - AAC direct-path totals with the stream's `samples`; reader-path totals with `samples` less "Encoder Delay" and "Padding" where the reader is seen to trim (scaled from the core rate to the output rate for HE-AAC: the only arithmetic the script adds, stated in its output);
  - AC-3 and E-AC-3 totals with `samples`, per independent substream where the decoder reports one.
- **Units:** per packet, frame or access unit, the decoder's result (decoded, refused with its code, concealed, nothing output) beside the analyzer's reading of the unit at the same bytes (a finding that points to it, or none). The match is by file offset and length, never by index, since the two readers may split damaged data differently.
- **Error behavior:** where the reader stops (unit and offset), whether the decoder outputs samples for a unit it reports as damaged (concealment), whether output resumes after damage, and the samples a unit gives against the samples its header states.
- **The FFmpeg column:** ffmpeg 8.0.1's count at the same stage (`-ignore_editlist 1` for the coded count of MP4, its default for the trimmed one), and its errors.

## The reference rule

- **General:** the reader path's total counts only where the reader reached the end of the stream without a failed read, an error message, a stall, a crash or a stop by the harness; else the direct path's total, if that path fed every unit and drained without a stall, crash or exception; else there is no reference. A partial total is recorded as `partialSamples` and is never a figure. A file whose payloads are encrypted has no reference.
- A reader total and a direct total are at different stages for Opus, Vorbis and AAC, so falling back to the direct path gives a reference for the coded total only; the trimmed total then has none.
- **Per format, in order of precedence:**
  - Opus: `opusdec`, then libopus direct.
  - Vorbis: `oggdec`, then libvorbis direct.
  - TTA: `tta -d`.
  - AAC: FDK direct, then Core Audio, then Media Foundation. FDK comes first because it runs on Linux from pinned source, so its figures are reproducible and can join the CI gate later, and Fraunhofer IIS is one of AAC's developers. Core Audio's reader is the reference for the trimmed total, since FDK has no file reader. Where FDK refuses an object type, the next decoder that decodes it is the reference; where none does, FFmpeg stays the reference and the file is marked so.
  - AC-3 and E-AC-3: Core Audio, then Media Foundation if found; if neither decodes, FFmpeg and liba52 stay the reference.
- **Disagreement between official decoders** (FDK against Core Audio, say) is listed per file. The rule still picks the reference, and the file is marked as one where the owners' decoders disagree.
- **A figure that differs between OS builds** is never a reference: the WMA check saw the Source Reader's count of damaged Voice files change between two builds of Windows Server 2025. Each record names the build, and the comparison flags a figure that changed for the same file bytes.

## Priming and padding

- Each decoder path declares what it outputs, and the probe confirms it on one file per case:
  - `opusdec` and `oggdec` trim what the granule positions call for (RFC 7845 defines it for Opus): on the valid fixtures they already write the analyzer's trimmed `samples`; a start offset and damaged granule positions are to be confirmed;
  - libopus and libvorbis fed directly trim nothing: libopus returns every packet's samples, and libvorbis returns what the overlap of blocks gives, the rule the analyzer's "Decoded Samples" follows;
  - FDK fed directly outputs one frame per access unit; its `outputDelay` is recorded, not subtracted;
  - Core Audio: whether `ExtAudioFile` trims iTunSMPB, the edit list or both, and what `kAudioConverterPrimeInfo` reports for the `AudioConverter` (probe);
  - Media Foundation: whether the MPEG-4 File Source applies the edit list or iTunSMPB (probe); the MFT fed directly trims nothing as far as documented.
- A decoder whose trimming is not understood from its records is compared only at the coded stage until it is.

## Harness rules

The lessons of the WMA check's reviews (W-1 to W-22, R2-1 to R2-9), as rules for every harness:

- **No padding:** a cut unit is fed from its first byte up to the first byte not received and never zero-filled to its declared size; the record holds the declared size, the bytes received and the missing ranges. In the WMA check a zero-filled cut object gave 300 frames more.
- **File order:** units are fed in the order their first byte appears in the file; an incomplete unit is closed when the next unit of its stream begins and at the end of the data, never at the end of the file out of order.
- **Memory bounds:** buffers grow by the bytes received, geometrically, within a cap per unit (16 MiB) and a cap on units held at once and their total; a unit is released once fed, and a cap that is hit is recorded. A declared size (an MP4 `stsz` entry, a seek table entry) never allocates by itself.
- **Bounded recursion** in every box or element walk.
- **One child process per file and path**, with a timeout (30 s), so a crash is recorded with its exit code or signal. On Windows the child runs the decoder on a worker thread and records a stall (inside which call, since when) before its own budget ends; on Linux and macOS the parent records a timeout and the child's last flushed unit.
- **Published records:** in the C harnesses each unit's record is one JSON line written and flushed before the next unit, and the parent assembles the file's record, so a killed child leaves what it did and nothing is shared between threads. In the Windows probe every change to an object the serializer reads stays under one lock, as R2-1 required.
- **Stops are kept:** the reason a loop stopped (end, error, stall, a call limit, a timestamp that does not advance) is never overwritten by a later one, and limits are exact (1,000 reads at one timestamp, not 1,001).
- **Resources freed:** media types, buffers and decoder handles are released on every path, and Media Foundation is shut down.
- **Versions pinned and recorded:** `environment.json` is written before the first file and again at the end: the runner image (`ImageOS`, `ImageVersion`), the OS build, the decoder libraries' file versions (Windows), the AudioToolbox and AudioCodecs bundle versions (macOS), the packages' versions from dpkg and the FDK commit (Linux), the ffmpeg version, and the modules each child loaded. Runner labels are pinned (`windows-2025`, `macos-15`, `ubuntu-26.04`), never `-latest`.
- **Artifacts survive a timeout:** the decode step's timeout is shorter than the job's, and the upload runs with `if: always()`.
- **Figures are never truncated** in the comparison's text, codes are printed in hex, and streams that are not the format under test are counted apart.
- **Docs say only what exists:** a rule that is decided but not yet followed by the analyzer is worded as the decision for the future (W-5).

## Records and the comparison

- **Record:** `records/<format>/<file>.json`, one per file: the file's path, size and SHA-256, and per decoder path its result:
  - `opened` or the failure to open with its code;
  - the formats it reports (rate, channels, layout);
  - `units`: per unit the file ranges, the bytes declared and received, the result code, the samples output and any flag the decoder sets (concealed, discontinuity);
  - `stoppedBy` (`end`, `error`, `stall`, `timeout`, `crash`, `limit`), `totalSamples` only for `end`, `partialSamples` otherwise;
  - the decoder's own messages (standard error of `opusdec`, `oggdec`, `tta`).
- **Comparison:** `scripts/fixtures/compare_reference_decoders.py <artifact folders>` runs on Linux. It reads the analyzer's figures from the committed goldens (or runs the CLI with `--encspect`, which the damage variants need), runs ffmpeg 8.0.1 for its column, and writes:
  - `comparison.txt`: one table per format with one row per file: the analyzer's figure and its stage, the reference figure and which path gave it, ffmpeg's, every other decoder's, and notes (reader stopped at unit n, units refused, concealed units, encrypted, no reference); then the units where a decoder's verdict differs from the analyzer's; then counts of agreement for each pair, per format;
  - `comparison.json`: the same, for the check below.
- **Committed results:** after a run is reviewed, the reference figures and the differences go into `tests/fixtures/<format>/reference-decoders.json`: per fixture (path and SHA-256), each decoder's figure and stage, the decoder's version, the runner image and the run ID, and each difference with its status (see below). A test in the format's cross-check job compares every golden with it: a golden that equals the reference passes, one that differs passes only as a listed difference, and an entry whose SHA-256 no longer matches the fixture is stale and fails until the run is repeated. This keeps the macOS and Windows results in CI without their runners (decision 7).
- **In the docs:** each format page gets a section "Reference for sample counts", as [asf-wma.md](../formats/asf-wma.md) has, and [development.md](../development.md) a section per decoder with "Differences found", citing the run whose artifact the figures come from.

## Differences and decisions

- Every difference is listed, never dropped. Each gets one of four statuses in `reference-decoders.json`:
  - `explained`: the analyzer equals the reference and FFmpeg differs, or a decoder documents the behavior (Media Foundation skips frames with coupling channel elements); nothing changes;
  - `pending`: the analyzer differs from the reference in a way that would change counts, findings or goldens;
  - `follow`: the project lead decided the analyzer follows the reference; the change is its own pull request with its goldens (and a `golden-changes.json` where the change can be stated);
  - `keep`: the project lead decided the analyzer keeps its figure, with the reason.
- **A pending difference goes to the project lead** as one item: the files, the figures of every column, the stage, the decoder versions and run IDs, whether the official decoders agree among themselves, what would change in the analyzer (count, finding, golden) and the rule the change would follow. Items are collected per format in the pull request that commits the format's `reference-decoders.json`, so the lead decides them together.
- The analyzer follows FFmpeg until the lead decides, and the docs say so.

## Fixtures

- **Undamaged:** every encoder-written fixture of the formats in scope: `tests/fixtures/ogg` (`external-*`), `tta` (`external-*`), `aac`, `adts`, `apple` and the encoder-written files of `mp4`, `ac3` (`external-*`, the `derived-*` files). The files in `tests/fixtures/apple` have no goldens, so the comparison takes their figures from the CLI. Hand-built files whose payloads are not coded audio (the TTA `built-*` frames, synthetic AAC blocks) are run too: their counts, verdicts and refusals are compared and their samples ignored.
- **Damaged, committed:** the damaged hand-built fixtures of those folders (for example `opus-packets-corrupt.opus`, `vorbis-packet-errors.ogg`, `crc-damaged.ac3`, `cut-frame.ac3`, `truncated.aac`, `crc-stereo-damaged.aac`, `sample-short.mp4`, `truncated.m4a`, `built-frame-crc-fails.tta`, `built-cut-mid-frame.tta`).
- **Damaged, derived in the run:** a script writes fixed variants of each undamaged encoder-written file, by rules and without randomness, so a run is reproducible: cut at the middle of the first, a middle and the last unit; one byte changed in a middle unit's payload; one unit or Ogg page removed; one unit repeated. The variants are not committed; the comparison runs the CLI on them. A variant that shows a behavior worth keeping becomes a committed fixture with its golden, after the lead's decision on it.
- **Downloaded:** the samples the repository already pins by SHA-256 and never commits: `tests/conformance/usac-samples.json` (xHE-AAC from Fraunhofer, the ISO/IEC 23003-3 conformance streams, one exhale file), `sbr-samples.json` (SBR and parametric stereo conformance streams, the aacPlus decoder check streams) and `eac3-samples.json` (Dolby's JOC test files and FATE's E-AC-3 files). The workflows fetch them through `downloads.py`; nothing comes from rarewares.org, and the one committed fixture first written by a rarewares build (`external-oggenc2-aotuv-rarewares.ogg`) is used as committed.
- **New committed fixtures:** none planned before the probe. Candidates the probe may call for: an E-AC-3 file with two independent substreams in MP4 (for Core Audio), an Ogg Opus file with a page lost in the middle (concealment), an ADTS file with a coupling channel element written by an encoder rather than by hand.

## Workflows

- **`.github/workflows/reference-decode.yml`**, one workflow with a job per platform and a final comparison job:
  - `linux` (`ubuntu-26.04`): builds FDK (cached as the faad2 build is) and the Ogg harness, builds `tta` with `libtta_tool.py`, runs every path over the selected fixtures;
  - `macos` (`macos-15`): builds the AudioToolbox harness and runs it;
  - `windows` (`windows-2025`): builds the probe and runs its AAC path (and the Dolby path where found);
  - `compare` (`ubuntu-26.04`, `needs` all three, `if: always()`): downloads their artifacts, builds the CLI for the derived variants, runs `compare_reference_decoders.py`, and uploads `comparison.txt` and `comparison.json`.
- **Triggers:** manual dispatch, with inputs for the formats and a file of extra paths, and a push to the branch `ci/reference-decode`, as the WMA workflow is run. No schedule and no path filter: the runs are read by a person, and a difference does not fail them. It is not part of `ci.yml`.
- **When to run it:** after a change to a parser in scope, to its fixtures or to the harness, and when a pinned runner label moves to a new OS build.
- **Time limits:** 60 minutes per job; the decode step 45 minutes; 30 seconds per child. Linux and macOS take a few minutes for the fixture count above; Windows' worst case is the WMA check's (three runs per file at 30 seconds each).
- **Artifacts:** `reference-decode-<platform>` per job and `reference-decode-comparison`, kept 30 days, so a review can read a run that is a few weeks old.
- **The CI gate:** the Linux paths of decided figures join the existing cross-check jobs (`ogg-`, `tta-`, `mp4-crosscheck`), which already run `opusdec` and `oggdec` on the valid Ogg fixtures; the macOS and Windows figures reach CI only through `reference-decoders.json`. `CiWorkflowTests` keeps every `ci.yml` job on `ubuntu-26.04`.

## Open questions and the probe

The first run of the workflow is a probe (rollout step 1): each job prints an inventory and decodes one or two files per case, uploads the records, and builds nothing else. Each question names what settles it.

1. **Which audio decoders each image has** (settles the DTS, TrueHD, MLP, AC-3 and xHE-AAC claims): on `windows-2022`, `windows-2025` and `windows-11-arm`, `MFTEnumEx` over the audio decoder category with every flag (field of use included) and `DMOEnum`, listing each decoder's CLSID, DLL, version and input subtypes; on `macos-15` and `macos-15-intel`, `kAudioFormatProperty_DecodeFormatIDs` and `kAudioFileGlobalInfo_ReadableTypes`.
2. **Core Audio and AC-3:** whether `ExtAudioFile` opens raw `.ac3`, raw `.ec3` and AC-3 or E-AC-3 in MP4, and whether `AudioConverter` decodes frames fed one at a time; how it handles independent substream 1 (`substreams.eac3`) and dependent substreams.
3. **Core Audio and xHE-AAC:** whether `usac` is decodable on `macos-15`, on `exhale.m4a` and one Fraunhofer sample.
4. **Trimming of each reader:** `ExtAudioFile` and the Source Reader on `apple-lc-m4a-cbr-st44.m4a` (iTunSMPB only), `ffmpeg-lc.m4a` (edit list only) and `fdkaac-lc.m4a` (both); `oggdec` on `vorbis-start-trimming.ogg` and `vorbis-start-offset.ogg`.
5. **FDK's object types:** 2.0.2 on an AAC Main, an LTP, an SSR (`ffmpeg-main.aac`, `ltp.aac`, `ssr-gain-control.aac`), an ELD and a USAC file; and whether 2.0.3 gives other figures on the whole corpus (if not, the encoder's pin stays the decoder's).
6. **FDK's error behavior:** what it outputs for a damaged access unit with concealment on (the default) and with it off, on `crc-stereo-damaged.aac` and `truncated.aac`.
7. **Reader paths on damage:** whether `opusdec`, `oggdec` and `tta -d` stop at the first damaged unit or go on, what they print and what exit code they return, on the damaged Ogg and TTA fixtures.
8. **Media Foundation on Windows Server:** whether the AAC MFT and the ADTS File Source are present on `windows-2025` (Server editions need the Media Foundation feature, which the WMA check shows is there), and whether it has xHE-AAC.
9. **Intel against arm64:** whether any count differs between `macos-15` and `macos-15-intel`.

## Rollout

1. **Probe** (one pull request, the workflow with inventory and the cases above, on a branch; about 1 day). Its findings are written into this design, which then goes to review and to the project lead.
2. **Shared parts and Opus and Vorbis** (about 3 days): the record format, `compare_reference_decoders.py` with the FFmpeg column, the derived damage variants, the Ogg harness, the Linux job, `reference-decoders.json` for `tests/fixtures/ogg` and its test, the docs sections. First because the tools are already in CI, the runs are fast on Linux, damaged fixtures exist with no decoder comparison yet, and one difference (`vorbis-packet-errors.ogg`) is already known; it shakes out the shared parts where iterating is cheapest.
3. **TTA** (about 1 day): `tta -d` over all 67 fixtures and the derived variants. Small, and the tool is built already.
4. **AAC with FDK** (about 3 days): the harness from `fdk_crc.c`, a minimal MP4 and ADIF reader of its own, ADTS and LOAS through the library; `aac`, `adts`, `apple`, `mp4` and the downloaded samples.
5. **Core Audio** (about 3 days): the macOS job, AAC first (the trimmed total), then AC-3 and E-AC-3 as far as the probe allows.
6. **Media Foundation AAC** (about 3 days): the MFT path and the AAC readers in the Windows probe, and its rename once the WMA records are unchanged.
7. **AC-3 and E-AC-3 on Windows**, only if the probe found the Dolby decoder on an image.
8. **Decisions:** each format's pending differences go to the project lead with the pull request that commits its `reference-decoders.json`; the analyzer changes that follow are their own pull requests, each with its goldens.

Each step ends with an independent review of the harness and the comparison against this design, as the WMA check had.

## Decisions

Proposed, for the project lead:

1. **Two paths per decoder, and the reader's total only when complete.** Reason: a reader stops at damage (the Source Reader on 14 Voice files), a direct path does not, and a partial total looked like agreement until W-4. Alternative: the reader path alone, which leaves damaged files without a reference.
2. **Compare like with like:** each decoder figure against the analyzer's figure at the same stage, with no trimming computed by the harness. Reason: the harness would otherwise repeat the analyzer's trimming rules, and an error in them would hide on both sides. Alternative: trim the direct paths' output in the harness.
3. **Grow the Windows probe instead of writing a second one.** Reason: its stall handling, locks and version record took two review rounds to get right. Alternative: a separate AAC probe, which repeats that work.
4. **C harnesses with flushed JSON Lines on Linux and macOS.** Reason: the decoders' APIs are C, and a record that is flushed per unit and assembled by the parent survives a crash and needs no lock. Alternative: .NET probes with P/Invoke, as on Windows.
5. **FDK first for AAC, Core Audio for the trimmed total.** Reason: pinned source on Linux gives reproducible figures that can join the CI gate; Core Audio is the only official reader of M4A gapless data on a runner. Alternative: Core Audio as the AAC reference, whose version moves with the runner image.
6. **TTA by `tta -d` only.** Reason: a direct path needs libtta's API, and reading its LGPL headers would keep that agent out of the clean-room TTA decoder that decision 10 of [decoders.md](decoders.md) plans. Alternative: a harness against libtta's API, written by an agent who then never works on the TTA decoder.
7. **Commit the reference figures and differences per fixture folder.** Reason: macOS and Windows runs cannot be part of `ci.yml`, and a later parser change should still show against them. Alternative: the WMA model, figures only in the docs and the artifact, which a parser change can drift from unseen.
8. **One FFmpeg for every platform's column**, run in the Linux comparison job. Reason: the WMA artifact's column came from Chocolatey's 9.0.2 while every other check uses 8.0.1. Alternative: install ffmpeg on each runner and record each version.

## Out of scope

- Comparing decoded PCM: the analyzer has no lossy decoder yet; that waits for the lossy decoders under "Later" in the plan.
- Opus and Vorbis in Matroska and WebM, and AAC and AC-3 in Matroska: neither Xiph's tools nor Core Audio read Matroska, and extracting the packets to Ogg or ADTS would add another tool's handling of the trimming fields.
- MP3 and MP2, which have more references than FFmpeg already (mpg123, madplay, minimp3); Speex, whose Xiph decoder `speexdec` is in the Ogg cross-check already; WMA, which has its check; the lossless formats, whose reference tools are their checks already.
- The MPEG reference software of ISO/IEC 14496-5 for AAC: its license and build were not examined; a later step may add it.
- AC-4, MPEG-H and the other formats of phase 5.
- Decrypting protected tracks: encrypted payloads have no reference.
- Any change to the analyzer, until the project lead decides a difference.

## Sources

- The plan, "Phase 3, continued"; [decoders.md](decoders.md) (decisions 9 and 10); [development.md](../development.md) ("WMA decoding by Microsoft's decoder", "Tool versions", "TTA", "Conformance streams and downloads"); [asf-wma.md](../formats/asf-wma.md); the format pages [opus.md](../formats/opus.md), [vorbis.md](../formats/vorbis.md), [ogg.md](../formats/ogg.md), [tta.md](../formats/tta.md), [aac.md](../formats/aac.md), [mp4.md](../formats/mp4.md), [ac3.md](../formats/ac3.md).
- The scripts `crosscheck_ogg_fixtures.py`, `crosscheck_mp4_fixtures.py`, `crosscheck_ac3_fixtures.py`, `crosscheck_tta_fixtures.py`, `fdk_tool.py`, `fdk_crc.c`, `libtta_tool.py`, `tool_versions.py`; the WMA probe and its workflow; the review findings of the WMA check.
- Microsoft Learn: "Dolby Audio Decoder", "AAC Decoder", "Supported Media Formats in Media Foundation"; Microsoft's support page "Codecs in Media Player" as reported by the press in May 2024 (AC-3 not inbox from Windows 11 24H2).
- Fraunhofer IIS audio blog, "xHE-AAC Audio Codec now in Windows 11" (2022).
- actions/runner-images release notes for `win11-arm64` (OS build 26200).
- github.com/mstorsjo/fdk-aac: the file list and `aac-dec.c`'s usage (ADTS and LOAS only); tags v2.0.2, v2.0.3 and v3.0.0.
- No FFmpeg, faad2, liba52 or libdca source was read for this design, and no libtta source.
