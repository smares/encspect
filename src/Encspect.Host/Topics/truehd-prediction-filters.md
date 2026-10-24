---
id: truehd-prediction-filters
title: TrueHD and MLP prediction filters
related: truehd-decoding-parameters, truehd-residual-coding, linear-prediction
references: Encspect's TrueHD and MLP format description (docs/design/truehd-format.md), section "Prediction filters"
---

**The problem.** A sample is close to what the samples before it suggest, so coding only the difference needs fewer bits ([linear prediction](linear-prediction)).

**How it works.** Each coded channel may have two filters. A FIR filter of up to 8 taps predicts the next sample from the last outputs of the filter. An IIR filter of up to 4 taps predicts it from the last prediction errors. The two together have at most 8 taps, and when both are present they share one shift. The prediction is the sum of the taps, each a coefficient times a past value, shifted right by the filter's shift. The decoded value is the prediction plus the residual, with the quantized low bits cleared, and the error is the decoded value minus the prediction. The coefficients are carried in the stream, with 1 to 16 bits each; an IIR filter may also carry the starting values of its error history. The histories continue from block to block and across restart headers.

**Sums.** The sums need more than 32 bits, so a decoder keeps them wider and wraps only the stored values.

**What you see.** For each channel, the orders and shifts of its filters under the block's parameters. A filter order past its limit, coefficient bits out of range, orders that add up to more than 8, a state on a FIR filter or two filters with different shifts is a finding.
