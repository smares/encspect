---
id: alac-prediction
title: Prediction in Apple Lossless
related: linear-prediction, alac-adaptive-golomb, mid-side-stereo, alac
references: Apple Lossless Audio Codec source (macosforge/alac, Apache License 2.0), dp_dec.c, dp_enc.c and matrix_dec.c
---

**The problem.** The samples of audio are not independent: each is close to a value that earlier samples predict. Storing the difference from the prediction makes the numbers smaller, so they take fewer bits ([linear prediction](linear-prediction)).

**How it works.** Each channel of a compressed element carries its own filter: a number of coefficients (up to 31), a shift that scales the sum, and a prediction type. The filter is a short adaptive filter: the decoder predicts a sample from the previous ones with the coefficients, adds the error it reads, and then nudges the coefficients by the sign of that error, so they keep following the signal without being resent. The first samples, before the filter has a full history, are stored as differences from the sample before. A prediction type other than 0 adds a second, first-order stage.

Before the filter, a pair can be mixed. The element stores two numbers, `mixBits` and `mixRes`: when `mixRes` is 0 the channels are left and right; otherwise the first is a weighted mean of both and the second their difference, a form of [mid/side stereo](mid-side-stereo) with a weight set per element.

**What you see in a file.** For each channel the analyzer reports the number of coefficients, the shift and the prediction type; for each pair the two mixing numbers. A coefficient count of 0 means the residuals are the samples themselves, mixed as the element says.

**Where it is specified.** In Apple's source, the files `dp_dec.c`, `dp_enc.c`, `matrix_dec.c` and `matrix_enc.c`.
