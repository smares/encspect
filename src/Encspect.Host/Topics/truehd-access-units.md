---
id: truehd-access-units
title: TrueHD and MLP access units
related: truehd-substreams, truehd-restart-header, crc, verification
references: Encspect's TrueHD and MLP format description (docs/design/truehd-format.md), sections "Access unit" and "Major sync"
---

**The problem.** A lossless stream has no frame boundaries a reader can find without knowing the syntax, and a decoder that joins a stream in the middle needs the stream's parameters from somewhere.

**How it works.** A TrueHD or MLP stream is a sequence of access units with no gaps. Each unit starts with a 4-byte header: a check nibble, the unit's length in 16-bit words and a timing counter. A unit holds 40 samples of each channel at 44.1 or 48 kHz, 80 at 88.2 or 96 kHz and 160 at 176.4 or 192 kHz. Some units carry a major sync after the header: the sync word 0xF8726FBA for TrueHD, 0xF8726FBB for MLP, then the sampling rate, the channel layouts, the number of substreams, a peak data rate and a CRC-16. A decoder can start only at such a unit. After the header and the major sync comes the substream directory, which gives each substream's end and whether it ends in check data ([substreams](truehd-substreams)).

**Checks.** The check nibble makes the parity of the header and the directory come out right, so every unit is checked. The major sync has its own [CRC](crc).

**What you see.** The units with their lengths, the major sync fields and the directory entries, and in the statistics the units, major syncs and samples. A unit with a length that does not fit, a major sync that fails its CRC, or bytes between units that no unit explains is a finding. Units before the first major sync cannot be read, because the substream count and the sampling rate are not yet known.
