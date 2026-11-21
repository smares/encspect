---
id: withheld-samples
title: Withheld samples
related: lossy-decoding, samples-and-lost-samples, measurements, bit-reservoir
references: Encspect's designs for lossy decoders (docs/design/lossy-decoders.md) and for damaged units (docs/design/damaged-units.md); RFC 7845, sections 4.2 and 4.6
---

**The problem.** In lossy coding a unit's output depends on the units before it: a transform adds the overlap of the previous unit, a filter keeps a history, a predictor adapts over many units. When a unit is lost, the first units after it cannot be rebuilt correctly.

**How it works.** The decoder never conceals a lost unit. After a gap it resets its state and holds back the output that depends on what was lost. Those positions are withheld samples, a count of samples per channel with a reason, such as after a gap or main data not available.

**Not lost samples.** [Lost samples](samples-and-lost-samples) are the declared samples of units that could not be read or decoded. Withheld samples belong to units that were read and decoded, whose output would be wrong. No measured block covers either, and the measurements count them apart.

**A failed checksum.** Only for the reason "checksum failed" is the audio still decoded and written: a unit whose own checksum fails (the [CRC](crc) article names them) is decoded, but the measurements leave it out and resume after it as after a gap. Dependent units go with it: the rest of its WavPack set, TrueHD units up to the next that restarts every substream, and after an MPEG audio, AC-3, DTS, Vorbis, Opus or Speex unit the span that a gap would withhold. A container's check counts as the unit's own: an Ogg page or Matroska Cluster whose CRC fails marks its packets. A unit with no checksum is never withheld on suspicion.

**How long.** After an overlapped transform or a filterbank the span is exact: one overlap, or the filter's history. The output after it equals what an undamaged stream decodes to. For recursive state it is the time the specification gives for it to converge, as RFC 7845 does for Opus (80 ms of pre-roll), or an estimate, and the format's documentation says which. A [bit reservoir](bit-reservoir) that reaches back into frames that are not available holds back the frame the same way.

**What you see.** A Withheld Samples figure in the measurements, with its reasons, and no finding of its own: a unit that failed to decode has one. The figure says what was held back, not what the audio held.
