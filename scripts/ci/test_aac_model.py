"""Tests of aac_model.sbr_bands() by the test vectors of docs/design/aac-sbr-frequency-tables.md: python3 scripts/ci/test_aac_model.py"""
import os
import sys
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures"))
import aac_model as M


def bands(rate, start, stop, scale, alter, xover, noise):
    return M.sbr_bands({"start": start, "stop": stop, "xover": xover, "freq_scale": scale, "alter_scale": alter, "noise_bands": noise}, rate)


# (Fs, start, stop, freq_scale, alter_scale, xover, noise_bands) -> (N_high, N_low, N_Q)
ACCEPTED = [
    ((44100, 5, 9, 2, 1, 0, 2), (16, 8, 3)),
    ((48000, 5, 9, 2, 1, 0, 2), (16, 8, 4)),
    ((48000, 5, 9, 2, 1, 7, 2), (9, 5, 2)),
    ((16000, 5, 9, 2, 1, 0, 2), (14, 7, 3)),
    ((44100, 15, 12, 1, 0, 0, 0), (10, 5, 1)),
    ((48000, 3, 5, 2, 0, 0, 2), (16, 8, 3)),
    ((48000, 3, 5, 2, 1, 0, 2), (14, 7, 3)),
    ((44100, 1, 6, 3, 0, 0, 2), (16, 8, 4)),
    ((32000, 13, 12, 3, 0, 0, 2), (10, 5, 2)),
    ((44100, 5, 14, 2, 1, 3, 1), (7, 4, 1)),
    ((44100, 5, 15, 0, 1, 1, 3), (13, 7, 4)),
    ((16000, 0, 0, 0, 0, 0, 2), (32, 16, 3)),
    ((16000, 0, 1, 0, 0, 0, 2), (32, 16, 3)),
    ((16000, 0, 1, 0, 1, 0, 2), (16, 8, 3)),
    ((16000, 0, 3, 0, 1, 0, 2), (18, 9, 3)),
    ((16000, 0, 2, 0, 1, 0, 2), (18, 9, 3)),
    ((16000, 0, 0, 1, 0, 0, 2), (20, 10, 3)),
]
REJECTED = [
    ((8000, 0, 0, 2, 1, 0, 2), "SBR has no frequency tables for an output rate of 8000 Hz"),
    ((22050, 0, 12, 0, 0, 0, 2), "the SBR range spans 49 QMF bands, more than the 48 allowed at 22050 Hz"),
    ((44100, 9, 15, 1, 0, 0, 2), "the SBR range spans 36 QMF bands, more than the 35 allowed at 44100 Hz"),
    ((24000, 15, 0, 0, 0, 0, 2), "the SBR range would start at QMF band 32 and end at 32"),
    ((44100, 14, 3, 0, 0, 0, 2), "the SBR master frequency table is empty"),
    ((24000, 15, 1, 2, 0, 0, 2), "the SBR master frequency table's first region is empty"),
    ((22050, 0, 0, 1, 0, 0, 2), "the SBR master frequency table has a band of no width"),
    ((22050, 14, 0, 3, 0, 2, 2), "bs_xover_band 2 lies beyond the 2 bands of the master frequency table"),
    ((16000, 11, 0, 3, 0, 2, 2), "the SBR range starts at QMF band 33, beyond 32"),
    ((88200, 0, 8, 0, 0, 0, 2), "the header gives 6 noise floor bands, more than 5"),
]


def main():
    for args, expected in ACCEPTED:
        got = bands(*args)
        assert got == expected, (args, got, expected)
    for args, message in REJECTED:
        try:
            bands(*args)
        except ValueError as e:
            assert str(e) == message, (args, str(e), message)
        else:
            raise AssertionError(f"{args} was accepted")
    defaults = M.sbr_bands({"start": 5, "stop": 9, "xover": 0}, 44100)
    assert defaults == (16, 8, 3), defaults
    one_band = M.sbr_bands({"start": 15, "stop": 12, "xover": 0, "freq_scale": 1, "alter_scale": 0, "noise_bands": 0}, 44100)
    assert one_band == (10, 5, 1), one_band
    print(f"ok: {len(ACCEPTED)} accepted, {len(REJECTED)} rejected")


if __name__ == "__main__":
    main()
