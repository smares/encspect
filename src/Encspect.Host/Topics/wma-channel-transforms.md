---
id: wma-channel-transforms
title: Windows Media Audio Professional channel transforms
related: wma-pro, wma-subframes, mid-side-stereo, joint-stereo
references: Encspect's format description docs/design/wma-pro-format.md
---

**The problem.** The channels of a recording are often alike. Coding sums and differences, or rotations, of channels instead of the channels themselves concentrates the energy in fewer values and saves bits (see [mid/side stereo](mid-side-stereo)).

**How it is coded.** Each subframe of a stream with more than one channel starts with a reserved bit, then divides its channels into groups. For more than two channels left, one bit per channel says whether it joins the group; the last one or two channels form the final group without bits. A group of two has one bit for a fixed transform: a sum and difference in a stereo stream, a rotation by 45 degrees otherwise. A group of more than two has a bit for the transform and a bit that says whether it sends a matrix, as rotation angles of six bits and sign bits, or uses a default matrix. A group of one has nothing.

**Where it applies.** For a group with a transform, one bit says it applies in every scale factor band of the subframe; otherwise one bit per band says where.

**What you see in a file.** Encspect shows each group with its channels and the number of bands its transform applies in, and counts the groups and bands that use a transform. In Encspect's test files stereo subframes are one group of both channels.
