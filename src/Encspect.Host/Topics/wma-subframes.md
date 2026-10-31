---
id: wma-subframes
title: Windows Media Audio tile headers and subframes
related: wma-pro, wma-lossless, wma-packets, block-switching, wma-channel-transforms
references: Encspect's format descriptions docs/design/wma-pro-format.md and wma-lossless-format.md
---

**The problem.** A transform codec codes quiet, steady stretches best with long transforms and sudden changes with short ones. Windows Media Audio Professional and Lossless therefore divide the samples of a frame into subframes of different lengths, and let each channel have its own division.

**The tile header.** It starts each frame. The decode flags state the most subframes M a channel may have in a frame; the shortest subframe is the frame length over M, and every length is the frame length divided by a power of two (Professional) or a multiple of the shortest (Lossless). A bit says whether all channels share the same division. If not, the header repeats a round: the channels that have the fewest samples so far each say with one bit whether they take part in the next subframe, and a length code gives its length. The round ends when every channel has the whole frame.

**Subframes.** The next subframe starts at the smallest number of samples any channel has covered. It holds the channels whose next subframe starts there and has that length, so a subframe is coded once for all of them.

**What you see in a file.** Encspect shows the tile header with each channel's list of subframe lengths, and each subframe with its start, length and channels. Its statistics count the subframes of each length.
