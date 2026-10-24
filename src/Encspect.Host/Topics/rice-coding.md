---
id: rice-coding
title: Rice coding
related: flac-subframes, linear-prediction, huffman-coding, monkeys-audio-range-coding
references: RFC 9639 (FLAC), section 9.2
---

**The problem.** The residual of a predictor is small numbers, positive and negative, most near zero. A code that gives small numbers short words and big numbers longer ones suits it, and the code should not need a table.

**How it works.** A Rice code with parameter k splits a non-negative number into a quotient and a remainder: the quotient, the number shifted right by k, is written in unary, as that many zeros and then a one, and the remainder is the k low bits as they are. FLAC first folds each signed residual to a non-negative number, so that 0, -1, 1, -2, 2 become 0, 1, 2, 3, 4. A suitable parameter is about the logarithm of the average magnitude, so the encoder picks it for each partition: the residual is cut into 2 to the partition order equal parts, up to 32,768, each with its own parameter, so the code follows loud and quiet stretches. The parameter takes 4 bits, or 5 in the second coding method, which allows parameters up to 30.

**Escape.** A partition whose parameter field is all ones is not Rice coded: a 5-bit width follows, and each sample is stored with that many bits. A width of 0 means all zeros.

**What you see.** The coding method, the partition order and each partition's parameter or escape in the first frames, and the range of parameters. A code whose value does not fit 32 bits is a finding.
