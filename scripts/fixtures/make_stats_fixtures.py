#!/usr/bin/env python3
"""Writes the stream statistics fixtures in tests/fixtures/stats.

Every hand-built manifest lists each MPEG Audio region's statistics and each stream's figures (see
stream_stats.py for the definitions); these fixtures are the ones built for them: several seconds of VBR audio for the
per-second series, VBR headers whose counts disagree with the stream or count their own frame,
gapless values longer than the stream, a sample rate change, free format, and a stream that an
embedded tag splits into two regions. Their frames have zero side information, which every
decoder plays as silence, so ffmpeg, mpg123 and libmad confirm the frame counts and the gapless
sample counts, and mp3guessenc the length, average bitrate and bitrate histogram
(crosscheck_mpeg_fixtures.py).

The script uses only the standard library and is deterministic, so CI reruns it and fails when
the committed files differ.
"""

import pathlib
import sys

import make_mpeg_fixtures as mpeg
from make_mpeg_fixtures import Frame
from make_vbr_fixtures import LAME_CBR, LAME_VBR, VbrFixture, encoder, vbr_audio, vbri_frame, xing_frame

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "stats"


class StatsFixture(VbrFixture):
    out = OUT


FIXTURES = []


def fixture(func):
    FIXTURES.append(func)
    return func


def seconds_of_vbr():
    """124 MPEG-1 frames at 44100 Hz (38.28 a second): about a second at 128 kbps, one at 192, one
    alternating 96 and 160, then ten frames at 320."""
    rates = [128] * 38 + [192] * 38 + [96, 160] * 19 + [320] * 10
    return vbr_audio(rates, rate=44100, mode="Joint stereo")


@fixture
def vbr_seconds():
    audio = seconds_of_vbr()
    f = StatsFixture("vbr-seconds", "Three seconds of VBR audio after a Xing header and LAME tag, so the bitrate and reservoir "
                     "series have a value for each second, and the gapless sample count leaves out LAME's delay and padding.",
                     encoder=encoder("LAME 3.100", "High"))
    return f.add_stream(xing_frame(audio, lame=dict(LAME_VBR, padding=1000)), audio)


@fixture
def xing_claims_more():
    audio = seconds_of_vbr()[:40]
    f = StatsFixture("xing-claims-more", "A Xing header and LAME tag written for 50 frames before a file that holds 40, as when a "
                     "file is cut at a frame boundary: the frame and byte counts disagree with the stream.",
                     encoder=encoder("LAME 3.100", "High"))
    whole = seconds_of_vbr()[:50]
    tag = xing_frame(whole, lame=dict(LAME_VBR, padding=1000))
    f.add_stream(tag, audio)
    return f


@fixture
def xing_claims_fewer():
    audio = seconds_of_vbr()[:60]
    f = StatsFixture("xing-claims-fewer", "A Xing header written for 40 frames before 60, as when frames are appended to a "
                     "finished file without a header of their own.", encoder=encoder("LAME 3.100", "High"))
    tag = xing_frame(audio[:40], lame=dict(LAME_VBR, padding=1000))
    f.add_stream(tag, audio)
    return f


@fixture
def xing_counts_own_frame():
    audio = vbr_audio([128, 160, 96, 192, 112, 128, 224, 64], rate=44100, mode="Joint stereo")
    f = StatsFixture("xing-counts-own-frame", "A Xing header without a LAME tag whose frame count includes its own frame, as the "
                     "Xing and Helix encoders count; that is consistent with the stream.")
    return f.add_stream(xing_frame(audio, frames=len(audio) + 1), audio)


@fixture
def vbri_claims_more():
    audio = vbr_audio([128, 160, 96, 192, 112, 128], rate=44100, mode="Joint stereo")
    f = StatsFixture("vbri-claims-more", "A VBRI header written for a longer file: its frame and byte counts exceed the stream "
                     "even counting its own frame.", encoder=encoder("Fraunhofer", "High"))
    tag = vbri_frame(audio + audio, bitrate=128)
    return f.add_stream(tag, audio)


@fixture
def gapless_exceeds_stream():
    audio = mpeg.stream(3, bitrate=128, rate=44100, mode="Joint stereo")
    f = StatsFixture("gapless-exceeds-stream", "A LAME tag whose delay and padding, 2000 and 3000 samples, are more than the 3456 "
                     "samples of the stream's three frames. Decoders that trim by them output nothing, so mpg123 counts the "
                     "frames without trimming.", encoder=encoder("LAME 3.100", "High"), crosscheck="mpg123", silent=False)
    return f.add_stream(xing_frame(audio, tag_id="Info", lame=dict(LAME_CBR, delay=2000, padding=3000)), audio)


@fixture
def sample_rate_change():
    f = StatsFixture("sample-rate-change-statistics", "Twenty frames at 44100 Hz, then twenty at 48000 Hz in the same region: "
                     "the duration adds both, and the reservoir starts again with the new stream.", crosscheck=False)
    f.add_frames(mpeg.stream(20, bitrate=128, rate=44100, mode="Joint stereo"))
    f.add_frames(mpeg.stream(20, bitrate=160, rate=48000, mode="Joint stereo"))
    f.finding("mpeg.stream-changed", "44100 Hz")
    return f


@fixture
def free_format_statistics():
    f = StatsFixture("free-format-statistics", "Forty free-format frames of 500 bytes plus padding at 44100 Hz: the distribution "
                     "has one bitrate, free, and the average comes from the frame lengths. ffmpeg does not read free format, so mpg123 "
                     "counts the frames.", crosscheck="mpg123", silent=False)
    padding = [0, 1] * 20
    f.add_frames([Frame(bitrate=None, free_length=500, padding=p, rate=44100, mode="Stereo") for p in padding])
    f.finding("mpeg.free-format", "500 bytes")
    return f


@fixture
def split_by_tag():
    audio = vbr_audio([128, 160, 96, 192, 112, 128, 224, 64, 160, 128], rate=44100, mode="Joint stereo")
    f = StatsFixture("split-by-tag", "A Xing stream with an ID3v2.4 tag embedded after its fifth frame, which the SEEK frame of "
                     "a leading tag points to: each region has its own statistics, and the header's counts cover the frames "
                     "of both. ffmpeg stops at the embedded tag, so libmad counts the frames.", crosscheck="madplay", silent=False)
    tag = xing_frame(audio)
    first_length = tag.length() + sum(frame.length() for frame in audio[:5])
    f.tag("ID3v2", mpeg.id3v2("Lead", major=4, extra_frames=mpeg.seek_frame(first_length)))
    f.add_stream(tag, audio[:5])
    f.tag("ID3v2", mpeg.id3v2("Middle", major=4))
    f.add_frames(audio[5:])
    return f


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for path in OUT.glob("*"):
        if not path.name.endswith(".report.json"):
            path.unlink()
    names = set()
    for build in FIXTURES:
        f = build()
        assert f.name not in names, f.name
        names.add(f.name)
        f.write()
    print(f"Wrote {len(names)} fixtures to {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
