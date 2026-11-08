---
id: withheld-samples
title: Withheld samples
related: lossy-decoding, samples-and-lost-samples, measurements, bit-reservoir
references: Encspect's design for lossy decoders (docs/design/lossy-decoders.md); RFC 7845, sections 4.2 and 4.6
---

**The problem.** In a lossy coding the output of a unit depends on the units before it: a transform adds the overlap of the previous unit, a filter keeps a history, a predictor adapts over many units. When a unit is lost, the first units after it cannot be rebuilt correctly, though their own bytes are intact.

**How it works.** The decoder never conceals a lost unit with made-up audio. After a gap it resets its state and holds back the output that depends on what was lost. Those positions are withheld samples, a count of samples per channel with a reason, such as after a gap, after frames of 0 bytes, or main data not available. Where a span ends inside a unit, the decoder writes the rest of that unit.

**Not lost samples.** [Lost samples](samples-and-lost-samples) are the declared samples of units that could not be read or decoded. Withheld samples belong to units that were read and decoded, whose output would be wrong. Both are positions that no measured block covers, and the measurements count them apart.

**How long.** After an overlapped transform or a filterbank the span is exact: one overlap, or the filter's history. The output after it equals what an undamaged stream decodes to. For recursive state it is the time the specification gives for it to converge, as RFC 7845 does for Opus (80 ms of pre-roll), or an estimate, and the format's documentation says which. A [bit reservoir](bit-reservoir) that reaches back into frames that are not available holds back the frame the same way.

**What you see.** A Withheld Samples figure in the measurements, with its reasons. No finding is raised for it: where a unit failed to decode, that has its own finding. The figure says what was held back, not what the audio contained.
