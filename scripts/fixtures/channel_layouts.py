"""ffmpeg's names for its channel layouts (libavutil/channel_layout.c, channel_layout_map), as the
speakers the analyzer names, so the cross-checks can compare the layout ffprobe reports with a
manifest's "channel_layout". ffmpeg orders every layout its own way, so only the speakers are
compared, not their order."""

FL, FR, FC, LFE = "front left", "front right", "front center", "LFE"
BL, BR, BC, SL, SR = "back left", "back right", "back center", "side left", "side right"
FLC, FRC = "front left of center", "front right of center"

FFMPEG = {
    "mono": [FC],
    "stereo": [FL, FR],
    "2.1": [FL, FR, LFE],
    "3.0": [FL, FR, FC],
    "3.0(back)": [FL, FR, BC],
    "4.0": [FL, FR, FC, BC],
    "quad": [FL, FR, BL, BR],
    "quad(side)": [FL, FR, SL, SR],
    "3.1": [FL, FR, FC, LFE],
    "5.0": [FL, FR, FC, BL, BR],
    "5.0(side)": [FL, FR, FC, SL, SR],
    "4.1": [FL, FR, FC, LFE, BC],
    "5.1": [FL, FR, FC, LFE, BL, BR],
    "5.1(side)": [FL, FR, FC, LFE, SL, SR],
    "6.0": [FL, FR, FC, BC, SL, SR],
    "6.1": [FL, FR, FC, LFE, BC, SL, SR],
    "6.1(back)": [FL, FR, FC, LFE, BL, BR, BC],
    "7.0": [FL, FR, FC, BL, BR, SL, SR],
    "7.1": [FL, FR, FC, LFE, BL, BR, SL, SR],
    "7.1(wide)": [FL, FR, FC, LFE, BL, BR, FLC, FRC],
    "7.1(wide-side)": [FL, FR, FC, LFE, FLC, FRC, SL, SR],
}


def same_speakers(ffmpeg_name, speakers):
    """Whether ffmpeg's layout of that name feeds the speakers listed, in any order; None where the
    name is not one of those above."""
    known = FFMPEG.get(ffmpeg_name)
    return None if known is None else sorted(known) == sorted(speakers)
