"""Tests of alac_model.walk_frame() and dyn_decomp() by cases worked from Apple's ALACDecoder.cpp (Decode) and ag_dec.c (dyn_decomp), Apache License 2.0,
Copyright (c) 2011 Apple Inc.: python3 scripts/ci/test_alac_model.py"""
import os
import sys
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures"))
import alac_model as M

MONO = [M.Channel([1099, -651, -43, 117])]
STEREO = [M.Channel([1166, -755, -9, 37, 16, 86, 43, 25]), M.Channel([1099, -651, -43, 117])]
CASES = []


def case(f):
    CASES.append(f)
    return f


def cookie(channels, frame_length=4096):
    return M.parse_cookie(M.cookie(channels=channels, frame_length=frame_length))


def wave(count, seed=1):
    return [((i * 37 + seed * 11) % 200) - 100 for i in range(count)]


def single(w, count, partial, instance=0, tag=M.SCE):
    """A compressed single channel element of count samples; partial says whether it states them (the writer would state them if they differ from the frame length)."""
    M.write_audio(w, tag, instance, [wave(count, instance)], 16, 4096, partial=partial, parameters=MONO)


def pair(w, count, partial):
    M.write_audio(w, M.CPE, 0, [wave(count, 1), wave(count, 2)], 16, 4096, partial=partial, parameters=STEREO)


def ending_at(residue):
    """A frame of one compressed single channel element whose last bit is at the given position modulo 8 (0 for a byte boundary), without an end tag: a
    sample count is found by trial, so that the packet's last byte leaves the given number of bits unused."""
    for count in range(1000, 1100):
        w = frame(lambda w: single(w, count, True))
        if w.position % 8 == residue:
            return w, count
    raise AssertionError


def frame(*writers):
    w = M.BitWriter()
    for write in writers:
        write(w)
    return w


@case
def partial_element_sets_the_frame_samples():  # Decode: numSamples is replaced by the element's 32-bit count, and *outNumSamples = numSamples
    w = frame(lambda w: pair(w, 1000, True), M.write_end)
    r = M.walk_frame(w.data(), cookie(2))
    assert r["status"] == "ok" and r["samples"] == 1000 and r["channels"] == 2 and r["differing"] is None and r["too_long"] is None


@case
def later_elements_keep_the_partial_count():  # numSamples carries over: the second element states no count and still has 3000 samples
    w = frame(lambda w: single(w, 3000, True), lambda w: single(w, 3000, False, 1), M.write_end)
    r = M.walk_frame(w.data(), cookie(2))
    assert r["status"] == "ok" and [e["samples"] for e in r["elements"][:2]] == [3000, 3000]
    assert [e["partial"] for e in r["elements"][:2]] == [True, False]
    assert r["samples"] == 3000 and r["differing"] is None


@case
def elements_before_the_partial_one_keep_the_frame_length():  # the first element was decoded with the cookie's frame length; the last element's count is the frame's
    w = frame(lambda w: single(w, 4096, False), lambda w: single(w, 3000, True, 1), M.write_end)
    r = M.walk_frame(w.data(), cookie(2))
    assert [e["samples"] for e in r["elements"][:2]] == [4096, 3000]
    assert r["samples"] == 3000 and r["differing"] == 1


@case
def a_second_partial_count_replaces_the_first():
    w = frame(lambda w: single(w, 3000, True), lambda w: single(w, 2000, True, 1), M.write_end)
    r = M.walk_frame(w.data(), cookie(2))
    assert r["samples"] == 2000 and r["differing"] == 1


@case
def a_partial_count_above_the_frame_length():  # the decoder's buffers hold the frame length; the walk goes on and says so
    w = frame(lambda w: single(w, 5000, True), M.write_end)
    r = M.walk_frame(w.data(), cookie(1))
    assert r["status"] == "ok" and r["samples"] == 5000 and r["too_long"] == 0


@case
def the_decoder_stops_once_the_cookies_channels_are_decoded():  # `if (channelIndex >= numChannels) break`: a further element is not decoded
    w = frame(lambda w: single(w, 4096, False), lambda w: single(w, 1500, True, 1), M.write_end)
    r = M.walk_frame(w.data(), cookie(1))
    assert r["samples"] == 4096, "the element after the stop does not change the frame's sample count"
    assert r["exceeding"] == 1 and r["channels"] == 2 and r["status"] == "ok"


@case
def no_end_tag_is_needed_after_the_last_channel():  # the packet ends where the last channel's residuals end, and no whole tag fits after it
    for residue in (0, 6, 7):
        w, count = ending_at(residue)
        r = M.walk_frame(w.data(), cookie(1))
        assert r["status"] == "end-missing" and r["samples"] == count and r["walked"] == 0 and r["exceeding"] is None, residue
    w, count = ending_at(3)
    assert M.walk_frame(w.data(), cookie(1))["status"] == "truncated", "five bits are left, room for a tag that is not there"


@case
def a_packet_that_ends_before_the_channels_are_decoded_is_cut():  # the loop's `bits->cur < bits->end` check: no end tag and channels still to come
    w, count = ending_at(0)
    r = M.walk_frame(w.data(), cookie(2))
    assert r["status"] == "truncated" and r["channels"] == 1


@case
def a_pair_that_does_not_fit_in_the_channels_left():  # case ID_CPE: `channelIndex + 2 > numChannels` goes to NoMoreChannels
    w = frame(lambda w: single(w, 4096, False), lambda w: pair(w, 4096, False), M.write_end)
    r = M.walk_frame(w.data(), cookie(2))
    assert r["exceeding"] == 1 and r["samples"] == 4096


@case
def the_end_tag_ends_the_walk_with_fewer_channels():  # case ID_END: the frame is byte-aligned and the zero-filled channels are the cookie's, not the stream's
    w = frame(lambda w: single(w, 4096, False), M.write_end)
    r = M.walk_frame(w.data(), cookie(2))
    assert r["status"] == "ok" and r["channels"] == 1 and r["walked"] == len(w.data())


@case
def fill_and_data_stream_elements_do_not_count_as_channels():
    w = frame(lambda w: M.write_fill(w, 3), lambda w: M.write_data_stream(w, 0, bytes(9)), lambda w: single(w, 4096, False), M.write_end)
    r = M.walk_frame(w.data(), cookie(1))
    assert [e["tag"] for e in r["elements"]] == [M.FIL, M.DSE, M.SCE, M.END] and r["channels"] == 1 and r["status"] == "ok"


@case
def reserved_bits_are_refused():  # `RequireAction(unusedHeader == 0, ...)`
    w = frame(lambda w: M.write_audio(w, M.SCE, 0, [wave(4096)], 16, 4096, parameters=MONO, reserved=1), M.write_end)
    r = M.walk_frame(w.data(), cookie(1))
    assert r["status"] == "reserved" and r["reserved"] == 0 and r["elements"][0]["end"] == 19


@case
def three_bytes_shifted_are_refused():  # `RequireAction(bytesShifted != 3, ...)`
    w = frame(lambda w: M.write_audio(w, M.SCE, 0, [wave(4096)], 16, 4096, parameters=MONO, shift_flags=3), M.write_end)
    assert M.walk_frame(w.data(), cookie(1))["status"] == "invalid"


@case
def coupling_and_program_config_elements_have_no_syntax():
    for tag in (M.CCE, M.PCE):
        w = frame(lambda w: w.write(tag, 3), lambda w: w.write(0, 5))
        r = M.walk_frame(w.data(), cookie(1))
        assert r["status"] == "unknown" and r["samples"] == 0


@case
def an_uncompressed_pair_reads_the_full_bit_depth():  # the escape path of ID_CPE sets chanBits = bitDepth, 2 * numSamples samples follow the header
    w = frame(lambda w: M.write_audio(w, M.CPE, 0, [wave(100), wave(100, 2)], 16, 4096, escape=True), M.write_end)
    r = M.walk_frame(w.data(), cookie(2))
    assert r["status"] == "ok" and r["samples"] == 100 and r["elements"][0]["end"] == 23 + 32 + 100 * 2 * 16


@case
def the_residuals_of_the_writer_are_read_back():  # dyn_decomp against ag_enc.c's dyn_comp: the same runs, long codes and end position
    residuals = [0] * 300 + [3, -2, 0, 0, 0, 1, 40000, -1, 0, 0] + [0] * 700 + [5] * 40 + [-90000] + [0] * 100
    w = M.BitWriter()
    runs, longs = M.encode_residuals(w, residuals, 10, 40, 14, 20)
    end = w.position
    data = w.data()
    position, got_runs, got_longs, fault = M.dyn_decomp(data, 0, len(residuals), 20, 10, 40, 14)
    assert (position, got_runs, got_longs, fault) == (end, runs, longs, None) and runs >= 3 and longs >= 2


@case
def a_run_longer_than_the_samples_left_is_refused():  # `RequireAction(c + n <= numSamples, ...)`
    residuals = [0] * 200
    w = M.BitWriter()
    M.encode_residuals(w, residuals, 10, 40, 14, 16)
    assert M.dyn_decomp(w.data(), 0, 100, 16, 10, 40, 14)[3] == "zero-run"


@case
def residuals_cut_short_are_truncated():  # `bitPos < maxPos` at the top of the loop
    w = M.BitWriter()
    M.encode_residuals(w, wave(500), 10, 40, 14, 16)
    assert M.dyn_decomp(w.data()[:100], 0, 500, 16, 10, 40, 14)[3] == "truncated"


if __name__ == "__main__":
    for f in CASES:
        f()
    print("ok", len(CASES), "cases")
