# Cross-checks against the formats' official decoders

Status: draft (2026-11-01), revised after an independent review; not decided, and nothing in it is built. It designs step 1 of "Phase 3, continued" in the [plan](../PLAN.md). Claims about the repository (goldens, fixtures, scripts, workflows, parsers) were checked at c63ada6. Facts about outside software come from the sources listed at the end; what no source confirms is marked **unverified** and listed under [Unverified claims and estimates](#unverified-claims-and-estimates), and what only the first run can settle is marked **probe** and listed under [Open questions and the probe](#open-questions-and-the-probe). The questions for the project lead are collected under [Questions for the project lead](#questions-for-the-project-lead).

- For the formats where FFmpeg is the only reference so far, the decoder of the format's owner, or its reference implementation, becomes the reference for sample counts, durations and the handling of damage. FFmpeg stays a second cross-check, and each known difference from it is listed per file.
- Every decoder is used as a black box, as the WMA check against Microsoft's decoder does (`.github/workflows/windows-wma-decode.yml`, "WMA decoding by Microsoft's decoder" in [development.md](../development.md)). That check is the model; the lessons of its two reviews are rules here (see [Harness rules](#harness-rules)).
- The analyzer does not change in the first steps. A difference that would change counts, findings or goldens goes to the project lead first (see [Differences and decisions](#differences-and-decisions)).
- Words: "reference" means the figure a comparison holds the analyzer to. "Official decoder" means the decoder of a format's owner, as in the plan. The files and the workflow are named after the second ("official-decode", `official-decoders.json`), so "reference" keeps one meaning. The MPEG reference software (ISO/IEC 14496-5) is always called that.

## Scope

- **In:**
  - Opus and Vorbis in Ogg: libopus and libvorbis, through Xiph's `opusdec` and `oggdec` and through a small harness that feeds the decoders packet by packet;
  - TTA: `tta -d` of libtta-c 2.3, the format authors' own program, which `libtta_tool.py` already builds;
  - AAC, HE-AAC, HE-AAC v2 and xHE-AAC, raw (ADTS, ADIF) and in MP4: Fraunhofer's FDK AAC decoder on Linux, Core Audio on macOS and the Media Foundation AAC decoder on Windows;
  - AC-3 and E-AC-3, raw and in MP4: Core Audio, and Media Foundation's Dolby audio decoder where a runner image has it (documentation says the server images do not, see [Windows](#windows));
  - a probe run that settles every open question below before anything else is built.
- **Not possible:** DTS and DTS-HD, Dolby TrueHD and MLP. The pages of Microsoft Learn read for this design list no decoder for them, and no Apple documentation read lists one in Core Audio. That is weak evidence: the same Learn list ("Supported Media Formats in Media Foundation", last dated 2018) names no Dolby, FLAC or ALAC decoder either. Only the probe's inventory of every registered decoder on every Windows and macOS image settles it (open question 1); FFmpeg stays their reference until then.
- **Out:** see [Out of scope](#out-of-scope).

The analyzer has no lossy decoder yet, so a cross-check compares the analyzer's parsed figures (counts, durations, priming and padding, which frames it calls damaged) with the decoders' output lengths and error behavior. No PCM is compared.

## The design in short

- One record format and one comparison script for every decoder and platform. Each file gets one JSON record per decoder, written by a child process of its own with a timeout, so a crash or a stall is recorded and the run goes on.
- Two paths per decoder where the decoder allows it, as in the WMA check, named `reader` and `direct` in the records:
  - **the reader path:** the owner's own file-level program or API (`opusdec`, `oggdec`, `tta -d`, Core Audio's `ExtAudioFile`, Media Foundation's Source Reader), which also applies the owner's trimming;
  - **the direct path:** the decoder itself, fed one packet, frame or access unit at a time, in file order, so that damaged data reaches the decoder and every unit has a result. Where a page or unit cannot reach it (libogg drops pages with a bad CRC), the record says so.
- Each decoder figure is compared with the analyzer's figure at the same stage: a count before trimming with the analyzer's coded total, a count after trimming with its trimmed total. The harness computes no trimming of its own.
- The reference follows a fixed rule per format and stage: the reader path's total only where it reached the end of the stream without an error, a stall or a stop; else the direct path's total; never a partial total.
- The decoders run on GitHub's runners: Linux in the cross-check jobs' image (`ubuntu-26.04`), macOS on `macos-15`, Windows on `windows-2025`. The comparison always runs on Linux, with the FFmpeg that `tool_versions.py` pins, so every platform's FFmpeg column comes from one version.
- The reference figures and the known differences are committed per fixture folder, so CI can hold the analyzer to them without a macOS or Windows runner.

## What the analyzer reports that a decoder can confirm

The figures below are read from the committed goldens; the names are the report's.

### Opus

- Stream: `samples` and `duration` after the pre-skip and the end trimming (72,000 samples for `external-opusenc-96.opus`).
- Nodes: "Pre-skip" (312), "Decoded Samples" (72,960: what the packets' TOC bytes give, before trimming), "End Trimming" (648), "Last Granule Position" (72,312), "Samples".
- Damage: the packet rules of RFC 6716 (`opus.packet-r1` to `-r7`, `opus.self-delimiting`), the granule cases (`opus.start-offset`, `opus.start-negative`, `opus.end-trimming-early`, `opus.end-trimming-large`, `opus.pre-skip-beyond`, `opus.granule-beyond-packets`), and Ogg's page findings (CRC, gaps, missing beginning- or end-of-stream pages).
- Today: `crosscheck_ogg_fixtures.py` already requires `opusdec`'s written samples to equal the manifest's on the fixtures oggz validates; mapping families 2, 3 and 255 are skipped (libopusfile 0.12 refuses them), and the four damaged Opus fixtures are not compared with any decoder.
- No committed Opus fixture has page damage: the Ogg page-damage fixtures (`bad-crc`, `sequence-gap`, `truncated-page`, `missing-eos`, `bos-missing`) are Speex or of no known codec. Page findings for Opus therefore come only from the derived variants (see [Fixtures](#fixtures)).

### Vorbis

- Stream: `samples` and `duration` after trimming (66,150 for `external-oggenc-q5.ogg`).
- Nodes: "Decoded Samples" (67,136: the overlap of the blocks), "Start Trimming", "End Trimming" (986), "Last Granule Position"; the start trimming is capped at the first two blocks and the end trimming at the last block, and more is a finding.
- Damage: setup and identification header findings, packet errors, granule errors, Ogg's page findings.
- Today: `oggdec` must write the manifest's samples on the valid fixtures; `vorbis-packet-errors.ogg` skips it because `oggdec` writes 3,072 samples fewer, 1,024 for each of its three packets that code no block (1,024 is the overlap share of a long block of 2,048, `Blocksize 1`). That is the first known difference this design hands to the project lead.
- Not compared with any decoder: `vorbis-header-layout`, `vorbis-ident-invalid`, `vorbis-setup-invalid`, `vorbis-setup-truncated`, `vorbis-vendor-variants` and `vorbis-packet-errors` (all `.ogg`). `vorbis-granule-errors.ogg` is damaged but valid for oggz, so `oggdec` is compared on it already.
- As for Opus, no committed Vorbis fixture has page damage.

### TTA

- Stream: `samples` (the samples of the frames read whole) and `duration`; no priming or padding.
- Damage: `tta.header-crc-mismatch`, `tta.seek-table-crc-mismatch`, `tta.frame-crc-mismatch`, `tta.frame-cut`, `tta.frames-missing`, `tta.frame-too-small`, the header field findings, `tta.audio-format-encrypted`.
- Today: `tta -d` decodes only the encoder-written files, inside the generator; ffmpeg is the cross-check's decoder. The 41 hand-built files hold bytes in place of coded samples, so a decoder's output samples mean nothing there, but its count, CRC verdicts and refusals do.

### AAC

- Stream: `samples` and `duration` count every access unit at the output rate: 1,024 per unit, 2,048 with SBR (24,576 for `apple-lc-m4a-cbr-st44.m4a`, which holds 22,050 samples of audio). `StreamInfo` carries no priming field.
- MP4 gapless nodes live under the track's "Gapless" node, one set per source:
  - "Encoder Delay (iTunSMPB)", "Samples (iTunSMPB)", "Padding (iTunSMPB)";
  - "Encoder Delay (Edit List)", "Samples (Edit List)", "Padding (Edit List)", "Media Time", "Segment Duration", "Roll Distance".
- The words "Encoder Delay" and "Padding" alone are the words of the iTunSMPB item's value in the metadata (`meta / iTunes Metadata / iTunSMPB / data / Value`), not stream-level nodes.
- A file with only an edit list has no iTunSMPB figures: `ffmpeg-lc.m4a` has `samples` 67,584 and "Samples (Edit List)" 66,150, and its "Padding (Edit List)" is 0, not 410.
- Which rate iTunSMPB counts at is not settled. [aac.md](../formats/aac.md) says the core rate, which is the media timescale (`fdkaac-he.m4a`: 2,048 + 33,075 + 717 = 35,840, half of the stream's 71,680); the analyzer's own comment in `Mp4Parser.Metadata.cs` says "at the output sample rate". Both cannot hold (open question 10; question 2 for the project lead).
- Findings on gapless data: an edit list and iTunSMPB that disagree, an iTunSMPB whose total is not the track's duration.
- Damage: ADTS CRC failures, cut and truncated frames, `aac.blocks-missing`, resync gaps, MP4 sample table findings (`mp4.samples-overlap`, samples outside the file), and the SBR and parametric stereo rules (`aac.sbr-data-sparse`, `aac.ps-without-mono-core`, `aac.sbr-without-channel-element`), which decide whether the output rate doubles.
- Today: ffmpeg must decode every access unit without an error, faad prints the configuration and the ADTS duration, and faad2's trace confirms every raw data block's syntax. No decoder's output length is compared.

### AC-3 and E-AC-3

- Stream: `samples` and `duration` from the frames: 1,536 per AC-3 frame, 256 per E-AC-3 audio block; one stream per configuration (`stream-changes.ac3`), and the independent substreams of one file in one playback group.
- In MP4 they carry edit lists: `external-ffmpeg-mp4-5.1-448k.mp4` and `external-ffmpeg-mp4-eac3-stereo-96k.mp4` have "Media Time" 256, "Samples (Edit List)" 23,712 and "Padding (Edit List)" 32, against a `samples` of 24,576.
- Damage: `ac3.crc1-mismatch`, `ac3.crc2-mismatch`, `ac3.frame-truncated`, `ac3.block-overrun`, `ac3.dependent-excess`, `ac3.streams-limited`; frames that break are still counted (`crc-damaged.ac3`: 12,288 samples with both CRC findings).
- Today: ffmpeg decodes each packet alone and must refuse exactly the damaged frames, and refuses every independent substream but 0; liba52 confirms the block ends. No decoder's output length is compared.

## The decoders and how each is driven

A cell marked (probe) holds a fact the first run must confirm. Facts from public documentation are named with their source in the bullets below the table.

| Format | Decoder | Runner | Reader path | Direct path |
| --- | --- | --- | --- | --- |
| Opus | libopus, libopusfile | `ubuntu-26.04` | `opusdec --rate 48000` (opus-tools 0.2) | harness: libogg pages, `opus_multistream_decode` per packet; the projection decoder for family 3 |
| Vorbis | libvorbis, libvorbisfile | `ubuntu-26.04` | `oggdec -R` (vorbis-tools 1.4.3) | harness: libogg pages, `vorbis_synthesis` and `vorbis_synthesis_pcmout` per packet, each packet with `granulepos` -1 and `e_o_s` 0 (see [Priming and padding](#priming-and-padding)) |
| TTA | libtta-c 2.3 | `ubuntu-26.04` | `tta -d` | none (see decision 6) |
| AAC | FDK AAC 2.0.2 | `ubuntu-26.04` | none | harness: `aacDecoder_Fill` and `aacDecoder_DecodeFrame`; one ADTS frame per call, `TT_MP4_ADIF` for ADIF, raw access units with a configuration for MP4 |
| AAC | Core Audio | `macos-15` | `ExtAudioFile` on the file | `AudioConverter` fed the access units with their magic cookie; prime method set and recorded (probe) |
| AAC | Media Foundation AAC decoder | `windows-2025` | Source Reader (MPEG-4 File Source, ADTS File Source) | the AAC decoder MFT fed one access unit per sample, with and without timestamps |
| AC-3, E-AC-3 | Core Audio | `macos-15` | `ExtAudioFile` (probe: which file types open) | `AudioConverter` with `kAudioFormatAC3` or `kAudioFormatEnhancedAC3`, fed one access unit per call (probe) |
| AC-3, E-AC-3 | Media Foundation Dolby audio decoder | only where the probe finds it | Source Reader | the MFT, unlocked for field of use (probe) |

### The unit lists

The direct paths of AAC and AC-3 do not each carry a reader of their own. Three readers (C for FDK, C for `AudioConverter`, C# for Media Foundation) would split damaged MP4 and ADTS data differently, and the differences found would be differences between the readers, not between the decoders.

- One extractor, a Python script that is not the analyzer, writes a unit list per file: the file's SHA-256, per unit the file offset, length and cut ranges, and the configuration (the AudioSpecificConfig, or `dac3` or `dec3`). Every direct path of every platform consumes that list. Every job runs the same script (the same Python version, recorded) on its own copy of the files, and the lists of the derived variants are made the same way; since a list depends only on the bytes, records are matched by SHA-256.
- It reads ADTS (frame by frame, header lengths), MP4 (`stsz`, `stco`, `stsc`) and fragmented MP4 (`moof`, `trun`). A file it cannot split is skipped and listed as skipped: ADIF has no frame lengths and is the one exception, handled by FDK's own `TT_MP4_ADIF` transport, matched by index for that file and said so in its record. ADIF gets no unit list, so the Core Audio and Media Foundation direct paths skip `adif.aac` (Media Foundation documents no ADIF support).
- An E-AC-3 unit for `AudioConverter` and the Dolby transform is the access unit as MP4 stores it (the independent frame and its dependent frames), not each frame alone.
- Fragmented files (`fragmented.mp4`, Fraunhofer's `Sintel_*_rap5s.mp4` in `usac-samples.json`) are in scope through `moof` and `trun`; if the extractor does not manage one, the file is listed as skipped.
- Raw ADTS is fed to FDK one frame per `aacDecoder_Fill`, so the library still checks the header and CRC and the harness knows each unit's offset. The bytes the extractor skips (junk, resync gaps: `junk-around.aac`, `damaged-header.aac`) are fed too, with the next unit and recorded as skipped ranges, so that FDK's own resync is exercised. The alternative, `numTotalBytes` and `numBadBytes` of `CStreamInfo` after every `aacDecoder_DecodeFrame`, is confirmed or discarded by the probe on `junk-around.aac` and `truncated.aac` (open question 5).

### Linux

- **libopus and libvorbis** come from Ubuntu 26.04's packages, as `opusdec` and `oggdec` do now. `tool_versions.py` asserts `opus-tools` 0.2 and `ogginfo` 1.4.3 already; the libraries (`libopus0`, `libopusfile0`, `libvorbis0a`, `libvorbisfile3`, `libogg0`) and the development packages the harness is built against (`libogg-dev`, `libopus-dev`, `libvorbis-dev`) join `PACKAGES` with the versions the probe records and `tool_versions.py` then asserts.
- **The Ogg harness** is a C program in `scripts/fixtures/` built with gcc against those headers (libogg, libopus, libvorbis: BSD licenses).
  - libogg drops a page that fails its CRC, so a damaged page never reaches the decoder. That is what Xiph's own tools see, but it differs from the aim of the direct path, and wherever the analyzer still reads packets from such a page the harness makes the difference. The record says so for every such file.
  - The record names the dropped pages with byte ranges: `ogg_sync_pageseek`'s negative returns give the bytes skipped, and `ogg_stream_packetout`'s -1 marks a hole in the packets.
- **libtta-c's `tta -d`**, built by `libtta_tool.py` from the pinned archive. Its usage text is the only documentation used, as for the encoder.
- **FDK AAC:** `fdk_tool.py` already builds libfdk-aac 2.0.2 from its pinned commit, because Ubuntu's libfdk-aac2 lacks SBR and parametric stereo, and `fdk_crc.c` already decodes ADTS files with it to list the CRC errors. The harness grows from `fdk_crc.c`.
  - Per access unit it records the error code, the samples written, whether `IS_OUTPUT_VALID` holds, and `CStreamInfo` (sample rate, frame size, channels, object types, `outputDelay`).
  - Documented in the library's header `aacdecoder_lib.h`: `FDK_audio.h` defines the transports the harness uses (`TT_MP4_ADTS`, `TT_MP4_ADIF`, `TT_MP4_RAW`), and `aacdecoder_lib.h` documents `aacDecoder_ConfigRaw` for raw access units. The header also documents the frame sizes of USAC, LD and ELD, and the error codes for a Main predictor (0x4007), SSR gain control (0x400A) and a coupling channel element (`AAC_DEC_UNSUPPORTED_CCE`, 0x4008). The probe confirms them (open question 6). If FDK drops frames with coupling channel elements, as Media Foundation is documented to, both official decoders would skip frames the analyzer counts, and the reference would exclude them: a `pending` item.
  - LOAS is dropped from the harness: the analyzer has no LOAS parser and the repository has no LOAS fixture, so there is nothing to compare.
  - The FDK repository's own example program is not used. It first appears in v3.0.0 (`aac-dec.c`; it is not in v2.0.2 or v2.0.3), reads only ADTS and LOAS and stops at the first failed decode.
- **Concealment and the drain are fixed, because they change the direct total.**
  - Energy interpolation (method 2) "adds additional signal delay of one frame", as `aacdecoder_lib.h` says. The header also defines `AACDEC_FLUSH`, "Flush all filterbanks to get all delayed audio".
  - The library's default depends on the object type: it opens with method 2, energy interpolation (`CConcealment_InitCommonData`, `conceal.cpp`; called with no user method, "auto mode"), and reverts to 1 itself for AAC-LD and AAC-ELD when the user set none, and always for USAC (`aacdecoder_lib.cpp`). An explicit 2 on a USAC stream fails with `AAC_DEC_SET_PARAM_FAIL`.
  - The harness therefore sets method 1 (noise substitution) with `AAC_CONCEAL_METHOD` for every object type and records it. That is not the library's default for AAC-LC and HE-AAC; one method for all object types keeps the direct totals comparable, and method 1 adds no frame of delay. Probe question 6 compares methods 0, 1 and 2 where each is accepted.
  - There is no "off": method 0 (spectral muting) still outputs a frame for a damaged unit.
  - A unit counts as output only when `IS_OUTPUT_VALID` holds. The direct total is the samples of valid outputs during decoding; the frames that `AACDEC_FLUSH` drains are counted apart as `flushedSamples`, so the coded total stays "one frame per access unit". `outputDelay` is recorded.
- **Licenses.** libopus and libvorbis are BSD, libtta-c is LGPL, FDK is under Fraunhofer's own license. All are test tools built in CI, linked only into the harnesses, never into Encspect and never redistributed. FDK's license grants no patent license (see [Questions for the project lead](#questions-for-the-project-lead)).

### macOS

- A C program against AudioToolbox, compiled with `clang -framework AudioToolbox` on the runner, as `apple-aac-fixtures.yml` already runs `afconvert` there. C rather than a .NET probe: AudioToolbox is a C API, and a JSON Lines record written and flushed per unit needs no lock (decision 4).
- Documented: AudioToolbox has the format constants `kAudioFormatMPEG4AAC`, the HE-AAC variants, `kAudioFormatAC3` and `kAudioFormatEnhancedAC3`, and decodes through `AudioConverter`; `ExtAudioFile` reads a file through `AudioFile`. Second-hand: Apple's WWDC 2020 session 10158 is reported to document file-based xHE-AAC playback since macOS 10.15 (the review knew the session from a search summary, not from the session). The probe settles it (open question 3).
- `AudioConverter` and xHE-AAC: third-party reports (FFmpeg trac #9564) say the converter refuses `usac` unless the stream description is filled out in full. The harness builds it from the magic cookie with `kAudioFormatProperty_FormatInfo`, so a refusal by the harness's setup is not taken for the decoder's.
- Probe: which formats `kAudioFormatProperty_DecodeFormatIDs` lists on `macos-15`; which file types `kAudioFileGlobalInfo_ReadableTypes` lists (raw `.ac3` and `.ec3`, ADTS, M4A with `ac-3` and `ec-3` entries); and what `ExtAudioFile` trims.
- The `AudioConverter` path sets `kAudioConverterPrimeMethod` and records it and `kAudioConverterPrimeInfo`, so that the path is known to be at the coded stage.
- The decoders are Apple's. Apple does not document whose code its AC-3 and E-AC-3 decoders are; that they are implementations under Dolby's license is **unverified**. They are the closest to the owner's decoder a runner offers.
- `macos-15` is arm64. The probe runs `macos-15-intel` too; the matrix keeps it only if a count differs between them.

### Windows

- **The AAC decoder** (Learn, "AAC Decoder"; `MSAudDecMFT.dll`) is documented for servers from Windows Server 2008 R2, so `windows-2025` should have it. Documented limits, which become expected differences:
  - no Main, SSR or LTP (Learn writes SSR as "Sample-Rate Scalable (SRS)"; the project says SSR);
  - no ADIF; frames with coupling channel elements skipped; no 960-sample frames; at most 6 channels and 48 kHz; one raw data block per ADTS frame;
  - only AAC-LC, HE-AAC v1 and HE-AAC v2 are listed, so AAC-LD and AAC-ELD (`eld*.m4a`) are expected refusals;
  - LATM and LOAS are listed as unsupported while Learn also says Windows 8 added them; the harness has no LOAS case.
- **The media type** is the second source of refusals. Learn says the input type's AudioSpecificConfig "must be 2, indicating AAC-LC", with an extension audio object type of 5 or 29. Explicitly hierarchical HE-AAC (`he-aac-hierarchical.m4a`, `he-aac-v2-hierarchical.m4a`) has object type 5 or 29, so the direct path may refuse it because of how the harness builds the type, not because of the stream.
  - A refusal of the media type is recorded apart from a refusal of a unit.
  - An AudioSpecificConfig is never rewritten without the record saying so and holding both versions.
- **xHE-AAC on Windows:** Fraunhofer's blog says Windows 11 got xHE-AAC decoding in "the latest 2022 update". The date and the Windows version are **unverified**; the design cites no more than the blog says. The decoder is Fraunhofer's, licensed to Microsoft. That it is no independent witness to FDK for USAC is an inference (the same licensor; not shown to be the same code). Whether Windows Server 2025 has it is open (probe).
- **The Dolby audio decoder** (Learn, "Dolby Audio Decoder"; `Msauddecmft.dll`):
  - Learn calls it "the Microsoft implementation of the Dolby Digital technology", so it is Microsoft's code. Its licensing note on the same page concerns versions of Windows prior to Windows 8; that the decoder of later versions is also under Dolby's license is an inference.
  - It lists "Minimum supported server: None supported", which already excludes every Windows Server image. `windows-2025` is such an image.
  - The press reported in May 2024 that Windows 11 24H2 clean installs lack the inbox AC-3 codec (upgrades keep it, OEMs may preinstall it). That matters only for `windows-11-arm`, a client image (build 26200) whose install history is not known.
  - The AAC decoder and the Dolby decoder are documented in the same DLL, so the DLL being present proves nothing. The probe also tries `CoCreateInstance(CLSID_CMSDDPlusDecMFT)`, not only `MFTEnumEx`, on `windows-2022`, `windows-2025` and `windows-11-arm`.
  - Documented for where it exists: only independent substream 0 is decoded, dependent substreams are skipped, up to 7.1, and the MFT must be unlocked for field of use.
  - Installing Dolby's decoder from the Microsoft Store on a runner is not pursued (see [Questions for the project lead](#questions-for-the-project-lead)).
- **The probe project:** `scripts/fixtures/windows/WmaDecodeProbe` already has the child processes, the stall budget, the version record and the locks. It grows an MFT path (`IMFTransform::ProcessInput`, `ProcessOutput`, drain) that consumes the unit lists, and a reader per format (Source Reader), and is renamed once the WMA records it writes are unchanged on the same image (decision 3).

## What is compared

Stage names: the **coded** stage is the sum of what the stream's units code, before any trimming; the **trimmed** stage is what remains after the owner's priming and padding rules.

- **Totals, per run of one output format.** A record holds one total per run of constant output format (rate, channels), in file order, and the comparison sets each against the analyzer's stream for that configuration. AC-3 reports one stream per configuration, and SBR that comes and goes changes the output rate in AAC; a single sum across formats would mix them.
- **Totals, stage by stage:**
  - Opus and Vorbis: reader-path totals with the stream's `samples` (trimmed); direct-path totals with "Decoded Samples" (coded);
  - `tta -d` with `samples`;
  - AAC and AC-3 in MP4, direct paths: totals with `samples` (coded).
  - AAC and AC-3 in MP4, readers: a reader's total with the analyzer's own figure of the source that reader is seen to apply, "Samples (Edit List)" or "Samples (iTunSMPB)", each source compared separately.
  - Where the source's rate differs from the decoder's output rate, the only arithmetic the script adds is a ratio of rates, printed. For the edit list it is the media timescale against the output rate, from the report. For iTunSMPB the rate is open (open question 10): until it is settled, iTunSMPB figures are compared only on files without SBR, where the core rate equals the output rate, or are printed under both candidate rates and marked unsettled.
  - raw ADTS and raw AC-3: coded stage only, and the readers' totals with `samples`; a reader that applies a priming of its own is recorded (open question 4);
  - AC-3 and E-AC-3 totals per independent substream where the decoder reports one.
- **Edit-list figures are compared exactly.** A reader that applies the edit list reads the same rounded segment duration the analyzer does, so its trimmed count should equal "Samples (Edit List)". The only slack is the rounding of a timescale conversion: when segment duration x rate / movie timescale is not a whole number, one sample of difference is allowed, and the comparison prints the exact fraction.
  - The segment duration is in the movie timescale (1,000 for `external-ffmpeg-mp4-5.1-448k.mp4`: "494 (0.494 s)"), which is why the analyzer's figure is a rounded one; that is an explanation, not a tolerance.
  - A wider tolerance would hide what the probe is meant to find. In that file the `mdhd` duration is 24,000 at 48,000 Hz; a reader that applies the media time (256) but plays to the end of the media gives 23,744, which is 32 more than "Samples (Edit List)" (23,712) and would pass a one-tick tolerance of 48.
  - The same holds for AAC edit lists.
- **Units:** per packet, frame or access unit, the decoder's result (decoded, refused with its code, concealed, nothing output) beside the analyzer's reading of the unit at the same bytes (a finding that points to it, or none). The match is by file offset and length, never by index, since the two readers may split damaged data differently; the one exception is ADIF, matched by index (see [The unit lists](#the-unit-lists)).
- **Error behavior:** where the reader stops (unit and offset), whether the decoder outputs samples for a unit it reports as damaged (concealment), whether output resumes after damage, and the samples a unit gives against the samples its header states.
- **The FFmpeg column:** ffmpeg 8.0.1's count at the same stage, and its errors. When ffmpeg exits with an error its partial count is recorded as an error and is never a figure.
  - MP4: `-ignore_editlist 1` for the coded count, its default for the trimmed one.
  - Ogg: ffmpeg applies Opus pre-skip and end trimming by itself. The coded stage is the sum of ffprobe's packet durations, which `crosscheck_ogg_fixtures.py` already compares; `-flags2 skip_manual` is the alternative, confirmed or discarded by the probe (open question 4).
  - Raw ADTS and raw AC-3 have no trimming in ffmpeg as far as this design knows (**unverified**); the coded and trimmed stage are then the same count, and the table says so.

## The reference rule

- **General:**
  - the reader path's total counts only where the reader reached the end of the stream without a failed read, a stop condition (below), a stall, a crash or a stop by the harness;
  - else the direct path's total, if that path fed every unit and drained without a stall, crash or exception;
  - else there is no reference. A partial total is recorded as `partialSamples` and is never a figure.
- **What stops a reader** is defined per tool, from the probe's findings (open question 7), and listed here once the probe has run. The candidates are a nonzero exit status and the standard error lines of `opusdec`, `oggdec` and `tta -d`, which print warnings and keep going. Warnings are recorded in a field of their own, apart from `stoppedBy`.
- **Counting:** a reader's samples are counted from the PCM bytes it actually wrote (`oggdec -R` raw output; raw output of `opusdec` and `tta -d` where they offer it, probe). They are never taken from a WAV header's data size: `opusdec` and `tta -d` may write a header before decoding, a killed child leaves it unpatched, and a cut TTA file may get the declared size.
- A reader total and a direct total can be at different stages (Opus, Vorbis, AAC), so falling back to the direct path gives a reference for the coded total only; the trimmed total then has none.
- **No reference:** a file whose payloads are encrypted with a key the project does not have: the MP4 `cenc-*`, `cbcs-*`, `adrm-*` and `audible-*` files. TTA is not in that group: `external-libtta-16-stereo-password.tta` was written with the test password "secret" (`make_external_tta_fixtures.py`), and `crosscheck_tta_fixtures.py` already decodes it with it, so `tta -dp secret` gives its reference.
- **Per format, per stage, in order of precedence:**
  - Opus: `opusdec`, then libopus direct (coded only).
  - Vorbis: `oggdec`, then libvorbis direct (coded only).
  - TTA: `tta -d`. TTA has no direct path (decision 6), so on a damaged file where `tta -d` stops there is no official reference and FFmpeg stays the reference, as for refused AAC object types.
  - AAC, coded stage: FDK direct, then `AudioConverter`, then the Media Foundation transform. FDK comes first because it runs on Linux from pinned source, so its figures are reproducible and can join the CI gate later, and Fraunhofer IIS is one of AAC's developers.
  - AAC, trimmed stage: `ExtAudioFile`, then the Source Reader. FDK has no file reader.
  - Where FDK refuses an object type, the next decoder that decodes it is the reference; where none does, FFmpeg stays the reference and the file is marked so.
  - AC-3 and E-AC-3, coded stage: `AudioConverter`, then the Dolby transform if found; trimmed stage (MP4): `ExtAudioFile`, then the Source Reader. If no decoder handles a file, FFmpeg and liba52 stay the reference.
- **Disagreement between official decoders** (FDK against Core Audio, say) is listed per file. The rule still picks the reference, and the file is marked as one where the owners' decoders disagree.
- **Builds:** a figure seen on one OS build is the reference, with the build recorded in the record. When a later run on another build gives another figure for the same file bytes, the figure gets the status `build-dependent` and is listed for the project lead. (The WMA check saw the Source Reader's count of damaged Voice files change between two builds of Windows Server 2025; those were partial counts, which the completeness rule excludes anyway.)

## Priming and padding

Each decoder path declares what it outputs, and the probe confirms it on one file per case (open question 4).

- **`opusdec` and `oggdec`** trim what the granule positions call for (RFC 7845 defines it for Opus): on the valid fixtures they already write the analyzer's trimmed `samples`; a start offset and damaged granule positions are to be confirmed.
- **libopus fed directly** trims nothing: it returns every packet's samples.
- **libvorbis fed directly** trims only if it is given granule positions.
  - `vorbis_synthesis()` copies the packet's `granulepos` and `e_o_s` into the block (`synthesis.c`), and `vorbis_synthesis_blockin()` (`block.c`) cuts the start when the first granule position is smaller than the samples counted, and the end when the packet carries `e_o_s`. Packets from `ogg_stream_packetout` carry the page's granule position on the last packet of each page and the end-of-stream flag, so feeding them unchanged gives the trimmed count, not "Decoded Samples" (67,136 coded and 66,150 trimmed for `external-oggenc-q5.ogg`).
  - The direct path therefore sets `granulepos` to -1 and `e_o_s` to 0 on every packet before `vorbis_synthesis`, so that libvorbis computes nothing from granule positions, and the record says so.
  - A third run with the packets' real granule positions, recorded at the trimmed stage, would show libvorbis's own granule handling as a further witness; it is put to the project lead.
- **FDK fed directly** outputs one frame per access unit; its `outputDelay` is recorded, not subtracted (see [Linux](#linux) for the concealment delay and the drain).
- **Core Audio:** whether `ExtAudioFile` trims iTunSMPB, the edit list or both, and what `kAudioConverterPrimeInfo` reports for the `AudioConverter` (probe). The same question holds for AC-3 and E-AC-3 in MP4, which carry edit lists (see the AC-3 section above): if a reader applies them, every MP4 AC-3 file would otherwise show a difference of 864 samples that the harness made.
- **Media Foundation:** whether the MPEG-4 File Source applies the edit list or iTunSMPB (probe); the MFT fed directly trims nothing as far as documented.
- A decoder whose trimming is not understood from its records is compared only at the coded stage until it is.

## Harness rules

The lessons of the WMA check's reviews (W-1 to W-22, R2-1 to R2-9), as rules for every harness:

- **No padding:** a cut unit is fed from its first byte up to the first byte not received and never zero-filled to its declared size; the record holds the declared size, the bytes received and the missing ranges. In the WMA check a zero-filled cut object gave 300 samples more per channel (11,060 against 10,760).
- **File order:** units are fed in the order their first byte appears in the file; an incomplete unit is closed when the next unit of its stream begins and at the end of the data, never at the end of the file out of order.
- **Memory bounds:** buffers grow by the bytes received, geometrically, within a cap per unit (16 MiB) and a cap on units held at once and their total; a unit is released once fed, and a cap that is hit is recorded. A declared size (an MP4 `stsz` entry, a seek table entry) never allocates by itself.
- **No exception from header fields:** a short format block or a size times channels never overflows or throws, so one bad field never loses the whole record (W-17).
- **A refused first call is not a failing unit:** a unit whose first input call was refused and whose retry succeeded does not count as failing (W-10).
- **Bounded recursion** in every box or element walk.
- **One child process per file and path**, with a timeout (30 s), so a crash is recorded with its exit code or signal. On Windows the child runs the decoder on a worker thread and records a stall (inside which call, since when) before its own budget ends; on Linux and macOS the parent records a timeout and the child's last flushed unit.
- **Published records:** in the C harnesses each unit's record is one JSON line written and flushed before the next unit, and the parent assembles the file's record, so a killed child leaves what it did and nothing is shared between threads. In the Windows probe every change to an object the serializer reads stays under one lock, as R2-1 required.
- **Stops are kept:** the reason a loop stopped (end, error, stall, a call limit, a timestamp that does not advance) is never overwritten by a later one, and limits are exact (1,000 reads at one timestamp, not 1,001).
- **The control run without timestamps** of the WMA check (`directWithoutTimestamps`) stays for the Media Foundation transform path, since that transform may also use input timestamps; the Linux and macOS paths have no timestamps to vary.
- **No dead code:** no child mode and no code path that nothing runs (R2-8).
- **Counts in the docs are the real ones:** the docs, comments and step names state the number of paths and runs that exist (W-19, R2-9).
- **Resources freed:** media types, buffers and decoder handles are released on every path, and Media Foundation is shut down.
- **Versions pinned and recorded:** `environment.json` is written before the first file and again at the end: the runner image (`ImageOS`, `ImageVersion`), the OS build, the decoder libraries' file versions (Windows), the AudioToolbox and AudioCodecs bundle versions (macOS), the packages' versions from dpkg and the FDK commit (Linux), the ffmpeg version, and the modules each child loaded. Runner labels are pinned, never `-latest`: `ubuntu-26.04`, `macos-15`, `windows-2025` for the runs, and for the probe also `windows-2022`, `windows-11-arm` and `macos-15-intel`.
- **Artifacts survive a timeout:** the decode step's timeout is shorter than the job's, and the upload runs with `if: always()`.
- **Figures are never truncated** in the comparison's text, codes are printed in hex, and streams that are not the format under test are counted apart.
- **Docs say only what exists:** a rule that is decided but not yet followed by the analyzer is worded as the decision for the future (W-5).

## Records and the comparison

- **Record:** `records/<format>/<file>.json`, one per file: the file's path, size and SHA-256, and per path (`reader`, `direct`) its result:
  - `opened` or the failure to open with its code, with a refusal of the media type apart from a refusal of a unit;
  - the output formats it reports (rate, channels, layout), and one total per run of one output format;
  - `units`: per unit the file ranges, the bytes declared and received, the result code, the samples output and any flag the decoder sets (concealed, discontinuity);
  - `stoppedBy` (`end`, `error`, `stall`, `timeout`, `crash`, `limit`), `totalSamples` only for `end`, `partialSamples` otherwise;
  - `warnings`: the decoder's own messages (standard error of `opusdec`, `oggdec`, `tta`), apart from `stoppedBy`.
- **Comparison:** `scripts/fixtures/compare_official_decoders.py <artifact folders>` runs on Linux. It reads the analyzer's figures from the committed goldens (or runs the CLI with `--encspect`, which the damage variants and the `apple` folder need), runs ffmpeg 8.0.1 for its column, matches records by SHA-256, and writes:
  - `comparison.txt`: one table per format with one row per file: the analyzer's figure and its stage, the reference figure and which path gave it, ffmpeg's, every other decoder's, and notes (reader stopped at unit n, units refused, concealed units, encrypted, no reference); then the units where a decoder's verdict differs from the analyzer's; then counts of agreement for each pair, per format. Every selected file without a record is listed as **not run**, so a step that ran out of time is never read as "no reference";
  - `comparison.json`: the same, for the check below.
- **Committed results:** after a run is reviewed, the reference figures and the differences go into `tests/fixtures/<format>/official-decoders.json`: per fixture (path and SHA-256), each decoder's figure and stage, the decoder's version, the runner image and the run ID, and each difference with its status (see below).
  - **The check** is a test in `Encspect.Tests`, next to the golden tests of the format (`OggFixtureTests`, `Mp4FixtureTests`, `Ac3FixtureTests`, `TtaFixtureTests`), so it runs whenever a golden changes, not only when a cross-check job does. The cross-check jobs run only on path changes, and AC-3's folder is not in the job list of the gate.
  - **What it compares** are the figures the reference speaks to: the stream's `samples`, "Decoded Samples" (Opus, Vorbis), "Samples (Edit List)" and "Samples (iTunSMPB)", and the unit findings; not the whole golden. A figure that equals the reference passes, one that differs passes only as a listed difference.
  - **Stale entries:** an entry whose SHA-256 no longer matches the fixture is stale. Failing it would mean that every regenerated fixture in these folders needs a macOS and Windows run before its pull request can pass; a status `stale` that is listed and does not fail is the alternative (question 5 for the project lead).
  - `tests/fixtures/apple` has no goldens, so nothing in `Encspect.Tests` reads its entries; those files stay in the run's artifact and in the docs.
  - This keeps the macOS and Windows results in CI without their runners (decision 7).
- **In the docs:** each format page gets a section "Reference for sample counts", as [asf-wma.md](../formats/asf-wma.md) has, and [development.md](../development.md) a section per decoder with "Differences found". The docs cite `official-decoders.json` and the run ID, not an artifact, since artifacts expire (see [Workflows](#workflows)).

## Differences and decisions

- Every difference is listed, never dropped. Each gets one of five statuses in `official-decoders.json` (plus `build-dependent` and, if the lead agrees, `stale`, above):
  - `explained`: the analyzer equals the reference and FFmpeg differs, or a decoder documents the behavior (Media Foundation skips frames with coupling channel elements); nothing changes;
  - `noted`: a difference that changes nothing in the analyzer, for example a decoder that conceals a unit the analyzer reports as damaged, or a unit verdict where the analyzer reports no figure;
  - `pending`: the analyzer differs from the reference in a way that would change counts, findings or goldens;
  - `follow`: the project lead decided the analyzer follows the reference; the change is its own pull request with its goldens (and a `golden-changes.json` where the change can be stated);
  - `keep`: the project lead decided the analyzer keeps its figure, with the reason.
- **A pending difference goes to the project lead** as one item: the files, the figures of every column, the stage, the decoder versions and run IDs, whether the official decoders agree among themselves, what would change in the analyzer (count, finding, golden) and the rule the change would follow. Items are collected per format in the pull request that commits the format's `official-decoders.json`, so the lead decides them together.
- Until the lead decides, the analyzer keeps its current figures, and the docs say so. For Ogg and TTA these already follow RFC 7845, the Vorbis I granule rules and the TTA format description and are held to `opusdec` and `oggdec` on the valid fixtures; only for the formats where FFmpeg was the reference is it FFmpeg's behavior that stays.

## Fixtures

- **Undamaged:** every encoder-written fixture of the formats in scope: `tests/fixtures/ogg` (`external-*`), `tta` (`external-*`), `aac`, `adts`, `apple` and the encoder-written files of `mp4`, `ac3` (`external-*`, the `derived-*` files). The files in `tests/fixtures/apple` have no goldens, so the comparison takes their figures from the CLI.
- **Hand-built:** files whose payloads are not coded audio (the TTA `built-*` frames, synthetic AAC blocks) are run too: their counts, verdicts and refusals are compared and their samples ignored.
- **Damaged, committed:** the damaged hand-built fixtures of those folders (for example `opus-packets-corrupt.opus`, `vorbis-packet-errors.ogg`, `crc-damaged.ac3`, `cut-frame.ac3`, `truncated.aac`, `crc-stereo-damaged.aac`, `sample-short.mp4`, `truncated.m4a`, `built-frame-crc-fails.tta`, `built-cut-mid-frame.tta`).
- **Damaged, derived in the run:**
  - a script writes fixed variants of each undamaged encoder-written file, by rules and without randomness: cut at the middle of the first, a middle and the last unit; one byte changed in a middle unit's payload; one unit or Ogg page removed; one unit repeated;
  - every job writes them with the same script, records each variant's SHA-256, and the comparison matches records by SHA-256, so every platform decodes the same bytes;
  - the variants are not committed; the comparison runs the CLI on them. A variant that shows a behavior worth keeping becomes a committed fixture with its golden, after the lead's decision on it.
- **Downloaded:** the samples the repository already pins by SHA-256 and never commits (fetched through `downloads.py`):
  - `tests/conformance/usac-samples.json`: xHE-AAC from Fraunhofer, the ISO/IEC 23003-3 conformance streams, one exhale file;
  - `sbr-samples.json`: SBR and parametric stereo conformance streams, the aacPlus decoder check streams (FATE's);
  - `eac3-samples.json`: Dolby's JOC test files and three E-AC-3 files of FFmpeg's sample archive (samples.ffmpeg.org, not FATE).
- Nothing comes from rarewares.org; the one committed fixture first written by a rarewares build (`external-oggenc2-aotuv-rarewares.ogg`) is used as committed.
- **No media in artifacts or caches.** Artifacts and caches hold records only. The Dolby Online Delivery Kit files and the FFmpeg archive files are covered by "None may be redistributed here" (`eac3-samples.json`), and an artifact is a copy.
- **New committed fixtures:** none planned before the probe. Candidates the probe may call for: an E-AC-3 file with two independent substreams in MP4 (for Core Audio), an Ogg Opus or Vorbis file with a page lost in the middle (concealment; none exists), an ADTS file with a coupling channel element written by an encoder rather than by hand.

## Workflows

- **`.github/workflows/official-decode.yml`**, one workflow with a job per platform and a final comparison job:
  - `linux` (`ubuntu-26.04`): builds FDK (cached as the faad2 build is) and the Ogg harness, builds `tta` with `libtta_tool.py`, runs every path over the selected fixtures;
  - `macos` (`macos-15`): builds the AudioToolbox harness and runs it;
  - `windows` (`windows-2025`): builds the probe and runs its AAC path (and the Dolby path where found);
  - `compare` (`ubuntu-26.04`, `needs` all three, `if: always()`): downloads their artifacts, builds the CLI for the derived variants, runs `compare_official_decoders.py`, and uploads `comparison.txt` and `comparison.json`.
- **Triggers:** manual dispatch, with inputs for the formats and a file of extra paths, and a push to the branch `ci/official-decode`, as the WMA workflow is run. No schedule and no path filter: the runs are read by a person, and a difference does not fail them. It is not part of `ci.yml`.
- **When to run it:** after a change to a parser in scope, to its fixtures or to the harness, and when a pinned runner label moves to a new OS build.
- **Fixture counts** (files other than reports and goldens): `aac` 53, `adts` 57, `apple` 31, `mp4` 187 (not all of them AAC), together about 330 files in the AAC folders; `ac3` 78; `ogg` 118; `tta` 67.
- **Derived variants:** the rules give six per file (three cuts, one changed byte, one unit removed, one unit repeated), so the AAC folders would become about 2,300 files. Where they run:
  - all variants run on Linux only (FDK, Ogg harness, `tta`);
  - macOS and Windows run the committed files and the variants of a fixed list of at most 20 undamaged files per format (one per encoder and object type), kept in the script.
- **Time limits:** 60 minutes per job; the decode step 45 minutes; 30 seconds per child.
  - Worst case, every child at its timeout: the committed AAC files at three runs on Windows are about 495 minutes, at two paths on macOS about 330; real runs take seconds per file, but a stall budget must still fit.
  - The jobs are a matrix by folder and format, in shards of at most 40 files for two paths (macOS), 26 for three runs (Windows) and 80 for one path (Linux FDK).
  - With the subset: about 450 AAC files on Windows are 18 shards, on macOS 12 shards, and the Linux FDK variants about 29. macOS runners have limited concurrency on GitHub-hosted plans (**unverified** for this repository's plan), so the macOS matrix may queue.
  - The comparison lists every selected file without a record as **not run** (see above).
- **Artifacts:** `official-decode-<platform>` per job and `official-decode-comparison`, kept 30 days (the WMA check keeps its artifacts 7 days), so a review can read a run that is a few weeks old.
  - They hold records, never media.
  - They expire. The lasting record is `official-decoders.json`, which the docs cite with the run ID; a review may also attach `comparison.txt` to the pull request.
- **The CI gate:** the Linux paths of decided figures join the existing cross-check jobs (`ogg-`, `tta-`, `mp4-crosscheck`), which already run `opusdec` and `oggdec` on the valid Ogg fixtures; the macOS and Windows figures reach CI only through `official-decoders.json`. `CiWorkflowTests` keeps every `ci.yml` job on `ubuntu-26.04`.

## Open questions and the probe

The first run of the workflow is a probe (rollout step 1): each job prints an inventory and decodes one or two files per case, uploads the records, and builds nothing else. Each question names what settles it.

1. **Which audio decoders each image has** (settles the DTS, TrueHD, MLP, AC-3 and xHE-AAC claims): on `windows-2022`, `windows-2025` and `windows-11-arm`, `MFTEnumEx` over the audio decoder category with every flag (field of use included), `DMOEnum`, and `CoCreateInstance(CLSID_CMSDDPlusDecMFT)`, listing each decoder's CLSID, DLL, version and input subtypes; on `macos-15` and `macos-15-intel`, `kAudioFormatProperty_DecodeFormatIDs` and `kAudioFileGlobalInfo_ReadableTypes`.
2. **Core Audio and AC-3:** whether `ExtAudioFile` opens raw `.ac3`, raw `.ec3` and AC-3 or E-AC-3 in MP4, and whether `AudioConverter` decodes access units fed one at a time; how it handles independent substream 1 (`substreams.eac3`) and dependent substreams; and E-AC-3 frames of fewer than six blocks (`two-blocks.eac3`, `coding-eac3-blocks.eac3`, `reduced-rate.eac3`), since a fixed frames-per-packet in the stream description could misstate counts.
3. **Core Audio and xHE-AAC:** whether `usac` is decodable on `macos-15`, on `exhale.m4a` and one Fraunhofer sample, with the stream description built from the magic cookie.
4. **Trimming of each reader** (and the coded stage of ffmpeg's Ogg count):
   - `ExtAudioFile` and the Source Reader on `apple-lc-m4a-cbr-st44.m4a` (iTunSMPB only), `ffmpeg-lc.m4a` (edit list only), `fdkaac-lc.m4a` (both), `gapless-disagree.m4a` (which source a reader applies when the two disagree), `edit-list-itunsmpb.m4a`, `itunsmpb-only.m4a`, and an ADTS file, since the ADTS readers may assume a priming of their own;
   - the same readers on `external-ffmpeg-mp4-5.1-448k.mp4` and `external-ffmpeg-mp4-eac3-stereo-96k.mp4` (AC-3 and E-AC-3 in MP4 with edit lists);
   - the `AudioConverter` path with `kAudioConverterPrimeMethod` set, and its `kAudioConverterPrimeInfo`;
   - `oggdec` on `vorbis-start-trimming.ogg` and `vorbis-start-offset.ogg`, and `opusdec` on `opus-granule-edges.opus`;
   - libvorbis fed directly with `granulepos` -1 against the real granule positions, on `external-oggenc-q5.ogg` (67,136 coded, 66,150 trimmed) and `vorbis-start-trimming.ogg`;
   - ffmpeg's Opus coded stage: the sum of ffprobe's packet durations against `-flags2 skip_manual`.
5. **FDK's unit offsets:** one ADTS frame per `aacDecoder_Fill` against `numTotalBytes` and `numBadBytes` after every decode, on `junk-around.aac` and `truncated.aac`; `TT_MP4_ADIF` on `adts/adif.aac`.
6. **FDK's object types and versions:** 2.0.2 on an AAC Main, an LTP, an SSR (`ffmpeg-main.aac`, `ltp.aac`, `ssr-gain-control.aac`), an ELD and a USAC file, and on the coupling channel element files (`coupling.aac`, `faac-style-coupling.aac`, `sbr-after-coupling-element.aac`), against the header's documented error codes; and whether 2.0.3 and the latest tag, v3.0.0, give other figures on the whole corpus. v3.0.0's ChangeLog lists the new `fdk-xhe-aac` encoder library and upstream fixes. If they differ, the choice of the pinned version is justified in the design (the encoder's pin stays the decoder's only if they do not).
7. **Reader paths on damage:** whether `opusdec`, `oggdec` and `tta -d` stop at the first damaged unit or go on, what they print on standard error and what exit status they return, on the damaged Ogg and TTA fixtures; which output options give raw PCM. The result becomes the list of stop conditions in [The reference rule](#the-reference-rule).
8. **Media Foundation on Windows Server:** whether the AAC MFT and the ADTS File Source are present on `windows-2025` (Server editions need the Media Foundation feature, which the WMA check shows is there), and whether it has xHE-AAC.
9. **Intel against arm64:** whether any count differs between `macos-15` and `macos-15-intel`.
10. **The rate of iTunSMPB:** core rate or output rate, on `fdkaac-he.m4a` and `apple-he-m4a-cbr-st44.m4a`, from the readers' trimmed totals; whether the media time and segment duration of the edit list also follow the muxer's timescales. The result settles the contradiction between [aac.md](../formats/aac.md) and the parser's comment (question 2 for the project lead).
11. **The Media Foundation media type:** whether the AAC MFT accepts the hierarchical HE-AAC files (`he-aac-hierarchical.m4a`, `he-aac-v2-hierarchical.m4a`) with the AudioSpecificConfig as stored, and what it needs for them.

## Rollout

1. **Probe** (one pull request, the workflow with inventory and the cases above, on a branch; 2 to 3 days). Even one or two files per case need most of each harness: the AudioToolbox program with both paths, the Media Foundation transform path in C#, the FDK raw-unit path with the unit extractor, and the Ogg harness; the WMA probe needed two review rounds. Its findings are written into this design, which then goes to review and to the project lead. (If the lead prefers a shorter first step, the probe is limited to the inventory and the reader paths, and the direct-path cases move into steps 2 to 6.)
2. **Shared parts and Opus and Vorbis** (about 3 days): the record format, `compare_official_decoders.py` with the FFmpeg column, the derived damage variants, the Ogg harness, the Linux job, `official-decoders.json` for `tests/fixtures/ogg` and its test, the docs sections. First because the tools are already in CI, the runs are fast on Linux, damaged fixtures exist with no decoder comparison yet, and one difference (`vorbis-packet-errors.ogg`) is already known; it shakes out the shared parts where iterating is cheapest.
3. **TTA** (about 1 day): `tta -d` over all 67 fixtures and the derived variants. Small, and the tool is built already.
4. **AAC with FDK** (about 3 days): the unit extractor, the harness from `fdk_crc.c`, ADTS frame by frame and ADIF through the library; `aac`, `adts`, `apple`, `mp4` and the downloaded samples.
5. **Core Audio** (about 3 days): the macOS job, AAC first (the trimmed total), then AC-3 and E-AC-3 as far as the probe allows.
6. **Media Foundation AAC** (about 3 days): the MFT path and the AAC readers in the Windows probe, and its rename once the WMA records are unchanged.
7. **AC-3 and E-AC-3 on Windows**, only if the probe found the Dolby decoder on an image.
8. **Decisions:** each format's pending differences go to the project lead with the pull request that commits its `official-decoders.json`; the analyzer changes that follow are their own pull requests, each with its goldens.

Each step ends with an independent review of the harness and the comparison against this design, as the WMA check had. The effort figures are estimates (see the end of this document).

## Decisions

Proposed, for the project lead:

1. **Two paths per decoder, and the reader's total only when complete.** Reason: a reader stops at damage (the Source Reader on 14 Voice files), a direct path does not, and a partial total looked like agreement until W-4. Alternative: the reader path alone, which leaves damaged files without a reference.
2. **Compare like with like:** each decoder figure against the analyzer's figure at the same stage, with no trimming computed by the harness; libvorbis's granule handling is switched off in the direct path so that it stays at the coded stage. Reason: the harness would otherwise repeat the analyzer's trimming rules, and an error in them would hide on both sides. Alternative: trim the direct paths' output in the harness.
3. **Grow the Windows probe instead of writing a second one.** Reason: its stall handling, locks and version record took two review rounds to get right. Alternative: a separate AAC probe, which repeats that work.
4. **C harnesses with flushed JSON Lines on Linux and macOS.** Reason: the decoders' APIs are C, and a record that is flushed per unit and assembled by the parent survives a crash and needs no lock. Alternative: .NET probes with P/Invoke, as on Windows.
5. **FDK first for AAC's coded total, Core Audio's reader first for the trimmed total.** Reason: pinned source on Linux gives reproducible figures that can join the CI gate, and FDK has no file reader. The precedence for the trimmed total (`ExtAudioFile` first) is provisional until open question 4 shows what each reader trims; Apple's encoder also wrote the iTunSMPB of the `apple` fixtures. Alternative: Core Audio as the AAC reference, whose version moves with the runner image.
6. **TTA by `tta -d` only.** Reason: a direct path needs libtta's API, and reading its LGPL headers would keep that agent out of the clean-room TTA decoder that decision 10 of [decoders.md](decoders.md) plans. Alternative: a harness against libtta's API, written by an agent who then never works on the TTA decoder.
7. **Commit the reference figures and differences per fixture folder,** checked in `Encspect.Tests`. Reason: macOS and Windows runs cannot be part of `ci.yml`, and a later parser change should still show against them. Alternative: the WMA model, figures only in the docs and the artifact, which a parser change can drift from unseen.
8. **One FFmpeg for every platform's column**, run in the Linux comparison job. Reason: the WMA artifact's column came from Chocolatey's 9.0.2 while every other check uses 8.0.1. Alternative: install ffmpeg on each runner and record each version.
9. **One unit extractor, not the analyzer, for every direct path.** Reason: three readers would split damaged data differently and invent differences between the decoders. Alternative: a minimal reader in each harness, with the differences between the readers recorded.

## Questions for the project lead

These are not decided in this document.

1. **Licenses of the test decoders.** The design declines Dolby's Store decoder because its terms were not examined, and has not examined FDK's either. Section 3 of the "Software License for The Fraunhofer FDK AAC Codec Library for Android" grants no patent license and says the software may be used "only for purposes that are authorized by appropriate patent licenses" (`aacdecoder_lib.h`, license text at its top). The precedent is `fdk_tool.py`: FDK is built in CI and used for this project's fixtures, never redistributed. Options:
   - (a) keep the precedent: use FDK, decline the Store decoder unexamined;
   - (b) examine both licenses first and decide for both; if the Store decoder's terms allow it and it installs on a runner, use it on `windows-11-arm`, the only route to an AC-3 decoder on Windows this design names;
   - (c) drop FDK and use Core Audio and the Media Foundation decoder only; the cost is no pinned AAC decoder and no Linux CI gate for AAC.
   - Recommendation: (a) now, with (b) as a task before the harness is built, so the decision does not rest on the precedent alone.
2. **The rate of iTunSMPB.** [aac.md](../formats/aac.md) says the figures count at the core rate (the media timescale); the comment in `Mp4Parser.Metadata.cs` says "at the output sample rate". The probe settles which is true (open question 10). Options:
   - fix the documentation and the comment with the result, in the same pull request;
   - if the probe shows the parser's handling is wrong (for example that the iTunSMPB-total finding compares at the wrong rate), the fix changes findings or goldens and goes to the lead as a `pending` item under this design's own rule.
   - Recommendation: both, as stated: text with the result, analyzer changes as pending items.
3. **The plan's wording.** `PLAN.md`, "Phase 3, continued", says "Dolby's decoders in Windows' Media Foundation and in macOS's Core Audio". Microsoft's own page calls its decoder Microsoft's implementation of Dolby Digital technology, and whose code Apple runs is not documented. Proposed wording: "the AC-3 and E-AC-3 decoders in Windows' Media Foundation (where a runner image has one) and in macOS's Core Audio", with "Docs say only what exists". Options: change it with this design, or after the probe. Recommendation: after the probe, with its inventory. (This design does not edit the plan.)
4. **libvorbis's own granule handling.** The direct path switches it off (decision 2). Options: leave it off, or add a third run with the packets' real granule positions as a further witness at the trimmed stage. Recommendation: add the run; it costs one more child per Vorbis file.
5. **Stale entries.** A stale `official-decoders.json` entry fails the test (as proposed), so every regenerated fixture needs a macOS and Windows run before its pull request passes. Alternative: a status `stale` that is listed in the test's output and does not fail. Recommendation: the non-failing status, with a list in the pull request description.
6. **The statuses `noted` and `build-dependent`** are added in this draft (`stale` only if question 5 is answered so). Options: keep them, or fold `noted` into `explained`. Recommendation: keep them.
7. **The reference implementation of AAC.** The plan says "the decoder of a format's owner, or its reference implementation". AAC's is the ISO/IEC 14496-5 software, deferred here ("license and build were not examined"), yet it would cover the object types FDK and Media Foundation refuse (Main, LTP, SSR). Options: examine its license and build now, as part of the probe; or leave it for a later step. Recommendation: examine its license now (a reading task, no build), and decide on the build with the probe's results on FDK's refusals.
8. **Probe size.** Options: the full probe at 2 to 3 days, or the inventory and the reader paths only, with the direct-path cases moved to the steps. Recommendation: the full probe, because its answers decide the design of steps 2 to 6.

## Out of scope

- Comparing decoded PCM: the analyzer has no lossy decoder yet; that waits for the lossy decoders under "Later" in the plan.
- Opus and Vorbis in Matroska and WebM, AAC and AC-3 in Matroska, and TTA in Matroska (`matroska/external-ffmpeg-tta.mka` and `external-mkvmerge-tta.mka`): neither Xiph's tools, libtta's program nor Core Audio read Matroska, and extracting the packets would add another tool's handling of the trimming fields.
- Opus and other codecs in MP4 outside the AAC and AC-3 paths (`ffmpeg-cenc-opus.mp4` is encrypted anyway), and AAC in CAF (the analyzer has no CAF parser yet).
- MP3 and MP2, which have more references than FFmpeg already (mpg123, madplay, minimp3); Speex, whose Xiph decoder `speexdec` is in the Ogg cross-check already; WMA, which has its check; the lossless formats, whose reference tools are their checks already.
- The MPEG reference software of ISO/IEC 14496-5 for AAC: its license and build were not examined; a later step may add it.
- AC-4, MPEG-H and the other formats of phase 5.
- Decrypting protected tracks whose key is not known: they have no reference. A test password the project knows (TTA's "secret") is not such a case.
- Any change to the analyzer, until the project lead decides a difference.

## Unverified claims and estimates

- **Unverified:**
  - which rate iTunSMPB counts at (open question 10);
  - every fact marked probe: which decoders each image has, what each reader trims, which file types Core Audio opens, which object types FDK 2.0.2 decodes, whether the Source Reader and the AAC MFT accept the files;
  - the date and Windows version of xHE-AAC in Windows 11 (Fraunhofer's blog says "the latest 2022 update"), and whether Windows Server 2025 has it;
  - that Apple's AC-3 and E-AC-3 decoders are implementations under Dolby's license;
  - that ffmpeg applies no trimming to raw ADTS and raw AC-3;
  - the terms of Dolby's Store decoder and the patent position of FDK's license (questions for the project lead);
  - that the MPEG-4 File Source and the Source Reader trim as Apple's `ExtAudioFile` might; none is documented.
- **Second-hand:** Apple's WWDC 2020 session 10158 and FFmpeg trac #9564 are known from the review's search summaries, not read; the FDK and libvorbis source claims come from the reviews' reading; the press reports on Windows 11 24H2 are press reports.
- **Estimates:** the effort in days of every rollout step; the worst-case minutes of the time limits (every child at its timeout); the fixture counts, which are file counts at c63ada6 and include some files that are not AAC.

## Sources

- The plan, "Phase 3, continued"; [decoders.md](decoders.md) (decisions 9 and 10); [development.md](../development.md) ("WMA decoding by Microsoft's decoder", "Tool versions", "TTA", "Conformance streams and downloads"); [asf-wma.md](../formats/asf-wma.md); the format pages [opus.md](../formats/opus.md), [vorbis.md](../formats/vorbis.md), [ogg.md](../formats/ogg.md), [tta.md](../formats/tta.md), [aac.md](../formats/aac.md), [mp4.md](../formats/mp4.md), [ac3.md](../formats/ac3.md).
- The scripts `crosscheck_ogg_fixtures.py`, `crosscheck_mp4_fixtures.py`, `crosscheck_ac3_fixtures.py`, `crosscheck_tta_fixtures.py`, `make_external_tta_fixtures.py`, `fdk_tool.py`, `fdk_crc.c`, `libtta_tool.py`, `tool_versions.py`; the WMA probe and its workflow; the review findings of the WMA check.
- Microsoft Learn: "Dolby Audio Decoder", "AAC Decoder", "Supported Media Formats in Media Foundation"; Microsoft's support page "Codecs in Media Player" as reported by the press in May 2024 (AC-3 not inbox from Windows 11 24H2).
- Fraunhofer IIS audio blog, "xHE-AAC Audio Codec now in Windows 11" (2022).
- Apple, WWDC 2020 session 10158 (xHE-AAC playback); FFmpeg trac #9564 (reports on `AudioConverter` and `usac`).
- actions/runner-images release notes for `win11-arm64` (OS build 26200).
- github.com/mstorsjo/fdk-aac: the headers `aacdecoder_lib.h` and `FDK_audio.h`, `conceal.cpp` and `aacdecoder_lib.cpp`, under the "Software License for The Fraunhofer FDK AAC Codec Library for Android"; tags v2.0.2, v2.0.3 and v3.0.0.
- libvorbis `lib/synthesis.c` and `lib/block.c` (BSD), for the granule handling of `vorbis_synthesis` and `vorbis_synthesis_blockin`.
- The FDK and libvorbis sources above were read by the independent review of this design (findings D-1, D-4, D-5) and checked again in the fix review at 31d5db6, not by the author of this text; the quotes and the line-level claims come from those reviews.
- No FFmpeg, faad2, liba52 or libdca source was read for this design, and no libtta source.
