---
id: linear-prediction
title: Linear prediction
related: flac-subframes, rice-coding, wavpack-decorrelation, monkeys-audio-prediction
references: RFC 9639 (FLAC), section 9.2
---

**The problem.** A sample of tonal or smooth audio can be estimated well from the samples just before it. Coding the estimate's error takes fewer bits than coding the sample.

**How it works.** A linear predictor estimates a sample as the sum of the last few samples, each multiplied by a coefficient. FLAC's linear predictors have an order of 1 to 32, the number of earlier samples used. The coefficients are integers of 1 to 15 bits, and the sum is shifted right by a stated number of bits, which makes them fixed-point fractions. The encoder computes the coefficients for each subframe from its samples, so they fit that stretch of audio, and stores them. The residual, the sample less its prediction, is coded with [Rice codes](rice-coding). The decoder repeats the prediction from the samples it has already rebuilt and adds the residual, so it needs the first samples, the warm-up, to start.

**Fixed predictors.** The five fixed predictors of order 0 to 4 need no coefficients. Order 1 predicts the last sample; order 2 continues the line through the last two; orders 3 and 4 do the same with a parabola and a cubic, with coefficients (2, -1), (3, -3, 1) and (4, -6, 4, -1).

**What you see.** Predictor Order, Coefficient Precision, Coefficient Shift and the Coefficients of each linear predictor, and in the statistics the average and highest order.
