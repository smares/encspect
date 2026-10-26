# Measurements of decoded audio: spectrum, cutoff, levels and loudness

Status: draft for the project lead, 2026-10-26. Nothing is built. A throwaway prototype outside the repository measured the costs and the numerical behavior (see [Performance and budget](#performance-and-budget) and [Determinism](#determinism)); nothing of it is committed. The decisions are under [Open decisions](#open-decisions), and the claims no source read for this design confirms are marked **unverified**; figures worked out rather than measured are marked **estimate**.

- It designs the "host-side PCM analyses" that [decoders.md](decoders.md) leaves for later ("The PCM path", "Host-side PCM analyses, later"): waveform, spectrogram and lowpass detection, and the level and loudness figures that the same pass can give.
- It keeps the decoder contract as approved and built: no parser, decoder or session changes for the measurements themselves.
- It fits the approved [parallel decoding](parallel-decoding.md) design (deferred), which calls the sink on the analysis thread in stream order.
- One name for the concept from here on: **measurements**. A measurement is a figure the host computes from decoded PCM; decoders.md's "host-side PCM analyses" are these, and its section is renamed with the first pull request (decision 2).

## Scope

- **In:**
  - the measurement framework on the PCM path: options, the host's fan-out from the sink, the result on `StreamInfo`, the writers, the budget, the exception boundary;
  - the measurements: the average spectrum and the spectral cutoff, the spectrogram and waveform grids, sample statistics (sample peak, RMS, DC offset, clipping runs, digital silence, bit-depth usage), loudness and loudness range (ITU-R BS.1770, EBU R 128), true peak;
  - a PCM route for WAV, Wave64 and AIFF, which have no decoder;
  - the CLI and GUI behavior, fixtures, cross-checks and the rollout.
- **Out, and why:**
  - decoders for lossy codings (MP3, AAC, Vorbis, Opus and the others): the plan keeps them under "Later". This design only makes sure they plug in without a contract change (see [Lossy formats later](#lossy-formats-later));
  - the TTA, ALAC and WMA Lossless decoders, which decision 5 of decoders.md moved to "Later";
  - DSD (WavPack DSD now, DSF and DSDIFF later): measuring it needs a DSD-to-PCM conversion, which is a decoder's job, not a measurement's;
  - playback, and any quality verdict (the plan's non-goals).

## The design in short

- The host's sink, which every decoder writes its PCM to (`DecodeContext.DeclareOutput`), forwards each block to a measurer of the stream as well as to the front end's sink. The measurer is host code; parsers and decoders do not change.
- Measuring is asked for in `DecodeOptions.Measure`, so one decode pass serves verification and measurement: measuring implies decoding, and decoding always compares the checksums over decoded audio.
- Off by default everywhere: the library measures only when asked; the CLI with `--measure`; the GUI in the detail window when the user decodes; the file list never.
- The result is a typed record on the stream, `StreamInfo.Measurements`, attached by the host after the format parser returns, as decoders.md fixes it. It adds figures, never nodes, so the tree is the same with or without measuring.
- Reports stay byte-identical on every platform: double-precision arithmetic with no fused multiply-add, own trigonometric tables and logarithm instead of the platform's math library, fixed summation order, element-wise vectors only. The prototype measured bit-identical spectra under JIT and NativeAOT, x86-64-v2 and v3, and with every vector extension turned off.
- Measurements report figures with units and their basis, never a verdict: "spectral cutoff 16,630 Hz, a step of 54.9 dB, in 644 of 644 frames with signal", not "lossy source".
- A work budget of their own, counted like the decoders' and as large, bounds their time; their memory is bounded per stream whatever the stream's length.
- First step: the framework with the average spectrum, the spectral cutoff and the grids (see [Rollout](#rollout)).

## Goal and user value

### What a user learns

- **Spectrogram:** level over time and frequency. It shows a lowpass as a flat ceiling, band-limited sections, inserted segments of another source, noise, hum and tones, clipped passages as vertical streaks, and lossy coders' marks (holes, bands that switch on and off, replicated high bands).
- **Average spectrum and spectral cutoff:** where the energy of the whole stream ends, with how steep an edge and how consistently over time. This is the prime use case: a lossless file made from a lossy encode keeps the encoder's lowpass, which the prototype shows as a step of 43 to 62 dB within a few hundred hertz, in every frame with signal (see [Lossless files from lossy encodes](#lossless-files-from-lossy-encodes)).
- **Waveform:** the envelope of each channel over time; clipping, silence, fades, gaps and level changes.
- **Sample statistics:** sample peak, RMS, DC offset, runs of clipped samples, digital silence at the start, the end and in total, and bit-depth usage (low bits that are always zero, as in 16-bit audio padded to 24 bits; float samples that are all 16- or 24-bit values).
- **Loudness, loudness range and true peak:** the figures of ITU-R BS.1770 and EBU R 128 that mastering and delivery are judged by.

### Evidence, not verdicts

- The plan's non-goal "quality verdicts" and CLAUDE.md's rules on observations apply to every figure, message and article here.
- The analyzer reports what it measured and how: a cutoff's frequency, the size of the step, the width of the transition, and the share of frames that show it. It never says "transcoded", "fake", "upsampled" or "lossy source".
- The measured signature is not unique to lossy coding. The prototype's 16 kHz brickwall filter applied to the lossless source gives the same figures as LAME at 128 kbps (60.6 dB step, 645 of 645 frames; see the table below). Mastering filters, sample rate conversion, old analog sources and deliberate band limits do the same. The `spectral-cutoff` article says so.
- The absence of a cutoff is no evidence either: LAME 3.100 at `-V 0` and oggenc at `-q 8` leave none in the prototype's input (largest step 2.3 to 2.4 dB).
- Articles may state encoder behavior only as observations backed by committed fixtures, with encoder versions and settings, scoped to "Encspect's test files", and saying that the analyzer does not name an encoder from them (see [Lossless files from lossy encodes](#lossless-files-from-lossy-encodes)).

### The analyses weighed

| Analysis | What it gives | Cost per sample of a channel (prototype, NativeAOT x86-64-v2) | How it is checked | Step |
| --- | --- | --- | --- | --- |
| Average spectrum, spectral cutoff | the prime use case | FFT 12.2 ns (4,096 points, hop 2,048, 128-bit vectors) | lossy-derived fixtures against the encoders' stated lowpass; synthetic band-limited noise | 1 |
| Spectrogram and waveform grids | the GUI panels | grid sums on top of the FFT: under 1 ns (**estimate**) | synthetic sweeps and tones at known times | 1 |
| Sample peak, RMS, DC offset | basic levels | 2.0 ns for all sample statistics together | exact, from integer samples | 2 |
| Clipping runs | runs at the extreme codes | in the 2.0 ns | exact | 2 |
| Digital silence | leading, trailing and total zero samples | in the 2.0 ns | exact | 2 |
| Bit-depth usage | padded or reduced depth | in the 2.0 ns | exact | 2 |
| Loudness, loudness range | BS.1770 integrated loudness, EBU loudness range, maximum momentary and short-term | 4.6 ns (K-weighting and mean square) | EBU test set, libebur128's expectations, FFmpeg's ebur128 | 4 |
| True peak | BS.1770 Annex 2 | 23.1 ns (12.5 ns in the x86-64-v3 build) | synthetic overs, FFmpeg's ebur128 | 4 |
| Dynamic range | EBU loudness range covers it | none beyond loudness | as loudness | 4 |
| Upsampling | the edge of the content below Nyquist | none beyond the spectrum | needs a bandwidth figure the step finder does not give (see [Spectral cutoff](#spectral-cutoff)) | later |
| Mid/side spectra, holes, replicated bands | lossy coders' finer marks | mid/side from the complex bins, no further FFT (**estimate**: about 1 ns) | needs fixtures that show each mark | later |

- **Why the spectrum first:** it is the prime use case, it needs the FFT, the grids and the determinism rules that everything else reuses, and it is where the risks are (cost, determinism, wording).
- **Why sample statistics second and small:** they are exact on integer samples, so their fixtures are exact too, and they cost almost nothing; they could also join step 1 (decision 1).
- **Why loudness and true peak later:** they need the BS.1770 filters at every sample rate, the gating, and their own cross-check set; true peak is the most expensive measurement.
- **Dynamic range:** EBU Tech 3342's loudness range is the standardized figure. The "DR" value of the Pleasurize Music Foundation's meter has no open specification this design found (**unverified**), so it is not offered.

## Which formats

### Decoded lossless formats

- **FLAC** (native and in Ogg), **WavPack** (PCM and floats, lossless and hybrid), **Monkey's Audio** (integers and floats), **TrueHD and MLP**: every stream whose decoder calls `DeclareOutput` is measured.
- **WavPack hybrid without its correction file:** the decoded audio is the lossy stream, as for verification (`DecodeSetup.IsLossy`). The measurements are of that lossy audio; the text and the GUI say "(lossy stream)" after the measured duration, as the verification line does.
- **TrueHD:** the decoder outputs the highest presentation it decodes (substreams 0 to 2); a Dolby Atmos substream is not decoded. Positions are untrimmed (decoders.md, "The PCM path"), so the samples to drop at the start are measured too, which the article on measurements says.

### WAV, Wave64 and AIFF: a PCM decoder

The samples are the data, so these formats need no reconstruction, only a route from the parser's data chunk to the sink.

- **(a) A "pcm" decoder, recommended.** `Encspect.Formats.Wav.Decoding` gets a decoder with the codec ID `pcm`. The WAV, Wave64 and AIFF parsers start a session with it (`StartDecoding("pcm", setup)`, `DecodeSetup.Format` the stated format, `Configuration` naming byte order, width and coding) and, only while the session `IsDecoding`, read the data chunk in slices of whole sample frames (64 KiB, **estimate** to tune) and hand each as a `CodedFrame`. The decoder unpacks little- and big-endian integers of 8 to 32 bits (8-bit WAV unsigned), 32- and 64-bit floats, and A-law and mu-law (the G.711 tables are format data). It declares no checksum.
  - The AIFF parser names the codec by ID only, so it does not reference the WAV project; parsers never reference each other, and the host routes by ID.
  - Later containers of PCM (MP4 `lpcm`, `sowt`, `twos`; Matroska `A_PCM/*`; CAF) use the same ID.
  - The unpacking code lives in the WAV project's `Decoding` namespace; if a second format project ever needs it as code, it moves to `Core.Abstractions` next to `CanonicalPcm`.
  - **What changes in reports:** with `--verify`, a PCM stream no longer reports `decode.no-decoder`; its stream gets no `Verification`, since it stores no checksum (a parser attaches a verification only where it declares a checksum, decoding or not, so no report depends on `IsDecoding`, decision 12 of parallel-decoding.md). Without decoding nothing changes: the parser still reads no samples, which `MemoryTests.TheSamplesOfAWaveFileCostNothing` keeps holding.
  - **Work:** `DecodeWork.ForSample` per sample of each channel, as every decoder charges; nothing per tap.
- **(b) The host reads the samples itself.** The host would need to know where a WAV file's samples are and how they are laid out, which is the parser's knowledge; it breaks the layering the plan keeps.
- **(c) No measurement for PCM files.** WAV and AIFF are where many lossless-from-lossy files live, so this loses much of the use case.
- **Decision 4.**

### TTA

- TTA has no decoder (decoders.md, decision 5). Measuring it needs one, built clean-room as decoders.md's decision 10 requires: libtta-c and FFmpeg's `tta.c` are LGPL. One agent reads them and writes a format description without code, as `truehd-format.md` was; another agent who has not read them implements from that description, with libtta-c and FFmpeg as black-box cross-checks of the decoded PCM's MD5.
- TTA stores only CRC-32s over coded data, so the decoder adds no verification; its correctness rests on the fixtures' decoded MD5 from libtta-c and FFmpeg.
- **Recommendation:** leave TTA out of this design's steps; its decoder is a change of its own under "Later", and its streams are measured once it lands, with no further work here (decision 12).

### Channels, rates, widths and DSD

- **Multichannel:** every channel is measured on its own (spectrum, statistics, peaks). The combined spectrum, the spectrogram and the stream's cutoff use the mean power of the channels other than the LFE (`Speaker.LowFrequency`), which is band-limited by design; a stream without a layout (`ChannelLayout` null) uses all channels and says so in its description.
- **Sample rates:** the FFT size follows the rate so that a frame lasts about 85 to 93 ms and a bin is about 10.8 to 11.7 Hz wide (decision 10): 2,048 points up to 24 kHz, 4,096 up to 48 kHz, 8,192 up to 96 kHz, 16,384 up to 192 kHz, 32,768 above. Cost grows with log N: the prototype's 8,192-point FFT costs 15.6 ns a sample against 12.2 at 4,096.
- **192 kHz and the grid:** the spectrogram's 512 rows cover 0 Hz to Nyquist, 187.5 Hz a row at 192 kHz. The GUI can show the range up to 24 kHz from the same rows; finer rows above need a later option.
- **Widths:** integers of 1 to 32 bits are scaled by 2^-(bits - 1) to full scale ±1; floats are taken as they are. Statistics on integer samples (peaks, DC, clipping, silence, bit usage) are computed on the integers, exactly.
- **Floats:** samples above full scale are counted ("Samples Over Full Scale"), not clipped. Non-finite samples (NaN, infinities) are counted, measured as 0, and raise `measure.non-finite-samples` (decision 6).
- **DSD:** WavPack's DSD decoder declares no PCM output. The stream's measurements say "not measured: the stream decodes to DSD" (status `NotPossible`), without a finding.

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
- **The decoder contract stays:** the sink never throws to the decoder. The measurer catches its own exceptions (see [Cancellation and the exception boundary](#cancellation-and-the-exception-boundary)); it does not throw on cancellation either: it stops working and leaves the throw to the decoder's next check of its token, which is linked to the analysis's.
- **No parser changes** beyond the PCM route of WAV, Wave64 and AIFF, which is a parser handing frames to a decoder as every decoded format does.
- **Attaching the result:** once the format parser returns, the host gives each stream whose first node is a session's `DecodeSetup.Stream` that session's `StreamMeasurements`, as decoders.md fixes it and as it sets `NodePaths`. A routed codec's stream (FLAC in Ogg) is matched the same way.
- **Order:** the measurer gets the block before the front end's sink. Both see the same span, valid only during the call; the measurer copies what it keeps (the window's overlap).

### One decode pass with verification

- Measuring implies decoding with the same `DecodeOptions` (stop, budget, sink). The checksums over decoded audio are compared as with `--verify`, at a cost that is small next to the measurements (FLAC's MD5 is about 28% of its decoding step, decoders.md "As built: the second speed pass").
- So `--measure` implies `--verify`, and a measured stream always has its verdict. A decoder option to skip the checksums would change the decoder contract for a small gain; it is not proposed (decision 3).
- `decode.limit` and `decode.stopped` end the measurements with the decoding: the measurements then cover the samples decoded, status `Stopped`, and the text says how much was measured.

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
    // Which measurements run; the default is every one the library has.
    public Measurements Include { get; init; } = Measurements.All;

    // The measurements' work budget in work units, counted apart from the decoders' (see "The work budget"); null for
    // the default. Tests and the fuzzer lower it. An option of the call, not a setting.
    public long? MaxWork { get; init; }
}

[Flags]
public enum Measurements
{
    None = 0,
    Spectrum = 1,           // average spectrum, spectral cutoff, spectrogram and waveform grids
    SampleStatistics = 2,   // peak, RMS, DC offset, clipping runs, digital silence, bit-depth usage
    Loudness = 4,           // BS.1770 integrated loudness, loudness range, maximum momentary and short-term loudness
    TruePeak = 8,
    All = Spectrum | SampleStatistics | Loudness | TruePeak,
}
```

- The flags grow with the rollout; `All` is what the library has at the time.
- The grids are always computed with the spectrum (they cost little beyond the FFT) and kept in the report; the JSON writer writes them only when asked (below).
- No setting, cache or console output below the front ends: all of this is an option of the call ("A library others could use").

### CLI

- `--measure`: decode, verify and measure every stream with a decoder; `--measure spectrum,levels` picks measurements by name (`spectrum`, `levels` for the sample statistics, `loudness`, `true-peak`).
- `--measure-grids`: also write the spectrogram and waveform grids into JSON (implies `--measure`). Text and CSV never hold them.
- `--hide measurements` hides the measurement lines of the text tree, as `--hide statistics` does for statistics.
- **Text:** lines under the stream's first node, after the "Verified:" line:

```
      Verified: yes (frame header CRC-8 3230 of 3230, frame CRC-16 3230 of 3230, STREAMINFO MD5 1 of 1)
      Measured: 300.0 s of 2 channels
        Spectral Cutoff: 16,630 Hz (a step of 54.9 dB, 16,559 to 16,914 Hz, in 644 of 644 frames with signal)
        Sample Peak: -0.3 dBFS (front left -0.3, front right -0.5)
        Integrated Loudness: -9.8 LUFS
        Loudness Range: 5.2 LU
        True Peak: +0.4 dBTP (front left +0.4, front right -0.1)
```

  - No cutoff: `Spectral Cutoff: none found (largest step 2.3 dB, at 1,992 Hz)`.
  - With `--explain`, each line's description and topic, as for nodes.
- **JSON:** `measurements` in the stream, after `verification` (see [JSON](#json)). A field is added and none changes meaning, so `schemaVersion` stays 1.
- **CSV:** five columns at the end: `Spectral Cutoff`, `Sample Peak`, `True Peak`, `Integrated Loudness`, `Loudness Range`, from the first audio stream (peaks the highest over its channels), empty without `--measure`, so a folder export never mixes measured and unmeasured rows. `Is Verified` is filled with `--measure` too.
- **Exit codes:** unchanged; `--fail-on` sees only the host's `measure.*` findings.

### GUI

- **Detail window:** decoding stays a command, as decoders.md decided for verification. The one command decodes, verifies and measures, so a file is decoded once; its label is decision 8. The analysis then shows:
  - under the graphs a heading "Audio", with "Waveform", "Spectrogram", "Spectrum" and, from step 4, "Loudness", drawn from the stream's measurements;
  - the cutoff as a line on the spectrogram and the spectrum, with its figures in the hover text;
  - hovering the spectrogram shows time, frequency and level; clicking it selects the frame that holds that time, as clicking a graph point does now. The GUI finds frames by byte offset today (`FrameLocator`), so finding one by sample position is new GUI code, designed here and not tried.
- **Before decoding:** the heading shows one line, "Decode the audio to see its waveform and spectrogram." with the command; for a stream without a decoder, "No decoder for <codec>: the waveform and spectrogram need the decoded audio." This is the plan's "appear only for formats that have a decoder and otherwise say so".
- **Drawing:** the spectrogram is the grid's bytes through a fixed 256-entry color ramp into an Avalonia `WriteableBitmap`, scaled to the panel; no charting library and no new dependency. The ramp is a perceptually even ramp defined in code (**unverified** which published ramp, and its license, if one is followed).
- **File list and summary cache:** never decode, so never measure; the list's columns stay as they are.
- **Folder parallelism:** the list analyzes `Parallelism` files at once without decoding; a decode in the detail window runs beside it with the degree parallel-decoding.md's decision 2 gives, and the measurer adds at most one thread (below).

## The result model

### Measurements on the stream

```csharp
// In Encspect.Core (Core.Abstractions), plain data like StreamVerification; computed by the host.
public sealed record StreamMeasurements
{
    public required MeasurementStatus Status { get; init; }      // Complete, Stopped, NotPossible, Failed

    // Samples per channel measured, and from which position of the stream (untrimmed, as PcmBlock.Position).
    public long Samples { get; init; }

    public int SampleRate { get; init; }

    // The channels measured, in the stream's order; the speakers where the decoder stated them.
    public IReadOnlyList<Speaker>? ChannelLayout { get; init; }

    public int Channels { get; init; }

    public SpectrumFigures? Spectrum { get; init; }

    public SpectrogramGrid? Spectrogram { get; init; }

    public WaveformGrid? Waveform { get; init; }

    public SampleFigures? SampleStatistics { get; init; }

    public LoudnessFigures? Loudness { get; init; }

    public TruePeakFigures? TruePeak { get; init; }
}

public sealed record SpectrumFigures
{
    public required int FftSize { get; init; }

    // The average level of each of the 512 rows (0 Hz to Nyquist) in tenths of a dB, combined (all channels but the LFE)
    // and per channel.
    public required ReadOnlyMemory<short> Combined { get; init; }

    public required IReadOnlyList<ReadOnlyMemory<short>> PerChannel { get; init; }

    public required SpectralCutoff Cutoff { get; init; }

    public IReadOnlyList<SpectralCutoff> ChannelCutoffs { get; init; } = [];
}

public sealed record SpectralCutoff
{
    public bool IsFound { get; init; }

    public int Frequency { get; init; }            // Hz, the edge: the highest bin over the level above by 6 dB
    public int TransitionStart { get; init; }      // Hz
    public int StepTenthsOfDb { get; init; }        // the step, in 0.1 dB
    public int FramesShowing { get; init; }
    public int FramesWithSignal { get; init; }
}
```

- Every figure is an integer in a fixed unit (whole hertz, tenths of a dB, samples), so the JSON and the goldens hold no floating-point text (see [Determinism](#determinism)). The writers turn them into `NodeValue` decimals with their units ("dBFS", "dBTP", "LUFS", "LU", "Hz", "dB", "%"), as node values are written.
- `SampleFigures`, `LoudnessFigures` and `TruePeakFigures` hold per-channel arrays and the stream's figure the same way; their fields follow the measurements listed under [Algorithms](#algorithms).
- **Why not `StreamInfo.Series`:** a series is numbers over a time or offset axis, owned by the parser. A spectrum is over frequency and a spectrogram is a grid. The loudness over time (short-term loudness each second) is a `Series` with the `Seconds` axis, kept in `LoudnessFigures`, so the parser's series stay the parser's; the `Series` class is reused, with its `Description`.
- **Why no nodes:** decoders.md's rule is "decoding never changes the parser's nodes", and the tree must be the same with or without decoding (`AnalysisOptions.Decoding`'s contract, `DecoderFixtureTests`). A host node "Measurements" would break both.

### The time-frequency grid

- **Bounded whatever the length:** the spectrogram has 512 rows (linear, 0 Hz to Nyquist; each row the mean power of its bins) and between 512 and 1,024 columns. A column starts as one FFT hop; whenever the columns would pass 1,024, adjacent pairs are merged and each column then covers twice the hops. So a stream of any length ends with 512 to 1,024 columns of 2^k hops each, the last one partial; a stream of fewer than 512 hops (about 24 s at 44.1 kHz) has one column per hop.
- The merge needs no stated length, so it works for streams whose length is unknown (a WAV file with a placeholder size) and gives the same grid wherever the length is known.
- **Waveform:** the same columns, with each channel's smallest and largest sample, scaled to 16-bit integers (floor of the value times 32,768, clamped).
- **Accumulation:** each cell sums power in single precision while it is open (2 MiB for 1,024 × 512 cells), in a fixed order; rounding to single precision is deterministic. Finished cells keep the sums for merging and are quantized only when the stream ends.

### dB scale and quantization

- **Reference:** 0 dB is the level of a full-scale sine centered on a bin (the window's coherent gain taken out, as Heinzel et al. normalize amplitude spectra). Noise reads lower per bin as the FFT size grows (10 log10 of the bin width), which the `spectrogram` article says.
- **Grid cells:** whole dB, offset by 200 and clamped to 1 to 220, so a cell is a byte (-199 to +20 dB); 0 means no data. The quietest 24-bit quantization noise per bin is about -177 dB at 4,096 points (**estimate**: -144 dBFS spread over 2,048 bins), inside the range.
- **Spectrum rows and figures:** tenths of a dB, as `short`.
- **Rounding:** floor of the value, from the deterministic logarithm (below). Quantization is for size and reading, not to hide differences between platforms; the arithmetic is made deterministic instead.

### JSON

```json
"measurements": {
  "status": "complete", "samples": 13230000, "sampleRate": 44100, "channels": 2,
  "spectrum": { "fftSize": 4096, "rows": 512, "hertzPerRow": 43.06640625,
    "combined": [-412, -398, …], "perChannel": [[-410, …], [-415, …]],
    "cutoff": { "isFound": true, "frequency": 16630, "unit": "Hz", "transitionStart": 16559, "step": 54.9, "stepUnit": "dB",
                "framesShowing": 644, "framesWithSignal": 644 } },
  "sampleStatistics": { "samplePeak": { "value": -0.3, "unit": "dBFS", "perChannel": [-0.3, -0.5] }, … },
  "spectrogram": { "columns": 1000, "rows": 512, "hopsPerColumn": 8, "secondsPerColumn": 0.371519, "levels": "<base64, column by column>" },
  "waveform": { "columns": 1000, "minimum": "<base64 of int16 little-endian, channel by channel>", "maximum": "…" }
}
```

- `spectrum.combined` and `perChannel` are in tenths of a dB as integers (documented as such, with `"levelUnit": "0.1 dB"`), so 512 rows are a few kilobytes per channel.
- `spectrogram` and `waveform` are written only with `--measure-grids` (decision 7). A grid of 1,024 × 512 bytes is 512 KiB, 683 KiB in base64; the waveform of 8 channels about 43 KiB.
- **Sizes without grids:** about 3 KB per channel for the spectrum and well under 1 KB for the other figures (**estimate**), so a measured stereo stream adds under 10 KB.

### Text and CSV

As under [CLI](#cli). The text never prints the spectrum rows or the grids, as it never prints per-frame series.

### Descriptions, topics and articles

- Measurements are not nodes, but every figure the writers print carries a description and a topic, as nodes do: constant strings in the host (`MeasurementText`, as `VerificationText` holds the verification's), one or two sentences on what the figure is, citing the specification section where there is one, without verdicts. `--explain` prints them; the GUI shows them as hover text. A test walks the measurements of every measured fixture and fails on a figure without a description or with a topic that does not exist, as `DescriptionTests` does for nodes.
- The loudness series carries its `Description`, as every series does.
- **Articles,** each 100 to 300 words, from checkable sources only (the specifications, the literature, the tool cross-checks, committed fixtures):
  - `measurements`: what is measured, from which samples (decoded, untrimmed, the presentation TrueHD decodes, the lossy stream of a WavPack hybrid file), the budget, determinism and quantization;
  - `spectrogram`: the short-time Fourier transform, window, frame and hop, the dB reference, why noise reads lower in finer bins, how the grid is bounded;
  - `spectral-cutoff`: the measurement, its figures, what a sharp edge is consistent with (an encoder's lowpass, a mastering filter, resampling, an anti-alias filter), what its absence does not show, and the observations the committed fixtures back;
  - `sample-peak-and-true-peak`: inter-sample peaks, BS.1770 Annex 2, dBTP;
  - `loudness`: BS.1770 K-weighting, channel weights, gating, LUFS and LKFS, EBU R 128's target;
  - `loudness-range`: EBU Tech 3342;
  - `dc-offset`, `clipping`, `digital-silence`, `bit-depth-usage` (linked with `wasted-bits`);
  - updated: `lowpass` (its sentence "the analyzer does not decode the audio, so it cannot measure the cutoff of other files" goes), `coded-bandwidth`, `verification` (one decode pass), `pcm-samples`, `wasted-bits`.
- **One concept, one name:** "spectral cutoff" is the measured edge; "lowpass" stays the encoder's setting (the LAME tag's Lowpass); "coded bandwidth" stays the figure the coded data states. The three articles link each other and never use one name for another.

### Findings

- **Recommendation:** measurements raise no finding about the audio. A cutoff, a clipped run, a padded bit depth or a loudness is a figure; a finding would read as a verdict and, through `--fail-on info`, act like one (decision 6).
- **The one exception proposed:** `measure.non-finite-samples`, a warning, for float samples that are NaN or infinite: the file holds something no reader can play as written, which is the plan's definition of a warning. It points to the stream's node.
- **The host's own:**
  - `measure.limit`, info: "Measuring stopped after 1,234.5 s of the stream: its work passed the budget of 34,359,738,368 work units that the file's size allows."
  - `measure.failed`, error: a measurement's bug, with the exception's type and message; that stream's measurements are dropped (status `Failed`) and the analysis carries on. The fuzzer fails on it, as on `decode.failed`.
  - `measure.streams-limited`, info: more streams than the measurement memory bound allows (below); the rest are not measured.
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

- **Window:** periodic Hann. Its highest sidelobe is -31.5 dB and its sidelobes fall 18 dB an octave (Harris 1978, Table 1), so 1 kHz away from a strong component (about 90 bins at 4,096 points and 44.1 kHz) its leakage is far below 16-bit quantization noise (**estimate** from those figures); the prototype's steps of 55 to 62 dB down to the quantization floor of about -129 dB confirm it in practice. Blackman-Harris would buy a cleaner stopband near the edge at twice the main lobe, which widens the measured transition; not needed.
- **Size:** by sample rate, about 85 to 93 ms and 10.8 to 11.7 Hz a bin (see [Channels, rates, widths and DSD](#channels-rates-widths-and-dsd); decision 10).
- **Hop:** half the frame, so every sample is weighted fully across two frames (Hann at 50 % overlap sums to a constant) and averaging follows Welch (1967). A hop of a whole frame halves the cost (6.1 against 12.2 ns a sample) but weights the samples at frame edges near zero, so short events between frames are missed; decision 10 keeps half.

### Channels

- Each channel is transformed on its own; power is averaged across channels for the combined spectrum and the grid, never computed from a downmix, which would cancel content in antiphase.
- The LFE is left out of the combined figures (above).
- **Mid/side, later:** the mid and side spectra are (X_L ± X_R) / 2 from the complex bins of the two channels already transformed, with no further FFT. They show where a joint stereo coder cut the side channel lower than the mid. They wait for fixtures that show it.

### Spectral cutoff

The method, as prototyped:

1. **Average spectrum:** the mean power of each bin over all frames, per channel and combined, in dB.
2. **Step search:** for each candidate bin k from 1 kHz to Nyquist less 1 % (the prototype searched from 2 kHz to Nyquist less 200 Hz), the mean level of a band of about 1 kHz under k against a band of the same width above k + 2 bins, clipped at the top of the search. The largest difference is the step.
3. **Edge:** from the step's bin upward, the highest bin whose level is more than 6 dB above the mean level of everything above the step; that is the reported cutoff frequency. **Transition start:** the lowest bin, from the step downward, still more than 3 dB below the band under it.
4. **Consistency over time:** the share of frames with signal that show the edge. The prototype kept every frame's levels, which grows with the stream. The design replaces it with a bounded histogram: each frame's own bandwidth (the highest of the 512 rows whose level is within a fixed distance of the frame's loudest row, **not prototyped**) counted over 512 rows, frames whose rows under the edge are more than 30 dB below the loudest frame counted apart as "without signal". Frames showing the edge are those whose bandwidth lies at or below the edge's row.
5. **Found or not:** a cutoff is reported as found where the step is at least 20 dB and at least half the frames with signal show it. Both thresholds come from the prototype's cases (below) and are fixed by the fixtures in the first pull request. Otherwise the report says "none found" and gives the largest step and its frequency.

What the prototype shows about robustness (30 s of stereo pink noise with a 330 Hz tone; see [Lossless files from lossy encodes](#lossless-files-from-lossy-encodes) for the encoders):

- **Quiet passages:** the average is dominated by loud frames, and the frame share counts only frames with signal. LAME at 128 kbps decoded, lowered by 30 dB and dithered with TPDF still gives a step of 26.6 dB in 642 of 644 frames.
- **Noise and dither:** TPDF dither lifts the floor above the edge a few dB (-124.5 against -128.7 dB). Noise-shaped dither (FFmpeg's `shibata`) lifts it to -94.9 dB, and the step falls from 54.9 to 31.2 dB, still found. Strong noise shaping on a quiet recording can hide an edge: then no cutoff is found, which is a missed edge, never an invented one.
- **Intentionally band-limited masters:** a 16 kHz brickwall (FFmpeg's `firequalizer`) on the lossless source gives 60.6 dB in 645 of 645 frames, the signature of a lossy encode. The report cannot tell them apart and does not try.
- **Natural rolloff:** four cascaded 16 kHz biquad lowpass filters give an 18.1 dB step spread from 18,292 to 19,025 Hz in 4 of 644 frames: not found.
- **22.05 versus 24 kHz Nyquist:** the search is relative to Nyquist, so it works at both rates (LAME at 128 kbps: 16,914 Hz at 44.1 kHz, 16,910 Hz at 48 kHz). An edge just below Nyquist is often an anti-alias or resampling filter; no fixture backs a figure for that yet, so the article says only that such filters exist.
- **Upsampling:** the source resampled from 44.1 to 48 kHz by FFmpeg's default resampler gave only a 7.7 dB step at 22,781 Hz in 0 of 702 frames: pink noise has little energy near 22 kHz, so the step finder does not see where the content ends. Reporting upsampling needs a second figure, the highest frequency whose level stays a set distance above the noise floor, which is left for later with its own fixtures.
- **No lowpass:** LAME `-V 0` (its lowpass disabled) and oggenc `-q 8` give no step above 2.4 dB, as the source itself (2.3 dB).

### Sample statistics

Exact on integer samples, in 64-bit integer sums; floats in double:

- **Sample peak:** the largest magnitude per channel, in dBFS (the most negative integer counts as full scale).
- **RMS:** per channel, in dBFS, a full-scale square wave reading 0 dB (the convention is stated in the article; AES17's sine-referenced convention reads 3.01 dB higher, **unverified** which the tools use).
- **DC offset:** the mean per channel, in percent of full scale and dBFS.
- **Clipping runs:** runs of at least 3 consecutive samples at the extreme codes (integers) or at or beyond full scale (floats), per channel: the number of runs, the samples in them, the longest. Three is a convention of this design, not a specification's; the article says so.
- **Digital silence:** samples that are exactly 0 in every channel at the start, at the end and in total.
- **Bit-depth usage:** for integers, the OR of every sample gives the low bits that are never set ("Low Bits Unused: 8 of 24") and the highest magnitude reached; for floats, whether every sample is a whole multiple of 2^-15 or 2^-23 in [-1, 1) ("Float Samples Hold 16-bit Values"). FLAC's per-subframe wasted bits (`wasted-bits`) are the coded counterpart.

### Loudness and loudness range

- **ITU-R BS.1770-5** (11/2023, the current revision; -4 has the same algorithm, **unverified** in detail), Annex 1:
  - K-weighting, a high-shelf pre-filter and a highpass (the "RLB" filter), whose coefficients the Recommendation gives for 48 kHz only. For other rates the coefficients are derived from the filters' analog parameters, as libebur128 does (MIT, its notice in the source and `THIRD-PARTY-NOTICES.md`, decision 13; the parameter values are **unverified** until that pull request reads its source).
  - Mean square per channel over gating blocks of 400 ms overlapping by 75 %; channel weights 1.0 for left, right and center and 1.41 for the surround channels, the LFE left out; the weights of other positions from the Recommendation's table (**unverified** for heights).
  - Gating: an absolute gate at -70 LUFS, then a relative gate 10 LU below the loudness of the blocks above it; the integrated loudness is -0.691 + 10 log10 of the weighted mean over the blocks that pass.
- **EBU R 128 and Tech 3341:** momentary loudness (400 ms) and short-term loudness (3 s), reported as their maxima; the loudness unit is LUFS, one name for what BS.1770 calls LKFS, which the article mentions once.
- **EBU Tech 3342:** loudness range from short-term values, absolute gate -70 LUFS, relative gate 20 LU below, the spread between the 10th and the 95th percentile.
- **Bounded memory:** block loudness values go into histograms of 0.01 LU from -70 to +10 LUFS (8,000 bins), each bin holding the count and the sum of its blocks' energies. The integrated loudness then sums energies exactly, except for the blocks in the one bin that holds the relative gate, which are taken whole or not at all (at most 0.01 LU off, **estimate**, against Tech 3341's tolerance of ±0.1 LU). The loudness range takes its percentiles from the short-term histogram. libebur128's histogram mode works the same way (**unverified** in detail).
- **Rates without a decoded 48 kHz:** the filters run at the stream's rate; 44.1 kHz and the high rates are tested against the cross-checks.

### True peak

- **BS.1770-5 Annex 2:** oversample by 4 with the Annex's interpolation filter (48 taps, 12 per phase), take the largest magnitude, report in dBTP. The factor for rates above 48 kHz follows the Annex's text (**unverified** here: the design assumes a factor that brings the rate to at least 176.4 kHz).
- **Cost:** 48 multiply-adds a sample of a channel; 23.1 ns with the four phases in vector lanes (x86-64-v2), 12.5 ns where the x86-64-v3 build has 256-bit vectors.
- **Skipping (estimate):** an interpolated value cannot exceed the sum of the filter's absolute taps times the largest sample magnitude among its 12 inputs. Where that bound is below the peak found so far, the window is skipped; the result is the same, and in music with an early loud peak most windows are skipped. Not prototyped.

## Determinism

Reports are compared byte for byte in the goldens on all six CI platforms and between the JIT and the NativeAOT build (`scripts/check_aot.sh`). Floating-point code can differ between machines for five reasons, and the design closes each:

### What can differ

- **Fused multiply-add:** `a * b + c` rounded once instead of twice. .NET is expected to emit it only for `Math.FusedMultiplyAdd` and the vector `FusedMultiplyAdd` methods (**unverified** as a general statement); the prototype found no automatic contraction on x64, in the JIT or in NativeAOT for x86-64-v3, which may use FMA instructions (below). Arm64 is not measured here (**unverified**); the goldens on the two Arm runners hold it.
- **The math library:** `Math.Sin`, `Math.Cos`, `Math.Log10`, `Math.Exp` and `Math.Pow` call the platform's C library, whose results may differ in the last bit between glibc, macOS and Windows (**unverified** for .NET 10's current implementations; not measurable on one machine). `Math.Sqrt`, +, -, * and / are correctly rounded IEEE operations everywhere.
- **Vector width and horizontal sums:** `Vector<T>` is 256 bits on AVX2 and 128 on Arm, and a sum across lanes adds in an order that depends on the width.
- **NaN semantics of minimum and maximum:** the x86 instructions return an operand, Arm's return NaN (**unverified** for .NET's `Vector128.Max` on each platform).
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
- **No platform math library:** trigonometric tables come from a polynomial evaluated with + and * only (the prototype's Taylor series on the first octant, with symmetry), logarithms from the exponent bits and a series (the prototype's `Log10Det`). `Math.Sqrt` is allowed.
- **Fixed order:** every sum runs in an order that depends only on the data's length; channels are combined in channel order; parallel work (below) never changes it.
- **Vectors element-wise only:** `Vector128` (and `Vector256`, which runs as two 128-bit halves where 256 bits are not available) for lane-independent operations, never `Vector<T>`, never a horizontal sum; lanes are combined in scalar code in a fixed order.
- **No NaN reaches the arithmetic:** non-finite inputs are counted and replaced with 0 before anything else.
- **Integers where exact:** peaks, sums for DC, clipping, silence and bit usage on integer samples are integer arithmetic.
- **Outputs on fixed grids:** figures are integers in fixed units (whole hertz, tenths of a dB), so no writer formats a double.

### How it is held

- `MeasurementDeterminismTests`: the raw spectra of a synthetic signal hash to a constant committed in the test, on every platform; the same for the K-weighting filter's output and the true-peak interpolation. A platform that differs fails this test before any golden.
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
| Sample statistics on integers (peak, sums, OR, clipping runs) | 2.0 ns | 2.3 ns | 2.2 ns |

  The JIT figures of the optimized FFT rows come from a run outside the machine lock, the rest of the JIT column from the first locked run; the CLI is released as NativeAOT, so its columns count.
- **The real decoder, same machine and build (NativeAOT x86-64-v2 of c517387):** a 300 s file of stereo 16-bit 44.1 kHz pink noise with a 440 Hz tone, FLAC `-5` (18.5 MB): analysis 0.132 s, `--verify` 0.390 s, so decoding costs **9.75 ns a sample of a channel**. The same audio as WAV (52.9 MB) analyzes in 0.061 s.

### Cost relative to decoding

**Estimates,** adding the kernels (NativeAOT x86-64-v2), for that 300 s stereo file (26.46 million samples of a channel):

| Measurements | Per sample of a channel | Added to the file's 0.39 s of `--verify` |
| --- | --- | --- |
| Spectrum (FFT, grids) | about 13 ns | about 0.35 s |
| and sample statistics | about 15 ns | about 0.40 s |
| and loudness and true peak (all) | about 43 ns | about 1.1 s |

- So the spectrum costs about 1.3 times FLAC's decoding, and everything about 4.4 times; against Monkey's Audio at insane or WavPack `-hh`, whose decoding costs 5 to 30 times FLAC's (decoders.md's tables), measuring is a small addition.
- A 300 s stereo file is measured in about 1.5 s in all; a 24-bit 96 kHz file costs about 2.2 times per second of audio (twice the samples, a larger FFT).

### The work budget

- **Why a budget:** measurements cost per decoded sample, and decoding a crafted file amplifies bytes into samples (a 49-byte FLAC frame codes more than 2 MB of PCM). The decoders' budget bounds the samples, so the measurements are bounded too, but only by about four times the decoders' time, since they cost more per sample than the cheapest decoding.
- **(a) A count of their own, as large as the decoders', recommended (decision 5):** the measurer charges work units per block before it does the work, at weights per sample of a channel in the decoders' unit (about 0.6 ns): 21 for the spectrum, 4 for the sample statistics, 8 for loudness, 38 for true peak (**estimate** from the kernels; a test sets them). The bound is the larger of 2^35 units and the file's size times the largest `WorkPerInputByte` among the sessions, the decoders' formula, counted apart. Past it, measuring stops (`measure.limit`) and decoding and verification go on.
  - Worst case: at most the decoders' time again (about 21 s for a small crafted file, 1.2 s per MB beyond 16 MiB).
  - Legitimate files: an hour of CD audio is 317.5 million samples of a channel, 2.2 × 10^10 units for everything, under the floor of 2^35 (3.4 × 10^10), so it is measured in full at any size; an hour of 8 channels at 24 bits and 192 kHz (5.5 × 10^9 samples, 3.9 × 10^11 units) needs a file of 190 MB, and such a file is several GB.
- **(b) Shared with the decoders:** simpler, but turning measurement on would make some files stop decoding earlier, so whether a stream verifies would depend on whether it was measured.
- **(c) None:** the decoders' budget bounds samples, and measurement time is at most about four times the decoding time it follows. Acceptable, but a crafted file then costs up to about 90 s more instead of 21 s (**estimate**: 2^35 units at 16 a sample is 2.1 × 10^9 samples, at 43 ns each).
- **The floor of the charge,** as for decoders: after each block the measurer has charged at least its weights times the samples it measured; a measurement that forgets to charge is `measure.failed` in every test.
- **`MeasurementWorkTests`,** in the `ComplexityTests` collection like `DecoderWorkTests`: each measurement's time per unit at most twice the FLAC reference's, in the nightly run.

### Memory

Per measured stream, independent of its length (**estimates** from the sizes, at 8 channels where it matters):

- the overlap buffer: one frame of samples per channel in doubles (4,096 × 8 × 8 B = 256 KiB; 16,384 points: 1 MiB);
- FFT scratch, twiddles and window: 6 arrays of the frame size in doubles (192 KiB at 4,096; 768 KiB at 16,384);
- average spectra: (channels + 1) × (N/2 + 1) doubles (590 KiB at 8 channels and 16,384 points);
- the spectrogram's cells in single precision (2 MiB) and the kept bytes (512 KiB);
- the waveform (1,024 columns × channels × 2 × 2 B, 32 KiB at 8 channels);
- the loudness histograms (8,000 bins × 16 B, twice: 256 KiB) and the true-peak filter state;
- in all under 6 MiB per stream. The report keeps the figures, the 512-row spectra and the grids: under 1 MiB per stream.
- **A bound per analysis:** at most 64 MiB of measurement state, about 10 streams at the largest sizes; further streams are not measured (`measure.streams-limited`). Decoded streams of one file are few today (Matroska's later routes may bring more).
- `MemoryTests`: a long stream measured, heap read from inside the last `Write`, growth per further block bounded, as `DecodingManyFlacFramesHoldsNothingPerFrame` does for decoders.
- No `ArrayPool<T>.Shared`, for the reason decoders.md gives.

### Cancellation and the exception boundary

- **Cancellation:** the measurer checks the analysis's token every 64 KiB samples of a channel and at every block. When it is canceled, the measurer stops working and returns; the decoder's next check of its own token (linked to the analysis's) throws, as decoders.md's contract has it. So the sink never throws, and the latency is at most one block (a WavPack set of up to 2^24 samples, measured in chunks with the check inside).
- **Stop:** `DecodeOptions.Stop` ends decoding, so the measurements end with status `Stopped`, covering what was decoded.
- **Exceptions:** the measurer catches every exception of its own code. An `OperationCanceledException` from the analysis's token cannot arise (it never throws on cancellation), so any exception is a bug: `measure.failed` (error), the stream's measurements are dropped, measuring of that stream stops, decoding goes on. Measurement code throws nothing on purpose: malformed PCM does not exist at this level (every bit pattern is a sample), and non-finite floats are counted.
- `OutOfMemoryException` passes through, as everywhere in the host.

### Streaming

- Nothing is loaded whole: the measurer sees blocks in stream order and keeps one frame of overlap.
- The PCM route of WAV and AIFF reads the data chunk in slices through `ByteReader`, as the other parsers read frames.

### Parallel decoding and the GUI

- parallel-decoding.md (approved, deferred) calls the sink in the commit, on the analysis thread, in stream order. The measurer would then add to the serial part: about 13 to 43 ns a sample of a channel next to commits of a few nanoseconds, which would eat most of the gain for WavPack (whose serial share is already 12 to 25 %).
- **So:** with parallel decoding, the measurer runs on a thread of its own that takes copied blocks from a bounded queue (two frames' worth, then back-pressure on the commit) and processes them in stream order. The copy costs about 1 ns a sample (**estimate**); the result is the same bits, since the order of every operation is unchanged. The channels of a block can be transformed on several threads, with the combined sums still added in channel order.
- Without parallel decoding, the measurer runs inline in the sink, which is simplest and what step 1 builds.
- The GUI's folder view never decodes, so its parallelism is untouched; a decode in the detail window takes the degree parallel-decoding.md's decision 2 gives, plus the measurer's thread.

## Verification

### Synthetic fixtures

Hand-built with the standard library (`make_measurement_fixtures.py`), regenerated and compared in CI, as WAV files (the PCM route) and as FLAC files (`flac` 1.5.0 in CI, the decoder route), each with a manifest from an independent model (`measurement_model.py`: its own DFT and filters, written from the specifications, not from the C# code):

- integer-phase sines at bin centers and between bins, at known levels;
- a logarithmic sweep, for the spectrogram's columns and rows;
- noise band-limited exactly at 11, 16, 19 and 20.5 kHz, as sums of sines with fixed random phases at bin centers, at 44.1 and 48 kHz; the same with TPDF dither, and quiet;
- natural rolloff (cascaded biquads), which must give no cutoff;
- clipping runs of 1, 2, 3 and 100 samples at both extremes, in integers and floats;
- DC offsets of known size; leading, trailing and inner digital silence;
- 16-bit audio padded to 24 bits, and floats that hold 16-bit values;
- true-peak overs: a sine at a quarter of the sample rate with a phase of 45°, whose samples peak at -3.01 dB of its true peak;
- 5.1 with an LFE that must not set the cutoff; 32-bit integers; floats with NaN and infinities; 192 kHz; a stream shorter than one frame; a stream longer than 512 hops, so columns merge.
- Expected: exact for the integer figures, within a stated tolerance for spectral figures (bin width for frequencies, 0.1 dB for levels) against the model, and exact against the golden.

### Lossless files from lossy encodes

`make_external_measurement_fixtures.py` encodes a pinned input (generated, a few seconds of stereo pink noise with a tone, at a level that keeps the FLAC files small) with pinned tools, decodes each with the encoder's own decoder where it has one, and commits the FLAC files (about 4 s each, a few MB in all, **estimate**). The manifest records the encoder's stated lowpass: LAME's tag Lowpass and the transition band its `--verbose` output prints; for oggenc and FFmpeg's encoders, the values their sources give (read and cited in that pull request; **unverified** here).

What the prototype measured on 30 s inputs (FFT of 4,096 points, Hann, hop 2,048; LAME 3.100, vorbis-tools 1.4.2, FFmpeg 6.1.1; decoded by `lame --decode` and by FFmpeg):

| Encode | Encoder's lowpass | Step | Edge (cutoff) | Transition from | Frames showing it |
| --- | --- | --- | --- | --- | --- |
| LAME `-b 128` | tag 17,000 Hz; band 16,538 to 17,071 Hz | 54.9 dB | 16,914 Hz | 16,559 Hz | 644 of 644 |
| LAME `-b 192` | tag 18,600 Hz; band 18,671 to 19,205 Hz | 55.7 dB | 18,938 Hz | 18,648 Hz | 644 of 644 |
| LAME `-b 320` | tag 20,500 Hz; band 20,094 to 20,627 Hz | 56.7 dB | 20,317 Hz | 20,037 Hz | 644 of 644 |
| LAME `-V 2` | tag 18,500 Hz; band 18,671 to 19,205 Hz | 55.5 dB | 18,982 Hz | 18,626 Hz | 644 of 644 |
| LAME `-V 5` | tag 17,000 Hz; band 16,538 to 17,071 Hz | 52.0 dB | 16,871 Hz | 16,527 Hz | 643 of 643 |
| LAME `-V 0` | tag 22,100 Hz; filter disabled | none (2.3 dB) | | | 0 of 644 |
| LAME `-b 128`, 48 kHz | tag 17,000 Hz; band 16,452 to 17,032 Hz | 54.2 dB | 16,910 Hz | 16,512 Hz | 701 of 701 |
| oggenc `-q 2` | not read | 62.2 dB | 16,667 Hz | 16,548 Hz | 644 of 644 |
| oggenc `-q 5` | not read | 60.5 dB | 20,435 Hz | 20,338 Hz | 644 of 644 |
| oggenc `-q 8` | not read | none (2.4 dB) | | | 0 of 644 |
| FFmpeg `aac` 96 kbps | not read | 43.1 dB | 16,688 Hz | 15,859 Hz | 645 of 645 |
| FFmpeg `aac` 128 kbps | not read | 61.5 dB | 17,323 Hz | 17,237 Hz | 645 of 645 |
| FFmpeg `aac` 192 kbps | not read | 59.2 dB | 19,423 Hz | 19,305 Hz | 645 of 645 |
| libopus 96 kbps (48 kHz) | not read | 46.6 dB | 20,648 Hz | 20,027 Hz | 702 of 702 |

- Every LAME edge lies inside the transition band LAME prints, and every transition starts within 60 Hz of that band's lower end; the tag's Lowpass is a rounded setting, not the edge (at `-V 2` the tag says 18,500 Hz and the band is the one of `-b 192`).
- These are the prototype's runs on throwaway files; the articles may state them only once committed fixtures reproduce them, worded as "in Encspect's test files, LAME 3.100 at `-b 128` leaves a spectral cutoff inside the transition band it prints; the analyzer does not name an encoder from a cutoff".
- The tests assert, per fixture, the cutoff within the stated band (LAME) or within a tolerance of the manifest's value, and "none found" where the encoder applies no lowpass.

### Cross-checks

Black-box, in a CI job on Ubuntu 26.04 (`crosscheck_measurement_fixtures.py`), with each tool's version asserted (`tool_versions.py`):

- **FFmpeg `ebur128`** (`peak=true+sample`): integrated loudness, loudness range, sample and true peak, within EBU Tech 3341's and 3342's tolerances (**unverified** which apply to true peak);
- **FFmpeg `astats`:** peak level, RMS level, DC offset, and its "Bit depth" (bits used), against the sample statistics;
- **sox `stats`** where the package is installed (**unverified** in the CI image): the same figures from a second tool;
- **sox `spectrogram`** and FFmpeg `showspectrumpic`: pictures for a person's review of the synthetic sweeps, not asserted;
- **the EBU loudness test set** (Tech 3341 and 3342 cases) and **libebur128's test expectations** (MIT): downloaded at pinned SHA-256 and never committed, as the ISO conformance streams are, since the test set's terms of use could not be read for this design (**unverified**); the expected values of each case come from the EBU documents.
- Differences fail the job until explained, as in every cross-check.

### Tests

- `MeasurementFixtureTests`: every measured fixture against its manifest, and a small `<name>.measure.json` golden of what measuring adds (the measurements with the grids replaced by their SHA-256), rewritten with `ENCSPECT_UPDATE_GOLDEN=1`, as the `.decode.json` goldens are; the tree must equal the one without measuring.
- `MeasurementDeterminismTests` (above), `MeasurementWorkTests` (above), `MemoryTests` (above).
- Description and topic tests over the measurements; `FindingCoverageTests` for every `measure.*` code.
- Cancellation: a cancel inside a large WavPack set and inside a long WAV data chunk ends the analysis promptly; Stop ends measuring with status `Stopped`.
- `CorruptFiles` and the fuzzer analyze with `--measure` and lowered `MaxWork` for both budgets, and fail on `measure.failed`.
- `check_aot.sh` runs with `--measure`; the vector paths run again with `DOTNET_EnableHWIntrinsic=0` in the `rest` shard.
- GUI: headless tests of the panels, `CapitalizationTests` for their labels.
- A milestone review of step 1 by an independent agent, as CLAUDE.md requires.

## Rollout

Each step is its own pull request, with fixtures, goldens, articles and documentation (`docs/cli.md`, `docs/gui.md`, `docs/development.md`):

1. **Framework and spectrum:** `DecodeOptions.Measure`, `MeasureOptions`, the sink's fan-out, `StreamMeasurements` and its writers (text, JSON, CSV), `--measure`, `--measure-grids`, `--hide measurements`; the deterministic kernel (tables, logarithm, FFT, window), the average spectrum, the spectral cutoff, the spectrogram and waveform grids; the budget, the memory bound, the boundary, the findings; the determinism, work, memory, fixture and fuzz tests; the synthetic and lossy-derived FLAC fixtures; the articles `measurements`, `spectrogram`, `spectral-cutoff` and the updates to `lowpass`, `coded-bandwidth` and `verification`; the `CLAUDE.md` rules; decoders.md's section renamed.
2. **Sample statistics:** peak, RMS, DC offset, clipping runs, digital silence, bit-depth usage, with their articles and cross-checks (`astats`).
3. **The PCM route:** the `pcm` decoder in the WAV project; the WAV, Wave64 and AIFF parsers hand their data chunks; the decode goldens of those fixtures change (`decode.no-decoder` goes).
4. **GUI panels:** Waveform, Spectrogram and Spectrum in the detail window, the decode command (decision 8), the captions before decoding.
5. **Loudness and true peak:** K-weighting at every rate, gating, loudness range, maxima, the loudness series, true peak; the EBU set and FFmpeg cross-checks; the Loudness panel.
6. **Later, each its own change:** the measurer on its own thread with parallel decoding; mid/side spectra and spectral holes; the bandwidth figure for upsampled content; the TTA decoder (clean-room), which then needs nothing here.

- **Why this order:** step 1 carries the use case and every risk (determinism, cost, wording), so it is reviewed first; steps 2 and 3 widen what is measured at little risk; the GUI follows once the data are settled, so its panels are drawn from a stable model; loudness and true peak have their own references and come once the framework is proven.
- **Alternative:** the GUI panels right after step 1, so users see the spectrogram sooner, before WAV files are measured. Decision 1.

## Open decisions

1. **The first step's scope.**
   - (a) Framework, average spectrum, spectral cutoff and the grids (the rollout above).
   - (b) As (a) with the sample statistics, which are cheap and exact.
   - (c) The framework with the sample statistics only, the spectrum second.
   - **Recommendation: (a).** It is the prime use case and carries the risks; (b) is acceptable if the pull request stays reviewable.
2. **The name.**
   - (a) "Measurements": `DecodeOptions.Measure`, `StreamInfo.Measurements`, `--measure`, `measure.*` codes, the `measurements` article; decoders.md's "host-side PCM analyses" renamed.
   - (b) "PCM analysis": `--pcm-analysis`, `StreamInfo.PcmAnalysis`.
   - **Recommendation: (a).** "Analysis" already names the whole report (`Analyzer`, `AnalysisOptions`), so a second "analysis" would blur one concept into two.
3. **Does measuring imply verification?**
   - (a) Yes: one decode pass, the checksums compared as with `--verify`.
   - (b) No: a decoder option to skip the checksums while measuring.
   - **Recommendation: (a).** No contract change, and the verdict comes almost free.
4. **The PCM route for WAV, Wave64 and AIFF.**
   - (a) A `pcm` decoder in the WAV project, by codec ID for every container of PCM.
   - (b) The host reads the samples.
   - (c) No measurements for PCM files.
   - **Recommendation: (a)** (see [WAV, Wave64 and AIFF: a PCM decoder](#wav-wave64-and-aiff-a-pcm-decoder)).
5. **The budget.**
   - (a) A count of its own, as large as the decoders', `measure.limit` stopping only the measurements.
   - (b) Shared with the decoders.
   - (c) None beyond the decoders'.
   - **Recommendation: (a).** Verification never depends on whether a file was measured, and the worst case stays within twice today's.
6. **Findings.**
   - (a) None about the audio but `measure.non-finite-samples` (warning); the host's `measure.limit`, `measure.failed`, `measure.streams-limited`.
   - (b) Also infos for a found cutoff, clipping runs or unused low bits.
   - **Recommendation: (a).** Figures are not verdicts, and an info through `--fail-on info` would act as one.
7. **Grids in JSON.**
   - (a) Only with `--measure-grids`, as base64 bytes; the default JSON has the figures and the 512-row spectra.
   - (b) Always.
   - (c) Never; the GUI reads them from the library.
   - **Recommendation: (a).** A grid is 683 KiB of base64 per stream, too much for every report, and other programs that read the JSON need a way to get it.
8. **The GUI's decode command.**
   - (a) One command that decodes, verifies and measures, labeled "Decode Audio", replacing "Verify Audio".
   - (b) Keep "Verify Audio" as the label and let it measure too.
   - (c) Decode automatically in the detail window for files under a size (say 200 MB).
   - **Recommendation: (a).** One decode, one command, and a label that is true for a WAV file, which has nothing to verify; (c) contradicts decoders.md's decision to keep decoding off everywhere and would decode while the user only browses.
9. **The FFT's source.**
   - (a) Written in-house from the literature (radix-2, real split).
   - (b) Following KISS FFT or PocketFFT (BSD-3-Clause), with the notice.
   - **Recommendation: (a)** (see [FFT](#fft)).
10. **Frame size and hop.**
    - (a) The size by sample rate (about 90 ms, 4,096 points at 44.1 and 48 kHz), hop half a frame.
    - (b) 4,096 points at every rate, hop half.
    - (c) As (a) with a whole-frame hop, at half the cost.
    - **Recommendation: (a).** The same time and frequency resolution at every rate, for about 1.3 times the cost at 192 kHz.
11. **If an Arm platform's spectra differ.**
    - (a) Find the operation and fix it under the rules (the expected case: a missed FMA or a library call).
    - (b) Compare grids within a tolerance in the goldens.
    - **Recommendation: (a)**, with (b) decided only then, for the one platform and with the cause named.
12. **TTA.**
    - (a) Out of these steps; its clean-room decoder stays under "Later" and is measured when it lands.
    - (b) Its decoder as a step of this rollout.
    - **Recommendation: (a).** It is a decoder's work with its own clean-room split, and the TTA use case is small next to FLAC and WAV.
13. **K-weighting at rates other than 48 kHz.**
    - (a) Derive the coefficients as libebur128 does (MIT), with its notice.
    - (b) Derive them from the analog filters described in the literature, without following an implementation.
    - **Recommendation: (a)**, as the most cross-checked derivation, with the notice.
14. **Dynamic range.**
    - (a) EBU loudness range only.
    - (b) Also the "DR" value of the Pleasurize Music Foundation's meter.
    - **Recommendation: (a).** No open specification of (b) was found.

## Unverified claims and estimates

- The costs of the full measurer, the per-stream memory, the JSON sizes and the work weights are sums of the prototype's kernels and the code's sizes, not measurements of a built measurer.
- The prototype's FFT and timings ran on one x64 machine. Arm64, macOS and Windows were not measured: neither their speed nor whether their spectra are bit-identical. Whether .NET's Arm64 JIT ever contracts a multiply and an add on its own is **unverified**; so is whether `Math.Cos` and `Math.Log10` differ between the platforms' libraries (the design avoids them either way).
- The per-frame bandwidth histogram of the cutoff's frame share and the true-peak skipping are designed, not prototyped.
- The cutoff thresholds (20 dB, half the frames) and the search band come from 20 prototype files; the fixtures set them.
- The lowpass of oggenc and FFmpeg's encoders was measured, not read from their sources.
- BS.1770-5's true-peak oversampling at rates above 48 kHz, its channel weights for height positions, EBU Tech 3341's tolerances for true peak, the terms of use of the EBU loudness test set and libebur128's filter parameters and histogram mode are to be read in the pull request that builds them.
- The DR meter's specification, the CI image's sox, a published color ramp and its license.
- The 24-bit noise floor per bin and Hann's leakage at 1 kHz from an edge are worked out from formulas.

## Sources

- The code at c517387: `Pcm.cs`, `DecodeContext.cs`, `StreamInfo.cs`, `CategorySeries.cs`, `StreamVerification.cs`, the host's `DecodeSession`, `DecodeRun`, `DecodeOptions`, `AnalysisOptions`, `ParserRouter`, the four decoders' `DeclareOutput` calls, the WAV, AIFF and TTA parsers, and the GUI's `GraphView`, `DetailViewModel` and `MainViewModel`.
- [decoders.md](decoders.md), [parallel-decoding.md](parallel-decoding.md), [coding-graphs.md](coding-graphs.md), the [plan](../PLAN.md), [cli.md](../cli.md), [gui.md](../gui.md), [development.md](../development.md).
- ITU-R BS.1770-5 (11/2023), "Algorithms to measure audio programme loudness and true-peak audio level", Annexes 1 and 2 (its listing at itu.int read; the text to be read in step 5).
- EBU R 128, EBU Tech 3341 (loudness metering, "EBU Mode") and Tech 3342 (loudness range); the EBU loudness test set.
- J. W. Cooley and J. W. Tukey, "An algorithm for the machine calculation of complex Fourier series", Mathematics of Computation 19 (1965).
- H. V. Sorensen, D. L. Jones, M. T. Heideman and C. S. Burrus, "Real-valued fast Fourier transform algorithms", IEEE Transactions on Acoustics, Speech, and Signal Processing 35(6) (1987).
- F. J. Harris, "On the use of windows for harmonic analysis with the discrete Fourier transform", Proceedings of the IEEE 66(1) (1978).
- P. D. Welch, "The use of fast Fourier transform for the estimation of power spectra", IEEE Transactions on Audio and Electroacoustics 15(2) (1967).
- G. Heinzel, A. Rüdiger and R. Schilling, "Spectrum and spectral density estimation by the Discrete Fourier transform (DFT)", Max-Planck-Institut für Gravitationsphysik (2002).
- License texts read on 2026-10-26: KISS FFT (`COPYING`, `LICENSES/BSD-3-Clause`), PocketFFT (`LICENSE.md`), PFFFT (`LICENSE.txt` of marton78/pffft), muFFT (`COPYING`), Math.NET Numerics (`LICENSE.md`), libebur128 (`COPYING`); minimp3's CC0 from `third_party/minimp3`.
- The tools of the prototype: LAME 3.100, vorbis-tools 1.4.2, FFmpeg 6.1.1 (Ubuntu's 6.1.1-3ubuntu5), flac 1.4.3.
