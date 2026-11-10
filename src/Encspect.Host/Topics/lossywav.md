---
id: lossywav
title: lossyWAV
related: wasted-bits, bit-depth-usage, riff-chunks, wavpack-wrapper, flac-metadata-blocks, encoder-detection, writer-detection
references: lossyWAV 1.4.2 (Encspect's test files were written by its POSIX port; the official Windows build agrees), its own check, whose probes are in tests/fixtures/wav/lossywav-probes.json; RFC 9639 (FLAC), section 8; the project's fixtures, tests/fixtures/*/*lossy*
---

**What it is.** lossyWAV changes PCM audio before a lossless coder sees it. Its output is PCM again, so it is neither the encoder nor the writer of the file that results: the report calls it the *processor*. In Encspect's test files it sets the lowest bits of the samples to zero, chosen for each short block of each channel.

**The marker.** lossyWAV 1.4.2 writes a `fact` chunk after the format chunk. Its body is text: `lossyWAV 1.4.2 @ 08/11/2026 08:37:19, --quality standard`, then CR LF and two NUL bytes: the version, the date and time of the run, and the settings as written. lossyWAV's own check takes a file for its output by this chunk alone, and so does Encspect. The correction file carries the same text.

**Where it survives, in Encspect's test files.**

- In the WAV file lossyWAV writes; with `--stdout` it writes no chunk, but the RIFF size still counts it.
- In FLAC only with `--keep-foreign-metadata`, in `riff` application blocks.
- In WavPack's wrapper, in Monkey's Audio's header data, in the HEAD block of an OptimFROG file and in the wave data of a TAK file.

**The pattern in the samples.** In Encspect's test files (lossyWAV 1.4.2; the standard preset, and portable at 48 and 96 kHz) the count of zeroed bits changes on a grid of the lossyWAV block: 256 samples at 22.05 and 32 kHz, 512 at 44.1 and 48 kHz, 1,024 at 88.2 and 96 kHz, 2,048 at 176.4 and 192 kHz.

**In the coded counts.** FLAC's wasted bits and WavPack's Zero Bits ([wasted bits](wasted-bits)) change on lossyWAV's grid in frames of its block or a divisor of it, and in WavPack blocks of whole multiples of 512 samples; 4,096-sample frames show no grid.

**What Encspect does.** It names lossyWAV from the marker text, with high confidence and the settings as evidence. A missing marker says nothing, since recoding can drop the chunk. Without it, those counts can name lossyWAV at low confidence and without a version: changes at 20 or more positions, all on the block's grid and not on a coarser one, and most units above the smallest count. Software that zeroes bits per block of exactly that length could show the same. The claim says what ran over the audio and nothing about the result.
