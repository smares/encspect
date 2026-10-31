# Measurements of decoded audio: spectrum, cutoff, levels and loudness

Status: approved by the project lead on 2026-10-26 with every recommendation of [Decisions](#decisions) as listed; steps 1 to 5 of the [Rollout](#rollout) are built (see [As built: step 1](#as-built-step-1), [step 2](#as-built-step-2), [step 3](#as-built-step-3), [step 4](#as-built-step-4) and [step 5](#as-built-step-5)), the rest is not. It was revised after two independent reviews. Two throwaway prototypes outside the repository measured the costs and the numerical behavior (see [Performance and budget](#performance-and-budget) and [Determinism](#determinism)); the second was written for the review and replaced the first's cutoff method and its table; nothing of either is committed. The claims that no source read for this design confirms are marked **unverified**; figures worked out rather than measured are marked **estimate**. The public specifications were read for the revision (ITU-R BS.1770-5, EBU R 128, EBU Tech 3341 and 3342; see [Sources](#sources)).

- It designs the "host-side PCM analyses" that [decoders.md](decoders.md) leaves for later ("The PCM path", "Host-side PCM analyses, later"): waveform, spectrogram and lowpass detection, and the level and loudness figures that the same pass can give.
- It keeps the decoder contract as approved and built: no parser, decoder or session changes for the measurements themselves. The PCM route of WAV, Wave64 and AIFF (step 3) adds a decoder, one record (`PcmCoding`) and one property of `DecodeSetup` to `Core.Abstractions`; the sink, the session and the router do not change.
- It fits the approved [parallel decoding](parallel-decoding.md) design (deferred, still approved and ready to build), which calls the sink on the analysis thread in stream order.
- One name for the concept from here on: **measurements**. A measurement is a figure the host computes from decoded PCM; decoders.md's "host-side PCM analyses" are these, and its section is renamed with the first pull request (decision 2). Its benchmark section "The measurements", which clashed with the name, is already renamed "The work-unit timings". The sample peak, RMS, DC offset, clipping, digital silence and bit-depth figures are one group, the **levels** (`--measure levels`), a name that does not collide with `NodeKind.Statistics` and `--hide statistics`. A figure the file states about itself (the BWF `bext` loudness values, ReplayGain, iTunNORM, USAC `loudnessInfo`) is **stated**; one this design computes is **measured**, and the two are never mixed under one name in a sentence (see [Descriptions, topics and articles](#descriptions-topics-and-articles)).

## Scope

- **In:**
  - the measurement framework on the PCM path: options, the host's fan-out from the sink, the result on `StreamInfo`, the writers, the budget, the exception boundary;
  - the measurements: the average spectrum and the spectral cutoff, the spectrogram and waveform grids, the levels (sample peak, RMS, DC offset, clipping runs, digital silence, bit-depth usage), loudness and loudness range (ITU-R BS.1770, EBU R 128), true peak;
  - a PCM route for WAV, Wave64 and AIFF, which had no decoder (built in step 3);
  - the CLI and GUI behavior, fixtures, cross-checks and the rollout.
- **Out, and why:**
  - decoders for lossy codings (MP3, AAC, Vorbis, Opus and the others): the plan keeps them under "Later". This design only makes sure they plug in without a contract change (see [Lossy formats later](#lossy-formats-later));
  - the TTA, ALAC and WMA Lossless decoders, which decision 5 of decoders.md moved to "Later";
  - DSD (WavPack DSD now, DSF and DSDIFF later): measuring it needs a DSD-to-PCM conversion, which is a decoder's job, not a measurement's;
  - playback, and any quality verdict (the plan's non-goals).

## The design in short

- The host's sink, which every decoder writes its PCM to (`DecodeContext.DeclareOutput`), forwards each block to a measurer of the stream as well as to the front end's sink. The measurer is host code; parsers and decoders do not change.
- Measuring is asked for in `DecodeOptions.Measure`, so one decode pass serves verification and measurement: measuring implies decoding, and decoding always compares the checksums over decoded audio. Measuring changes no verification and no `decode.*` finding.
- Off by default everywhere: the library measures only when asked; the CLI with `--measure`; the GUI in the detail window when the user decodes; the file list never.
- The result is a typed record on the stream, `StreamInfo.Measurements`, attached by the host after the format parser returns, as decoders.md fixes it, for every session of a measured analysis, including those that produce no PCM (they carry a status). It adds figures, never nodes, so the tree is the same with or without measuring.
- Reports stay byte-identical on every platform: double-precision arithmetic with no fused multiply-add, own trigonometric tables, logarithm and power function instead of the platform's math library, fixed summation order, element-wise vectors only, no `*Native` or `*Estimate` API, subnormals flushed. The first prototype measured bit-identical spectra under JIT and NativeAOT, x86-64-v2 and v3, and with every vector extension turned off.
- Measurements report figures with units and their basis, never a verdict: "spectral cutoff 17,054 Hz, a step of 60.4 dB, in 644 of 644 frames with signal", not "lossy source".
- A work budget of their own, counted like the decoders' and as large, bounds their time; their memory is bounded per stream whatever the stream's length, and per-channel figures are capped at 32 channels.
- First step: the framework with the average spectrum, the spectral cutoff and the grids, on the decoders that exist (see [Rollout](#rollout)).

## Goal and user value

### What a user learns

- **Spectrogram:** level over time and frequency. It shows a lowpass as a flat ceiling, band-limited sections, inserted segments of another source, noise, hum and tones, clipped passages as vertical streaks, and lossy coders' marks (holes, bands that switch on and off, replicated high bands).
- **Average spectrum and spectral cutoff:** where the energy of the whole stream ends, with how steep an edge and how consistently over time. This is the prime use case: a lossless file made from a lossy encode keeps the encoder's lowpass, which the second prototype shows as a step of 34 to 69 dB, from the start of the transition to the edge in 130 to 1,260 Hz, in every frame with signal (see [Lossless files from lossy encodes](#lossless-files-from-lossy-encodes)).
- **Waveform:** the envelope of each channel over time; clipping, silence, fades, gaps and level changes.
- **Levels:** sample peak, RMS, DC offset, runs of clipped samples, digital silence at the start, the end and in total, and bit-depth usage (low bits that are always zero, as in 16-bit audio padded to 24 bits; float samples that are all 16- or 24-bit values).
- **Loudness, loudness range and true peak:** the figures of ITU-R BS.1770 and EBU R 128 that mastering and delivery are judged by.

### Evidence, not verdicts

- The plan's non-goal "quality verdicts" and CLAUDE.md's rules on observations apply to every figure, message and article here.
- The analyzer reports what it measured and how: a cutoff's frequency, the size of the step, the width of the transition, and the share of frames that show it. It never says "transcoded", "fake", "upsampled" or "lossy source".
- The measured signature is not unique to lossy coding. The prototype's 16 kHz brickwall filter applied to the lossless source gives the same kind of figures as LAME at 128 kbps (a 67.5 dB step in 645 of 645 frames, against 60.4 dB in 644 of 644; see the table below). Mastering filters, sample rate conversion, old analog sources and deliberate band limits do the same. The `spectral-cutoff` article says so.
- The absence of a cutoff is no evidence either: LAME 3.100 at `-V 0` and oggenc at `-q 8` leave none in the prototype's input (largest step 2.4 dB, the source's own 2.3 dB), and a quiet lossy decode under strong dither can hide one (see [Spectral cutoff](#spectral-cutoff)).
- Articles may state encoder behavior only as observations backed by committed fixtures, with encoder versions and settings, scoped to "Encspect's test files", and saying that the analyzer does not name an encoder from them (see [Lossless files from lossy encodes](#lossless-files-from-lossy-encodes)).

### The analyses weighed

| Analysis | What it gives | Cost per sample of a channel (prototype) | How it is checked | Step |
| --- | --- | --- | --- | --- |
| Average spectrum, spectral cutoff | the prime use case | window, FFT and power 11.2 ns (4,096 points, hop 2,048, 128-bit vectors) and the frame counters 1.6 ns for each channel and for the combined spectrum, about 13.6 ns for stereo (second prototype, JIT; the first, NativeAOT, read 12.2 ns for the FFT) | lossy-derived fixtures against the encoders' stated lowpass; synthetic band-limited noise | 1 |
| Spectrogram and waveform grids | the GUI panels | grid sums on top of the FFT: under 1 ns (**estimate**) | synthetic sweeps and tones at known times | 1 |
| Sample peak, RMS, DC offset | basic levels | 2.0 ns for all the levels together (first prototype) | exact, from integer samples | 2 |
| Clipping runs | runs at the extreme codes | in the 2.0 ns | exact | 2 |
| Digital silence | leading, trailing and total zero samples | in the 2.0 ns | exact | 2 |
| Bit-depth usage | padded or reduced depth | in the 2.0 ns | exact | 2 |
| Loudness, loudness range | BS.1770 integrated loudness, EBU loudness range, maximum momentary and short-term | 4.6 ns (K-weighting and mean square; first prototype) | EBU test set, libebur128 as a black-box tool, FFmpeg's ebur128 | 5 |
| True peak | BS.1770 Annex 2 | 23.1 ns (12.5 ns in the x86-64-v3 build; first prototype) | synthetic overs, libebur128 and FFmpeg's ebur128 with their tolerances | 5 |
| Dynamic range | EBU loudness range covers it | none beyond loudness | as loudness | 5 |
| Upsampling | the edge of the content below Nyquist | none beyond the spectrum | needs a bandwidth figure the step finder does not give (see [Spectral cutoff](#spectral-cutoff)) | later |
| Mid/side spectra, holes, replicated bands | lossy coders' finer marks | mid/side from the complex bins, no further FFT (**estimate**: about 1 ns) | needs fixtures that show each mark | later |

- **Why the spectrum first:** it is the prime use case, it needs the FFT, the grids and the determinism rules that everything else reuses, and it is where the risks are (cost, determinism, wording).
- **Why the levels second and small:** they are exact on integer samples, so their fixtures are exact too, and they cost almost nothing; they could also join step 1 (decision 1).
- **Why loudness and true peak later:** they need the BS.1770 filters at every sample rate, the gating, and their own cross-check set; true peak is the most expensive measurement.
- **Dynamic range:** EBU Tech 3342's loudness range is the standardized figure. The "DR" value of the Pleasurize Music Foundation's meter has no open specification this design found (**unverified**), so it is not offered.

## Which formats

### Decoded lossless formats

- **FLAC** (native and in Ogg), **WavPack** (PCM and floats, lossless and hybrid), **Monkey's Audio** (integers and floats), **TrueHD and MLP**: every stream whose decoder calls `DeclareOutput` is measured.
- **WavPack hybrid without its correction file:** the decoded audio is the lossy stream, as for verification (`DecodeSetup.IsLossy`). The measurements are of that lossy audio; the text and the GUI say "(lossy stream)" after the measured duration, as the verification line does.
- **TrueHD:** the decoder outputs the highest presentation it decodes (substreams 0 to 2); a Dolby Atmos substream is not decoded. Positions are untrimmed (decoders.md, "The PCM path"), so the samples to drop at the start are measured too, which the article on measurements says.

### WAV, Wave64 and AIFF: a PCM decoder

The samples are the data, so these formats need no reconstruction, only a route from the parser's data chunk to the sink.

- **(a) A "pcm" decoder, recommended.** `Encspect.Formats.Wav.Decoding` gets a decoder with the codec ID `pcm`, registered in `DefaultParsers.Decoders` next to the others. The WAV, Wave64 and AIFF parsers start a session with it (`StartDecoding("pcm", setup)`) and, only while the session `IsDecoding`, read the data chunk in slices of whole sample frames (64 KiB, **estimate** to tune) and hand each as a `CodedFrame`. The decoder declares no checksum.
  - The AIFF parser names the codec by ID only, so it does not reference the WAV project; parsers never reference each other, and the host routes by ID.
  - Later containers of PCM (MP4 `lpcm`, `sowt`, `twos`; Matroska `A_PCM/*`; CAF) use the same ID.
  - The unpacking code lives in the WAV project's `Decoding` namespace; if a second format project ever needs it as code, it moves to `Core.Abstractions` next to `CanonicalPcm`.
  - **Work:** `DecodeWork.ForSample` per sample of each channel, as every decoder charges; nothing per tap.
- **The descriptor.** `DecodeSetup.Format` is a WAVEFORMATEX, which cannot say big-endian, and AIFF's Common Chunk is not a WAVEFORMATEX and cannot be read with the WAV project's code. So the parser states the layout in a neutral record in `Core.Abstractions`, carried by one new property, `DecodeSetup.Pcm` (`Configuration` stays null and `Format` unused by this decoder):

```csharp
public enum PcmEncoding { SignedInteger, UnsignedInteger, Float, ALaw, MuLaw }

public sealed record PcmCoding
{
    public required PcmEncoding Encoding { get; init; }

    public required bool IsBigEndian { get; init; }

    // Bits one sample takes in the data, a whole number of bytes: 8, 16, 24 or 32 for integers, 32 or 64 for floats, 8 for
    // A-law and mu-law.
    public required int ContainerBits { get; init; }
}
```

- **What the decoder outputs:** integers of 8 to 32 bits as `Integer` blocks of `ContainerBits` (unsigned 8-bit minus 128), 32-bit floats as `Float32`, 64-bit floats as `Float64`, A-law and mu-law expanded to 16-bit `Integer` (the G.711 tables are format data; the scaling to 16 bits is checked against FFmpeg's decoded MD5, **unverified** until step 3, which checked all 256 codes: see [As built: step 3](#as-built-step-3)). `DecodeSetup.BitsPerSample` is the output width, so the per-sample charge follows it.
- **The cases the parsers map:**
  - WAV: little-endian, or big-endian for RIFX; 8-bit is unsigned, wider integers signed; IEEE float of 32 and 64 bits; A-law and mu-law.
  - AIFF: big-endian signed integers; AIFF-C `NONE` and `twos` the same; `sowt` little-endian signed; `in24` and `in32` big-endian 24 and 32 bits; `raw ` unsigned 8-bit; `fl32` and `fl64` big-endian floats; `alaw` and `ulaw` (the type table is `AifcTypes.cs`, with its sources).
  - Valid bits smaller than the container (`wValidBitsPerSample` of 20 in 24 bits; AIFF's sample size of 12 in 16 bits): the decoder outputs the container's value as stored and ignores the valid-bit count, which the parsers keep as a figure of the file. If the file pads at the low end, the bit-depth usage shows the unused low bits. Whether the specifications place the valid bits at the top of the container is **unverified** here (the WAV parser's descriptions do not say, and the AIFF text was not read for this design); fixtures of a 20-in-24 WAV and a 12-bit AIFF, decoded by FFmpeg, settle what a decoder must do (they did, in step 3: the container's value, see [As built: step 3](#as-built-step-3)).
  - Integers of 33 bits or more (a WAV container of 40 or 64 bits): `PcmBlock` holds 32-bit integers, so the decoder refuses the setup, `decode.not-possible` (info) and status `NotPossible`; the measurements' record says so too. 64-bit floats stay.
- **Data that is not PCM stays out.** The parser starts the session only where it reports a PCM stream whose coding `PcmCoding` can describe. `DataChunks.AddStream` also runs for IMA ADPCM, Microsoft ADPCM and unknown codings (`DataChunks.cs`, lines 131 to 153), which start no `pcm` session and get no `Verification`, so the GUI caption for them is the neutral one of [GUI](#gui). A-law or mu-law declared with other than 8 bits (the fixture `built-alaw-16-bits.wav`, finding `wav.companded-bits`) is not describable either: the parser starts no session, and the stream is reported as it is today. The parser also starts none for a data chunk handed to a codec parser (by format tag or sub-format GUID), for DTS in 16-bit stereo samples, and for IEC 61937 bursts, recognized or not, so none of these starts a `pcm` session, so their bytes are never measured as PCM; their codec parsers' streams have their own decoders, where there is one.
- **Slices have no node of their own.** A slice is not a unit of the parser's tree. `CodedFrame.NodeName` is the data chunk's node name and `Bytes` the slice, and the decoder raises no finding about a slice (every bit pattern is a sample); `CodedFrame.Node` carries the data chunk's node, so a host finding about a slice would point to it. The session's findings (`decode.limit`, `decode.stopped`) point to the stream.
- **What changes in reports** (decision 15 gives the alternatives):
  - the WAV, Wave64 and AIFF parsers start the `pcm` session in every analysis, as TTA's parser starts one to finish it with its tallies, and finish it with an empty list of checksums. The stream then carries a `Verification` in the model, with `Decoding` `NotRequested` or `Decoded`, and no checksums, so `IsVerified` stays null and the CSV's `Is Verified` stays empty;
  - **the JSON writer leaves out a verification that declares no checksum**, as the text writer does, so the documented contract holds (`ReportJsonWriter.cs` lines 14 and 15, cli.md: `verification` is written for a stream of a coding that stores checksums) and the `.report.json` goldens of WAV, Wave64 and AIFF do not change. The model keeps the object for the GUI;
  - **`--verify` alone does not decode PCM.** A decoder that declares no checksum is opened only when `Measure` or `Output` is set (a flag on the decoder, or on the session's setup, read by the router); otherwise the session stays `NotRequested` and the data chunk is not read, so `--verify` costs a WAV folder nothing and the JSON never claims `decoding: decoded` with nothing compared. The GUI's Decode Audio sets `Measure`, so it decodes;
  - the text output and `DetailViewModel.VerificationText` (which lists every non-null verification today) leave the "Verified:" line out where a verification declares no checksum, so a WAV file's text does not change;
  - `--verify` raises no `decode.no-decoder` for these formats, and did not before: their parsers never started a session (decoders.md, "As built in step 3: WAV and AIFF", and [cli.md](../cli.md) were corrected for this). Only the router raises it, for a session whose codec ID has no decoder (TTA);
  - without decoding nothing is read: the parser still reads no samples, which `MemoryTests.TheSamplesOfAWaveFileCostNothing` keeps holding. The `.decode.json` goldens of these fixtures change only where `Measure` or `Output` is set; the `.report.json` ones do not change.
- **Tests:** `pcm` joins `DecoderWorkTests` (a case of integers and one of floats, with its time per unit) and the decoder-list assertion of `DecodeRouteTests` (`["flac", "wavpack", "monkeys-audio", "truehd", "mlp", "pcm"]`); a cross-check compares the MD5 of the decoded samples with FFmpeg's for the WAV, AIFF and Wave64 fixtures (`ffmpeg -i f -c:a pcm_<format> -f md5 -`, the format being the canonical layout of the decoder's output), including the 12-bit, 20-in-24, `sowt`, `in24`, `raw `, A-law and mu-law cases.
- **(b) The host reads the samples itself.** The host would need to know where a WAV file's samples are and how they are laid out, which is the parser's knowledge; it breaks the layering the plan keeps.
- **(c) No measurement for PCM files.** WAV and AIFF are where many lossless-from-lossy files live, so this loses much of the use case.
- **Decision 4.**

### TTA

- TTA has no decoder (decoders.md, decision 5). Measuring it needs one, built clean-room as decoders.md's decision 10 requires: libtta-c and FFmpeg's `tta.c` are LGPL. One agent reads them and writes a format description without code, as `truehd-format.md` was; another agent who has not read them implements from that description, with libtta-c and FFmpeg as black-box cross-checks of the decoded PCM's MD5.
- TTA stores only CRC-32s over coded data, so the decoder adds no verification; its correctness rests on the fixtures' decoded MD5 from libtta-c and FFmpeg.
- **Recommendation:** leave TTA out of this design's steps; its decoder is a change of its own under "Later", and its streams are measured once it lands, with no further work here (decision 12).

### Channels, rates, widths and DSD

- **Multichannel:** every channel is measured on its own (spectrum, levels, peaks). The combined spectrum, the spectrogram and the stream's cutoff use the mean power of the channels other than the LFE (`Speaker.LowFrequency` and `Speaker.LowFrequency2`), which are band-limited by design; a stream without a layout (`ChannelLayout` null) uses all channels and says so in its description.
- **At most 32 channels are measured.** A WAV header may declare 65,535 channels; measured in full that is about 2 GiB of overlap buffers, 1 GiB of average spectra and about 200 MB of per-channel JSON (**estimate**, from the sizes). The measurer takes the first 32 channels of a block (the combined figures cover those), the record says how many channels the stream has and how many were measured (`Channels`, `ChannelsMeasured`), and `measure.channels-limited` (info) says so. Each stream's state is sized from its format, before anything is allocated, against the analysis's memory bound (see [Memory](#memory)).
- **Sample rates:** the FFT size follows the rate so that a frame lasts 85 to 170 ms: the smallest power of two that is at least 0.085 s of samples, at least 1,024 and at most 32,768 points. At the common rates that is 85 or 93 ms with bins of about 10.8 to 11.7 Hz: 2,048 points at 22.05 and 24 kHz, 4,096 at 44.1 and 48 kHz, 8,192 at 88.2 and 96 kHz, 16,384 at 176.4 and 192 kHz, 32,768 at 352.8 and 384 kHz and at every higher rate, where frames get shorter (65,536 points at 768 kHz would break the memory and weight tables, and a header may state 4 GHz). Others get longer frames: 128 ms at 8, 16 and 32 kHz (1,024, 2,048 and 4,096 points), 93 ms at 11.025 kHz (1,024). The spectrum costs a little more per sample as the size grows (measured, see [Measured](#measured); decision 10).
- **Rates the measurements do not take:** a rate of 0 Hz (a header that states none) gives status `NotPossible` for the whole record. Below 8,000 Hz the spectrum and the grids, the levels and the true peak are measured, the spectral cutoff is not searched and loudness is `NotPossible` ("needs a sample rate of 8,000 Hz or more", see [Loudness and loudness range](#loudness-and-loudness-range)). Fixtures at 2 kHz, 8 kHz and 0 Hz hold these edges.
- **192 kHz and the grid:** the spectrogram's 512 rows cover 0 Hz to Nyquist, 187.5 Hz a row at 192 kHz. The GUI can show the range up to 24 kHz from the same rows; finer rows above need a later option.
- **Widths:** integers of 1 to 32 bits are scaled by 2^-(bits - 1) to full scale ±1; floats are taken as they are. The levels on integer samples (peaks, DC, clipping, silence, bit usage) are computed on the integers, exactly.
- **Floats:** samples above full scale are counted ("Samples Over Full Scale"), not clipped. Non-finite samples (NaN, infinities) are counted, measured as 0, and raise `measure.non-finite-samples` (decision 6). **Large finite floats** are clamped to ±1,024 times full scale (+60.2 dB) before anything else, because 1e300 would overflow to infinity and then NaN in the butterflies and filters; a clamped sample counts as over full scale, and the record says some were clamped (`HasClampedSamples`), so a peak that reads +60.2 dBFS is the clamp, not the file.
- **Gaps:** a decoder that lost frames makes the next block's `Position` jump (the parser then sets `CodedFrame.FollowsGap`). The measurer measures the samples it gets and handles the jump so that nothing spans it:
  - no FFT frame, K-weighting state, gating block or true-peak history crosses a gap: the overlap is dropped and the filter states are reset at the first sample after it, and gating blocks that would span it are not formed;
  - the grids follow the stream's positions, not the count of measured samples: the columns a gap covers hold no data (a cell of 0 is "no data", see [dB scale and quantization](#db-scale-and-quantization)), and a gap of any size is a few arithmetic steps, not a loop over its hops (a crafted position of 2^40 costs the same as a small one);
  - runs do not span it: a clipping run ends at the gap, the trailing digital silence starts again after it, and the samples inside it are neither silent nor clipped but unknown; `Samples` counts the samples measured and `LostSamples` the positions skipped (`Gaps` their number);
  - a block whose `Position` is below the next expected one (overlapping or backward) is not a bug: the positions come from the file (FLAC frame numbers in `State.cs`, WavPack's `BlockIndex` in `DecodeFeed.cs`), so a spliced or crafted file has them, and `measure.failed` (an error that fails the fuzzer) would be wrong. The measurer ends with status `Stopped` and the info `measure.positions-jump` ("the stream's positions went back at sample N"); the figures cover what came before. `measure.failed` stays for real exceptions.
- **Format changes:** `PcmBlock` carries its own channel count and sample format, and the contract says only that `DeclareOutput` is called once, so nothing in the types stops a decoder from writing a block that differs from the declared output. The review suspected TrueHD, whose `Write` takes `m = LastMatrixChannel + 1` from the state at every access unit (`Decoding/StreamDecoder.cs`, line 472, after the declaration at line 483). It is guarded today: lines 156 to 158 compare that same substream's channel count with the count the stream was opened with and throw `InvalidDataException` (reported as `decode.frame-invalid`, the unit not decoded, so the next block's position jumps and the gap rules apply) before `Write` runs. So no decoder of today changes format; the measurer checks anyway, as a guard for the contract and for the decoders to come: it compares every block's channel count and sample format with the declared output, and on a difference ends with status `FormatChanged` and the info `measure.format-changed`, the figures covering what came before. Verification and the front end's sink are not affected.
- **DSD:** WavPack's DSD decoder declares no PCM output. The stream's measurements say "not measured: the stream decodes to DSD" (status `NotPossible`), without a finding (see [Attaching the result](#on-the-decoder-hook)).

### Lossy formats later

Nothing here blocks lossy decoders:

- they write `Float32` blocks through the same sink, and every measurement reads floats;
- their priming and padding samples are measured as decoded, since positions are untrimmed; a later change can let the host skip what the stream reports (`edit-lists-and-priming`, the LAME tag's delay and padding);
- for a lossy stream the spectral cutoff can sit next to the coded bandwidth the parsers already report ([coding-graphs.md](coding-graphs.md), "Coded bandwidth") and the LAME tag's lowpass, three figures the articles keep apart;
- decoders whose reconstruction is floating point must be deterministic themselves for the goldens; that is their design's concern.
- Candidate sources, by license, for that later work: minimp3 (CC0, already in `third_party` as a test tool), libvorbis and libopus (BSD-3-Clause, **unverified** here), libmpcdec (BSD, whose notice the Musepack parser already carries). faad2 is GPL and excluded.

## Where it runs

### On the decoder hook

- `DecodeRun.OpenSink` creates, for each stream whose session declares output, the front end's target (as now) and, where `DecodeOptions.Measure` is set, a `StreamMeasurer` (internal to `Encspect.Host`). `GuardedSink.Write` passes each block to both; `EndByHost` ends both.
- **The decoder contract stays:** the sink never throws to the decoder. The measurer catches its own exceptions (see [Cancellation and the exception boundary](#cancellation-and-the-exception-boundary)); it does not throw on cancellation either: it stops working, remembers that it did, and leaves the throw to the decoder's next check of its token, which is linked to the analysis's.
- **No parser changes** beyond the PCM route of WAV, Wave64 and AIFF, which is a parser handing frames to a decoder as every decoded format does.
- **Attaching the result:** the measurer is created at `OpenSink`, which only a session that declares output reaches, so the record cannot hang on the sink. The host attaches one `StreamMeasurements` to **every session of a measured analysis**, when the session closes, as decoders.md fixes it and as it sets `NodePaths`: the stream whose first node is the session's `DecodeSetup.Stream` gets it (a routed codec's stream, FLAC in Ogg, is matched the same way). Its content comes from what the session did:
  - a measurer that ran: its figures and status (`Complete`, `Stopped`, `FormatChanged`, `Failed`);
  - a session with `DecodeStatus.NoDecoder` (TTA), `NotPossible` (a refused configuration), or one whose decoder never declared output (WavPack's DSD, which `DecodeSetup.IsDsd` states, so also when decoding stopped before its first block): status `NotPossible` with the reason as text ("no decoder for TTA", "the stream decodes to DSD"), no finding of its own;
  - a session that decoded nothing because the stream stopped or the budget ran out before the first block, or whose frames all failed: status `Stopped`, no figures. (A stream whose setup states 0 samples and which was handed no frame, with no stop to explain it, is `NotPossible`, "the stream holds no samples"; step 3.)
  - A stream without any session (MPEG audio, AAC and the other formats whose parsers start none) gets no record at all, as it has no `Verification`. The front ends read that absence as "no decoder" (see [GUI](#gui)).
- **Order:** the measurer gets the block before the front end's sink. Both see the same span, valid only during the call; the measurer copies what it keeps (the window's overlap).

### One decode pass with verification

- Measuring implies decoding with the same `DecodeOptions` (stop, budget, sink). The checksums over decoded audio are compared as with `--verify`, at a cost that is small next to the measurements (FLAC's MD5 is about 28% of its decoding step, decoders.md "As built: the second speed pass").
- So `--measure` implies `--verify`, and a measured stream always has its verdict. A decoder option to skip the checksums would change the decoder contract for a small gain; it is not proposed (decision 3).
- A stream measured to its end with a frame that was not decoded (`decode.frame-invalid`) is `Incomplete`, not `Stopped`: a decoder may have written blocks of that frame before it failed, so the figures cover what the decoder delivered. The reason counts both figures ("1 frame was not decoded, and the decoder delivered 114,688 samples against the 73,728 it counted as decoded"); the text line and the GUI caption carry it. A stop or a budget makes it `Stopped` whatever else.
- `decode.limit` and `decode.stopped` end the measurements with the decoding: the measurements then cover the samples decoded, status `Stopped`, and the text says how much was measured.
- **Nothing the decoder or the verification does depends on measuring.** The checksums compared, `IsVerified` and every `decode.*` finding are equal with and without `--measure`, also when `measure.limit` stops the measurements first, because the measurer's budget is counted apart from the decoders' (decision 5) and the measurer never throws. A test holds it for every decoded fixture (see [Tests](#tests)).

### Options in the host API

```csharp
public sealed class DecodeOptions
{
    // ... Stop, Output, MaxWork as now.

    // Null (the default): nothing is measured. Set: every decoded stream is measured as it is decoded, and the figures go
    // to StreamInfo.Measurements. Measuring needs decoding, so a caller sets AnalysisOptions.Decoding to measure.
    public MeasureOptions? Measure { get; init; }
}

public sealed class MeasureOptions
{
    // Which measurements run; null (the default) means every one the library has when the code runs. (An `All` member of the
    // flags enum would be a constant compiled into the caller, and a caller built against an older library would keep
    // asking for the kinds that library had.)
    public MeasurementKinds? Include { get; init; }

    // The measurements' work budget in work units, counted apart from the decoders' (see "The work budget"); null for
    // the default. Tests and the fuzzer lower it. An option of the call, not a setting.
    public long? MaxWork { get; init; } // negative: ArgumentOutOfRangeException from Analyze, as for the decoders' budget
}

[Flags]
public enum MeasurementKinds
{
    None = 0,
    Spectrum = 1,           // average spectrum, spectral cutoff, spectrogram and waveform grids
    Levels = 2,             // peak, RMS, DC offset, clipping runs, digital silence, bit-depth usage
    Loudness = 4,           // BS.1770 integrated loudness, loudness range, maximum momentary and short-term loudness
    TruePeak = 8,
}

// The kinds the library has, as a property so that it is read when it runs (never a const or a field initializer in a caller).
public static class MeasurementSets
{
    public static MeasurementKinds All => MeasurementKinds.Spectrum | MeasurementKinds.Levels | MeasurementKinds.Loudness | MeasurementKinds.TruePeak;
}
```

- The enum is `MeasurementKinds` because `StreamInfo.Measurements` already names the record, and a type of the same name next to a property of that name would read as one thing in two places. `--measure`'s names (`spectrum`, `levels`, `loudness`, `true-peak`) map one to one onto its members.
- The flags grow with the rollout; `MeasurementSets.All` is what the library has at the time.
- The grids are always computed with the spectrum (they cost little beyond the FFT) and kept in the report; the JSON writer writes them only when asked (below).
- No setting, cache or console output below the front ends: all of this is an option of the call ("A library others could use").

### CLI

- `--measure`: decode, verify and measure every stream with a decoder; `--measure spectrum,levels` picks measurements by name (`spectrum`, `levels`, `loudness`, `true-peak`).
- `--measure-grids`: also write the spectrogram and waveform grids into JSON (implies `--measure`). Text and CSV never hold them.
- `--hide measurements` hides the measurement lines of the text tree, as `--hide statistics` does, and the `measurements` object of the JSON, as `--hide` leaves sections out of the JSON too (decision 17).
- **Text:** lines under the stream's first node, after the "Verified:" line. The figure names are those the BWF `bext` chunk already uses for the loudness values it *states* (`Chunks.cs`: "Integrated Loudness", "Loudness Range", "Maximum True Peak Level", "Maximum Momentary Loudness", "Maximum Short-Term Loudness"); the measured ones sit under "Measured:", the stated ones under the `bext` chunk, and both read the same way:

```
      Verified: yes (frame header CRC-8 3230 of 3230, frame CRC-16 3230 of 3230, STREAMINFO MD5 1 of 1)
      Measured: 300.0 s of 2 channels
        Spectral Cutoff: 17,054 Hz (a step of 60.4 dB, transition from 16,559 Hz, in 644 of 644 frames with signal)
        Sample Peak: -0.3 dBFS (front left -0.3, front right -0.5)
        Integrated Loudness: -9.8 LUFS
        Loudness Range: 5.2 LU
        Maximum True Peak Level: +0.4 dBTP (front left +0.4, front right -0.1)
```

  - No cutoff: `Spectral Cutoff: none found (largest step 2.3 dB, at 2,003 Hz)`.
  - A silent channel reads `-inf dBFS`; text shows tenths with a sign for the peaks, and a value of -0.0 is written 0.0.
  - With `--explain`, each line's description and topic, as for nodes.
- **JSON:** `measurements` in the stream, after `verification` (see [JSON](#json)). A field is added and none changes meaning, so `schemaVersion` stays 1.
- **CSV:** five columns at the end, named with their units as the existing ones are (`Duration (s)`, `Sample Rate (Hz)`): `Spectral Cutoff (Hz)`, `Sample Peak (dBFS)`, `Maximum True Peak Level (dBTP)`, `Integrated Loudness (LUFS)`, `Loudness Range (LU)`, from the first audio stream (peaks the highest over its channels), empty without `--measure`, so a folder export never mixes measured and unmeasured rows. `Is Verified` is filled with `--measure` too.
  - **Negative numbers.** `ReportCsvWriter.Field` puts an apostrophe before a value that starts with `=`, `+`, `-`, `@` or a control character, so every negative loudness or peak would be written as `'-0.3`. The formula guard is for text that comes from files. The writer's numeric fields (the ones it formats from numbers: counts, `Fixed`, these five) skip the guard; a test writes a measured file with negative values and reads them back as numbers. A numeric field cannot start a formula: its first character is a digit or a minus sign followed by a digit.
- **Exit codes:** unchanged; `--fail-on` sees only the host's `measure.*` findings.

### GUI

- **Detail window:** decoding stays a command, as decoders.md decided for verification. The one command analyzes the file again with `AnalysisOptions.Decoding` and `DecodeOptions.Measure` set, so it decodes, verifies and measures in one pass and a file is decoded once; its label is **Decode Audio** (decision 8), which replaces Verify Audio. The analysis then shows:
  - under the graphs a heading "Audio", with "Waveform", "Spectrogram", "Spectrum" and, from step 5, "Loudness", drawn from the stream's measurements;
  - the cutoff as a line on the spectrogram and the spectrum, with its figures in the hover text;
  - hovering the spectrogram shows time, frequency and level; clicking it selects the frame that holds that time, as clicking a graph point does now. The GUI finds frames by byte offset today (`FrameLocator`), so finding one by sample position is new GUI code, designed here and not tried.
- **When the command is enabled, and the captions before decoding.** Today `DetailViewModel.CanVerifyAudio` is true only while a stream has `Verification is { Decoding: DecodeStatus.NotRequested }`, and a PCM stream has no `Verification`, so the command would stay disabled for WAV, AIFF and Wave64. Decision 15 fixes that by giving PCM streams a session, hence a `Verification` with a decoding status, in every analysis. The rule then reads:
  - **Between step 3 and step 4** the button is still "Verify Audio", and PCM streams would enable it with nothing to show. So step 3 adds the filter of `VerificationText` and keeps `CanVerifyAudio` requiring a declared checksum (`Verification is { Decoding: NotRequested, Checksums.Count: > 0 }`); step 4 drops that extra condition together with the rename. (The alternative, moving the rename into step 3, makes step 3 carry GUI work with no panel to justify it.)
  - **Enabled** (renamed `CanDecodeAudio`): a report is shown, nothing is loading or decoding, and some stream has `Verification.Decoding == NotRequested`. The expression is the present one; what changes is that PCM streams satisfy it.
  - **Caption of a stream before decoding**, one line under the heading "Audio" (sentence case):
    - `NotRequested`: "Decode the audio to see its waveform and spectrogram." with the command;
    - `Verification` null or `NoDecoder`: "The audio of this stream is not decoded here." with the codec's name when it has one, and no claim that no decoder exists: a FLAC, WavPack or TrueHD stream in Matroska or MP4 (`external-ffmpeg-flac.mka`, `external-ffmpeg-truehd.mka`, `external-ffmpeg-wavpack.mka`, `mp4/ffmpeg-cenc-flac.mp4`) is reported with no verification, because the container does not hand it to its codec parser (`matroska.codec-not-analyzed`), although a decoder for the coding exists. This is the plan's "appear only for formats that have a decoder and otherwise say so", worded as what is true. The invariant that a test holds is narrower: every stream a parser reports through a codec route (`ParseStream`, `ParsePackets`) or as its own format, with a registered decoder's codec ID, starts a session in every analysis. The test walks the fixtures of each registered decoder ID that are not in the not-analyzed list;
    - `NotPossible`: "The audio cannot be decoded: <reason>." (a refused configuration; WavPack's DSD: "the stream decodes to DSD, which is not measured");
    - decoded, with a measurements record whose status is `NotPossible`, `FormatChanged`, `Stopped` or `Failed`: the panels it has and a line with the status text.
- **Drawing:** the spectrogram is the grid's bytes through a fixed 256-entry color ramp into an Avalonia `WriteableBitmap`, scaled to the panel; no charting library and no new dependency. The ramp is matplotlib's magma (or viridis), whose 256 entries are format data: the table in `BIDS/colormap`'s `colormaps.py` carries the header "released under the CC0 license / public domain dedication" and asks for credit, which `THIRD-PARTY-NOTICES.md` gives (read on 2026-10-26 from that file; the text is the source of the claim, no other copy of the license was read).
- **File list and summary cache:** never decode, so never measure; the list's columns stay as they are.
- **Folder parallelism:** the list analyzes `Parallelism` files at once without decoding; a decode in the detail window runs beside it with the degree parallel-decoding.md's decision 2 gives, and the measurer adds at most one thread (below).
- **Everything the rename touches** (decision 8; one pull request, step 4):
  - `DetailViewModel`: `CanVerifyAudio` to `CanDecodeAudio`, `VerifyAudioAsync` and its cancel, `IsVerifying`, the comments that say "Verify Audio" (lines 442 and 461), and the error text "Not verified: " to "Not decoded: " (line 558);
  - `DetailWindow.axaml`: the button (`VerifyAudioButton`, content "Verify Audio" to "Decode Audio") and its tooltip (which today says to compare the checksums; it also names the measurements), and the tooltip of the verification line, which says "choose Verify Audio" (line 19);
  - `DetailWindow.axaml.cs`: the macOS File menu entry (`Shortcuts.Item(this, "Verify Audio", ...)`, line 162);
  - `tests/Encspect.Gui.Tests/VerifyAudioTests.cs`: renamed with the tests, including the one that asserts `StartsWith("Not verified: ")`; `CapitalizationTests` reads the labels from the windows, and "Decode Audio" is Title Case (both words capitalized, no function word), so it passes unchanged;
  - `docs/gui.md` lines 87 to 90 (the command, its enabling, the failure text "Not verified: …", the macOS menu), which will also say that the CLI's `--verify` and `--measure` and the GUI's one command run the same decode;
  - the four format docs that name the command: `docs/formats/flac.md`, `wavpack.md`, `monkeys-audio.md` and `truehd.md`;
  - the approved texts that say "Verify Audio": decoders.md (lines 45, 822, 1274, 1327, 1339) and parallel-decoding.md (lines 36, 450, 552, 622, 627), edited with the first implementation pull request as their status lines allow, and noted as renamed;
  - the comment in `DecodeContext.cs` ("later to the host's PCM analyses") and decoders.md's "Host-side PCM analyses, later", which become the measurements.

## The result model

### Measurements on the stream

```csharp
// In Encspect.Core (Core.Abstractions), plain data like StreamVerification; computed by the host.
public sealed record StreamMeasurements
{
    public required MeasurementStatus Status { get; init; }      // Complete, Stopped, NotPossible, FormatChanged, Failed

    // Why less than was asked was measured, as text for the front ends ("the stream decodes to DSD", "no decoder for TTA",
    // "loudness needs a sample rate of 8,000 Hz or more"); null where everything asked was measured.
    public string? Reason { get; init; }

    // Samples per channel measured (not counting gaps), the position of the first one (untrimmed, as PcmBlock.Position),
    // and what the gaps skipped.
    public long Samples { get; init; }

    public long FirstPosition { get; init; }

    public long LostSamples { get; init; }

    public int Gaps { get; init; }

    public int SampleRate { get; init; }

    // The channels measured, in the stream's order; the speakers where the decoder stated them.
    public IReadOnlyList<Speaker>? ChannelLayout { get; init; }

    // The stream's channels, and how many were measured (at most 32).
    public int Channels { get; init; }

    public int ChannelsMeasured { get; init; }

    // True where a float sample beyond 1,024 times full scale was clamped to that.
    public bool HasClampedSamples { get; init; }

    public SpectrumFigures? Spectrum { get; init; }

    public SpectrogramGrid? Spectrogram { get; init; }

    public WaveformGrid? Waveform { get; init; }

    public LevelFigures? Levels { get; init; }

    public LoudnessFigures? Loudness { get; init; }

    public TruePeakFigures? TruePeak { get; init; }
}

public enum MeasurementStatus
{
    Complete,

    // Decoding ended before the stream did (the front end's stop, the decoders' budget, the measurer's own budget); the
    // figures cover what was measured.
    Stopped,

    // Nothing, or less than asked, can be measured: no decoder, DSD, a rate of 0 Hz, a configuration the decoder refused.
    NotPossible,

    // A block's channels or format differed from the declared output (no decoder does this today; see "Format changes").
    FormatChanged,

    // A measurement's bug (measure.failed); the figures are dropped.
    Failed,
}

public sealed record SpectrumFigures
{
    public required int FftSize { get; init; }

    // The average level of each of the 512 rows (0 Hz to Nyquist) in tenths of a dB, combined (all channels but the LFE)
    // and per channel. A row with no power at all holds short.MinValue.
    public required ReadOnlyMemory<short> Combined { get; init; }

    public required IReadOnlyList<ReadOnlyMemory<short>> PerChannel { get; init; }

    public required SpectralCutoff Cutoff { get; init; }

    public IReadOnlyList<SpectralCutoff> ChannelCutoffs { get; init; } = [];
}

public sealed record SpectralCutoff
{
    public bool IsFound { get; init; }

    public int Frequency { get; init; }            // Hz, the cutoff: the edge, where the frames' own steps end (see Spectral cutoff); 0 where none is found

    public int TransitionStart { get; init; }      // Hz, where the transition to the edge starts; 0 where none is found

    public int StepFrequency { get; init; }        // Hz, where the largest step lies (found or not)

    public int StepTenthsOfDb { get; init; }       // the largest step, in 0.1 dB (found or not)

    public int FramesShowing { get; init; }

    public int FramesWithSignal { get; init; }
}
```

- **"The cutoff" is the edge:** `Frequency`, the highest row at which half of the frames with signal show an own step, less the band's width (see [Spectral cutoff](#spectral-cutoff) and "As built: step 1"). The start of the transition and the position of the step are separate figures, and the text prints the cutoff first. The cutoff is never below the step and equals it on a sharp edge (LAME `-b 64` in Encspect's mono fixture: transition from 16,538 Hz, step and cutoff at 16,796 Hz).
- Every figure is an integer in a fixed unit (whole hertz, tenths of a dB, samples), so the JSON and the goldens hold no floating-point text (see [Determinism](#determinism)). A level that is minus infinity (a silent channel's peak) is `int.MinValue` in tenths, `short.MinValue` in a row; writers print `-inf` in text, `null` in JSON and nothing in CSV. The writers turn the integers into `NodeValue` decimals with their units ("dBFS", "dBTP", "LUFS", "LU", "Hz", "dB", "%"), as node values are written, by placing the decimal point (604 is 60.4), never by formatting a double.
- `LevelFigures`, `LoudnessFigures` and `TruePeakFigures` hold per-channel arrays and the stream's figure the same way; their fields follow the measurements listed under [Algorithms](#algorithms). Loudness uses the BWF `bext` chunk's names for its figures ("Integrated Loudness", "Loudness Range", "Maximum True Peak Level", "Maximum Momentary Loudness", "Maximum Short-Term Loudness"), as `Chunks.cs` has them.
- **Why not `StreamInfo.Series`:** a series is numbers over a time or offset axis, owned by the parser. A spectrum is over frequency and a spectrogram is a grid. The loudness over time (short-term loudness) is a `Series` with the `Seconds` axis, kept in `LoudnessFigures` and bounded like the grids (below), so the parser's series stay the parser's; the `Series` class is reused, with its `Description`.
- **Why no nodes:** decoders.md's rule is "decoding never changes the parser's nodes", and the tree must be the same with or without decoding (`AnalysisOptions.Decoding`'s contract, `DecoderFixtureTests`). A host node "Measurements" would break both.

### The time-frequency grid

- **Bounded whatever the length:** the spectrogram has 512 rows (linear, 0 Hz to Nyquist; each row the mean power of its bins) and between 512 and 1,024 columns. A column starts as one FFT hop; whenever the columns would pass 1,024, adjacent pairs are merged and each column then covers twice the hops. So a stream of any length ends with 512 to 1,024 columns of 2^k hops each, the last one partial. A stream of up to 1,024 hops (about 47 s at 44.1 kHz and 4,096 points) keeps one column per hop; merging starts with the 1,025th.
- **The partial last column** holds the mean over the hops it has: each cell keeps the power sum and the count of hops it summed, so a partial column is not understated by empty hops. The waveform's last column holds the extremes of the samples it has.
- The merge needs no stated length, so it works for streams whose length is unknown (a WAV file with a placeholder size) and gives the same grid wherever the length is known.
- **Gaps:** columns follow positions. A gap leaves its columns without data (cells of 0), and a gap longer than the columns left advances the clock by arithmetic (merging empty columns), not by a loop over the hops.
- **Waveform:** the same columns, with each channel's smallest and largest sample, scaled to 16-bit integers (the smallest rounded down and the largest up of the value times 32,768, clamped to -32,768 and 32,767).
- **Accumulation:** each cell sums power in single precision while it is open (2 MiB for 1,024 × 512 cells), in a fixed order; rounding to single precision is deterministic. Finished cells keep the sums for merging and are quantized only when the stream ends.

### dB scale and quantization

- **Reference:** 0 dB is the power of the bin a full-scale sine is centered on (the window's coherent gain taken out, as Heinzel et al. normalize amplitude spectra). A row is the mean power of its bins, so the sine itself reads lower in its row. The Hann window shares its power over the center bin and the two neighbors as 1 : 0.25 : 0.25 (1.5 in all, against the center bin's 1). Where both neighbors are in the row the sine reads 10 log10(1.5 / binsPerRow) dB: -4.3 dB at 4 bins a row (4,096 points, 44.1 and 48 kHz), -7.3 at 8 (96 kHz), -10.3 at 16 (192 kHz), -13.3 at 32 (352.8 kHz); where one neighbor is in the next row (the sine's bin at the row's edge, and always at 2 bins a row) 10 log10(1.25 / binsPerRow): -2.0 dB at 2 bins (22.05 kHz, `sine-bin-22050`), -5.1 at 4; with 1 bin a row (1,024 points, 8 kHz) 0 dB (the row's bin is the center). **Chosen basis, and why:** the mean keeps a row a power per bin, which is what noise and a lossy coder's floor are, comparable between rows and between files of one rate; a sum over the row would make a sine comparable across rates but noise would then climb 3 dB each time the rate doubles. Figures across sample rates are therefore comparable for the shape of a spectrum and for noise at equal bin width, not for a tone's absolute level. Noise reads lower per bin as the FFT size grows (10 log10 of the bin width), which the `spectrogram` article says.
- **Grid cells:** whole dB, offset by 200 and clamped to 1 to 220, so a cell is a byte (-199 to +20 dB); 0 means no data. The quietest 24-bit quantization noise per bin is about -177 dB at 4,096 points (**estimate**: -144 dBFS spread over 2,048 bins), inside the range.
- **Spectrum rows and figures:** tenths of a dB, as `short`.
- **Rounding, one rule for every figure:** to the nearest, halves up: `floor(x * scale + 0.5)` with scale 1 for grid cells and 10 for tenths, where x comes from the deterministic logarithm (below), then clamped. Flooring would show a peak of 32,767 in 16 bits (20 log10(32,767 / 32,768) = -0.0003 dB) as -0.1 dBFS; rounded to the nearest tenth it reads 0.0. Quantization is for size and reading, not to hide differences between platforms; the arithmetic is made deterministic instead.

### JSON

```json
"measurements": {
  "status": "complete", "samples": 13230000, "firstPosition": 0, "lostSamples": 0, "gaps": 0,
  "sampleRate": 44100, "channels": 2, "channelsMeasured": 2,
  "spectrum": { "fftSize": 4096, "rows": 512, "binsPerRow": 4,
    "rows": { "unit": "dB", "valuesPerUnit": 10, "combined": [-412, -398, …], "perChannel": [[-410, …], [-415, …]] },
    "cutoff": { "isFound": true,
                "frequency": { "value": 17054, "unit": "Hz" }, "transitionStart": { "value": 16559, "unit": "Hz" },
                "stepFrequency": { "value": 16753, "unit": "Hz" }, "step": { "value": 60.4, "unit": "dB" },
                "framesShowing": 644, "framesWithSignal": 644 } },
  "levels": { "samplePeak": { "value": -0.3, "unit": "dBFS", "perChannel": [-0.3, -0.5] }, … },
  "spectrogram": { "columns": 1000, "rows": 512, "hopsPerColumn": 8, "hopSamples": 2048, "unit": "dB", "valueOffset": 200, "noData": 0,
                   "power": "<base64, column by column>" },
  "waveform": { "columns": 1000, "unit": "16-bit sample codes", "minimum": "<base64 of int16 little-endian, channel by channel>", "maximum": "…" }
}
```

- **One shape per figure:** a single value is `{ "value", "unit" }` (null for minus infinity), the same figure per channel adds `"perChannel": [...]` in that unit, and `isFound`-style booleans and plain counts (`samples`, `framesShowing`) are bare. No figure carries its unit in a sibling key.
- **Arrays state their scale:** the spectrum rows (`spectrum.rows`) are integers with `unit` and `valuesPerUnit` (10 values per dB), so a reader divides by it; the grid states its `valueOffset`. Neither uses a floating-point number to say what a row or a column is.
- **No doubles for geometry:** the row spacing is `sampleRate * binsPerRow / fftSize` hertz (43.06640625 Hz at 44,100 Hz, 4,096 points and 4 bins a row) and the time of a column is `hopsPerColumn * hopSamples / sampleRate` seconds (0.371519… s here); the JSON gives the integers, never the quotient (the hop is half the FFT size, `hopSamples`).
- `spectrogram` and `waveform` are written only with `--measure-grids` (decision 7). A grid of 1,024 × 512 bytes is 512 KiB, 683 KiB in base64; the waveform of 8 channels about 43 KiB.
- **Sizes without grids:** about 3 KB per channel for the spectrum (a few more with the cutoff) and well under 1 KB for the other figures (**estimate**), so a measured stereo stream adds under 10 KB, and a 32-channel stream about 100 KB; channels beyond 32 add nothing.

### Text and CSV

As under [CLI](#cli). The text never prints the spectrum rows or the grids, as it never prints per-frame series.

### Descriptions, topics and articles

- Measurements are not nodes, but every figure the writers print carries a description and a topic, as nodes do: constant strings in the host (`MeasurementText`, as `VerificationText` holds the verification's), one or two sentences on what the figure is, citing the specification section where there is one, without verdicts. `--explain` prints them; the GUI shows them as hover text. A test walks the measurements of every measured fixture and fails on a figure without a description or with a topic that does not exist, as `DescriptionTests` does for nodes.
- The loudness series carries its `Description`, as every series does.
- **Stated and measured stay apart.** Loudness appears in files as stated values: the BWF `bext` chunk's (`Integrated Loudness` and the others), ReplayGain's, iTunNORM, USAC's `loudnessInfo`, AC-3's dialogue normalization. The measured figures use the `bext` names on purpose (one concept, one name), so each description and article says which kind it is: a measured figure is "computed from the decoded audio", a stated one "written by the file's creator, not checked here". Neither text compares them or calls a difference an error.
- **Articles,** each 100 to 300 words, from checkable sources only (the specifications, the literature, the tool cross-checks, committed fixtures):
  - `measurements`: what is measured, from which samples (decoded, untrimmed, the presentation TrueHD decodes, the lossy stream of a WavPack hybrid file), stated against measured, the budget, determinism and quantization;
  - `spectrogram`: the short-time Fourier transform, window, frame and hop, the dB reference, why noise reads lower in finer bins, how the grid is bounded;
  - `spectral-cutoff`: the measurement, its figures, what a sharp edge is consistent with (an encoder's lowpass, a mastering filter, resampling, an anti-alias filter), what its absence does not show (a quiet decode under dither, mixed content), and the observations the committed fixtures back;
  - `sample-peak-and-true-peak`: inter-sample peaks, BS.1770 Annex 2, dBTP;
  - `loudness`: BS.1770 K-weighting, channel weights, gating, LUFS and LKFS, EBU R 128's target;
  - `loudness-range`: EBU Tech 3342;
  - `dc-offset`, `clipping`, `digital-silence`, `bit-depth-usage` (linked with `wasted-bits`);
  - updated: `lowpass`, `coded-bandwidth`, `verification` (one decode pass), `pcm-samples`, `wasted-bits`, and the articles that name a stated loudness, `bwf-broadcast-extension` and `replaygain` first (then `usac`, `itunes-metadata` and `dialogue-normalization` where they name one), each with a sentence that the measured loudness is another figure.
- **The `lowpass` article's rework.** Today it calls the encoder's setting "a cutoff frequency" and "its cutoff" ("LAME records its cutoff in the LAME tag's Lowpass field"), which collides with the measured spectral cutoff, and it says "A low cutoff at a high bitrate can point to audio that was encoded more than once", an inference no fixture backs. The rework:
  - calls the encoder's setting the **lowpass frequency** throughout, and "cutoff" only for the measured edge, linked to `spectral-cutoff`;
  - drops "the analyzer does not decode the audio, so it cannot measure the cutoff of other files" (it can, with decoding) and says that a spectral cutoff is measured from decoded audio and may match, or not, the lowpass frequency a tag states;
  - removes the "encoded more than once" sentence; what an edge does and does not show goes into `spectral-cutoff`, only as far as committed fixtures back it.
- **One concept, one name:** "spectral cutoff" is the measured edge; "lowpass frequency" is the encoder's setting (the LAME tag's Lowpass); "coded bandwidth" stays the figure the coded data states. The three articles link each other and never use one name for another.

### Findings

- **Recommendation:** measurements raise no finding about the audio. A cutoff, a clipped run, a padded bit depth or a loudness is a figure; a finding would read as a verdict and, through `--fail-on info`, act like one (decision 6).
- **The one exception proposed:** `measure.non-finite-samples`, a warning, for float samples that are NaN or infinite: the file holds something no reader can play as written, which is the plan's definition of a warning. It points to the stream's node.
- **The host's own:**
  - `measure.limit`, info: "Measuring stopped after 1,234.5 s of the stream: its work passed the budget of 34,359,738,368 work units that the file's size allows."
  - `measure.failed`, error: a measurement's bug, with the exception's type and message; that stream's measurements are dropped (status `Failed`) and the analysis carries on. The fuzzer fails on it, as on `decode.failed`.
  - `measure.streams-limited`, info: more streams than the measurement memory bound allows (below); the rest are not measured.
  - `measure.channels-limited`, info: more than 32 channels; the first 32 are measured.
  - `measure.positions-jump`, info: a block's position was below the next expected one; measuring stopped there.
  - `measure.format-changed`, info: a block's channels or format differed from the declared output; the figures cover what came before.
- Each code gets its row in the finding-coverage test.

## Algorithms

### FFT

- **In-house, recommended (decision 9):** a radix-2 real FFT through a complex FFT of half the size, with the split step of Sorensen et al. (1987), in double precision. The prototype's version is about 150 lines; its optimized form (per-stage contiguous twiddles, split real and imaginary arrays, no bounds checks, an optional `Vector128` butterfly loop) is bit-identical to the plain one.
- **Candidates for following a permissive implementation,** their license texts read for this design (fetched from each project's repository on 2026-10-26):
  - KISS FFT (Mark Borgerding): BSD-3-Clause (`COPYING` and `LICENSES/BSD-3-Clause`);
  - PocketFFT (Max-Planck-Society; the C++ version's `LICENSE.md`): BSD-3-Clause;
  - PFFFT (Julien Pommier, with UCAR's FFTPACK-derived code; marton78's fork `LICENSE.txt`): a BSD-like license that asks for its notices in source and binary distributions;
  - muFFT (Hans-Kristian Arntzen): MIT;
  - Math.NET Numerics: MIT, but a library dependency, which "no third-party audio libraries" and the plan's habit of writing its own code argue against.
  - Excluded: FFTW (GPL) and FFmpeg's FFT code (LGPL).
- **Why in-house:** the transform is short, the determinism rules (no FMA, own tables, fixed order) are easier to hold in code written for them than in a port, and a power-of-two radix-2 transform is textbook (Cooley and Tukey 1965). Following KISS FFT or PocketFFT with its notice is the alternative if a mixed-radix or faster transform is wanted later.

### Window, size and hop

- **Window:** periodic Hann. Its highest sidelobe is -31.5 dB and its sidelobes fall 18 dB an octave (Harris 1978, Table 1), so 1 kHz away from a strong component (about 90 bins at 4,096 points and 44.1 kHz) its leakage is far below 16-bit quantization noise (**estimate** from those figures); the prototype's steps of 34 to 69 dB down to the quantization floor of about -129 dB confirm it in practice. Blackman-Harris would buy a cleaner stopband near the edge at twice the main lobe, which widens the measured transition; not needed.
- **Size:** by sample rate, 85 to 170 ms, 10.8 to 11.7 Hz a bin at the common rates (see [Channels, rates, widths and DSD](#channels-rates-widths-and-dsd); decision 10).
- **Hop:** half the frame. The sum of Hann windows shifted by half a frame is constant (1), so every sample's *amplitude* is weighted fully across the two frames it falls in; the sum of their *squares*, which is what a power spectrum averages, is not constant but runs from 0.5 (where the two windows are equal) to 1, so a short event is weighted between half and fully in the averaged power depending on where it falls. Averaging follows Welch (1967). A hop of a whole frame halves the cost (6.1 against 12.2 ns a sample, first prototype) but weights the samples at frame edges near zero, so short events between frames are missed; decision 10 keeps half.

### Channels

- Each channel is transformed on its own; power is averaged across channels for the combined spectrum and the grid, never computed from a downmix, which would cancel content in antiphase.
- The LFE (`Speaker.LowFrequency` and `Speaker.LowFrequency2`) is left out of the combined figures (above).
- **Mid/side, later:** the mid and side spectra are (X_L ± X_R) / 2 from the complex bins of the two channels already transformed, with no further FFT. They show where a joint stereo coder cut the side channel lower than the mid. They wait for fixtures that show it.

### Spectral cutoff

The method, as prototyped in the second prototype (the first prototype's frame share kept every frame's levels, which grows with the stream; the bounded method below replaces it, and the thresholds and the table are the new method's):

1. **Average spectrum:** the mean power of each bin over all frames, per channel and combined, in dB.
2. **Step search:** for each candidate bin k from 2 kHz to Nyquist less 1 %, the mean level of a band of 1 kHz under k against the mean level of a band of the same width above k + 2 bins, clipped at the top of the search. The largest difference is the step (the lowest k on a tie). Candidates also stop at the last row that still has a row above it (row 505 of 512), so the row counters of step 4 cover every candidate. Rates below 8,000 Hz are not searched (status text "not searched: the sample rate is below 8,000 Hz").
   - **Why from 2 kHz and not 1 kHz:** a start at 1 kHz was tried in the second prototype. From 1 kHz the largest step of the plain pink noise source is 6.2 dB, of brown noise 10.4 dB and of LAME `-V 0` 6.2 dB, all at the first candidate (1,001 Hz): a spectral slope, not an edge, and it would be printed as "largest step 6.2 dB, at 1,001 Hz". From 2 kHz they are 2.3, 4.6 and 2.4 dB. A real bass-heavy recording can have a steeper slope than brown noise (**unverified**: no music was in the prototype's inputs), which the 25 dB threshold below keeps clear of.
3. **Edge and transition start:** *as built (the prototype's rule was the highest bin more than 6 dB above the mean level of everything above the step, on the average spectrum; see "As built: step 1")*: the highest candidate row above the step's at which at least half of the frames with signal show their own step (the counters of step 4, read at that row), moved down by W - 1 rows (W the band's width in rows: the first row of the band under it); that is the reported cutoff frequency (**the edge**). **Transition start:** the lowest bin, from the step downward, still more than 3 dB below the band under it.
4. **Consistency over time, in one bounded pass.** The edge is known only at the end of the stream, so the frames cannot be classified by it, and "the loudest frame" is known only at the end too. So the host counts, for every candidate row, how many frames show a step there, and at what level:
   - **Per frame,** for each channel and for the combined power, the bins are reduced to 512 rows of mean power and sums are taken over the rows from the top down (suffix sums), so that a band near the edge is not added to the bass: with 24-bit and float sources the bass is up to 1e16 times the power near the edge, and a prefix sum from row 0 would lose the band's low digits (**estimate**, not prototyped; the second prototype used prefix sums). For each candidate row r the band under it has W rows (W = 1 kHz in rows, 23 at 44.1 kHz and 4,096 points) and the band above it W rows from r + 1, clipped at the top of the search. The frame's **own step at r reaches T** when the mean power under is at least 10^(T/10) times the mean power above: a comparison of products, no logarithm. (This is the mean of power in each band; step 2 uses the mean of dB, a different mean, so a frame's own step and the average step are not the same quantity and T is not the step threshold.)
   - **Counters:** a histogram of 512 rows by 72 levels, two 32-bit counters per cell: `all` (frames) and `show` (frames whose own step reaches T). The level axis is the binary exponent of the mean power under r, read from the bits of the double (one bin per factor of 2, 3.01 dB; exponents -64 to +7, clamped): exact, deterministic, no logarithm. That is 295 KB for the combined spectrum and for each channel measured.
   - **T = 15 dB.**
   - **At the end,** for the step's row r\*: the reference level is the highest exponent that at least 1 % of the frames at that row reach (read from the same histogram, from the top), not the single loudest frame. **Frames with signal** are those at most 10 exponents (30.1 dB) below it; **frames showing the edge** are those of them counted in `show[r*]`. A frame whose under-band is more than 30 dB below the reference is counted as without signal, so a silent passage does not count against the edge (LAME `-b 128` for 20 s and then 10 s of digital silence: 431 of 431). One click cannot move the reference (a full-scale noise burst of 4,096 samples in a 30 s file took 2 of 644 frames out with the single-loudest-frame rule and 0 with the 1 % rule, second prototype).
   - **Cost:** 1.6 ns per sample of the channel it serves at 4,096 points (0.7 to 2.4 ns from 2,048 to 32,768 points, 5.1 ns at 1,024), added to the FFT's (see [Measured](#measured)).
5. **Found or not:** a cutoff is reported as found where the step is at least 25 dB and at least half the frames with signal show it. Otherwise the report says "none found" and gives the largest step and its frequency. Both thresholds come from the second prototype's table below and are fixed by the fixtures in the first pull request.

**The second prototype's table** (30 s of stereo pink noise with a 330 Hz tone, quantized to 16 bits; FFT of 4,096 points at 44.1 and 48 kHz, 2,048 at 22.05 kHz and 1,024 at 8 kHz, Hann, hop half; the step, its frequency, the cutoff (edge), the start of the transition and the frames of step 4 at T = 15 dB; the first prototype's code is gone, so its table is not reproduced and the figures below differ from it in the last digits of the edge and in the step, since the source, the edge rule and the band edges were rewritten):

| Input | Largest step | At | Cutoff (edge) | Transition from | Frames showing, of those with signal | Found |
| --- | --- | --- | --- | --- | --- | --- |
| LAME `-b 128` | 60.4 dB | 16,753 Hz | 17,054 Hz | 16,559 Hz | 644 of 644 | yes |
| LAME `-b 192` | 61.0 dB | 18,820 Hz | 19,121 Hz | 18,659 Hz | 644 of 644 | yes |
| LAME `-b 320` | 62.7 dB | 20,209 Hz | 20,424 Hz | 20,037 Hz | 644 of 644 | yes |
| LAME `-V 2` | 60.8 dB | 18,831 Hz | 19,121 Hz | 18,626 Hz | 644 of 644 | yes |
| LAME `-V 5` | 58.1 dB | 16,731 Hz | 16,979 Hz | 16,538 Hz | 644 of 644 | yes |
| LAME `-V 0` | 2.4 dB | 2,003 Hz | none |  | 0 of 644 | no |
| LAME `-b 128`, 48 kHz | 59.4 dB | 16,734 Hz | 17,062 Hz | 16,523 Hz | 702 of 702 | yes |
| oggenc `-q 2` | 69.0 dB | 16,591 Hz | 16,731 Hz | 16,548 Hz | 644 of 644 | yes |
| oggenc `-q 5` | 53.0 dB | 20,618 Hz | 20,973 Hz | 20,349 Hz | 644 of 644 | yes |
| oggenc `-q 8` | 2.4 dB | 2,003 Hz | none |  | 0 of 644 | no |
| FFmpeg `aac` 96 kbps | 42.1 dB | 15,913 Hz | 17,119 Hz | 15,859 Hz | 644 of 645 | yes |
| FFmpeg `aac` 128 kbps | 68.5 dB | 17,280 Hz | 17,367 Hz | 17,237 Hz | 645 of 645 | yes |
| FFmpeg `aac` 192 kbps | 64.2 dB | 19,348 Hz | 19,703 Hz | 19,305 Hz | 645 of 645 | yes |
| libopus 96 kbps (48 kHz) | 48.8 dB | 20,367 Hz | 20,906 Hz | 20,039 Hz | 702 of 702 | yes |
| LAME `-b 64`, 22.05 kHz | 64.1 dB | 10,099 Hz | 10,218 Hz | 9,981 Hz | 644 of 644 | yes |
| LAME `-b 32`, 8 kHz | 44.5 dB | 3,938 Hz | 3,953 Hz | 3,812 Hz | 467 of 467 | yes |
| brickwall 11 kHz on the lossless source | 68.9 dB | 11,068 Hz | 11,208 Hz | 10,993 Hz | 645 of 645 | yes |
| brickwall 16 kHz on the lossless source | 67.5 dB | 16,064 Hz | 16,204 Hz | 15,988 Hz | 645 of 645 | yes |
| brickwall 16 kHz, TPDF dither | 63.5 dB | 16,064 Hz | 16,161 Hz | 15,988 Hz | 645 of 645 | yes |
| natural rolloff, one 2-pole 16 kHz lowpass | 19.1 dB | 21,781 Hz | none |  | 644 of 644 | no |
| natural rolloff, two cascaded | 21.9 dB | 20,586 Hz | none |  | 644 of 644 | no |
| natural rolloff, four cascaded | 21.0 dB | 18,938 Hz | none |  | 644 of 644 | no |
| LAME `-b 128`, shibata noise shaping | 60.4 dB | 16,753 Hz | 17,054 Hz | 16,559 Hz | 644 of 644 | yes |
| LAME `-b 128`, -30 dB, TPDF dither | 33.5 dB | 16,613 Hz | 16,721 Hz | 16,538 Hz | 644 of 644 | yes |
| LAME `-b 128`, -45 dB, TPDF dither | 20.1 dB | 16,559 Hz | none |  | 630 of 644 | no |
| LAME `-b 128`, -60 dB, TPDF dither | 6.7 dB | 16,527 Hz | none |  | 0 of 644 | no |
| source resampled 44.1 to 48 kHz | 7.6 dB | 22,746 Hz | none |  | 0 of 702 | no |
| the 16-bit source (pink noise and a 330 Hz tone) | 2.3 dB | 2,003 Hz | none |  | 0 of 644 | no |
| white noise | 0.1 dB | 2,132 Hz | none |  | 0 of 644 | no |
| brown noise | 4.6 dB | 2,003 Hz | none |  | 0 of 644 | no |
| LAME `-b 128` for 6 s, then the source (20 % lossy) | 2.3 dB | 2,003 Hz | none |  | 0 of 644 | no |
| LAME `-b 128` for 15 s, then the source (50 %) | 3.2 dB | 16,527 Hz | none |  | 291 of 644 | no |
| LAME `-b 128` for 24 s, then the source (80 %) | 7.2 dB | 16,538 Hz | none |  | 513 of 644 | no |
| LAME `-b 128` for 20 s, then 10 s of digital silence | 49.3 dB | 16,688 Hz | 16,904 Hz | 16,548 Hz | 431 of 431 | yes |

For an input where nothing is found, the frames column counts the frames of step 4 at the row of the largest step.

- **Margins of the thresholds:** every input with a lowpass has a step of 33.5 dB or more (the lowest is LAME `-b 128` lowered by 30 dB and dithered; the first prototype read 26.6 dB for it), and every input without one 21.9 dB or less (the highest is the natural rolloff of two cascaded lowpass sections). 25 dB sits between them, 3.1 dB above the rolloff and 8.5 dB below the dithered case. The frame share does not separate them: the rolloff cases show a step in every frame because they have one in the average too.
- **LAME's own statements** (the tag's Lowpass byte and the band `--verbose` prints): `-b 128` and `-V 5` tag 17,000 Hz, band 16,538 to 17,071 Hz; `-b 192` tag 18,600 Hz, band 18,671 to 19,205 Hz; `-b 320` tag 20,500 Hz, band 20,094 to 20,627 Hz; `-V 2` tag 18,500 Hz, same band as `-b 192`; `-V 0` tag 22,100 Hz, filter disabled; `-b 128` at 48 kHz tag 17,000 Hz, band 16,452 to 17,032 Hz. Every cutoff lies inside the printed band except the 48 kHz one, 30 Hz above it (three bins); every transition starts within 75 Hz of the band's lower end (0 to 71 Hz). The tag's value is a rounded setting, not the edge.
- **Quiet passages:** the average is dominated by loud frames, and the frame share counts only frames with signal. LAME at 128 kbps decoded, lowered by 30 dB and dithered with TPDF still gives a step of 33.5 dB in 644 of 644 frames.
- **Noise and dither:** TPDF dither lifts the floor above the edge. At -45 dB the step falls to 20.1 dB and at -60 dB to 6.7 dB: no cutoff is found. Noise-shaped dither (FFmpeg's `shibata`) at full level leaves 60.4 dB. Strong dither on a quiet recording can hide an edge, which is a missed edge, never an invented one; the `spectral-cutoff` article says so.
- **Intentionally band-limited masters:** a 16 kHz brickwall (FFmpeg's `firequalizer`) on the lossless source gives 67.5 dB in 645 of 645 frames, the signature of a lossy encode; with TPDF dither 63.5 dB. The report cannot tell them apart and does not try.
- **Natural rolloff:** a 16 kHz digital lowpass of 2 poles gives a step of 19.1 dB, two sections 21.9 dB and four 21.0 dB (the first prototype's four cascaded sections gave 18.1 dB), all below 25 dB and so not found, although every frame shows them. The steps come largely from the digital filter's zero at Nyquist; this is the weakest margin of the method and the reason the step threshold is not lower.
- **Mixed content:** half or more of a file from a lossy source and the rest not is not found: LAME `-b 128` for 15 s and then the source gives a step of 3.2 dB, for 24 s (80 %) 7.2 dB, although 291 of 644 and 513 of 644 frames show an own step of 15 dB at the step's row. The average spectrum's power mean is lifted above the edge by the frames that have content there. Finding such an edge from the counters (the row with the most frames showing) would use data the pass already holds; it is not built, because a sparse real spectrum (a solo instrument) would also show own steps in many frames (**unverified**, no music in the prototype).
- **22.05 versus 24 kHz Nyquist, low rates:** the search is relative to Nyquist, so it works at 22.05, 44.1 and 48 kHz (LAME at 128 kbps: 17,054 Hz at 44.1 kHz, 17,062 Hz at 48 kHz) and at 8 kHz (LAME `-b 32`: 3,953 Hz of Nyquist 4,000 Hz). An edge just below Nyquist is often an anti-alias or resampling filter; no fixture backs a figure for that yet, so the article says only that such filters exist.
- **Upsampling:** the source resampled from 44.1 to 48 kHz by FFmpeg's default resampler gave only a 7.6 dB step at 22,746 Hz in 0 of 702 frames: pink noise has little energy near 22 kHz, so the step finder does not see where the content ends. Reporting upsampling needs a second figure, the highest frequency whose level stays a set distance above the noise floor, which is left for later with its own fixtures.
- **No lowpass:** LAME `-V 0` (its lowpass disabled) and oggenc `-q 8` give no step above 2.4 dB, as the source itself (2.3 dB).

### Levels

Exact on integer samples; floats in double. The sums are wider than 64 bits where they must be: the sum of squares of full-scale 24-bit samples passes `long.MaxValue` after 131,071 samples (about 3 s), that of 32-bit samples after 2, and the DC sum of 32-bit samples after about 2^32. So each block adds its squares in a 64-bit partial sum (or `UInt128` where the width needs it) that is folded into an `Int128`/`UInt128` accumulator before it can overflow; peaks, ORs and run counts stay 64-bit. The 2.0 ns of the first prototype was measured with 64-bit sums; timed again with these accumulators in step 2 it is about 2.0 ns, 1.7 to 2.3 over three runs (see [As built: step 2](#as-built-step-2)):

- **Sample peak:** the largest magnitude per channel, in dBFS (the most negative integer counts as full scale), rounded to the nearest tenth (see [dB scale and quantization](#db-scale-and-quantization)). A channel whose samples are all 0 has no peak: minus infinity, printed `-inf`.
- **RMS:** per channel, in dBFS, a full-scale square wave reading 0 dB (the convention is stated in the article; AES17's sine-referenced convention reads 3.01 dB higher, **unverified** which the tools use).
- **DC offset:** the mean per channel, in percent of full scale and dBFS.
- **Clipping runs:** runs of at least 3 consecutive samples at the extreme codes (integers) or at or beyond full scale (floats), per channel: the number of runs, the samples in them, the longest. Three is a convention of this design, not a specification's; the article says so. A run ends at a gap, and a run at one extreme followed at once by the other is two runs.
- **Digital silence:** samples that are exactly 0 in every channel at the start, at the end and in total; not counted across a gap.
- **Bit-depth usage:** for integers, the OR of every sample gives the low bits that are never set ("Low Bits Unused: 8 of 24") and the highest magnitude reached; for floats, two booleans, `HasOnly16BitValues` and `HasOnly24BitValues`: whether every sample is a whole multiple of 2^-15 (2^-23) in [-1, 1). They are figures, not labels; the text writes them as "Has Only 16-Bit Values: yes". FLAC's per-subframe wasted bits (`wasted-bits`) are the coded counterpart.

### Loudness and loudness range

- **ITU-R BS.1770-5** (11/2023; its text was read for this revision from itu.int; earlier revisions were not compared), Annex 1:
  - **K-weighting,** a high-shelf pre-filter and a highpass (the "RLB" filter). The Recommendation gives the coefficients for 48 kHz only (Tables 1 and 2) and says that other rates "will require different coefficient values, which should be chosen to provide the same frequency response that the specified filter provides at 48 kHz". At **48,000 Hz the Recommendation's published coefficients are used**, as written; at every other rate the coefficients are derived from the filters' analog parameters, as libebur128 does (decision 13). The derived coefficients at 48 kHz differ from the published ones by 1.8 × 10^-15 in the cascaded polynomial (second prototype), so the choice changes no figure, but the published ones keep the EBU test set exact by definition.
  - **The derivation,** from libebur128's source (MIT; read on 2026-10-26): shelf f0 = 1681.974450955533 Hz, Q = 0.7071752369554196, gain G = 3.999843853973347 dB, Vh = 10^(G/20), Vb = Vh^0.4996667741545416, K = tan(π f0 / fs), a bilinear transform with K; highpass f0 = 38.13547087602444 Hz, Q = 0.5003270373238773. Inverting the bilinear transform of the Recommendation's 48 kHz coefficients, from the Recommendation alone, gives f0 1681.97445095553 Hz, Q 0.7071752369554184, G 3.9998438539733447 dB and exponent 0.4996667741545441 for the shelf, and f0 38.13547087611304 Hz, Q 0.5003270373250421 for the highpass: the same to 11 to 14 digits (second prototype). So the parameters are recoverable without an implementation, which is option (b) of decision 13.
  - **Lowest rate: 8,000 Hz.** The derivation is unstable below fs = 2 f0 = 3,364 Hz, where tan(π f0 / fs) turns negative (the largest pole radius is 1.31 at 3 kHz and 1.98 at 2 kHz, 0.94 at 4 kHz; second prototype). Above that it is stable but its response drifts from the 48 kHz one, over 20 Hz to 0.45 of the rate: 1.4 dB at 4 kHz, 0.50 dB at 8 kHz, 0.29 dB at 11.025 kHz, 0.15 dB at 16 kHz, 0.08 dB at 22.05 kHz and under 0.05 dB from 32 kHz up. Loudness is measured from 8,000 Hz (a choice of this design: the drift stays at or under 0.5 dB); below it the status is `NotPossible` with the reason "loudness needs a sample rate of 8,000 Hz or more".
  - **Gating blocks:** 400 ms to the nearest sample (libebur128: `(rate + 5) / 10` samples per 100 ms, four of them), overlapping by 75 %, so a block every 100 ms; the interval ends at the end of a gating block and an incomplete block at the end is not used. A stream shorter than 400 ms has no integrated loudness ("none: shorter than 400 ms"). Blocks never span a gap (see [Channels, rates, widths and DSD](#channels-rates-widths-and-dsd)).
  - **Channel weights.** BS.1770-5 Annex 1 gives 1.0 for left, right and center, 1.41 for the surround channels and leaves the LFE out; Annex 3 (Table 4, read) gives the rule by position for every layout: with an elevation under 30 degrees, 1.00 for an azimuth under 60 degrees, **1.41 for 60 to 120 degrees (both included)** and 1.00 from over 120 degrees to 180; at 30 degrees of elevation or more, 1.00. Table 5 lists it per loudspeaker: M±060, M±090 and M±110 1.41; M±135, M±180, M±030, M+000 and every height and bottom position 1.00. Applied to `Speaker` by the layout the stream states:
    - `FrontLeft`, `FrontRight`, `FrontCenter`, `FrontLeftOfCenter`, `FrontRightOfCenter`, `BackCenter`: 1.0;
    - `SideLeft`, `SideRight`: 1.41 (azimuth ±90);
    - `BackLeft`, `BackRight`: **by the layout.** With a `SideLeft` and `SideRight` in the layout they are the rear pair of a 7.1 (M±135): 1.0. Without them they are the surround pair (M±110, as 5.0, 5.1 and quadraphonic have it, the `Speaker` comment's convention): 1.41;
    - `TopFrontLeft`, `TopFrontCenter`, `TopFrontRight`, `TopBackLeft`, `TopBackCenter`, `TopBackRight`, `TopCenter`, `TopSideLeft`, `TopSideRight`, `BottomFrontLeft`, `BottomFrontCenter`, `BottomFrontRight`: 1.0 (elevation of 30 degrees or more);
    - `FrontLeftWide`, `FrontRightWide`: 1.0, **unverified assumption**: no source that can be checked here gives their azimuth (BS.1770-5 Table 5 has M+-060 at 1.41 only in configuration H, 9+10+3, which does not name them as wide; ISO/IEC 23091-3 and SMPTE ST 2036-2 were not available). At 60 degrees or more the rule would give 1.41, which raises a stream's loudness by up to 1.5 dB where they carry the signal; change the weight (one line in `LoudnessWeights`) once a source confirms the azimuth;
    - `LowFrequency` and `LowFrequency2`: left out, as Annex 3 says of "the LFE channels".
    - **No layout (`ChannelLayout` null) and a `Discrete` channel:** mono is one channel of weight 1.0 (a full-scale 997 Hz sine in the left, center or right input reads -3.01 LKFS, Annex 1); two channels without a layout are left and right, both 1.0; with three or more channels and no stated positions (or a `Discrete` among them) loudness is `NotPossible` ("the channel positions are not stated"), since a weight would be a guess (decision 18).
  - **What the tools do** (black-box; FFmpeg 6.1.1's `ebur128` filter with a 997 Hz sine in one channel at a time; libebur128's source for its map): FFmpeg weights 1.41 every channel at a side or rear position, including the rear pair of a 7.1, `BC` of a 6.1 and `TBL` of a 7.1.4, which Table 4 weights 1.0; its mono, stereo, center and front channels read 1.0, its LFE is left out. libebur128 weights 1.41 only M±060, M±090 and M±110, as the Recommendation does, and its default map leaves channels above the sixth unused, so a cross-check of a 7.1 sets its map explicitly (`Mp135` and `Mm135` for the rear pair). So the FFmpeg cross-check is asserted on mono, stereo, 3.0, 5.0 and 5.1 (back or side surrounds) and quadraphonic, where the three agree; for 7.1, 6.1 and layouts with top-back channels it compares FFmpeg's figure with the one computed from FFmpeg's observed weights, and libebur128 carries the assertion. Only FFmpeg 6.1.1 ran for this; the CI image's version was not run (**unverified**).
  - **Gating:** an absolute gate at -70 LKFS, then a relative gate 10 LU below the loudness of the blocks above the absolute one; the integrated loudness is -0.691 + 10 log10 of the weighted mean over the blocks that pass (equations 4 to 7, read).
- **EBU R 128 and Tech 3341:** momentary loudness (400 ms, rectangular) and short-term loudness (3 s), reported as their maxima; the loudness unit is LUFS, one name for what BS.1770 calls LKFS, which the article mentions once. A stream shorter than 3 s has no short-term maximum.
- **EBU Tech 3342:** loudness range from short-term values taken at least 10 times a second (an overlap of 2.9 s or more; here every 100 ms), absolute gate -70 LUFS, relative gate 20 LU below the power mean of the values above the absolute gate, the spread between the 10th and the 95th percentile (the document's reference code picks `sorted[round((n - 1) p / 100 + 1)]`, p = 10 and 95).
- **Bounded memory, exact gates (as built after Gate 3):** the energy of each gating block and of each short-term value that passes the absolute gate is kept, up to 131,072 of each (2^17: 1 MiB of doubles each, 3.6 hours at 10 a second), and the integrated loudness and the loudness range are computed from them exactly: BS.1770-5 equation 7 keeps or drops each block on its own against the relative gate, and so does Tech 3342 each short-term value. Sums run in the order the values were kept, which depends only on the length of the stream; the percentiles come from the values sorted in place (a sort of at most 131,072 doubles, charged with the first block as part of `LoudnessMeter.AllocationUnits`: `SortUnits` = 2^25 units, **measured:** 131,072 uniform random doubles sorted in 11 ms in the fix run and in 20 to 30 ms in the review's runs, at the reference's time per unit of about 1 ns, so 2^25 units (about 34 ms) is 1.1 to 3 times the sort; `TheSortOfTheLoudnessRangeTakesNoMoreThanItsCharge` times the sort against its charge with the bound of the other work tests, 2). The per-stream estimate covers the lists with the room a growing array needs. Until the cap the lists grow with the stream (8 bytes a block and a short-term value, doubling), so `MemoryTests.DecodingAndMeasuringALongWaveFileHoldsNothingPerSlice` allows that growth (81.7 bytes a slice of 64 KiB of stereo 44.1 kHz noise, measured) on top of its bound for other retention. **Beyond 131,072 values of either kind** (a stream of more than about 3.6 hours) the gates work on histograms of 0.01 LU bins from -70 to +80 LUFS (15,000 bins, each holding the count and the sum of its values' energies; 240 KB each, one for the gating blocks and one for the short-term values), filled for every stream, and `LoudnessFigures.IsGatedExactly` is false; the text report and the JSON (`gatingBlocks.isGatedExactly: false`) say so. **Why +80:** samples are limited to 1,024 times full scale (+60.2 dBFS); with the largest channel weights and the K-weighting gain a block stays under about +78.7 LUFS (**estimate**: 32 channels, each at +60.2 dBFS, at the weight 1.41 and a K-weighting gain of up to 4 dB), so no block reaches the top bin, which also takes anything louder, its energy sum exact. (The first version stopped at +30 LUFS, took a gate above it into the top bin, and read +42.2 LUFS for a stream at +45.0: `top-bin.wav`.)
  - **Error bound of the histogram path.** The relative gate lies in one bin; the bin's values are taken whole when their mean energy is above the gate and left out whole otherwise. If p is the share of the passing values that lie in that bin, the mean energy changes by at most the factor 1 / (1 - p), so the integrated loudness is wrong by less than -10 log10(1 - p) LU (not 4.34 p LU, which the first design stated and which holds only for small p). With p of 0.91, as in `gate-straddle-2.wav` (a quiet segment of one level at the gate), that is almost 10 LU: the whole-bin method read -19.0 LUFS where the exact value is -9.9, and FFmpeg's histogram does the same. A smooth distribution puts about 0.05 % of the blocks in one bin, 0.002 LU. The loudness range, whose percentiles come from the bins, is at 0.01 LU resolution in this path and its gate is subject to the same bound.
  - **The short-term series is bounded** like the grids: the values at 100 ms go into the histogram for the loudness range and into a series of at most 1,024 points, merging adjacent pairs as the stream grows (each point the energy mean of the 100 ms values it covers, in LUFS; the maximum short-term loudness is kept exactly, apart). One value each second for the whole stream would grow with its length.
  - The loudness range takes its percentiles from the sorted short-term values (the histogram's bins, at 0.01 LU, beyond 131,072 values), against Tech 3342's ±1 LU; the range is rounded to a tenth from the energies' own levels.
- **Rates other than 48 kHz:** the filters run at the stream's rate; 44.1 kHz and the high rates are tested against the cross-checks.

### True peak

- **BS.1770-5 Annex 2** (read): oversample, take the largest magnitude, report in dBTP. The Annex presumes 48 kHz and a 4× oversampling (a 48-tap, 4-phase FIR, whose 48 coefficients it lists as "one set that would satisfy the requirements"); it says that "incoming signals at higher sampling rates require proportionately less over-sampling (e.g. for an incoming signal at 96 kHz sample rate a 2× over-sampling would be sufficient)" and that meters with an oversampled rate of at least 192 kHz may indicate dB TP. So the factor is 4 below 96 kHz, 2 from 96 kHz up to 192 kHz and 1 (the sample peak) from 192 kHz, the same as libebur128's (read in its source). At 44.1 kHz the 4× rate is 176.4 kHz, below the Annex's 192 kHz for indicating dB TP; libebur128 does the same, and so does this design. The Annex gives no 2× filter; this design takes phases 0 and 2 of the 4× filter (the positions 0 and 1/2 of the same prototype lowpass), **unverified** against any reference until step 5 checks it against libebur128 and the Tech 3341 tolerance.
- **Prototype check of the 4× filter** (the Annex's coefficients, 48 kHz, second prototype): a stereo sine at fs/4 with amplitude 0.5 and phase 45° reads -5.98 dBTP (Tech 3341 signal 16 expects -6.0 +0.2/-0.4), fs/6 at 60° -6.32 (signal 17), fs/8 at 67.5° -6.03 (18), fs/4 at 1.41 amplitude +3.03 (19, expects +3.0). FFmpeg 6.1.1's `ebur128=peak=true`, whose true peak is resampler-based, reads -5.4, -6.0, -6.0 and +3.6 on the same files, 0.6 dB over the first and last, outside the tolerance, so the FFmpeg cross-check of true peak allows +0.7/-0.1 dB against our figure (measured on 6.1.1; asserted again with the CI's version) and the assertions on the synthetic overs rest on the Annex's expected values, not on FFmpeg. On the LAME `-b 128` decode FFmpeg reads -9.3 and the Annex's filter -9.25 dBTP.
- **Cost:** 48 multiply-adds a sample of a channel; 23.1 ns with the four phases in vector lanes (x86-64-v2), 12.5 ns where the x86-64-v3 build has 256-bit vectors (first prototype).
- **Skipping (estimate):** an interpolated value cannot exceed the sum of the filter's absolute taps times the largest sample magnitude among its 12 inputs. A window is skipped only where that bound, multiplied by 1 + 2^-46 (the rounding error of a sum of 12 products is about 2^-49.3 relative, so 2^-50 would not cover it), is below the peak found so far; the result is the same, and in music with an early loud peak most windows are skipped. Not prototyped.

## Determinism

Reports are compared byte for byte in the goldens on all six CI platforms and between the JIT and the NativeAOT build (`scripts/check_aot.sh`). Floating-point code can differ between machines for these reasons, and the design closes each:

### What can differ

- **Fused multiply-add:** `a * b + c` rounded once instead of twice. .NET is expected to emit it only for `Math.FusedMultiplyAdd` and the vector `FusedMultiplyAdd` methods (**unverified** as a general statement); the prototype found no automatic contraction on x64, in the JIT or in NativeAOT for x86-64-v3, which may use FMA instructions (below). Arm64 is not measured here (**unverified**); the goldens on the two Arm runners hold it. **The ban rests on the hash test, not on the language:** the C# and CLI specifications let an implementation compute floating-point intermediates at higher precision than their type (quoted from memory, **unverified** here), so nothing in the language promises two roundings; the rule is kept because a platform that contracts fails `MeasurementDeterminismTests` before it fails a golden.
- **The math library:** `Math.Sin`, `Math.Cos`, `Math.Tan`, `Math.Log10`, `Math.Exp` and `Math.Pow` call the platform's C library, whose results may differ in the last bit between glibc, macOS and Windows (**unverified** for .NET 10's current implementations; not measurable on one machine). `Math.Sqrt`, +, -, * and / are correctly rounded IEEE operations everywhere.
- **Vector width and horizontal sums:** `Vector<T>` is 256 bits on AVX2 and 128 on Arm, and a sum across lanes adds in an order that depends on the width. The BCL's vectorized reductions (`TensorPrimitives`) choose their own order and width and may change with the runtime.
- **NaN and out-of-range semantics of conversions, minimum and maximum.** Measured on .NET 10.0.401, x64 (second prototype):
  - `(int)NaN` is 0, a double-to-integer cast saturates (`(int)1e300` is 2,147,483,647, `(long)-1e300` is long.MinValue), and `Vector128.ConvertToInt64` does the same (NaN gives 0, 1e300 gives long.MaxValue);
  - `Vector128.ConvertToInt64Native` returns **long.MinValue** for both NaN and 1e300: the `*Native` variants give the hardware's answer;
  - `Vector128.Max(NaN, 1)` is NaN, in either operand order; `Vector128.MaxNative(NaN, 1)` is 1 and `MaxNative(1, NaN)` is NaN: the native variant returns an operand, as the x86 instruction does, and Arm's returns NaN (**unverified** on Arm).
  So the same call differs between its plain and its `*Native` form on one machine, and the `*Native` forms are what a platform is free to differ on. The plain forms are defined by the runtime and are the ones allowed.
- **Subnormals:** not a difference in value but in time; a recursive filter whose input has gone to silence can sit in the subnormal range, and every operation on a subnormal is slow (below).
- **Summation order:** parallel or reordered sums give different roundings.

### What was measured

The prototype computed the power spectra of 2^20 samples of a test signal (two integer-phase triangle tones and noise from a linear congruential generator), 511 frames of 4,096 points, 1,047,039 values, and hashed the raw doubles:

- **Identical** (SHA-256 prefix EDDB28414F3D2326 in every case): the JIT build with its defaults on this machine (AVX-512), with `DOTNET_EnableHWIntrinsic=0`, `DOTNET_EnableAVX=0`, `DOTNET_EnableAVX512F=0`, `DOTNET_TieredCompilation=0` and `DOTNET_TC_QuickJitForLoops=0`; the NativeAOT build for x86-64-v2 and for x86-64-v3 (which may use FMA instructions); and the optimized scalar and `Vector128` transforms against the plain one.
- **What breaking a rule does,** against that baseline:

| Variant | Raw values that differ | Largest relative difference | Cells that differ at 1 dB | At 0.1 dB |
| --- | --- | --- | --- | --- |
| `Math.FusedMultiplyAdd` in the butterflies | 95.89 % | 1.6 × 10^-9 | 0 | 0 |
| Twiddles and window from `Math.Cos` instead of the polynomial table (2,980 of 4,096 entries differ in the last bits) | 99.54 % | 9.8 × 10^-9 | 0 | 0 |
| `Math.Log10` for the dB values instead of the own logarithm | (same powers) | | 0 | 0 |
| Input rounded to single precision (a stand-in for a float FFT) | 100 % | 0.22 | 1,038 | 10,513 |

- **Reading it:** a last-bit difference changes almost every raw value but, here, no quantized cell. It still can: a value within 10^-8 dB of a step boundary flips, and over the grids of all fixtures on six platforms a flip somewhere is a matter of time. So quantization alone is not the guarantee; identical arithmetic is. Single precision is not an option for the spectra.

### The rules

They go into `CLAUDE.md` with the first pull request:

- **No fused multiply-add** in measurement code: no `Math.FusedMultiplyAdd`, no vector `FusedMultiplyAdd` or `MultiplyAddEstimate`.
- **No platform math library:** trigonometric tables come from a polynomial evaluated with + and * only (the prototype's Taylor series on the first octant, with symmetry), logarithms from the exponent bits and a series (the prototype's `Log10Det`). `Math.Sqrt` is allowed. **Own `Tan` and `Pow` too,** for the K-weighting derivation of decision 13 (`tan(π f0 / fs)`, `10^(G/20)` and `Vh^0.4996…`): `Tan` from the same sine and cosine polynomials with range reduction, `Pow` and `Exp` from the exponent bits and a polynomial. The derivation runs once per stream, so its speed does not matter; a hash test over the coefficients of every rate that the tests use holds it.
- **No `*Native` and no `*Estimate` API:** neither `ConvertToInt64Native`, `MaxNative`, `MinNative` and the other `*Native` methods, nor `ReciprocalEstimate`, `ReciprocalSqrtEstimate`, `MultiplyAddEstimate` and the other `*Estimate` ones: their results for NaN, out-of-range values and rounding are platform-defined (measured above). A test over the compiled `Encspect.Host` and `Core.Abstractions` assemblies' measurement namespace fails on a call of one, as `DecodingBoundaryTests` checks its boundary.
- **No BCL vectorized reductions:** no `TensorPrimitives`, and no `System.Linq` or other BCL sum, dot product, maximum or average over floating-point spans.
- **Fixed order:** every sum runs in an order that depends only on the data's length; channels are combined in channel order; parallel work (below) never changes it.
- **Vectors element-wise only:** `Vector128` (and `Vector256`, which runs as two 128-bit halves where 256 bits are not available) for lane-independent operations, never `Vector<T>`, never a horizontal sum; lanes are combined in scalar code in a fixed order.
- **No NaN and no huge value reaches the arithmetic:** non-finite inputs are counted and replaced with 0, and finite floats beyond 1,024 times full scale are clamped to that and counted, before anything else (a Float64 WAV holding 1e300 would give infinity and then NaN in the butterflies and filters).
- **Subnormals are flushed:** every recursive state (the K-weighting biquads, any later filter) is set to 0 where its magnitude is below 1e-30, at fixed stream positions (where `Position % 4096 == 0`) and at gaps and discontinuities, never at the end of a decoder's block, whose size would make the result depend on how the decoder cuts blocks (the chunking-independence test). Measured (second prototype, JIT with tiering off, the 48 kHz K-weighting in transposed direct form II, 20 million samples): zeros 134 ms (6.7 ns a sample); an impulse and then zeros **4,056 ms (203 ns a sample)**, because the states go subnormal after 4,517 samples (0.09 s at 48 kHz) and stay at ±4.9 × 10^-324 and ±2.6 × 10^-322 for good; the same with the flush every 4,096 samples 136 ms (6.8 ns), and with a flush every sample 140 ms. A review had measured 1,621 ms against 81 ms in another filter form; the effect is a factor of 20 to 30 either way, so a crafted file of one click and then silence would cost thirty times the time the work budget charges. 1e-30 is -600 dB, far below any figure. `MeasurementWorkTests` times the case "an impulse, then silence" against the case of zeros and fails on a ratio above 2.
- **Integers where exact:** peaks, sums for DC, clipping, silence and bit usage on integer samples are integer arithmetic.
- **Outputs on fixed grids:** figures are integers in fixed units (whole hertz, tenths of a dB), so no writer formats a double.
- **The kernels are compiled optimized:** each kernel method carries `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`, so the JIT compiles it fully optimized at its first call, bypassing tiering. Otherwise the hash test could run the unoptimized tier-0 code for its short input while the real analysis, after tiering up, runs tier-1 code (and the JIT is free to choose different instructions in each). The attribute is ignored by NativeAOT, which compiles optimized anyway.

### How it is held

- `MeasurementDeterminismTests`: the raw spectra of a synthetic signal hash to a constant committed in the test, on every platform; the same for the K-weighting filter's output (including an impulse followed by silence, after the flush), the true-peak interpolation and the derived coefficients for each rate. A platform that differs fails this test before any golden. The kernels it runs are the ones the analysis runs, optimized (above).
- The `.measure.json` goldens (below) on all six platforms, and `check_aot.sh` with `--measure`.
- The vector paths are tested against the scalar ones with `DOTNET_EnableHWIntrinsic=0`, as the decoders' are ("Vector paths of the decoders" in [development.md](../development.md)).

### Rejected alternatives

- **Fixed-point FFT:** deterministic by construction, but 24- and 32-bit input needs 64-bit products with 128-bit intermediates or block scaling, which is more code and likely slower (**estimate**); the double path is shown deterministic where it was measured.
- **Tolerances instead of byte equality:** the goldens would compare grids within a tolerance and stop being literal; kept only as the fallback of decision 11 if an Arm platform turns out to differ for a reason no rule can remove.
- **Leaving raw spectra out of the goldens:** they are already left out (only quantized values are reported); the risk is in the quantized values, which must stay.

## Performance and budget

### Measured

- **Machine:** the build machine of the earlier steps (Intel Xeon at 2.80 GHz, 4 vCPUs, AVX-512, Linux 6.18, .NET 10.0.401), one pinned core (`taskset -c 1`), under the machine lock, load average 0.7 to 1.4; minimum of 7 rounds for the prototype, of 5 runs for the CLI.
- **The prototype, per sample of one channel:**

| Kernel | NativeAOT x86-64-v2 | NativeAOT x86-64-v3 | JIT (AVX-512) |
| --- | --- | --- | --- |
| FFT, plain first version (bounds checks, masked table), 4,096 points, hop 2,048 | 56.9 ns | 48.9 ns | 40.5 ns |
| FFT, optimized scalar, 2,048 / 4,096 / 8,192 points, hop half | 15.2 / 17.3 / 19.1 ns | 12.9 / 14.5 / 18.4 ns | 13.7 / 15.9 / 19.0 ns |
| FFT, `Vector128` butterflies, 2,048 / 4,096 / 8,192 points, hop half | 11.2 / 12.2 / 15.6 ns | 10.4 / 10.7 / 15.2 ns | 10.8 / 12.9 / 16.1 ns |
| FFT, `Vector128`, 4,096 points, hop whole | 6.1 ns | 5.4 ns | 6.5 ns |
| K-weighting (two biquads) and mean square | 4.6 ns | 4.4 ns | 4.8 ns |
| True peak, 4 × 12 taps, plain loop | 49.0 ns | 46.6 ns | 53.7 ns |
| True peak, the four phases in `Vector256` lanes | 23.1 ns | 12.5 ns | not run |
| Levels on integers (peak, sums, OR, clipping runs) | 2.0 ns | 2.3 ns | 2.2 ns |

  The JIT figures of the optimized FFT rows come from a run outside the machine lock, the rest of the JIT column from the first locked run; the CLI is released as NativeAOT, so its columns count.
- **The second prototype (written for the review), per sample of one channel,** the same machine, one pinned core under the machine lock (`flock -x`, load average 0.3 to 0.6), JIT with `DOTNET_TieredCompilation=0`, minimum of 7 rounds, a real FFT of N points through a complex FFT of N/2 (split step), double precision, Hann window, hop N/2, `Vector128` butterflies, no fused multiply-add. "Window, FFT and power" includes the window multiply, the bit reversal and the power accumulation; "frame counters" are those of step 4 of [Spectral cutoff](#spectral-cutoff) for the one spectrum they serve. The scalar column has no vectors.

| Points | Window, FFT and power, `Vector128` | Window, FFT and power, scalar | Frame counters (one spectrum) | Stereo: FFT + 1.5 counter sets | Weight in work units (ns / 0.6, rounded up) |
| --- | --- | --- | --- | --- | --- |
| 1,024 | 9.8 ns | not run | 5.1 ns | 17.4 ns | 29 |
| 2,048 | 10.4 ns | 13.4 ns | 2.4 ns | 14.0 ns | 24 |
| 4,096 | 11.2 ns | 14.5 ns | 1.6 ns | 13.6 ns | 23 |
| 8,192 | 14.6 ns | 17.8 ns | 1.1 ns | 16.2 ns | 27 |
| 16,384 | 15.1 ns | 19.4 ns | 0.7 ns | 16.2 ns | 27 |
| 32,768 | 16.5 ns | 20.8 ns | 0.7 to 1.3 ns | 17.5 ns | 30 |

  - "1.5 counter sets" is one set per channel plus one for the combined spectrum, shared by two channels. The first prototype read 12.2 ns at 4,096 points (NativeAOT x86-64-v2); the JIT reads within 10 % of it.
  - The cost does not grow with log2 N alone: 8,192 points cost 1.3 times 4,096 (14.6 against 11.2 ns), the step where the working set (twiddles, window, two arrays of N/2 doubles) stops fitting the first-level cache; from 8,192 to 32,768 it grows by 13 % (**unverified** as the cause; one machine).
  - The cost of a larger FFT at high rates is measured, not extrapolated: 16,384 points cost 16.2 ns against 13.6 at 4,096, 1.2 times; at 96 kHz (8,192 points) also 1.2 times. Per second of stereo audio that is 1.2 ms at 44.1 kHz, 3.1 ms at 96 kHz (2.6 times) and 6.2 ms at 192 kHz (5.2 times).
  - K-weighting in a plain loop (two biquads in transposed direct form II, the flush check, and the mean square) took 6.7 ns a sample here against 4.6 ns in the first prototype; the weights are set by the test from the built kernel, not from either.
- **The real decoder, same machine and build (NativeAOT x86-64-v2 of c517387):** a 300 s file of stereo 16-bit 44.1 kHz pink noise with a 440 Hz tone, FLAC `-5` (18.5 MB): analysis 0.132 s, `--verify` 0.390 s, so decoding costs **9.75 ns a sample of a channel**. The same audio as WAV (52.9 MB) analyzes in 0.061 s.

### Cost relative to decoding

**Estimates,** adding the kernels (the first prototype's NativeAOT figures, except the spectrum's, from the second prototype), for that 300 s stereo file (26.46 million samples of a channel):

| Measurements | Per sample of a channel | Added to the file's 0.39 s of `--verify` |
| --- | --- | --- |
| Spectrum (FFT, counters, grids) | about 14.5 ns | about 0.38 s |
| and levels | about 16.5 ns | about 0.44 s |
| and loudness and true peak (all) | about 44 ns | about 1.2 s |

- So the spectrum costs about 1.5 times FLAC's decoding, and everything about 4.5 times; against Monkey's Audio at insane or WavPack `-hh`, whose decoding costs 5 to 30 times FLAC's (decoders.md's tables), measuring is a small addition.
- A 300 s stereo file is measured in about 1.6 s in all; a 24-bit 96 kHz file costs about 2.6 times per second of audio (twice the samples, a larger FFT).

### The work budget

- **Why a budget:** measurements cost per decoded sample, and decoding a crafted file amplifies bytes into samples (a 49-byte FLAC frame codes more than 2 MB of PCM). The decoders' budget bounds the samples, so the measurements are bounded too, but only by about four times the decoders' time, since they cost more per sample than the cheapest decoding.
- **(a) A count of their own, as large as the decoders', recommended (decision 5):** the measurer charges work units per block before it does the work, at weights per sample of a channel in the decoders' unit (about 0.6 ns):
  - **spectrum: by the FFT size,** 29, 24, 23, 27, 27 and 30 for 1,024, 2,048, 4,096, 8,192, 16,384 and 32,768 points (the table above: the cost per sample is not independent of N, and 8,192 points cost 1.3 times 4,096, so one weight for every size would be wrong);
  - 4 for the levels, 8 for loudness, 38 for true peak at 4 times, 30 at 2 times and 4 without oversampling (**estimate** from the kernels; a test sets them).
  - The charge is per sample of a measured channel, so a block of 32 channels costs 32 times; channels beyond the cap are not charged because they are not measured.
  - The bound is the larger of 2^35 units and the file's size times the largest `WorkPerInputByte` among the sessions, the decoders' formula, counted apart. Past it, measuring stops (`measure.limit`) and decoding and verification go on.
  - Worst case: at most the decoders' time again (about 21 s for a small crafted file, 1.2 s per MB beyond 16 MiB).
  - Legitimate files: an hour of CD audio is 317.5 million samples of a channel, 2.2 × 10^10 units for everything, under the floor of 2^35 (3.4 × 10^10), so it is measured in full at any size; an hour of 8 channels at 24 bits and 192 kHz (5.5 × 10^9 samples, 3.9 × 10^11 units) needs a file of 190 MB, and such a file is several GB.
- **(b) Shared with the decoders:** simpler, but turning measurement on would make some files stop decoding earlier, so whether a stream verifies would depend on whether it was measured.
- **(c) None:** the decoders' budget bounds samples, and measurement time is at most about four times the decoding time it follows. Acceptable, but a crafted file then costs up to about 90 s more instead of 21 s (**estimate**: 2^35 units at 16 a sample is 2.1 × 10^9 samples, at 43 ns each).
- **The floor of the charge,** as for decoders: after each block the measurer has charged at least its weights times the samples it measured; a measurement that forgets to charge is `measure.failed` in every test.
- **`MeasurementWorkTests`,** in the `ComplexityTests` collection like `DecoderWorkTests`: each measurement's time per unit at most twice the FLAC reference's, in the nightly run, for every FFT size and for the case "an impulse, then silence" (see [Determinism](#determinism)). In `Compare` (the spectrum, levels, loudness and true peak cases) each timed run lasts at least 0.15 s or 16 passes of the signal, whichever comes first (the 192 kHz true peak and the levels reach only about 0.1 s at 16 passes), since a run of a few milliseconds, as the levels' are, is decided by one scheduler pause or collection. `RunDoubles`, the impulse case and the levels-on-spectrum case still time single passes.

### Memory

Per measured stream, independent of its length and sized from the declared format before anything is allocated (**estimates** from the sizes; C measured channels, at most 32; N the FFT size):

- the overlap buffer: one frame of samples per channel in doubles (C × N × 8 B);
- FFT scratch, twiddles and window: 6 arrays of the frame size in doubles (48 N B, shared);
- average spectra: (C + 1) × (N/2 + 1) doubles;
- the frame counters of the cutoff: (C + 1) × 512 × 72 × 2 counters of 4 B, 295 KB each (the combined spectrum and each channel);
- the spectrogram's cells in single precision (2 MiB) and the kept bytes (512 KiB);
- the waveform (1,024 columns × C × 2 × 2 B);
- the loudness histograms (10,000 bins × 16 B, twice: 320 KB), the 1,024-point short-term series and the true-peak filter state;

| Stream | Total |
| --- | --- |
| 2 channels, 4,096 points (CD) | 4.0 MiB |
| 6 channels, 8,192 points (5.1 at 96 kHz) | 5.8 MiB |
| 8 channels, 4,096 points | 6.0 MiB |
| 8 channels, 32,768 points (8 channels at 384 kHz) | 10.0 MiB |
| 32 channels (the cap), 32,768 points | 25.9 MiB |

Under 6 MiB holds only for up to 8 channels at 4,096 points; 32,768 points need about 7.5 MiB without the frame counters. The report keeps the figures, the 512-row spectra and the grids: under 1 MiB per stream.

- **A bound per analysis:** at most 64 MiB of measurement state; a stream's state is sized from its format when its output is declared and reserved from that bound, so two streams of the largest size fit and a third is not measured (`measure.streams-limited`); a stream whose own size is above the bound cannot occur at 32 channels (26 MiB). Decoded streams of one file are few today (Matroska's later routes may bring more).
- **A bound per stream on channels:** 32 (see [Channels, rates, widths and DSD](#channels-rates-widths-and-dsd)); the format's channel count, which a header can set to 65,535, never sizes an allocation.
- `MemoryTests`: a long stream measured, heap read from inside the last `Write`, growth per further block bounded, as `DecodingManyFlacFramesHoldsNothingPerFrame` does for decoders; a WAV that declares 65,535 channels stays within the bound.
- No `ArrayPool<T>.Shared`, for the reason decoders.md gives.

### Cancellation and the exception boundary

- **Cancellation:** the measurer checks the analysis's token every 64 KiB samples of a channel and at every block. When it is canceled, the measurer stops working and returns, and **remembers that it stopped**; the decoder's next check of its own token (linked to the analysis's) throws, as decoders.md's contract has it. So the sink never throws, and the latency is at most one block (a WavPack set of up to 2^24 samples, measured in chunks with the check inside).
- **The last block.** A cancel that arrives while the last block is measured, with no later check by the decoder, would end the decode normally and leave a record that says `Complete` over figures cut short. So: a measurer that stopped on the token ends with status `Stopped`, never `Complete`, whatever the decoder does next; and at the end, before the host attaches any record, it checks the token once more and, if it is canceled, throws `OperationCanceledException` from the host's own boundary (not from the sink), as it does for every cancel of the analysis.
- **Stop:** `DecodeOptions.Stop` ends decoding, so the measurements end with status `Stopped`, covering what was decoded.
- **Exceptions:** the measurer catches every exception of its own code. An `OperationCanceledException` from the analysis's token cannot arise (it never throws on cancellation), so any exception is a bug: `measure.failed` (error), the stream's measurements are dropped, measuring of that stream stops, decoding goes on. Measurement code throws nothing on purpose: malformed PCM does not exist at this level (every bit pattern is a sample), non-finite floats are counted and large ones clamped.
- `OutOfMemoryException` passes through, as everywhere in the host.

### Streaming

- Nothing is loaded whole: the measurer sees blocks in stream order and keeps one frame of overlap.
- The PCM route of WAV and AIFF reads the data chunk in slices through `ByteReader`, as the other parsers read frames.

### Parallel decoding and the GUI

- parallel-decoding.md (approved, deferred by the owner on 2026-10-26 in favor of new formats and deeper analysis such as this) calls the sink in the commit, on the analysis thread, in stream order. The measurer would then add to the serial part: about 14 to 44 ns a sample of a channel next to commits of a few nanoseconds, which would eat most of the gain for WavPack (whose serial share is already 12 to 25 %).
- **So:** with parallel decoding, the measurer runs on a thread of its own that takes copied blocks from a bounded queue and processes them in stream order, with back-pressure on the commit when the queue is full. The queue is **bounded in samples, not in blocks**: at most 2^21 sample values (channels × samples; 8 MiB as 32-bit values, 16 MiB as doubles), in chunks of whole sample frames, so a block of a WavPack set of 2^24 samples or of many channels is split on the way in instead of copied whole (two blocks of the largest size would be 2 × 64 MiB of copies, **estimate**). The copy costs about 1 ns a sample (**estimate**); the result is the same bits, since the order of every operation is unchanged. The channels of a block can be transformed on several threads, with the combined sums still added in channel order.
- Without parallel decoding, the measurer runs inline in the sink, which is simplest and what step 1 builds.
- The GUI's folder view never decodes, so its parallelism is untouched; a decode in the detail window takes the degree parallel-decoding.md's decision 2 gives, plus the measurer's thread.

## Verification

### Synthetic fixtures

Hand-built with the standard library (`make_measurement_fixtures.py`), regenerated and compared in CI, as FLAC files (`flac` 1.5.0 in CI, the decoder route) in step 1, and as WAV files too once the PCM route exists (step 3; see [Rollout](#rollout)), each with a manifest from an independent model (`measurement_model.py`: its own DFT and filters, written from the specifications, not from the C# code):

- integer-phase sines at bin centers and between bins, at known levels;
- a logarithmic sweep, for the spectrogram's columns and rows;
- noise band-limited exactly at 11, 16, 19 and 20.5 kHz, as sums of sines with fixed random phases at bin centers, at 44.1 kHz (the 16 kHz one also at 48 kHz); the same with TPDF dither, and quiet (-30, -45 and -60 dB, which the lossy-derived table shows go from found to hidden);
- natural rolloff (cascaded biquads: one, two and four sections), which must give no cutoff;
- mixed content (part band-limited, part not), which must give no cutoff, and a band-limited passage followed by digital silence, which must give one;
- clipping runs of 1, 2, 3 and 100 samples at both extremes, in integers and floats;
- DC offsets of known size; leading, trailing and inner digital silence; **silence after an impulse** (the subnormal case of [Determinism](#determinism), at 48 kHz and at 44.1 kHz);
- 16-bit audio padded to 24 bits, and floats that hold 16-bit values;
- true-peak overs: a sine at a quarter of the sample rate with a phase of 45°, whose samples peak at -3.01 dB of its true peak;
- 5.1 with an LFE that must not set the cutoff, and 7.1 with a rear pair (weight 1.0) against the same signal in a 5.1 with a surround pair (1.41); mono and stereo without a layout, and 5.0 without one (loudness `NotPossible`);
- 32-bit integers; floats with NaN and infinities; **floats of 1e300** (clamped, counted, no NaN in any figure); 192 kHz; **rates of 2 kHz, 8 kHz and 0 Hz**; a stream shorter than one frame; a stream longer than 1,024 hops, so columns merge;
- **many channels:** a WAV that declares 65,535 channels with a short data chunk (measured as 32, `measure.channels-limited`, within the memory bound), and one of 33 channels;
- **gaps:** a FLAC stream with frames removed (the router's `FollowsGap`), a WAV-sized stream whose positions jump, and a jump of 2^40 samples (the grid advances by arithmetic); the expected `Samples`, `LostSamples` and `Gaps`, columns without data, and no frame, gating block or run across the gap;
- **format change:** a scripted decoder (as `DecodeRouteTests` has) that writes a block with another channel count or sample format after `DeclareOutput`: status `FormatChanged`, `measure.format-changed`, the figures up to the change, verification unchanged; and a TrueHD stream whose channel count changes between access units, which must keep giving `decode.frame-invalid` for the unit and a gap in the measurements, not `FormatChanged` (it holds the guard above);
- Expected: exact for the integer figures, within a stated tolerance for spectral figures (bin width for frequencies, 0.1 dB for levels) against the model, and exact against the golden.

### Lossless files from lossy encodes

`make_external_measurement_fixtures.py` encodes a pinned input (generated, a few seconds of stereo pink noise with a tone, at a level that keeps the FLAC files small) with pinned tools, decodes each with the encoder's own decoder where it has one, and commits the FLAC files (about 4 s each, a few MB in all, **estimate**). The manifest records the encoder's stated lowpass: LAME's tag Lowpass and the transition band its `--verbose` output prints; for oggenc and FFmpeg's encoders, the values their sources give (read and cited in that pull request; **unverified** here).

The second prototype's runs on 30 s inputs are the table under [Spectral cutoff](#spectral-cutoff) (LAME 3.100, vorbis-tools 1.4.2, FFmpeg 6.1.1, 16-bit quantization). The 4 s fixtures have about a seventh of the frames, so the tests assert the thresholds, not the frame counts.

- These are the prototype's runs on throwaway files; the articles may state them only once committed fixtures reproduce them, worded as "in Encspect's test files, LAME 3.100 at `-b 128` and 44.1 kHz leaves a spectral cutoff inside the transition band it prints; the analyzer does not name an encoder from a cutoff".
- The tests assert, per fixture, the cutoff within the stated band (LAME) or within a tolerance of the manifest's value, and "none found" where the encoder applies no lowpass.

### Cross-checks

Black-box, in a CI job on Ubuntu 26.04 (`crosscheck_measurement_fixtures.py`), with each tool's version asserted (`tool_versions.py`):

- **libebur128 as a tool,** not only its expected values: built from the pinned source (MIT; `COPYING` read) with a driver of about 60 lines that reads float PCM and prints the integrated loudness, the loudness range, the maximum momentary and short-term loudness, and the sample and true peak, with an explicit channel map. Its source is neither committed nor followed; it is a program the job runs, as `mp3guessenc` is. It carries the 7.1, 6.1 and height layouts, where FFmpeg's weights differ from Table 4 (see [Loudness and loudness range](#loudness-and-loudness-range)), and every rate.
- **FFmpeg `ebur128`** (`peak=true+sample`): integrated loudness, loudness range, sample and true peak, within EBU Tech 3341's and 3342's tolerances (±0.1 LU, ±1 LU) on mono, stereo, 3.0, 5.0, 5.1 and quadraphonic. **True peak:** Tech 3341 gives +0.2/-0.4 dBTP for its signals 15 to 23 (read), but FFmpeg 6.1.1's resampler-based true peak read 0.6 dB over the Annex 2 filter on a quarter-rate sine (see [True peak](#true-peak)), so against FFmpeg the check allows +0.7/-0.1 dB of our figure and the synthetic overs are asserted against the expected values with Tech 3341's tolerance. Both are re-measured with the CI's FFmpeg.
- **FFmpeg `astats`:** peak level, RMS level, DC offset, and its "Bit depth" (bits used), against the levels.
- **sox `stats`** where the package is installed (**unverified** in the CI image): the same figures from a second tool;
- **sox `spectrogram`** and FFmpeg `showspectrumpic`: pictures for a person's review of the synthetic sweeps, not asserted;
- **the EBU loudness test set** (Tech 3341 and 3342 cases) and **libebur128's test expectations** (MIT): fetched at pinned SHA-256 and never committed, as the ISO conformance streams are, since the set's readme allows its use "only for technical testing purposes"; the expected values of each case come from the EBU documents (Tech 3341's Table 1 and Tech 3342's cases, read).
- **The decoded samples of WAV, AIFF and Wave64** against FFmpeg's MD5 (see [WAV, Wave64 and AIFF: a PCM decoder](#wav-wave64-and-aiff-a-pcm-decoder)).
- Differences fail the job until explained, as in every cross-check.

### Tests

- `MeasurementFixtureTests`: every measured fixture against its manifest, and a small `<name>.measure.json` golden of what measuring adds (the measurements with the grids replaced by their SHA-256), rewritten with `ENCSPECT_UPDATE_GOLDEN=1`, as the `.decode.json` goldens are; the tree must equal the one without measuring.
- **Chunking independence:** the same audio as FLAC at two block sizes, as WavPack, as Monkey's Audio and, from step 3, as WAV gives identical measurements (the figures and the grids' hashes), since the measurer must not depend on how the decoder cuts blocks (a FFT frame spanning two blocks, a gating block, a true-peak window).
- **Measuring changes nothing else:** verification and every `decode.*` finding are equal with and without `--measure` on every decoded fixture, and also with the measurer's budget lowered so that `measure.limit` fires first.
- `MeasurementDeterminismTests` (above), `MeasurementWorkTests` (above), `MemoryTests` (above).
- **CSV:** a test writes a measured file whose peaks and loudness are negative and reads the row back, expecting `-0.3` and not `'-0.3`, and one whose title starts with `=`, expecting the guard.
- **The decoder invariant of the GUI:** every stream of a fixture of a registered decoder ID has a `Verification` in a default analysis (see [GUI](#gui)).
- Description and topic tests over the measurements; `FindingCoverageTests` for every `measure.*` code.
- Cancellation: a cancel inside a large WavPack set and inside a long WAV data chunk ends the analysis promptly; Stop ends measuring with status `Stopped`; a cancel in the last block never leaves `Complete`.
- `CorruptFiles` and the fuzzer analyze with `--measure` and lowered `MaxWork` for both budgets, and fail on `measure.failed`.
- **`check_aot.sh` with `--measure`:** the script's reports are about 870 MB already, and the measurements add the grids' hashes and spectra to each, and the time of the measurer (about 4.5 times the decoding's for everything) on top of the verification's; the first implementation pull request times the script with and without `--measure` and decides whether it measures every fixture or the measured ones only.
- The vector paths run again with `DOTNET_EnableHWIntrinsic=0` in the `rest` shard.
- GUI: headless tests of the panels, `CapitalizationTests` for their labels.
- A milestone review of step 1 by an independent agent, as CLAUDE.md requires.

## Rollout

Each step is its own pull request, with fixtures, goldens, articles and documentation (`docs/cli.md`, `docs/gui.md`, `docs/development.md`):

1. **Framework and spectrum:** `DecodeOptions.Measure`, `MeasureOptions`, `MeasurementKinds`, the sink's fan-out, `StreamMeasurements` attached to every session and its writers (text, JSON, CSV with the numeric-field exemption), `--measure`, `--measure-grids`, `--hide measurements`; the deterministic kernel (tables, logarithm, `Tan`, `Pow`, FFT, window), the average spectrum, the spectral cutoff with its frame counters, the spectrogram and waveform grids; gaps, format changes and the 32-channel cap; the budget, the memory bound, the boundary, the findings; the determinism, work, memory, fixture and fuzz tests; the synthetic and lossy-derived fixtures **as FLAC files only**; the articles `measurements`, `spectrogram`, `spectral-cutoff` and the updates to `lowpass`, `coded-bandwidth`, `verification`, `bwf-broadcast-extension` and `replaygain`; the `CLAUDE.md` rules; decoders.md's section renamed.
   - **Why FLAC only:** fixtures as WAV files would go "through the PCM route", which arrives with step 3. Step 1 measures the streams that have decoders (FLAC, and the WavPack, Monkey's Audio and TrueHD fixtures the chunking test needs); the WAV twins of the synthetic fixtures are added in step 3 with the route, and its goldens.
   - **The alternative** is to move the PCM route (and decision 15's `Verification` for PCM streams) into step 1, so that WAV fixtures are measured from the start; it makes step 1 larger (a decoder, a descriptor, three parsers' goldens). Decision 1 covers it.
2. **Levels:** peak, RMS, DC offset, clipping runs, digital silence, bit-depth usage, with their articles and cross-checks (`astats`). Built; see [As built: step 2](#as-built-step-2).
3. **The PCM route** (built; see [As built: step 3](#as-built-step-3)): the `pcm` decoder in the WAV project, `PcmCoding` and `DecodeSetup.Pcm`; the WAV, Wave64 and AIFF parsers start the session and hand their data chunks; the `verification` in the model, with the writers' and `VerificationText`'s filter, the router's rule that a checksum-free decoder opens only with `Measure` or `Output`, and the button still requiring a declared checksum; the decode goldens of those fixtures change; the WAV twins of the synthetic fixtures; the FFmpeg MD5 cross-check; `DecoderWorkTests` and the decoder-list test.
4. **GUI panels:** Waveform, Spectrogram and Spectrum in the detail window, the decode command and everything its rename touches (decision 8, [GUI](#gui)), the captions before decoding. Built; see [As built: step 4](#as-built-step-4).
5. **Loudness and true peak:** K-weighting at every rate from 8 kHz, gating, loudness range, maxima, the loudness series, true peak; the EBU set, libebur128 and FFmpeg cross-checks; the Loudness panel. Built; see [As built: step 5](#as-built-step-5).
6. **Later, each its own change:** the measurer on its own thread with parallel decoding (deferred with it); mid/side spectra and spectral holes; the bandwidth figure for upsampled content; a cutoff for mixed content from the frame counters; for the levels, a minimum length for the stretches that count as silence and clipping at the extremes of the bits in use (16-bit audio padded to 24 bits tops out at 0x7FFF00, so only its negative runs count today); the TTA decoder (clean-room), which then needs nothing here.

- **PLAN.md, with step 1** (`docs/PLAN.md`): the architecture diagram's arrow `Host -.-> GUI` labeled "decoded PCM for waveform, spectrogram" becomes "measurements of the decoded audio" (the PCM stays in the host; the GUI receives figures and grids); the Decoding row of "Decisions so far" (line 38) names the measurements; the GUI list's "Waveform and spectrogram panels that need decoded audio" (line 804) becomes the Waveform, Spectrogram, Spectrum and Loudness panels drawn from a decode, with the caption for formats without a decoder; "Later" loses "Decoders for waveform and spectrogram, format by format" in its present wording (it becomes the lossy decoders that the measurements wait for) and gains nothing else.
- **Why this order:** step 1 carries the use case and every risk (determinism, cost, wording), so it is reviewed first; steps 2 and 3 widen what is measured at little risk; the GUI follows once the data are settled, so its panels are drawn from a stable model; loudness and true peak have their own references and come once the framework is proven.
- **Alternative:** the GUI panels right after step 1, so users see the spectrogram sooner, before WAV files are measured. Decision 1.

## Decisions

The project lead approved the recommendation of each decision below on 2026-10-26: 1(a), 2(a), 3(a), 4(a), 5(a), 6(a), 7(a), 8(a), 9(a), 10(a), 11(a), 12(a), 13(a), 14(a), 15(a), 16(b), 17(a), 18(a) and 19(a). The options are kept for the record.

Each has its options and a recommendation; 15 to 19 are new with the revision.

1. **The first step's scope.**
   - (a) Framework, average spectrum, spectral cutoff and the grids, on the streams that have decoders, with FLAC fixtures (the rollout above).
   - (b) As (a) with the levels, which are cheap and exact.
   - (c) The framework with the levels only, the spectrum second.
   - (d) As (a) with the PCM route moved into step 1, so that WAV fixtures are measured from the start.
   - **Recommendation: (a).** It is the prime use case and carries the risks; (b) is acceptable if the pull request stays reviewable; (d) adds a decoder, a descriptor and three parsers' goldens to the riskiest step.
2. **The name.**
   - (a) "Measurements": `DecodeOptions.Measure`, `StreamInfo.Measurements`, `MeasurementKinds`, `--measure`, `measure.*` codes, the `measurements` article, the group "levels"; decoders.md's "host-side PCM analyses" renamed.
   - (b) "PCM analysis": `--pcm-analysis`, `StreamInfo.PcmAnalysis`.
   - **Recommendation: (a).** "Analysis" already names the whole report (`Analyzer`, `AnalysisOptions`), so a second "analysis" would blur one concept into two; "levels" avoids "statistics", which names `NodeKind.Statistics` and `--hide statistics`.
3. **Does measuring imply verification?**
   - (a) Yes: one decode pass, the checksums compared as with `--verify`.
   - (b) No: a decoder option to skip the checksums while measuring.
   - **Recommendation: (a).** No contract change, and the verdict comes almost free.
4. **The PCM route for WAV, Wave64 and AIFF.**
   - (a) A `pcm` decoder in the WAV project, by codec ID for every container of PCM, with a neutral `PcmCoding` descriptor in `Core.Abstractions` carried by `DecodeSetup.Pcm`.
   - (b) The host reads the samples.
   - (c) No measurements for PCM files.
   - **Recommendation: (a)** (see [WAV, Wave64 and AIFF: a PCM decoder](#wav-wave64-and-aiff-a-pcm-decoder)).
5. **The budget.**
   - (a) A count of its own, as large as the decoders', `measure.limit` stopping only the measurements, the spectrum's weight by FFT size.
   - (b) Shared with the decoders.
   - (c) None beyond the decoders'.
   - **Recommendation: (a).** Verification never depends on whether a file was measured, and the worst case stays within twice today's.
6. **Findings.**
   - (a) None about the audio but `measure.non-finite-samples` (warning); the host's `measure.limit`, `measure.failed`, `measure.streams-limited`, `measure.channels-limited` and `measure.format-changed`.
   - (b) Also infos for a found cutoff, clipping runs or unused low bits.
   - **Recommendation: (a).** Figures are not verdicts, and an info through `--fail-on info` would act as one.
7. **Grids in JSON.**
   - (a) Only with `--measure-grids`, as base64 bytes; the default JSON has the figures and the 512-row spectra.
   - (b) Always.
   - (c) Never; the GUI reads them from the library.
   - **Recommendation: (a).** A grid is 683 KiB of base64 per stream, too much for every report, and other programs that read the JSON need a way to get it.
8. **The GUI's decode command.**
   - (a) One command that decodes, verifies and measures, labeled "Decode Audio", replacing "Verify Audio", enabled while a stream's `Verification.Decoding` is `NotRequested` (decision 15 makes that true of PCM streams), with the captions of [GUI](#gui) and the rename list there.
   - (b) Keep "Verify Audio" as the label and let it measure too.
   - (c) Decode automatically in the detail window for files under a size (say 200 MB).
   - **Recommendation: (a).** One decode, one command, and a label that is true for a WAV file, which has nothing to verify; (c) contradicts decoders.md's decision to keep decoding off everywhere and would decode while the user only browses.
9. **The FFT's source.**
   - (a) Written in-house from the literature (radix-2, real split).
   - (b) Following KISS FFT or PocketFFT (BSD-3-Clause), with the notice.
   - **Recommendation: (a)** (see [FFT](#fft)).
10. **Frame size and hop.**
    - (a) The size by sample rate (the smallest power of two that is at least 0.085 s of samples, from 1,024 to 32,768 points; 4,096 at 44.1 and 48 kHz), hop half a frame.
    - (b) 4,096 points at every rate, hop half.
    - (c) As (a) with a whole-frame hop, at half the cost.
    - **Recommendation: (a).** The same time and frequency resolution at every common rate, for about 1.2 times the cost of 4,096 points at 96 and 192 kHz (measured, see [Measured](#measured)).
11. **If an Arm platform's spectra differ.**
    - (a) Find the operation and fix it under the rules (the expected case: a missed FMA or a library call).
    - (b) Compare grids within a tolerance in the goldens.
    - **Recommendation: (a)**, with (b) decided only then, for the one platform and with the cause named.
12. **TTA.**
    - (a) Out of these steps; its clean-room decoder stays under "Later" and is measured when it lands.
    - (b) Its decoder as a step of this rollout.
    - **Recommendation: (a).** It is a decoder's work with its own clean-room split, and the TTA use case is small next to FLAC and WAV.
13. **K-weighting at rates other than 48 kHz.**
    - (a) Derive the coefficients as libebur128 does (MIT), with its parameters and its notice: `scripts/release/licenses/libebur128.txt` (its `COPYING`, MIT, Copyright (c) 2011 Jan Kokemüller), fetched and checked by `make_notices.py` at a pinned SHA-256 as libmpcdec's are, a row in `THIRD-PARTY-NOTICES.md`'s table and its section, and the notice in the source file.
    - (b) Derive them from the Recommendation alone: BS.1770-5 Annex 1 says other rates "should be chosen to provide the same frequency response that the specified filter provides at 48 kHz", and inverting the bilinear transform of its Tables 1 and 2 recovers the analog parameters (the second prototype reproduces libebur128's to 11 to 14 digits), which are then re-applied at the other rate. No implementation is followed, so no notice is needed.
    - **Recommendation: (a)**, as the most cross-checked derivation, with the notice, as the owner asked for; (b) is the choice if the notice is to be avoided, with libebur128 as the black-box cross-check either way. Both use the Recommendation's own coefficients at 48 kHz and the own `Tan` and `Pow` of [Determinism](#determinism).
14. **Dynamic range.**
    - (a) EBU loudness range only.
    - (b) Also the "DR" value of the Pleasurize Music Foundation's meter.
    - **Recommendation: (a).** No open specification of (b) was found.
15. **How a PCM stream states its decoding status** (without a `Verification`, which PCM streams have not today, "Decode Audio" would stay disabled for WAV, AIFF and Wave64, and the GUI could not tell a decodable stream with nothing to verify from one that is not decoded).
    - (a) The PCM parsers start the `pcm` session in every analysis and finish it with no checksums, so the model carries `Verification { Decoding, Checksums: [] }`; the text and JSON writers and `VerificationText` leave a verification with no declared checksum out, so reports and `.report.json` goldens do not change; a decoder that declares no checksum is opened only when `Measure` or `Output` is set, so `--verify` alone reads no PCM; in step 3 the button still requires a declared checksum (see [GUI](#gui)).
    - (a2) As (a) with the JSON writing the object (`decoding`, `decodedSamples`, empty `checksums`) and `--verify` decoding PCM to compare nothing: it changes the documented JSON contract (`ReportJsonWriter.cs` lines 14 and 15, cli.md "verification is written for a stream of a coding that stores checksums"), every WAV, AIFF and Wave64 golden, and the I/O of folder runs and `check_aot.sh`.
    - (b) A new field on `StreamInfo` for the decoding status of streams that have a decoder.
    - (c) The GUI treats a missing `Verification` of a PCM format as decodable, by a list of format names.
    - **Recommendation: (a).** It reuses the one status mechanism (TTA's parser already starts a session only to finish it with its tallies), needs no second source of truth for `DecodeStatus`, leaves the enabling expression as it is and changes no report. (a2) is (a) without the two guards; (b) duplicates the status next to `Verification` and every parser would still have to fill it; (c) puts parser knowledge in the GUI, which the plan keeps out. A stream that a container does not hand to a codec parser has no session under any option, and the GUI says only that its audio is not decoded here.
16. **Do A-law and mu-law count as `IsLossy`?** (`DecodeSetup.IsLossy` puts "(lossy stream)" after the measured duration.)
    - (a) Yes: companded 8-bit audio is a lossy coding of the signal, and measurements of it describe a coarser signal than the original.
    - (b) No: the stream stores its samples as they are, and the flag means that the audio a checksum vouches for is a lossy coding's output.
    - **Recommendation: (b).** (a) would change the meaning `StreamVerification.IsLossy` has in decoders.md (a WavPack hybrid file without its correction file); the article on companding explains why such a recording has a high noise floor and a low bit depth.
17. **`--hide measurements` in JSON.**
    - (a) It leaves `measurements` out of the JSON too, as `--hide` leaves sections out of text and JSON (docs/cli.md).
    - (b) Text only; measured JSON always has them.
    - **Recommendation: (a)**, for one rule; `--measure-grids` still decides the grids.
18. **Loudness when channel positions are not stated** (`ChannelLayout` null, or a `Discrete` channel).
    - (a) One channel and two channels get weight 1.0; three or more are `NotPossible`.
    - (b) Assume the WAVE channel mask's order (front left, front right, front center, LFE, back left, back right, …) from the channel count.
    - (c) Always weight 1.0.
    - **Recommendation: (a).** A weight would be a guess for (b) and (c), and BS.1770 weights by position.
19. **How many channels are measured.**
    - (a) At most 32, `measure.channels-limited` above.
    - (b) No cap beyond the memory bound.
    - (c) At most 8.
    - **Recommendation: (a).** 32 channels cost 26 MiB at the largest FFT size and cover every layout the project reads (22.2 has 24); a header may declare 65,535.

## Unverified claims and estimates

- The costs of the full measurer, the per-stream memory, the JSON sizes and the work weights are sums of the prototypes' kernels and the sizes of the arrays, not measurements of a built measurer. The weights by FFT size and the frame counters' cost are the second prototype's, JIT with tiering off; the other kernels' are the first prototype's, NativeAOT.
- The prototypes' FFT and timings ran on one x64 machine. Arm64, macOS and Windows were not measured: neither their speed nor whether their spectra are bit-identical. Whether .NET's Arm64 JIT ever contracts a multiply and an add on its own is **unverified**; so is whether `Math.Cos` and `Math.Log10` differ between the platforms' libraries (the design avoids them either way), and how the `*Native` and `Max` forms behave on Arm. The C# and CLI statements about intermediate precision are quoted from memory.
- **The cutoff method** was measured on synthetic inputs only: 34 files of pink noise, white and brown noise, and their lossy decodes and filters. No music was among them, so the 2 kHz search start, the 25 dB threshold (3.1 dB above the natural rolloff, 8.5 dB below the weakest lossy case), the behavior on sparse spectra (a solo instrument) and on bass-heavy recordings, and the mixed-content limit are **unverified** on real recordings; the fixtures and a set of real files set them in the first pull request. The true-peak skipping and the row counters at 32 channels are designed, not prototyped.
- The lowpass of oggenc and FFmpeg's encoders was measured, not read from their sources.
- **Read for this revision:** BS.1770-5 (Annexes 1 to 3, equations, Tables 1 to 5), EBU R 128, Tech 3341 (Table 1, 2.6) and Tech 3342 (3.1 and the reference code), libebur128's `ebur128.c` and `ebur128.h` (the filter parameters, the channel map, the weights, the histogram, the oversampling factors) and the `colormaps.py` header of BIDS/colormap. **Still unread or unverified:** earlier revisions of BS.1770; the azimuth of the wide channels in ISO/IEC 23091-3; the G.711 scaling to 16 bits and the placement of valid bits in WAVEFORMATEXTENSIBLE and AIFF; the 2× true-peak filter (the Annex gives none); FFmpeg 8's weights and true peak; the CI image's sox; the DR meter's specification.
- The 24-bit noise floor per bin and Hann's leakage at 1 kHz from an edge are worked out from formulas.
- Bounds computed rather than measured: the loudest block (+19 LUFS for 24 channels, about +11 for 5.0), the gate-bin error 4.34 p LU, the channel-count memory of a 65,535-channel header, the two 64 MiB copies of the parallel queue.

## Sources

- The code at c517387: `Pcm.cs`, `DecodeContext.cs`, `DecodeSetup.cs`, `StreamInfo.cs`, `CategorySeries.cs`, `StreamVerification.cs`, the host's `DecodeSession`, `DecodeRun`, `DecodeOptions`, `AnalysisOptions`, `ParserRouter`, `VerificationText`, `ReportCsvWriter`, the four decoders' `DeclareOutput` calls (TrueHD's `Decoding/StreamDecoder.cs`), the WAV (`DataChunks.cs`, `Chunks.cs`'s `bext` reading, `FormatTags.cs`), AIFF (`AifcTypes.cs`) and TTA parsers, the topic `lowpass.md`, and the GUI's `GraphView`, `DetailViewModel`, `DetailWindow.axaml(.cs)`, `CapitalizationTests` and `VerifyAudioTests`.
- [decoders.md](decoders.md), [parallel-decoding.md](parallel-decoding.md), [coding-graphs.md](coding-graphs.md), the [plan](../PLAN.md), [cli.md](../cli.md), [gui.md](../gui.md), [development.md](../development.md).
- ITU-R BS.1770-5 (11/2023), "Algorithms to measure audio programme loudness and true-peak audio level", Annex 1 (K-weighting, Tables 1 to 3, gating, equations 1 to 7), Annex 2 (true peak, the 48-tap filter) and Annex 3 (Tables 4 and 5, position weights), read from itu.int on 2026-10-26.
- EBU R 128, EBU Tech 3341 (loudness metering, "EBU Mode": Table 1 tolerances, 2.6 true peak) and Tech 3342 (2023: loudness range, 3.1 and the reference code), read from tech.ebu.ch on 2026-10-26; the EBU loudness test set itself was not read.
- libebur128 (Jan Kokemüller, MIT): `ebur128/ebur128.c`, `ebur128.h` and `COPYING` of the master branch, read on 2026-10-26.
- BIDS/colormap `colormaps.py` (Nathaniel J. Smith, Stefan van der Walt and Eric Firing; the header releases it under CC0), read on 2026-10-26.
- J. W. Cooley and J. W. Tukey, "An algorithm for the machine calculation of complex Fourier series", Mathematics of Computation 19 (1965).
- H. V. Sorensen, D. L. Jones, M. T. Heideman and C. S. Burrus, "Real-valued fast Fourier transform algorithms", IEEE Transactions on Acoustics, Speech, and Signal Processing 35(6) (1987).
- F. J. Harris, "On the use of windows for harmonic analysis with the discrete Fourier transform", Proceedings of the IEEE 66(1) (1978).
- P. D. Welch, "The use of fast Fourier transform for the estimation of power spectra", IEEE Transactions on Audio and Electroacoustics 15(2) (1967).
- G. Heinzel, A. Rüdiger and R. Schilling, "Spectrum and spectral density estimation by the Discrete Fourier transform (DFT)", Max-Planck-Institut für Gravitationsphysik (2002).
- License texts read on 2026-10-26: KISS FFT (`COPYING`, `LICENSES/BSD-3-Clause`), PocketFFT (`LICENSE.md`), PFFFT (`LICENSE.txt` of marton78/pffft), muFFT (`COPYING`), Math.NET Numerics (`LICENSE.md`), libebur128 (`COPYING`); minimp3's CC0 from `third_party/minimp3`.
- The tools of the prototypes: LAME 3.100, vorbis-tools 1.4.2, FFmpeg 6.1.1 (Ubuntu's 6.1.1-3ubuntu5), flac 1.4.3, .NET 10.0.401.

## As built: step 1

Step 1 (framework and spectrum) follows the design; where it was ambiguous or wrong against the code, the reading closest to the intent was taken, and each is listed here. Nothing departs from the architecture.

### Departures and readings

- **The search runs on the 512 rows, and a frequency is a row's upper edge.** The prototype searched bins; the design's later text (rows, 505, the counters) is on rows, so the step search, the edge and the transition work on the average spectrum's rows in tenths of a dB, with integer sums. The reported frequencies are whole hertz at the upper edge of a row (43 Hz wide at 44.1 and 48 kHz): the step is the boundary above its row, the cutoff the upper edge of the edge row, the transition start the lower edge of its row. A row with no power counts as -200 dB in the search. The figures differ from the prototype's table in the last row.
- **The bands above a candidate stop at row 506**, the last row that starts below 99 % of Nyquist (the design's "top of the search"), so the candidate row 505 still has one row above it. With the bands running to row 511 the notch that digital filters have at Nyquist gave pink noise through a 16 kHz lowpass steps of 29 and 26 dB; stopping at row 506 gives 18.5, 22.3 and 21.0 dB, as the design's prototype (19.1, 21.9, 21.0).
- **The level of a frame at a row** (the counters' axis) is the binary exponent of the mean power under the row, taken as `under * (1 / W)`; the own-step comparison is `under * above_rows >= 10^1.5 * W * over`, which is a product comparison as designed.
- **The record is smaller than the design's.** `StreamMeasurements` has no `Levels`, `Loudness` or `TruePeak` (steps 2 and 5 add them); `MeasurementKinds` has `Spectrum` only (`None` and `Spectrum`), and `--measure` takes only `spectrum`. Added: `SpectrumFigures.Frames`, `SpectralCutoff.IsSearched` (false under 8,000 Hz), `StreamMeasurements.NonFiniteSamples`, and the constants of `MeasurementLimits` (a static class: a type named `Measurements` would have read as the property of `StreamInfo` it is next to).
- **JSON.** The design's example has two keys `rows` in `spectrum`; the count is `rowCount` and `rows` holds the arrays. Added: `reason`, `channelLayout`, `hasClampedSamples`, `nonFiniteSamples`, `channelCutoffs` (a cutoff for each measured channel), and `isSearched: false`. `--measure-grids` is carried to the JSON writers by `ReportFilter.WritesGrids`, so no writer signature changed.
- **CSV.** Only `Spectral Cutoff (Hz)` is added (empty where no cutoff is found); the columns for peaks and loudness come with the steps that compute them. The numeric exemption is in place and tested (`ReportCsvWriter.Cell`), but no step-1 column is negative.
- **Columns.** A frame belongs to the column of the hop where it starts; a column without a frame holds 0 in the spectrogram and 0 and 0 in the waveform. A stream shorter than one frame is measured as one frame padded with zeros (the design left it open); frames never cross a gap, so a stretch between gaps shorter than a frame has none.
- **Status of a record.** A decoder that failed or stopped early gives `Stopped`; a session whose decoder declared no output and decoded samples is read as the DSD case; a stream refused for the memory bound is `NotPossible` with `measure.streams-limited`.
- **Cancellation** uses the decode run's token (the analysis's, the front end's stop and the decoders' budget), so a stop also stops the measuring. The final check is in `Analyze`, before any record is attached.
- **The memory bound covers the records.** A record stays in the report after its stream ends, so the host keeps its share (the grids and rows, `StreamMeasurer.RecordBytes`, up to about 0.6 MiB) reserved until the analysis ends and releases only the working state; a stream that no longer fits is `NotPossible` with `measure.streams-limited` (the design's alternative of dropping its grids was not taken). `MeasurementRouteTests` runs 600 streams in one analysis and holds their grids under the bound.
- **The grid's time origin** is the hop the stream's first sample falls in, not position 0, so frame numbers that start late (a FLAC at sample 2^24) do not fill the grid with empty columns.
- **The exception boundary** also covers making the measurer (inside `DeclareOutput`, which the decoder calls) and making the figures (at the close): a bug there is the stream's `measure.failed` with status `Failed`, never `parse.failed` or `decode.failed`. `MeasureOptions.Probe` (internal) raises one at `open`, `block` or `result`.
- **Allocation.** The grid's cells and the cutoff counters (about 2.3 MiB and 0.3 MiB a channel) are allocated with the first frame and charged 2^21 units for it, so a stream that never fills a frame, or the thousands of streams of a crafted Ogg file, cost nothing in memory or time. The memory bound's estimate (`StreamMeasurer.EstimateBytes`) is held by a test against the allocation of a 65,535-channel stream.
- **Tests.** The block-size independence of the real decoders is tested by keeping the PCM of FLAC, WavPack, Monkey's Audio and TrueHD fixtures and measuring it cut four ways, not by twin files; the committed twin is the 16 kHz FLAC at two block sizes. `MeasureOptions.Probe` is an internal seam that lets a test raise the exception of a measurement bug.
- **Fixtures.** FLAC cannot hold floats, more than 8 channels or a rate of 0, so those cases are scripted decoders (`MeasurementTests`, `MeasurementRouteTests`). The lines fixtures are dithered but one, because a signal that repeats with the frame's period has a quantization error that repeats too, a floor of lines that does not average over frames (the edge then jumps to a random high row); the natural rolloff is pink noise (white noise gave steps over 25 dB). `lines-16000-44100-quiet-45` finds its cutoff (32.7 dB) where the prototype's lossy decode at -45 dB did not (20.1 dB): the fixtures' observations are the ones the articles state. The lossy-derived files are 1 s of mono (the design had 3 to 4 s of stereo) with half the design's bitrates, for the size of the repository: 50 files and 3.0 MB in all, 35 of them synthetic. The bitrates were halved because mono at the stereo bitrates left FFmpeg's aac with no cutoff and gave the LAME files the same lowpass; at the halved rates LAME `-b 64` has the stereo `-b 128` lowpass, so the files are not the design's but the same kind. Lost coverage: `-b 96` and `-b 160` share the 20.5 kHz lowpass, so they test one setting twice.
- **The synthetic list departs from the design's** in dropping the 48 kHz twins of the lines at 11, 19 and 20.5 kHz (the 16 kHz lines remain at both rates), for the size of the repository; 48 kHz stays covered by the 16 kHz pair, the impulse fixture and `lame-b64-48000`.
- **The edge uses the frames' own steps (changed after review).** The first build took the design's rule: the highest row more than 6 dB above the mean level above the step, on the average spectrum. It depended on the share of transition frames: "lines then silence" (a brickwall at 16 kHz, then digital silence) read 17,140 Hz because the splatter of the cut inside a frame lifts a few frames' rows, and the fixture's tolerance had grown from 1,000 to 1,500 Hz. The edge now comes from the counters the consistency test uses (the figure is built from the frame counters, not from the average spectrum). From the step's row, walk up while at least half of the frames with signal show their own step of 15 dB at the row, and stop at the first row that does not (a tone above the edge cannot set it: a 19 kHz tone at -70 dBFS over a 16 kHz FIR read 19,035 Hz when the walk came down from row 505). The edge is the last showing row less W - 1 rows, W being the band's width in rows: the lowest row of the band under it, because a frame shows a step at a row while the band under it still reaches its signal.
  - **What the figure means.** The cutoff is never below the step. It equals the step on a sharp edge and lies up to a few hundred Hz above it on a gentle slope (`lines-slope-15000-17000`: step 15,848 Hz, cutoff 16,193 Hz, between the pass edge at 15 kHz and the stop edge at 17 kHz). The shift of W - 1 rows is exact only for a brickwall with a flat, deep stopband; elsewhere the clamp to the step's row decides: for an edge within W rows of row 505 (the walk ends at the last candidate), for the skirts that Hann leakage puts around a strong component, and where the frames' steps are weak.
  - **Results.** Every fixture stayed inside its tolerance (the construction, and the printed band of every LAME file with one); "lines then silence" reads 16,021 Hz with the default tolerance; the lossy files' cutoffs lie 0 to 43 Hz above their step (the first rule read 43 to 1,593 Hz above it). The model, the manifests, the goldens, the articles and the description were updated. `CutoffFinderTests` pins a tone above the edge, an edge within W of row 505 and one splatter frame.
  - **Limits that remain.** Whether a cutoff is found at all still depends on the average step, so broadband clicks can hide one: clicks every 0.5 s on a 16 kHz lowpass brought the step to 23.3 dB. The frames with signal are those within 30 dB of the level 1 % of the frames reach, so with signal in under 1 % of the frames silence counts as signal and no cutoff is found: 0.3 s of signal in 60 s found none.
  - **Order of findings.** A failure while sizing the record (`bytes`) or making the figures (`result`) adds its `measure.failed` after the findings `Finish` has added for the stream (`measure.limit`, `measure.channels-limited`); both paths do the same, and findings without a node keep the order they were added in, so the order is consistent: the failure comes last.
- **Cross-check.** `crosscheck_measurement_fixtures.py` (flac, FFmpeg, ffprobe decode the same samples as the model) runs in the FLAC job; FFmpeg's `showspectrum` and `astats` are not needed in step 1. It ran here with flac 1.4.3 and FFmpeg 6.1.1 (the script asserts the 26.04 versions): 49 files, no difference.
- **`check_aot.sh`** (passes) runs with `--measure` over every fixture: JIT 19.0 s with `--verify`, 19.6 s with `--measure`, so it measures all of them.
- **Not done in step 1:** the PCM route, levels, loudness, true peak, the GUI and the TTA decoder (steps 2 to 5 and decision 12).

### Measured

- **Time**, interleaved with the FLAC reference per round (`MeasurementWorkTests`, median of five): two channels 13.9 ns a sample of a channel at 44.1 kHz (4,096 points), 15.2 at 22.05 kHz, 17.4 at 8 kHz (1,024 points), 16.3, 15.1 and 18.0 at 88.2, 176.4 and 352.8 kHz; one channel 14.7, 19.3, 18.8, 16.7, 17.7 and 16.3; with the cutoff counters and both grids, against the design's 13.6 for the second prototype. The ratios to the reference's time per unit are 1.03 to 1.45 with the design's weights, so they stay (a mono case at 2,048 points is the highest). A single stereo round under load has read 16.2 ns at 4,096 points. The window, FFT and power alone cost 11 to 12 ns. The figures at the end of a stream (the grid's 524,288 logarithms) cost about 20 ms and are not timed.
- **Memory:** `MeasuringManyFlacFramesHoldsNothingPerFrame` reads 0.27 bytes per further frame; the report keeps the rows and grids (under 1 MiB). `MeasuringManyChainedStreamsStaysWithinTheBound` (400 chained Ogg FLAC streams of 33 s at 8 kHz, two minutes): 232 are measured, their grids hold 61.8 MB, and measuring adds 63.0 MB to what the report keeps and 62.5 MB to the peak, under the bound of 64 MiB; the other 168 say `measure.streams-limited`.
- **Determinism:** the pinned hashes and the grids' hashes of 50 fixtures agree with the JIT defaults, `DOTNET_EnableHWIntrinsic=0`, `DOTNET_EnableAVX2=0`, `DOTNET_EnableAVX512F=0`, `DOTNET_TieredCompilation=0` and `DOTNET_TieredPGO=0` on x64; the NativeAOT build (published for linux-x64 here) passes `scripts/check_aot.sh` with `--measure` on all 8,006 inputs, and its `--measure-grids --json` output over the measurement fixtures (every grid) is byte-identical to the JIT build's, also with `DOTNET_EnableHWIntrinsic=0`.

## As built: step 2

Step 2 (levels) follows the design; where the text left something open or was ambiguous against the code, the reading closest to the intent was taken and each is listed here. Nothing departs from the architecture, and no finding code is new: the levels raise none (decision 6) but the existing `measure.non-finite-samples`, which they share with the spectrum.

### Departures and readings

- **The record.** `LevelFigures` (in `StreamMeasurements.Levels`) has `Format`, `BitsPerSample` (0 for floats), `Stream` and `Channels`, the last two being the same record, `ChannelLevels`. Every figure is an integer in a fixed unit: a level in tenths of a dB (`MeasurementLimits.NoLevel`, `int.MinValue`, for minus infinity), counts in samples, the DC offset in millionths of a percent of full scale (`DcOffsetMicropercent`, signed, so one least significant bit of 24-bit audio, 12 millionths of a percent, still shows) with its magnitude as a level in tenths.
- **The stream's figures** were left open by the design and are: the sample peak of the highest channel; the RMS level of all measured channels together (the square root of the mean of all their squares, in channel order); the DC offset of the channel with the largest one; clipping runs and samples added up over the channels and the longest run the longest of any; digital silence of the sample frames in which every measured channel is 0 (the design's "in every channel"), with the same three counts per channel besides, so that a dead channel shows; the low bits that no sample of any channel has set (the OR of the channels' ORs) and the highest magnitude of any.
- **Clipping runs** are runs of at least `MeasurementLimits.MinimumClippingRun` (3) consecutive samples at the same extreme: the most positive code or the most negative code (a run of the maximum followed by the minimum is two short runs), floats at or beyond +1.0 or -1.0. The extreme codes are those of the declared bits (`2^(bits - 1) - 1` and `-2^(bits - 1)`). A run is counted when it ends (a sample that is not the same extreme, a gap, the end of the stream); one that spans pieces or blocks is carried in the state. Because the extremes are those of the declared bits, audio with low bits unused is asymmetric: 16-bit audio padded to 24 bits tops out at 0x7FFF00, never at 0x7FFFFF, so only its negative runs count; clipping at the extremes of the bits in use is on the step 6 list.
- **Digital silence** has two figures (changed after review): the stretches that start and end the measured samples (`SilentAtStart`, `SilentAtEnd`, the text `Digital Silence`), and the count of every sample frame that is exactly 0 in all channels (`ZeroSamples`, the text `Zero Samples`, JSON `levels.zeroSamples`, a figure of its own beside `digitalSilence`; the design's "in total"). The count includes the zeros of quiet audio and of a sine's zero crossings (5,122 in `synthetic-lines-16000-44100-quiet-60`, 173 in `synthetic-sine-24-bits`), which a figure named silence would mislead about, so it is named for what it counts. The start is the run of zero frames before the first frame that is not 0, and it also ends at a gap; the end is the run after the last frame that is not 0, and it starts again after a gap; a stream that is 0 throughout has the start, the end and the count equal. The design gives no minimum length for the count; a minimum stretch is on the step 6 list.
- **Bit-depth usage.** Integers: `LowBitsUnused` is the number of trailing zero bits of the OR of every sample, `null` (not "24 of 24") where every sample is 0, and `HighestMagnitude` the largest magnitude as a code (the most negative code is 2^(bits - 1)). Floats: `HasOnly16BitValues` and `HasOnly24BitValues`, true when every sample is a whole multiple of 2^-15 (2^-23) in [-1, 1), so a +1.0 sample makes both false and a stream of zeros has both true; 16-bit values are 24-bit values, so the first being true makes the second true. They are the stream's, the AND over the channels.
- **Floats** go through the stream measurer's conversion that the spectrum uses (NaN and infinity counted and measured as 0, magnitudes over 1,024 clamped and flagged, so a peak of +60.2 dBFS is the clamp), once, whatever kinds are asked for; the sums are made sample by sample in double, with no compensation and no vector, in the order of the samples, so the figures are the same for every cutting of blocks. The text reports `Has Only 16-Bit Values` and `Has Only 24-Bit Values` for them and no `Low Bits Unused`.
- **Exact sums.** Squares are added in a 64-bit partial sum per piece of at most `2^(66 - 2 bits) - 1` samples (262,143 at 24 bits, so a whole piece of 16,384; 4,095 at 27 bits) and the partial is folded into a `UInt128`; from 31 bits on, where the fold would come every 15 samples or less, each square goes into the `UInt128` directly. The sum of a piece (at most 16,384 samples of at most 2^31 each, under 2^45) is a `long` folded into an `Int128`. The DC offset in millionths of a percent is `(2 * |sum| * 10^8 + n * fullScale) / (2 * n * fullScale)` in `UInt128`, the mean rounded half away from zero, and the double conversion for the dB figures splits the `UInt128` into four exact 32-bit parts added in a fixed order (`LevelsMeter.ToDouble`), so it rounds the same on every platform. `SumsWiderThan64BitsAreExact` holds it: 400,000 samples of -2^23 and 100,000 of -2^31 read 0.0 dBFS, 0.0 dBFS peak and -100 percent exactly.
- **Pieces.** The levels take a block in pieces of at most 16,384 samples of a channel (the largest hop), the pieces of the spectrum when the spectrum runs, so the scratch arrays (the OR of the channels, the zero flags of a float channel; 64 KiB each) are fixed and allocated when first needed (one for streams of several channels, one more for floats). `StreamMeasurer.EstimateBytes` counts them and 256 bytes for each channel and the record; `TheMemoryEstimateCoversWhatTheLevelsAllocateEvenForAHeaderOf65535Channels` holds the estimate against the allocation for integers and floats, alone and with the spectrum.
- **A stream without a rate** (0 Hz) has its levels, which need none; the spectrum is not measured.
- **The work weight** is 4 units for each sample of each measured channel for integers, added to the spectrum's weight (or alone), so the levels are 4 of 27 units with the spectrum; the design's weight stands for integers (see Measured). Floats are 14 units (changed after review): they are converted safely first and checked against the bit-depth grids, and doubles of any magnitude, subnormal ones included, took 7.5 to 10.6 ns a sample, 3.4 to 4.5 times the reference at 4 units and 1.0 to 1.3 at 14.
- **Names.** Text: `Sample Peak` (the design's), `RMS Level` (the abbreviation RMS is a spec acronym), `DC Offset`, `Clipping`, `Digital Silence`, `Zero Samples`, `Low Bits Unused` (the design's), and `Has Only 16-Bit Values` and `Has Only 24-Bit Values` (the design's). JSON: `samplePeak`, `rms`, `dcOffset` (in `%`) and `dcOffsetLevel` (in dBFS) as `{value, unit, perChannel}` (a single-channel stream has no `perChannel`), `clipping`, `digitalSilence` and `bitDepth` as objects of counts with `perChannel` arrays of objects. The design's `levels` example showed `samplePeak` only.
- **Text,** one line per figure, with the figure of each channel in parentheses for a stream of several channels (by speaker where the stream states distinct ones, else `channel 1`); counts are grouped (`1,000 samples`), seconds appear from a tenth of a second.
- **CSV.** One column is added, `Sample Peak (dBFS)`, after `Spectral Cutoff (Hz)` (the design's other columns come with their steps): the highest over the channels of the first audio stream, one decimal, empty for silence and without `--measure`. It is the first negative number the writer's numeric exemption writes; `TheCsvFillsIsVerifiedAndTheCutoffOnlyWithMeasure` and `TheCsvWritesANegativePeakAsANumberAndGuardsAFormulaInAName` read it back as `-8.7`, not `'-8.7`. Every test that counts the CSV's columns changed (ObjectAudio, Protection, CommandLine, TypedData and Ogg tests).
- **`--measure` kinds.** `levels` is a name; `--measure levels` runs the levels alone (about 2 ns a sample against about 15 with the spectrum), and `--measure` alone runs both.
- **Articles.** New: `levels` (sample peak and RMS level, the dBFS convention, exactness), `clipping`, `dc-offset`, `digital-silence`, `bit-depth-usage`; updated: `measurements`, `wasted-bits`. The design's `sample-peak-and-true-peak` article comes with true peak (step 5), and `pcm-samples` with the PCM route (step 3), where WAV and AIFF samples are first measured. The articles state observations only about Encspect's test files (the levels fixtures), none about an encoder.
- **Fixtures.** Eleven synthetic FLAC files (61 FLAC files and 3.2 MB in all): `levels-sine-full-scale`, `-most-negative-8-bits`, `-square-full-scale`, `-dc-offset`, `-clipping-runs`, `-silence-positions`, `-silence-stereo`, `-low-bits-24-in-16`, `-silent`, `-gaps` and `-full-scale-32-bits`; a float stream, which FLAC cannot hold, and the 65,535-channel header are scripted decoders in `MeasurementLevelsTests`. The design's "a stream longer than..." cases are step 1's. Every manifest and golden gained the levels; the other fixtures' numbers did not change.
- **The model's levels** are independent of the analyzer: `levels_of` works on Python integers from the decoded samples (see development.md). The model works the tenths of a dB out in 60 decimal digits and the analyzer's must equal them; counts and the DC offset in millionths of a percent are compared exactly too.
- **Cross-check.** `astats` and `silencedetect` (development.md, "Measurements"): peak, RMS and DC offset to the convention of astats' sample scale (it divides by 2^(B - 1) - 1, Encspect by 2^(B - 1)), bit depth and samples with `astats`, the long stretches of silence with `silencedetect`. The design's "sox stats where the package is installed" was not done: sox is not on this machine. The design's open question of which RMS convention the tools use is answered for FFmpeg's astats: a full-scale square wave reads 0 dB and a full-scale sine -3.010045 dB, as here.
- **`scripts/check_aot.sh`** (the path is `scripts/`, not `scripts/ci/`) measures all fixtures with `--measure`, now including the levels.
- **Not done in step 2:** the PCM route, the GUI's panels (the levels have no panel yet), loudness and true peak, `sox`.

- **Changes after the independent review:**
  - **Samples beyond the declared bits.** A decoder that does not clamp (FLAC's restore on a corrupt frame) can write a sample outside the declared width, which would wrap the 64-bit partial sum sized from the width (ten samples of -2^31 in a 16-bit stream read +89.3 dB instead of +96.3). A piece whose smallest or largest sample is outside the range goes into the `UInt128` square by square.
  - **Floats under 1e-30** are kept for the levels (a sample that is not 0, a finite peak) and flushed to 0 only in the spectrum's buffers, after the levels have read them. The waveform's minimum and maximum read the flushed values, as before.
  - **Subnormal cost.** Samples under 1e-30 reach the levels, and a Float64 stream can carry subnormal numbers, on which every operation costs 20 to 30 times a normal one (a microbenchmark of the first form read 3.1 ns a sample on normal values and 49.9 ns on subnormal ones, at a charge of 4 units). Rule: a float sample with a magnitude under 1e-150 takes part in no arithmetic. Its square (under 1e-300; the smallest normal double is 2.2e-308, so 1e-150 leaves headroom for the squares and for their sum) would be near the subnormal range, and a subnormal input would be added to the DC sum; so the sums take 0 instead and the comparisons (peak, zero samples, silence, clipping, `Has Only` checks, where such a sample is not a multiple of 2^-23) use the raw value. The cost is figures under 1e-300 left out of a sum: a stream of such samples has a finite peak and no RMS level or DC offset (`-inf`). `TheLevelsOfDoublesOfAnyMagnitudeTakeAtMostTwiceTheFlacReferencesTimePerUnit` times streams around 1e-310, 1e-160 and 0.5 against the reference.
  - **JSON.** `zeroSamples` is a figure of the levels beside `digitalSilence` (`{value, unit: samples, perChannel}`), since it is not part of the silence.
  - **The stream's DC offset** picks the channel by the size of its sum, not by the rounded figure, so a channel with no level never beats one with an offset.
  - **The gaps fixture** now removes the second, fourth and sixth blocks, so that the stretch at the start ends at a gap, the stretch at the end starts after one, and two samples before a gap and one after it would make a run of three; the old one passed every test with the resets removed. Planted removals now fail: `state.EndRun()` in `Gap()` 4 tests (one unit, three on the fixture), `state.Silence.Gap()` 5 (two unit, three on the fixture), `_joint.Gap()` 1 (the stereo unit test; a mono stream has no joint state).
  - **The model** works the tenths of a dB out in 60 decimal digits and the tests compare them exactly.
  - **The cross-check** treats every field that `astats` or `silencedetect` does not print, and a file for which nothing was compared, as a failure; `astats` also reads the gap files.
  - **The CSV's** `IsNumber` accepts only an optional minus sign, digits and a fraction.

### Measured

Same machine as step 1 (Xeon at 2.80 GHz, 4 vCPUs, .NET 10.0.401, JIT), one pinned core, `MeasurementWorkTests` interleaved with the FLAC reference; the machine was shared with other sessions, so the load average read 1.3 to 2.9 during the runs (the design's limit is 1.5) and each figure below is the median of five rounds of one run.

- **The levels with the 128-bit accumulators: about 2.0 ns a sample of a channel** at 16, 24 and 32 bits, for noise, one channel or two (1.7 to 2.3 ns over four runs; the first test of a process reads up to 3.7 ns). The first prototype's 2.0 ns, measured with 64-bit sums, stands: the 128-bit partial sums cost nothing measurable (24-bit samples fold every 2^18 samples; 32-bit samples add each square to the `UInt128`, 1.8 to 1.9 ns). The ratio to the reference's time per unit is 0.9 to 1.1 with the weight of 4 units, so the weight stays.
  - **The worst cases:** a full-scale square wave, which makes every piece scan for runs, 3.4 ns before the scan counted a run inside the loop with one comparison (the first build kept its counters in the state) and 2.8 ns after (ratio 1.38); digital silence, 2.9 to 4.2 ns in the first two tests of a process before the work tests warmed up three times and 2.6 ns after (ratio 1.08), because the library's vector routine that looks for a sample other than 0 starts as precompiled code.
  - **The square wave on Linux ARM (nightly of e85cc17):** `TheLevelsTakeAtMostTwiceTheFlacReferencesTimePerUnit` failed for 16-bit stereo square with a ratio of 2.12 (the other levels cases passed). Measured on x64 in alternating isolated runs, the square wave was the slowest levels case: 2.2 to 2.9 ns a sample against 1.5 to 2.1 for noise, ratio 1.33 to 1.57 (noise 0.7 to 1.2, but up to 1.66 once for 32-bit noise on a loaded machine), so it had the least margin even on x64 and the x64 intrinsics hid part of the gap. The cause is the scalar clipping scan, which walks every sample of a run. The weight stays at 4 units and the bound at 2: the kernel is faster instead. A run of 16 or more samples is now skipped with `IndexOfAnyExcept` (the library's vectorized integer search, which also has an ARM path); shorter runs stay in the loop. Same machine, builds alternated twice: square 1.97 and 2.07 ns a sample, ratio 1.00 and 1.14, against 2.18 and 2.29 ns, ratio 1.37 and 1.29, before; noise unchanged. `ARunOfAnyLengthIsCountedOnceWhereverItEnds` holds the counts around the switch and across pieces. Not measured on ARM here: if the nightly of that platform still reads close to 2 for this case, the next step is a weight of 5 for integers, not a looser bound.
  - **With the spectrum** the levels add 0.6 to 3 ns to the 13.2 to 14.7 ns of the spectrum at 44.1 kHz stereo (two runs; the cases that run first in a process read higher), and the whole stays at 16.6 to 20.0 ns at every FFT size, ratios 1.02 to 1.22 (table in development.md).
- **Kernels compiled optimized:** every method that loops over samples carries `AggressiveOptimization`. Without it the 32-bit case read 6.4 ns (3.5 times the reference) in the first test of a run, from tier-0 code of the per-sample 128-bit loop.
- **An impulse, then silence** against zeros, the fastest of five alternating rounds each: 0.062 s against 0.061 s (ratio 1.02). The first form of the test, one run of each, read 1.96 to 2.11 whichever came first, so it was changed.
- **Memory:** `MeasuringTheLevelsOfManyFlacFramesHoldsNothingPerFrame` holds the growth per further frame under the bound of the decoders' test and makes the report keep under 100 KB more; the levels' state is 256 bytes a channel plus a scratch array of 64 KiB for streams of several channels, one more for floats, and 128 KiB of doubles for floats measured without the spectrum.
- **Determinism:** `TheLevelsOfSyntheticStreamsHashToConstants` (16-bit with a gap, 24-bit, floats with NaN and a huge sample) and the 11 new goldens agree under the JIT defaults and `DOTNET_EnableHWIntrinsic=0` (3,054 measurement and description tests); `scripts/check_aot.sh` (NativeAOT for linux-x64 against the JIT) reports the same on 8,033 inputs with `--json --measure`.
- **`check_aot.sh` time:** the JIT run over the corpus takes 15.5 to 16.1 s with `--verify` and 16.9 s with `--measure` (spectrum and levels), so it still measures every fixture.
- **`--json` without `--measure`** is byte-identical to `main`'s over all of `tests/fixtures` (8,033 inputs, the new fixtures included; the version line aside).

## As built: step 3

Step 3 (the PCM route) follows the design; where the text left something open or was ambiguous against the code, the reading closest to the intent was taken and each is listed here. Nothing departs from the architecture; the contract grew by one record (`PcmCoding`), one property of `DecodeSetup` (`Pcm`), one member of `IDecoder` (`DeclaresChecksums`), one property of `StreamVerification` (`HasChecksums`) and one helper of `Core.Abstractions` (`PcmFeed`). No finding code is new.

### Departures and readings

- **`PcmCoding` carries the sample rate and the speakers besides the design's three fields.** The design's record has the encoding, the byte order and the container bits, and says `Configuration` and `Format` stay unused; but the decoder must call `DeclareOutput` with a `PcmFormat`, whose sample rate and channel layout (the LFE the combined spectrum leaves out, and later the loudness weights) only the container knows. So `PcmCoding.SampleRate` (0 where the container states none, `int.MaxValue` for a WAV rate above it) and `PcmCoding.ChannelLayout` (null where it states none) travel in it, and `Format` stays unused. `PcmCoding.OutputBits` (16 for A-law and mu-law) is what the parsers put in `DecodeSetup.BitsPerSample`.
- **The router's rule is a property of the decoder.** The design leaves "a flag on the decoder, or on the session's setup" open; it is `IDecoder.DeclaresChecksums`, a default interface member that is true, false for `pcm`. `ParserRouter.StartDecoding` returns a `NotRequested` session for such a decoder unless `DecodeOptions.Measure` or `Output` is set, so `--verify` alone opens no PCM session, reads no byte of the samples and costs a folder of WAV files nothing. The decoder wrappers of the tests (`CountingDecoder`, `HollowDecoder`) forward it.
- **`StreamVerification.HasChecksums`** (false when the list of checksums is empty) is what the text writer, the JSON writer, `DetailViewModel.VerificationText` and `CanVerifyAudio` read to leave out a verification that declares none. It is not tied to PCM: a verification with no checksum from any other source would be left out too, and none exists today (every stream of every fixture reports its verification as before; see "Measured" for the records that differ). `CanVerifyAudio` is `Verification is { Decoding: NotRequested, HasChecksums: true }`; step 4 drops the extra condition with the rename.
- **The feed is shared code.** `PcmFeed.Run` in `Core.Abstractions` (the design allowed moving the unpacking there only for a second project; the feed is not the unpacking) reads the data chunk in slices into one reused buffer and hands each as a `CodedFrame`, for the WAV, Wave64 and AIFF parsers alike. A slice is 64 KiB of whole sample frames, or one sample frame where that is larger (65,535 channels of 64-bit floats take 512 KB); the frames are clamped to the bytes the file holds, so a data chunk cut short or a size that is a placeholder never reads past the end. The unpacking itself stays in the WAV project's `Decoding` namespace.
- **Which streams get a session.**
  - WAV and Wave64 (one path, `DataChunks.AddStream`): linear codings with a width the coding has (integers of any byte width, floats of 32 and 64 bits, A-law and mu-law of 8), a block alignment that is 0 or the channels times the bytes of a sample, at least one channel, and the first data chunk outside a `wavl` list with no `slnt` silence. Not the codings that route data to a codec parser, DTS in PCM samples, IEC 61937 bursts (recognized or not), and the widths and alignments the findings `wav.float-bits`, `wav.companded-bits` and `wav.bits-per-sample-zero` or the alignment finding report; those streams are reported as before, with no `Verification`.
  - Integers of 40 bits and wider start a session, whose decoder refuses them (`decode.not-possible`, info, status `NotPossible`, the record's reason the finding's text). A width over 64 bits is refused by the router before the decoder (it states the channels and bits).
  - AIFF: the types that store samples one after another (`NONE`, `twos`, `sowt`, `in24`, `in32`, `raw `, `fl32`, `fl64`, `alaw`, `ulaw`), with a Sound Data Chunk and a sample size the type follows (a sample size of 1 or more where the type states no width). The sound data starts at the chunk's offset field's end; the frames are the stream's count (the Common Chunk's, cut to the sound data). A sample size over 32 bits starts a session the decoder refuses, as for WAV (`decode.not-possible`); a size of 0, the packed codings and an unlisted type start none. `sowt` is read at the stated width.
  - A data chunk with no whole sample frame starts a session that declares no output: verification `Decoded` with 0 samples, and, new in this step, the record `NotPossible` with the reason "the stream holds no samples", only where the setup states 0 samples, no frame was handed or failed and nothing stopped the decoding (the first version also fired on streams that hold samples but decoded no frame, 4 of 7 changed records wrongly; those and a stop or the budget before the first block stay `Stopped`, as on main, which a test pins over the 7 fixtures).
- **The container's value is the sample, whatever the valid bits.** The 20-in-24 fixture and the 12-bit WAV and AIFF fixtures decode to the container's value as FFmpeg does (the MD5 cross-check), so the design's open question has the answer it needed: nothing is narrowed or shifted. Where the bits sit is settled by sources read for this: AIFF 1.3, "Sample Points" (left-justified in the bytes, the rest zero) and Microsoft's Multimedia Programming Interface and Data Specifications 1.0, "Data Packing for PCM WAVE Files" (the amplitude bits in the most significant bits of the integer, the rest zero; a plain PCM `fmt ` chunk states them in `wBitsPerSample`, the extensible form also in `wValidBitsPerSample`); the decoder relies on neither and outputs the container's value.
- **A-law and mu-law are not lossy streams (decision 16(b))**, and the 16-bit scaling is verified: all 256 codes of each, in `built-alaw-all-codes.wav` and `built-mulaw-all-codes.wav`, decode to the MD5 of FFmpeg's decoder. The expansion is built from the segment formulas (no table), documented in development.md.
- **Findings:** none new. `decode.not-possible` is the one a PCM file can raise; the sink never fails, and the decoder raises none for a slice (every bit pattern is a sample).
- **The decode goldens.** No WAV or AIFF fixture had a `.decode.json`: the design's "goldens change" is read as "the PCM fixtures get them". Every fixture the decoder takes (150 WAV and Wave64 files and 103 AIFF files) has one (the status, the samples decoded, no checksum), beside the manifest's new `stream.pcm` (the samples and the MD5 of the canonical layout, from `pcm_model.py`), which `DecodedAudioMatchesManifest` holds the analyzer to. The `.report.json` goldens did not change, as the design says.
- **The WAV twins.** 22 of the synthetic FLAC fixtures as WAV files (the widths 8, 24 and 32 bits, 192 kHz in 24 bits, the 5.1 with its LFE, 2 and 8 kHz, a stream shorter than a frame, and the levels fixtures but the gap one), a Wave64 and two AIFF twins of the sines, and six fixtures only WAV can hold (floats of 32 and 64 bits, NaN and infinities, 1e300 and a subnormal, 33 channels, a header of 65,535). A twin shares its FLAC file's manifest and golden: `MeasurementPcmTests` asserts that the golden of its measurements (grids hashed) and its decoded MD5 equal the FLAC file's. The design's list had more twins (every synthetic fixture, the 48 s file); the repository takes 2.0 MB for these, and the rest add widths, rates and layouts the 22 already cover. The design's 5.1 versus 7.1 comparison and "5.0 without a layout" belong to loudness (step 5).
- **The cross-checks.** `crosscheck_wav_fixtures.py` compares FFmpeg's decoded MD5 with the manifest's on 161 fixtures; the differences it explains are the files FFmpeg refuses or reads as another coding (`pcm_f24le`, AC-3 bursts), the AIFF sound data beyond the frame count, and RIFX, where FFmpeg 6.1.1 reads the samples as little-endian (skipped, not asserted: 8.0.1 may differ). `crosscheck_measurement_fixtures.py` compares the 30 PCM files of the measurement directory with FFmpeg; only the header of 65,535 channels is skipped (FFmpeg refuses it). Both ran here with FFmpeg 6.1.1, the versions' assertions bypassed; CI runs 8.0.1.
- **Corrupt files.** `CorruptFiles` decodes and measures every variant of the WAV, Wave64 and AIFF fixtures (as for FLAC), with both budgets lowered; `PcmMalformedInputTests` adds the named cases: a data chunk cut short or of an odd size, a block alignment that disagrees, 65,535 channels (also of 64-bit floats), widths no coding has, a sample rate of 4 billion, a sound data offset past the end or of 4 billion, and every truncation of a file. The fuzzer already ran with `Measure`, so it reaches the decoder; no fuzz-regression file was needed, since nothing failed.
- **Not done in step 3:** the GUI (the command stays "Verify Audio" and PCM streams do not enable it), loudness and true peak, `sox`, and the TTA decoder.

### Measured

- **Time of the decoder**, `DecoderWorkTests` (five cases, each charging 2^29 units, interleaved with the FLAC reference, load 1.3 to 1.4, the nightly run on this machine): 0.08 to 0.11 ns a unit against the reference's 0.50 to 0.63, ratios 0.14 to 0.17, decoding 66 to 86 percent of the analysis with decoding on (three runs). In ns a sample of a channel that is 1.4 (A-law), 1.5 (16-bit), 1.9 (24-bit, 6 channels, big-endian), 2.2 (32-bit floats) and 3.1 (64-bit floats), the read of the file's bytes included. The weight is the fixed `DecodeWork.ForSample`, and it is six times what the time asks; it stays, since the floor of the charge fixes it and a lower one would break the budget's meaning for no use. All 96 cases of `DecoderWorkTests` and `MeasurementWorkTests` passed in 10 minutes, the measurements' ratios as in step 2.
- **A whole file** (`--measure` of 120 s of 16-bit stereo noise, 21 MB, load 4, three runs): 0.26 s with `--verify` (the samples are not read, so this is the start-up and the structure), 0.38 s with `--measure levels` and 0.62 to 0.69 s with `--measure`, which is 34 ns a sample of a channel for everything above the start-up, the JIT of the kernels included (the work tests give 14 to 17 ns without the start-up).
- **Memory:** `DecodingAndMeasuringALongWaveFileHoldsNothingPerSlice` reads 0.00 bytes a further slice of 64 KiB (10 MiB against 40 MiB) with decoding and with measuring; the report keeps 7.8 KB with decoding and 345 KB with measuring (the grids and rows), and `ALongWaveFileAllocatesNoBufferPerSlice` 192 bytes allocated a slice (the mapped bytes and the host's bookkeeping; the buffer, the planar arrays and the measurer's state are reused).
- **Corrupt files:** `CorruptWavFileTests`, `CorruptWavBuiltFileTests` and `CorruptAiffFileTests` (363 fixtures, about 1,300 variants each) decode and measure every variant: 4 minutes of wall time on a loaded 4-core machine for the three together. The path filter, `shards.json`'s note and the shard assignment (`corrupt-c`) are as before.
- **Determinism:** the 3,200 measurement and description tests (the WAV twins' goldens among them) agree with `DOTNET_EnableHWIntrinsic=0`; `scripts/check_aot.sh` (NativeAOT for linux-x64 against the JIT) reports the same on 8,086 inputs with `--json --measure`.
- **`--json` without `--measure` or `--verify`:** byte-identical to `main`'s (e8f51bc) for every fixture of `tests/fixtures`, the new ones included, in the text tree, `--json`, `--csv` and with `--verify` too; so no fixture's output changed without `--measure`. With `--measure` the WAV, Wave64 and AIFF fixtures that the decoder takes gain `measurements`, which is the point; their `verification` is not written.
- **`--measure --ndjson` against `main`'s over all 8,357 inputs of `tests/fixtures` (the review's comparison, after its fix):** 288 records differ, all of WAV, Wave64 and AIFF files (153 in `wav`, 104 in `aiff`, 31 in `measurement`) and no other format's. Each gains `measurements`: 280 `complete`; 4 `notPossible` with "not measured: the sample rate is not stated" (AIFF files whose rate is 0); 2 `notPossible` for integers wider than 32 bits (`wav/built-pcm-40-bit.wav`, `aiff/built-sample-size-33.aiff`, which also gain `decode.not-possible`); 2 `notPossible` with "the stream holds no samples" (`wav/built-empty-data.wav`, `aiff/built-sound-offset-past-end.aiff`). Three more gain a finding of the measurements besides: `synthetic-pcm-channels-33.wav` and `-65535.wav` (`measure.channels-limited`) and `synthetic-pcm-float32-non-finite.wav` (`measure.non-finite-samples`). The 7 FLAC, WavPack and Monkey's Audio records that the first build changed from `stopped` are `stopped` again.
- **Tests:** the full `Encspect.Tests` suite of a pull request (everything but the nightly trait) passed on 30,019 tests and failed two, `ReportMatchesGoldenFile` of the two all-codes fixtures, whose goldens did not exist yet (added after the first golden run, then created and passing); the 96 nightly tests pass; `Encspect.Gui.Tests` pass (330).

## As built: step 4

Step 4 (the GUI's panels) follows the design: the command, the captions, the three panels with the cutoff, the hover, the click, the keys and the automation text, and everything the rename touches. Where the design was silent or the code said otherwise, the reading closest to its intent was taken, and each is listed here. Nothing departs from the architecture: the GUI reads `StreamInfo.Measurements` and the host's `MeasurementText` and adds no type to the host or the core.

### Departures and readings

- **The command.** "Decode Audio" replaces "Verify Audio" in every place the design lists (`CanDecodeAudio`, `DecodeAudioAsync`, `StopDecoding`, `IsDecoding`, the button, the tooltips, the macOS File menu entry, "Not decoded: ", `DecodeAudioTests`, `docs/gui.md`, the four format docs, `decoders.md` and `parallel-decoding.md`, which carry a note that says so). The command analyzes again with `Decoding = new DecodeOptions { Measure = new MeasureOptions() }` (every kind the library has). `CanDecodeAudio` is `Verification is { Decoding: NotRequested }` for some stream, with the extra condition on declared checksums dropped, so WAV, Wave64 and AIFF streams enable it. A second button of the same command sits under the caption of the heading "Audio" while a stream says `NotRequested`; it is enabled by `GraphsViewModel.CanDecode`, which the detail model sets wherever it raises `CanDecodeAudio`.
- **What opens after the command.** When the decode that the command started ends, the first stream with panels shows its spectrogram (a reload, or showing another file, keeps the entry chosen before, as the other graphs do). The command does not change the tab.
- **The heading "Audio"** is the last of `GraphHeadings` and every audio stream has it: with panels, or with its caption only. So the list of a file has a stream choice entry for each audio stream and a WAV file, which has no series, has a Graphs tab; "No graphs: …" shows only for a file with no audio stream. `GraphsViewModel.DecoderNote` ("Waveform and spectrogram need a decoder, which … does not have yet") is gone; the caption replaces it, and where a stream has no entry at all the caption fills the chart's place (`EmptyNote`).
- **The panels' entries:** "Waveform", "Spectrogram", "Spectrum" (with the forms "Combined" and, for a stream of several channels, "Per Channel") and "Figures". They exist for the grids and figures the record has, so a record whose status is `Stopped` or `FormatChanged` lists what it holds and a `Failed` one none. "Figures" is the design's silence on where the levels and the cutoff figures sit in the window: a fourth panel that lists the lines `MeasurementText.Lines` gives the command line (the "Measured" line, "Spectral Cutoff", "Sample Peak", "RMS Level", "DC Offset", "Clipping", "Digital Silence", "Zero Samples", "Low Bits Unused"), each with its description as hover text and a name in the automation tree. Step 5's loudness lines will appear in it with no change; its own "Loudness" panel will be another entry.
- **Captions,** worded as the design words them, with the cases it leaves open filled by the status:
  - `NotRequested`: "Decode the audio to see its waveform and spectrogram." and the command.
  - `Verification` null or `NoDecoder` with no record: "The audio of this {codec} stream is not decoded here." with `StreamInfo.Codec` ("FLAC", "MPEG-1 Layer III"), and no command. It says nothing about whether a decoder exists. A TTA stream says `NoDecoder` in every analysis, so the command is not enabled for it and the caption stays this one (the record "no decoder for TTA" exists only in a measured analysis, which the GUI cannot ask for a stream that says `NoDecoder`).
  - A record of status `NotPossible`: "The audio cannot be decoded: {reason}." with the record's reason, the first letter lowercased where the second is lowercase too (a decoder's refusal starts with a capital; "DSD" and "TTA" keep theirs): "…: the stream decodes to DSD, which is not measured." A `Verification` of `NotPossible` with no record reads "…: the stream cannot be decoded." and cannot happen in the GUI, since a session is opened only when decoding is asked for.
  - `Stopped`: "Decoding stopped after 1.5 s of audio; the figures cover what was measured." (or "…before any audio was measured."), `FormatChanged`: "Measuring stopped where the format of the decoded blocks changed; the figures cover what came before.", `Failed`: "The measuring failed, so the audio has no figures.", and a decoded stream with no record: "The audio was decoded, but not measured."; each stands under the heading with the panels the record has.
  - A `Complete` record has no caption.
- **The invariant test** (`DecoderSessionInvariantTests`): it analyzes every fixture of `flac`, `wavpack`, `monkeys-audio`, `truehd`, `wav`, `aiff`, `matroska`, `mp4`, `ogg`, `measurement` and `tta` without decoding and asks that each stream whose codec name belongs to a registered decoder (the names the parsers report: "FLAC", "WavPack lossless", "WavPack hybrid", "WavPack lossy", "Monkey's Audio", "Dolby TrueHD", "MLP", "PCM", "PCM (floating-point)", "A-law", "Mu-law") has `Verification.Decoding == NotRequested`, unless the file's report has a `*.codec-not-analyzed` finding for it or the file is on a short list with its reason. The findings are counted as a budget of streams: a container states one for each stream it does not hand over, at the track's sample entry, which no node of the stream covers, so a file with more streams without a session than findings fails, and one finding does not excuse the other streams of its file. The list holds the thirteen PCM fixtures the `pcm` session is not started for because `PcmCoding` cannot describe them (a block alignment that does not fit, a float of 16 bits, a 14-byte format chunk, a list of wave data, a sample size of 0, a missing or empty data chunk, A-law with 16 bits or in a 14-byte format chunk, which states no width: `built-alaw-16-bits.wav` and `built-format-14-bytes-alaw.wav`, both malformed), which the design's "Data that is not PCM stays out" fixes; the test also holds that each of them still has no session, and that every registered decoder was seen on a stream of its own. A theory holds the four files the design names (`external-ffmpeg-flac.mka`, `external-ffmpeg-truehd.mka`, `external-ffmpeg-wavpack.mka`, `mp4/ffmpeg-cenc-flac.mp4`): no verification and a `codec-not-analyzed` finding.
- **The spectrogram's colors.** The grid's byte goes through a table of 256 entries (`GraphView.RampColors`, one per byte): 0 is no data (black on the bitmap, as a level of -160 dB or less), 1 to 220 are levels of -199 to +20 dB, and a level maps to the ramp's entry `round((dB + 160) / 160 × 255)` clamped to 0 to 255, so the ramp covers -160 dB (the entry 0, nearly black) to 0 dB (the entry 255, pale yellow). That range is a reading of the design, which leaves it open: music sits between -40 and -120 dB and the quantization noise of 16 bits at -130 dB per bin (4,096 points), so it shows. The ramp is the magma of matplotlib: 256 entries of `colormaps.py` of BIDS/colormap (the file's license comment, "released under the CC0 license / public domain dedication … We would appreciate credit", was read from the file on 2026-10-28), each value times 255 rounded to the nearest, halves up. `THIRD-PARTY-NOTICES.md` carries the credit and the license comment; `make_notices.py` generates it from `scripts/release/licenses/matplotlib-colormaps.txt` (`--fetch` pins the file by SHA-256), and `--check` passes.
- **Colors beyond the ramp: none,** so the validator was not run again. The cutoff line and the marks on the spectrogram are white strokes on a dark halo (neutral), the waveform and the single spectrum line take the accent color as the line graphs do, the per-channel spectrum takes the six validated hues of the category charts (gray beyond six), and the cutoff line of the spectrum is the text color.
- **The cutoff** is a dashed line on the spectrogram (horizontal, labeled with its frequency) and on the spectrum (vertical, labeled), at `SpectralCutoff.Frequency` of the combined spectrum; its figures are in the hover when the pointer is within two rows of it (`MeasurementText.Cutoff`, the text the command line prints, after "Spectral cutoff: ").
- **Finding a frame by time (`SampleLocator`).**
  - **Why not exact.** No node, series or record holds the sample position of a frame; the tree has byte offsets. The design asks for new GUI code, so the lookup uses what a stream reports: the byte offsets of its frames (the first per-frame series' x values), its sample count (`StreamInfo.Samples`) and the measured positions.
  - **Where the frames start.** The frames cover `Total` = `StreamInfo.Samples` samples from `Base`, the end of what was measured (first position, samples and the samples the gaps skipped) less that count: 4,096 for the cut `flac/built-first-frame-removed-md5-zero.flac`, 0 for `monkeys-audio/built-seek-first-entry-off.ape`, whose first frame failed to decode, and 2^24 for a fragment cut there. Where the measuring stopped early the end is not the stream's, so `Base` is the first measured position; a stream that states no sample count takes the measured samples and the gaps. A first version spread the end over the frames from position 0, which put every click of a late stream on the last frame.
  - **The reading** is that frames last equally long, but the last, as the per-second graphs already assume: position p of N frames and T samples is in frame floor((p - Base) × N / T). With the true length F, (N − 1)F < T ≤ NF, so (p - Base) × N / T exceeds (p - Base) / F by less than 1: the answer is exact where the last frame is full and otherwise the true frame or the next one, never an earlier one. A stream of frames of different lengths (FLAC's variable block size) can be off by more.
  - **Gaps** lie inside the span, since the measured positions count the samples a gap skipped, so a gap moves no frame and the bound holds across it, as it does for the rest of the stream. It holds only where the stream's sample count includes the samples of the frames the decoder lost.
  - **A frame's mark.** The estimate of a frame's first sample is never later than the true one and less than one frame earlier, so the mark of a frame selected in the tree can be a column early. A frame selected by a click on the panel keeps the clicked column as its mark, so the picture shows where the pointer was. The tests hold both bounds over every position of streams of 25 frames with each length of the last, a late origin (4,096 and 2^24), a gap, the stop, edges, clamping, the inverse (`PositionOfFrame` of a frame's offset leads back to the frame), 128-bit arithmetic for 2^42 samples, and the case of no frames.
  - **A column's time** is the middle of its samples: the grid's origin is the first position rounded down to a whole hop (the measurer's own rule), a column covers `HopsPerColumn × HopSamples` samples. The mark of a selected frame is the column that holds the frame's first sample.
  - **PCM** (WAV, Wave64, AIFF) has no frames: the panels draw and hover, a click selects nothing. A byte position would be exact there (data start and block alignment), but the stream does not state where its data starts, and the tree would need the data chunk's node.
  - **Not done:** an exact lookup would need the frame's first sample in the report (the session's `CodedFrame.FirstSample` has it); it is a change of the model, for a later step if the one-frame bound is found to matter.
- **Rounding and text.** Times in the hover are in tenths of a second, truncated ("0:00.5"); frequencies are the middle of a row to the nearest hertz (43.07 Hz rows at 44.1 kHz); levels of the spectrogram are whole dB, of the spectrum tenths. The y axis of the spectrum is a multiple of 20 dB above the loudest row and below the quietest (at least 60 dB between), the x axis of the spectrogram and the waveform nice steps from 0.1 s to hours.
- **Keyboard and automation:** as the design says and the existing graphs do (see `docs/gui.md`, "Audio panels"); the spectrogram's cursor starts on the cutoff's row. The graph's help text lists the axes, the cutoff and what the cursor is on, which a test reads.
- **Not changed:** the JSON, the text and CSV writers, the host, the core, the parsers, `CLAUDE.md`. `docs/PLAN.md`'s GUI list names the panels and the captions; the README mentions the panels.

### Measured

- **The drawing,** in the headless test with software drawing on this machine, over grids of 1,024 columns and 512 rows (8 channels for the waveform and the per-channel spectrum): 58 ms for a frame of the spectrogram and 49 ms for a pointer move (which draws one), 33 and 32 ms for the waveform, 17 and 11 ms for the spectrum. The tests hold a frame to the 500 ms of the other graphs and a move, which draws one frame, to 250 ms, the design's speed target for a pointer move.
- **Tests:** `Encspect.Gui.Tests` pass in full; the new tests are `AudioPanelTests` (the captions, the panels, the hover, the click, the keys, the automation text, the ramp, the drawing and its speed), `SampleLocatorTests`, the renamed `DecodeAudioTests`, a case in `CapitalizationTests` for the Audio labels, and `ScreenshotTests.RendersTheAudioPanels` (before decoding, the spectrogram with its cutoff hovered and a frame selected, the spectrum, the waveform, the figures, the waveform and the spectrum of six channels, and a sweep, in light and dark mode); `Encspect.Tests.DecoderSessionInvariantTests` holds the invariant.
- **Not done in step 4:** loudness and true peak (step 5, with the "Loudness" panel), `sox`, the TTA decoder, a locator exact for streams of frames of different lengths, and a run of the dataviz validator (no color was added).

## As built: step 5

Step 5 (loudness and true peak) follows the design; where the text left something open, was ambiguous against the code or was wrong, the reading closest to the intent was taken and each is listed here. Nothing departs from the architecture; no finding code is new.

### Departures and readings

- **The maximum momentary loudness is taken every 20 ms.** The design says blocks every 100 ms, and Tech 3341's test 13 (a burst of 400 ms that starts at every multiple of 20 ms, maximum M = -23.0 +-0.1 LUFS for each of 20 files) cannot be met with a window that slides in steps of 100 ms: a burst 50 ms off the grid reads 0.58 dB low. So the meter sums squares in granules of a fifth of 100 ms (`(k * ((rate + 5) / 10) + 2) / 5` samples from the start of a run, so that every fifth boundary is a multiple of the step the Recommendation's blocks use), and the momentary loudness is read after each granule from the last 20. The gating blocks (400 ms, 75 % overlap), the short-term values of the loudness range, the series and the maximum short-term loudness stay on the 100 ms grid. The maximum momentary loudness of a stream can therefore read higher than a meter that reads it every 100 ms (FFmpeg's `ebur128`, libebur128 polled once a block) by up to 0.58 dB on a short burst; on the lossy-derived files and the synthetic ones the cross-check finds 0.05 LU at most against libebur128 polled at the same 20 ms.
- **Nothing but a ring of 150 granules is kept.** The design's histograms of 0.01 LU (two, 160 KB each, a count and an energy sum a bin) and the series of at most 1,024 points are as designed; the filtered audio and the blocks are not kept, only the weighted sums of squares of the last 150 granules (3 s). The histograms are made with the first block of 400 ms, and the work of making them (2^18 units) is charged with the stream's first samples, as the spectrum's big arrays are.
- **The record.** `LoudnessFigures` and `TruePeakFigures` (in `Measurements.cs`) hold tenths of a LUFS, of a LU and of a dBTP (`NoLevel` for minus infinity), the channel weights in hundredths, `LoudnessAbsence` for a figure without a value (`TooShort`: fewer than 400 ms or 3 s between gaps; `BelowGate`: blocks, none above -70 LUFS), the numbers of gating blocks (all, above the absolute gate, above both) and, besides the design's figures, the 10th and 95th percentile of the loudness range in LUFS. `NotMeasured` carries the reason that the design puts in a `NotPossible` status: it belongs to the loudness or the true peak and not to the record, whose other figures are measured: below 8,000 Hz (`loudness needs a sample rate of 8,000 Hz or more`), at 0 Hz (`the sample rate is not stated`, for the true peak too), and for three or more channels without stated positions (`the channel positions are not stated`; one channel and two channels without positions are measured as mono and as left and right, a stream of LFE alone has `the stream holds no channel that loudness includes`).
- **The short-term series** has the end of the 3 s window of the first value a point covers as its x (the first point is at 3.0 s), seconds from the first measured sample; a silent stretch reads -70 LUFS (a series cannot hold minus infinity); a point after a gap starts a new point (two values across a gap are never averaged while the series is still unmerged). It is kept as a `Series` of `LoudnessFigures`, with its description, and the JSON writes it as `x` and `values` arrays.
- **Rounding.** All loudness figures are tenths, rounded to the nearest, halves up, from the deterministic logarithm: `floor(100 log10 z - 6.91 + 0.5)`. The loudness range is the difference of the two percentile bins in hundredths, rounded to a tenth, so it is exact to the 0.01 LU of a bin.
- **True peak.** The sample peak is always part of it (libebur128 does the same): the Annex's 48-tap filter has its output instants at 1/8, 3/8, 5/8 and 7/8 of a sample from the input's, so the original samples are not among them. The 2 times oversampling takes phases 0 and 2, positions 1/8 and 5/8; the design's open question is answered by the Tech 3341 signals 15 to 19 at 96 kHz (see Measured). A window of 128 outputs is skipped when the largest of the 11 + 128 samples it reads times 2.03 (the largest sum of absolute taps rounded up, 2.0228) and a margin of 2^-46 cannot exceed the peak found so far; the result is identical, which a test holds against the scalar loop that skips nothing. At 192 kHz and above the figure is the sample peak and no filter runs.
- **Weights.** The channel weights are `LoudnessWeights` as the design reads Table 4, with one addition the design left open: two channels that state a `Discrete` speaker are left and right (1.0), as two channels with no layout are; three or more with a `Discrete` among them are not measured.
- **Work.** Loudness 8 units a sample of a measured channel (an LFE is charged and not filtered); true peak 38 units at 4 times, 30 at 2 times (it was 22: see Measured) and 4 at no oversampling (the design's 1 for the sample peak did not cover the conversion to doubles and the scan: 3.3 to 3.8 times the reference's time per unit; the weight of 4 gives 0.8). The first block of a stream also charges 2^18 units for the histograms.
- **Text.** The lines follow the order of the Broadcast Wave `bext` chunk's loudness fields (integrated loudness, loudness range, maximum true peak level, maximum momentary and maximum short-term loudness); the true peak has a sign on a positive value and the figure of each channel in parentheses; `Loudness: not measured (...)` stands for the first two and the maxima. The design's example showed three lines.
- **CSV and JSON.** The three columns are as designed (`Maximum True Peak Level (dBTP)`, `Integrated Loudness (LUFS)`, `Loudness Range (LU)`, in tenths with one decimal, empty where the stream has no figure) and are numeric fields, so they are never guarded. JSON `loudness` and `truePeak` are described in [cli.md](../cli.md).
- **The GUI's Loudness panel** (`LoudnessGraph`, `GraphView.Loudness.cs`) is one line of the short-term series over time with the integrated loudness as a dashed line and a header of the figures; it is offered for a stream of 3 s or more (the others have the figures in "Figures", which lists the five new lines too). Its points are placed by their times (the middle of the stretch each covers), so a stretch lost to a gap shows as a longer step of the line; a gap is not marked otherwise. See [gui.md](../gui.md).
- **The EBU set:** `tech.ebu.ch` serves the test-set zip to a browser and answers scripted requests with 403 (the Tech 3341 PDF downloads), so the project lead downloaded version 5 (30 March 2016, 70 WAV files) once, and its only address in CI is the private repository's copy, pinned by SHA-256 in `check_ebu_loudness_set.py`. Its readme allows the material to be used "only for technical testing purposes", so it is never committed. The script runs the analyzer on the real files in the measurement cross-check job: 71 figures of every case in the set, all within the documents' tolerances on the first run, the authentic programmes included (Tech 3341 case 7 and Tech 3342 case 5 read -23.0 LUFS and 5.0 LU, case 8 and Tech 3342 case 6 -23.0 LUFS and 15.0 LU). The Tech 3341 and 3342 signals of the documents' Tables 1 are also generated in the tests (cases 1 to 6, 9, 10, 12, 13, 15 to 23, and the per-reading cases 11 and 14 as one stream for each reading), so the tests hold them without the token. Tech 3341 case 6's 5-channel file has no channel mask in its WAV header, so by decision 18 it is `not measured`; the 6-channel WAVE_FORMAT_EXTENSIBLE file is the case that is checked.
- **Cross-check tolerances** differ from the design's in two places, both measured: FFmpeg's true peak (one decimal; the difference is compared in whole tenths, both limits included, and printed for every fixture with FFmpeg's version) may lie 0.3 dB under the analyzer's (the design allowed 0.1: the Annex 2 filter's ripple lifts the analyzer's figure for a sine at 4.3 kHz of -6.02 dBFS to -5.8, where FFmpeg's resampler reads -6.0), and libebur128's true peak is held to 0.7 dB (its 49-tap windowed sinc differs from the Annex filter by 0.58 dB on wideband noise); the loudness range is compared only from 40 short-term values on (four files reach it: the two added in review, `loudness-range-steps-8000` and `loudness-range-gates-8000`, are 30 s and 45 s long, the second so that its range does not depend on the sampling of the short-term loudness), since libebur128 takes them once a second and FFmpeg's start with a window that is not yet full.
- **Licenses.** `KWeighting.Derive` follows libebur128 v1.2.6's `ebur128_init_filter` (the parameters of the two filters and the bilinear transforms, with `DetMath`'s tangent and power in place of the C library's); the source file, `THIRD-PARTY-NOTICES.md` and `scripts/release/licenses/libebur128.txt` carry its MIT notice, and `make_notices.py --fetch` checks the `COPYING` at the pinned tag by SHA-256. The gating, the histograms' layout (0.01 LU bins from -70 to +30 LUFS, a count and an energy sum, against libebur128's 0.1 LU bins of counts), the granules and the true peak are written from the Recommendation and the EBU documents. FFmpeg's source was not read.
- **Articles.** New: `loudness`, `loudness-range`, `sample-peak-and-true-peak`; updated: `measurements`, `levels`, `bwf-broadcast-extension`, `replaygain`, `dialogue-normalization`, `itunes-metadata` and `usac`, each with a sentence that a measured loudness is another figure than the stated one.
- **Not done in step 5:** `sox` (not installed), a run of the CI image's FFmpeg 8.0.1 (the cross-check ran with 6.1.1), the NativeAOT comparison on all fixtures is in Measured.

### Review fixes

An independent review of step 5 found the following; each is fixed with a test, and the tests that guard a bug were run against the planted bug.

- **Tests that catch what they claim to.** The skipping of the true peak is held to the scalar loop on the interpolated peak alone (`InterpolatedPeak`, not the sample peak that hid a late click), on bursts of an fs/4 sine at 45 degrees after quiet noise and silence, at 4 and 2 times oversampling, 15 offsets, 3 silences, 3 burst lengths and 4 block sizes (540 cases a factor); planting "skip the next window too" fails it. The flush of the loudness filter is held by `TheRawGranuleSumsOfAnImpulseAndSilenceDoNotDependOnTheBlocksAndHashToAConstant`: the raw doubles of every granule's weighted sum and of the maximum momentary energy, for noise, an impulse and silence at 48 and 44.1 kHz, hash equal for blocks of 4,096, 777, 1,000, 5,000 and 16,384 samples and to a pinned constant; planting a flush at the start of each block fails it.
- **Wording.** The maximum momentary loudness is taken every 20 ms in the description, the record's comment and everywhere else (the article, cli.md and gui.md were right); a stream that a stop or a budget ended early says "the part of the stream that was measured is shorter than 400 ms" (or 3 s), not that the stream is shorter; the range's text for values under the gate says "every short-term value".
- **The absolute gate** is `energy > AbsoluteGate` at both places (BS.1770-5 and Tech 3342 both say "above"); the short-term values used `>=` before. `IsAboveAbsoluteGate` is tested at the gate and one ulp either side.
- **The top of the histograms.** Superseded by exact gating (Gate 3): the values are kept and compared on their own, so a loudness above +30 LUFS is gated and ranked like any other; the histograms (used past 131,072 values) run to +80 LUFS, above anything the samples can reach. A test measures a sine at +40 dBFS and the Gate 3 input `top-bin.wav`.
- **The loudness range is compared with the other tools on four files** (`synthetic-long-bursts-48-seconds`, `loudness-steps-8000`, and the new `loudness-range-steps-8000` of 30 s with a range of 15 LU and `loudness-range-gates-8000` of 45 s with both gates at work); the gates fixture has a 20 s plateau at its low end because libebur128 takes the short-term loudness once a second, and a range across short steps depends on that sampling (a first version of it read 12.0 against the model's 14.7 in libebur128). Differences: 0.50 LU (libebur128), 0.10 LU (FFmpeg 6.1.1).
- **The FFmpeg true-peak limits** are compared in whole tenths (-3 to +7), both included, and the script prints for each fixture FFmpeg's version, both figures, the difference and the margin. The worst case, +0.7 dB on `synthetic-white-44100`, sits on the limit; FFmpeg 8.0.1 has not been run, the tolerance is unchanged, and the CI log shows the margin.
- **The EBU set** is pinned (see above); without a pin the job would print a `::warning::` and build nothing. The script's file name patterns are checked against version 5 of the set.
- **The memory test of chained streams** bounds two figures: the report keeps 61.8 MB more with measuring (14.7 MB without), under 64 MiB of records and 4 MiB of other state, and the analysis holds 0 bytes above the heap it starts and ends with, under 8 MiB of working state. It first bounded the peak less the peak without measuring, which could not hold: each peak is read from the heap before its analysis, which the leftovers of earlier work make differ between runs, so the plain peak read 15.3 MB on the build machine and 1.1 MB on ubuntu-26.04 (below the 14.7 MB its own report keeps), and the same 76.8 MB peak passed here and failed in CI (the earlier 76.7 MB reading was the same). The histograms stay allocated with the first block: it saves 320 KB for every stream shorter than 400 ms (many short chained streams), at the price of one condition.
- **The WAVE memory test (`DecodingAndMeasuringALongWaveFileHoldsNothingPerSlice`) failed on three CI runners** at 8.95 to 26.25 bytes per further slice (macOS, ubuntu-arm and ubuntu x64), with `isMeasuring: False` never failing. Nothing in the measuring path is held in proportion to the length.
  - **Evidence:** the state held at the last slice (`Live` read inside the sink, less the heap with the report alive) was 3,655,632 bytes at 160 slices and 3,655,280 at 640, 3,655,216 at 1,280 and 3,655,184 at 2,560 (-0.19 per further slice), the same in 16 consecutive readings under a load of three spinning processes and with `TieredCompilation` off or call counting never starting. Consecutive collections in one reading differ by about 100 bytes.
  - **Phase:** at other ratios the bounded structures are in other phases: the held state is 3.48 to 3.83 MB between 40 and 960 slices and what is kept (what the report takes over from the measurers) moves the other way, held plus kept being 4.01 MB at every length. At 160 against 640 (a factor of 4) the phases are the same, so a phase is not the cause of the CI figures.
  - **Cause:** the reading, not the code. A bound of 8 bytes over 480 further slices is 3.8 KB, below the 8 KB of an allocation context and far below the 64 KB to which `Live` makes two collections agree; the CI figures are 4 to 13 KB. The mechanism of that residue on the runners was not reproduced locally (4 processors; load; tiering).
  - **Change:** the test reads at 160 and 2,560 slices (a factor of 16, the same phase), where 8 bytes a slice is 19 KB, and also bounds the held state alone (5 MB measuring, 0.5 MB without; 3.66 MB and 0.2 MB measured). A list that gains 8 bytes in `StreamMeasurer.Write` reads 13.07 per further slice (3,656,656 and 3,688,016 bytes) and fails it; the plant is reverted.
- **Articles.** `loudness-range` no longer says that DR has no open specification, nor that the range "can read misleadingly high"; `replaygain` no longer says that a ReplayGain peak is a sample peak.
- **The Loudness panel** places its points by time (the middle of the stretch each covers), so a gap is a longer step of the line, and finds the point under the pointer by time; a test covers a gap, and the panel is in the screenshot tests (light and dark).

### Measured

Same machine as the earlier steps (Xeon at 2.80 GHz, 4 vCPUs, .NET 10.0.401, JIT), under the machine lock (`flock -x`), `MeasurementWorkTests` interleaved with the FLAC reference, median of five rounds.

- **Loudness alone:** 3.6 to 4.7 ns a sample of a channel at every rate from 8 to 192 kHz and for noise, a square wave, zeros and an impulse (the design's prototype: 4.6 ns); the ratio to the reference's time per unit at the weight of 8 is 1.2 to 1.6.
- **True peak alone,** noise and a full-scale square wave, where nothing can be skipped: 10.3 to 12.4 ns a sample of a channel at 4 times (22.4 ns/12.5 ns in the design's prototype for x86-64-v2/v3 with 256-bit lanes; 128-bit lanes here, by the rules), 8.3 to 8.7 ns at 2 times, 1.3 to 1.5 ns at no oversampling; ratios 0.7 to 0.95 at 38 units, 0.8 at 4. At 2 times the filter was one chain of 12 dependent additions and cost as much per sample as the 4 times loop; the weight of 22 gave ratios of 1.0 to 1.4 (2.11 in the nightly run on a macOS Intel machine). The 2 times loop now computes two outputs at a time (same sums, same figures) and takes 57 to 70 % of the 4 times loop's time per sample (two measurements, 8.3 to 9.5 ns against 11.3 to 12.3 ns); the weight is 30, which gives ratios of 0.72 to 0.81.
- **Everything together** at 44.1 kHz in stereo, noise: 27.7 ns a sample of a channel (the spectrum 11 to 13, the levels 1.5 to 2, loudness 4.5, true peak 10), ratio 0.93 at the sum of the weights (23 + 4 + 8 + 38 units); the design's estimate was about 44 ns.
- **Determinism:** the pinned hashes (the K-weighting coefficients of 12 rates, the loudness of three streams of 12 s at 48, 44.1 and 11.025 kHz with an impulse and silence, the raw true peak after each piece of 777 samples at the three oversamplings) agree under the JIT defaults, `DOTNET_EnableHWIntrinsic=0`, `DOTNET_EnableAVX2=0` and `DOTNET_TieredCompilation=0` (204 measurement tests, goldens and model comparisons included), and `scripts/check_aot.sh` (NativeAOT for linux-x64 against the JIT, `--json --measure`) reports the same on 8,399 inputs; the vector loop, the scalar loop and the skipping give the same bits. The 68 manifests of the cross-check were compared to libebur128 and FFmpeg: largest differences 0.05 LU (integrated, maxima), 0.50 LU (loudness range against libebur128, 0.10 against FFmpeg, on the 4 files that have 40 short-term values), 0.0 dB (sample peak), 0.58 dB (true peak against libebur128) and -0.2/+0.7 dB (against FFmpeg 6.1.1).
- **The EBU cases** (generated, 48 kHz): every one of Tech 3341 cases 1 to 6, 9 to 14 and 15 to 23 and Tech 3342 cases 1 to 4 is within its tolerance; the true peaks read -6.0, -6.0, -6.3, -6.0 and +3.0 dBTP for signals 15 to 19 (at 44.1, 48 and 96 kHz alike) against -6.0, -6.0, -6.0, -6.0 and +3.0 +0.2/-0.4, and -0.2, -0.1, -0.3 and -0.1 for signals 20 to 23 (the synthesis of the burst, a Kaiser-windowed sinc lowpass at 0.45 of the rate, is this test's own; signal 22 has 0.1 dB of margin).
