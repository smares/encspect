---
id: lossywav
title: lossyWAV
related: wasted-bits, bit-depth-usage, riff-chunks, wavpack-wrapper, flac-metadata-blocks, encoder-detection, writer-detection
references: lossyWAV 1.4.2 (the POSIX port; the official Windows build agrees, as tests/fixtures/wav/lossywav-probes.json records), its own check; RFC 9639 (FLAC), section 8; the project's fixtures, tests/fixtures/*/*lossy*
---

**What it is.** lossyWAV changes PCM audio before a lossless coder sees it. Its output is PCM again, so it is neither the encoder nor the writer of the file that results: the report calls it the *processor*. In Encspect's test files it sets the lowest bits of the samples to zero, a count it chooses for each short block of each channel, so a lossless coder spends fewer bits on them (see [wasted bits](wasted-bits)).

**The marker.** lossyWAV 1.4.2 writes a `fact` chunk after the format chunk. Its body is text: `lossyWAV 1.4.2 @ 08/11/2026 08:37:19, --quality standard`, then CR LF and two NUL bytes. It holds the version, the date and time of the run and the settings in lossyWAV's long option names, as written and not as applied. In Encspect's test files (written by the POSIX port of lossyWAV 1.4.2), lossyWAV's own check takes a file for its output by this chunk alone (probes in tests/fixtures/wav/lossywav-probes.json), and so does Encspect: a `fact` chunk before the data that starts with `lossyWAV `. The correction file lossyWAV can write carries the same text.

**Where it survives, in Encspect's test files.**

- In the WAV file lossyWAV writes; with `--stdout` it writes no chunk, but the RIFF size still counts it.
- In FLAC only when flac keeps the WAV file's other chunks (`--keep-foreign-metadata`), one `riff` application block per chunk.
- In WavPack's wrapper and in Monkey's Audio's header data.

**The pattern in the samples.** In Encspect's test files (lossyWAV 1.4.2, the standard preset, and the portable preset for those at 48 and 96 kHz) the count of zeroed bits changes on a grid of the lossyWAV block, counted from the first sample: 256 samples at 22.05 and 32 kHz, 512 at 44.1 and 48 kHz, 1,024 at 88.2 and 96 kHz, 2,048 at 176.4 and 192 kHz. In the noise-floor files at the standard preset the two channels' counts agree for 31 to 45 percent of the 32-sample spans. Two 16-bit files at that preset hold no zeroed bits at all. The analyzer does not use the pattern yet.

**What Encspect does.** It names lossyWAV from the marker text, with high confidence and the settings as evidence. A missing marker says nothing, since recoding can drop the chunk. The claim says what ran over the audio and nothing about the result.
