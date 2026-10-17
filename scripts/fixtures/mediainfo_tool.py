"""Runs MediaInfo (MediaArea's MediaInfo, BSD-2-Clause; Ubuntu 26.04's mediainfo package, on
MediaInfoLib 26.01) and reads its JSON output for the cross-check scripts.

MediaInfo reads container and stream headers and decodes no audio, so it is one more independent
reader of the header fields, encoder strings and tags the analyzer reports. Each script compares
what MediaInfo reports with the fixture's golden report (*.report.json), which the tests hold to the
fixture and its manifest: the formats' names, channels, sample rate, frame and sample counts,
bitrate and bitrate mode, encoder evidence, stream size and tags, wherever MediaInfo reports them.

Every field that is not compared on a fixture is skipped with its reason, and a reason that names
MediaInfo's own reading cites MediaInfoLib's source (github.com/MediaArea/MediaInfoLib, tag v24.01,
Source/MediaInfo; the reasons were written against 24.01 and are re-checked against 26.01 as the cross-checks are adapted).
Each script prints how often each field was compared and each reason it was not (Tally), and fails on a listed difference that no longer occurs, so the lists stay current.
"""

import base64
import collections
import json
import os
import re
import subprocess
import sys

VERSION = "26.01"


def require():
    """Exits unless MediaInfo 26.01 is on the PATH: the skips below describe that version's reading. The version is read from
    "MediaInfoLib - v26.01" in the output of --Version, whatever follows it (a patch number, a build suffix)."""
    try:
        output = subprocess.run(["mediainfo", "--Version"], capture_output=True, text=True, check=True).stdout
    except (OSError, subprocess.CalledProcessError) as error:
        sys.exit(f"MediaInfo is needed: {error}")
    if not re.search(rf"MediaInfoLib - v{re.escape(VERSION)}(?![\d])", output):
        sys.exit(f"MediaInfo {VERSION} is needed, found: {' '.join(output.split())}")


class Failed(Exception):
    """MediaInfo exited with a non-zero code (negative: ended by that signal) or wrote nothing."""

    def __init__(self, returncode):
        super().__init__(f"MediaInfo exited with {returncode}" if returncode else "MediaInfo wrote nothing")
        self.returncode = returncode


def read(path, full=False):
    """MediaInfo's tracks of the file as {"General": track, "Audio": [tracks], ...}, every value a
    string as MediaInfo writes it (custom fields under "extra"). Raises Failed where MediaInfo exits
    with an error or a signal or writes nothing: that is a problem of the fixture, not a file
    without a format (see Tally.read). With full, the fields of --Full as well."""
    # Without a UTF-8 locale, MediaInfo writes every character outside ASCII as "?".
    environment = dict(os.environ, LC_ALL="C.UTF-8")
    command = ["mediainfo", "--Output=JSON", *(["--Full"] if full else []), str(path)]
    result = subprocess.run(command, capture_output=True, text=True, env=environment)
    if result.returncode != 0 or not result.stdout.strip():
        raise Failed(result.returncode)
    document = json.loads(result.stdout)
    media = (document[0] if isinstance(document, list) else document).get("media") or {}
    tracks = {}
    for track in media.get("track", []):
        kind = track["@type"]
        if kind == "General":
            tracks[kind] = track
        else:
            tracks.setdefault(kind, []).append(track)
    tracks.setdefault("General", {})
    tracks.setdefault("Audio", [])
    return tracks


def read_or_problem(path, problems, full=False):
    """read(), with a failure added to the fixture's problems and None returned, for a script without a Tally."""
    try:
        return read(path, full)
    except Failed as failure:
        problems.append(str(failure))
        return None


def text(value):
    """A MediaInfo string; it writes a value that is not valid UTF-8 as base64, which is decoded
    as ISO-8859-1 here, byte for byte."""
    if isinstance(value, dict) and value.get("@dt") == "binary.base64":
        return base64.b64decode(value["#value"]).decode("latin-1")
    return value


# MediaInfo's speaker names in ChannelLayout (MediaInfoLib's channel layout strings, such as
# AC3_ChannelLayout_lfeon in File_Ac3.cpp and Aac_ChannelLayout in File_Aac_Main.cpp), as the
# analyzer names them; "M" is mono, and "Ls"/"Rs" are the surround pair, at the sides where a back
# pair ("Lb"/"Rb") is present as well.
SPEAKERS = {"L": "front left", "R": "front right", "C": "front center", "M": "front center", "LFE": "LFE", "Ls": "side left",
            "Rs": "side right", "Lb": "back left", "Rb": "back right", "Cb": "back center", "Cs": "back center", "S": "back center",
            "Lss": "side left", "Rss": "side right"}


def speakers(layout):
    """MediaInfo's ChannelLayout as the analyzer's speakers, sorted; None for a name not in SPEAKERS."""
    names = (layout or "").split()
    if not names or any(n not in SPEAKERS for n in names):
        return None
    return normalized(SPEAKERS[n] for n in names)


def normalized(names):
    """Speakers sorted, with a lone surround pair at the back: formats name it a surround pair
    without saying side or back, and MediaInfo, ffmpeg and the analyzer place it differently."""
    names = list(names)
    if "side left" in names and "back left" not in names:
        names = ["back left" if n == "side left" else "back right" if n == "side right" else n for n in names]
    return sorted(names)


def golden(media):
    """The golden report of a fixture: the analyzer's report the tests compare with every run."""
    stem = media.name[: -len(media.suffix)] if media.suffix else media.name
    path = media.with_name(stem + ".report.json")
    return json.loads(path.read_text(encoding="utf-8"))[0]


def walk(node):
    """The node and every node below it, depth first."""
    yield node
    for child in node.get("children", []):
        yield from walk(child)


def child(node, *names):
    """The node reached through children of these names, or None."""
    for name in names:
        node = next((c for c in (node or {}).get("children", []) if c["name"] == name), None)
    return node


def value(node, *names):
    """The value of the node reached through children of these names, or None."""
    found = child(node, *names) if names else node
    return None if found is None else found.get("value")


def leading_number(value):
    """The number a value starts with, as "8 (AC-3)" gives 8; None without one."""
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return value
    match = re.match(r"-?\d+(\.\d+)?", str(value or ""))
    return None if match is None else (float(match.group()) if match.group(1) else int(match.group()))


# Where MediaInfo puts each tag's title, artist and album: ID3v2's and ID3v1's title as the track
# name (Tag/File_Id3v2.cpp, TIT2; Tag/File_Id3.cpp), the title of APE (Tag/File_ApeTag.cpp), Lyrics3
# v2 (Tag/File_Lyrics3v2.cpp, ETT), Vorbis comments (Tag/File_VorbisCom.cpp, keys in any case) and
# MP4's iTunes metadata (Multiple/File_Mpeg4_Elements.cpp, moov_meta___nam) as the title.
TAG_FIELDS = {
    "ID3v2": {"TIT2": "Track", "TT2": "Track", "TPE1": "Performer", "TP1": "Performer", "TALB": "Album", "TAL": "Album"},
    "ID3v1": {"Title": "Track", "Artist": "Performer", "Album": "Album"},
    "APE": {"TITLE": "Title", "ARTIST": "Performer", "ALBUM": "Album"},
    "Lyrics3": {"ETT": "Title", "EAR": "Performer", "EAL": "Album"},
    "Vorbis Comment": {"TITLE": "Title", "ARTIST": "Performer", "ALBUM": "Album"},
    "iTunes Metadata": {"Title": "Title", "Artist": "Performer", "Album": "Album"},
}


def tag_values(report):
    """{field: [value of each tag that fills it]} for MediaInfo's Title, Track, Performer and Album.
    Several entries of one tag for a field are joined with " / ", as MediaInfo fills a field that
    already holds a value; an APEv2 list counts with its first value only, as MediaInfo reads an
    item's value as one string, which ends at the NUL that separates the list (Tag/File_ApeTag.cpp,
    Get_UTF8)."""
    found = collections.defaultdict(list)
    for node in walk(report["root"]):
        fields = TAG_FIELDS.get(node["name"])
        if fields is None or (node["name"] in ("ID3v2", "ID3v1", "APE", "Lyrics3") and node.get("kind") != "Tag"):
            continue
        entries = node.get("children", [])
        if node["name"] == "APE":
            entries = (child(node, "Items") or {}).get("children", [])
        values = collections.defaultdict(list)
        for entry in entries:
            key = entry["name"].upper() if node["name"] in ("APE", "Vorbis Comment") else entry["name"]
            field = fields.get(key)
            value = entry.get("value")
            if node["name"] == "APE":
                value = next((c.get("value") for c in entry.get("children", []) if c["name"] == "Value"), value)
            if field is not None and isinstance(value, str) and value:
                values[field].append(value)
        for field, texts in values.items():
            found[field].append(" / ".join(texts))
    return found


def compare_tags(tally, general, report):
    """MediaInfo's Title, Track, Performer and Album must each be what one of the file's tags holds
    there, be there whenever a tag holds one and be absent otherwise. Which tag wins where several do
    is MediaInfo's choice, so any of them counts. Where MediaInfo finds no format, it reads no tag,
    so all four must be absent. MediaInfo copies the title to the track name where no tag gives
    one, and the track name to the title (File__Analyze_Streams.cpp, File__Analyze::Tags)."""
    values = tag_values(report)
    fields = ("Title", "Track", "Performer", "Album")
    if values and not general.get("Format"):
        # File__Tags_Helper runs inside the parser of each format, so MediaInfo reads no tag of a
        # file whose format it does not recognize: it must then show none.
        for field in fields:
            tally.compare(f"tag {field.lower()} without a format", text(general.get(field)), None)
        return
    titles = values.get("Title") or values.get("Track") or []
    tracks = values.get("Track") or values.get("Title") or []
    for field, expected in zip(fields, (titles, tracks, values.get("Performer", []), values.get("Album", []))):
        actual = text(general.get(field))
        if not expected:
            tally.compare(f"tag {field.lower()}", actual, None)
            continue
        wanted = actual if actual in expected else expected[0] if len(expected) == 1 else f"one of {expected}"
        tally.compare(f"tag {field.lower()}", actual, wanted)


def byte_count(node):
    """The bytes a node covers: the sum of its ranges where they are not contiguous."""
    ranges = node.get("ranges")
    return sum(r["length"] for r in ranges) if ranges else node["length"]


def stream_node(report, stream):
    """The first node of a report's stream."""
    node = report["root"]
    for index in stream["nodes"][0]:
        node = node["children"][index]
    return node


class Tally:
    """Counts each field compared with MediaInfo and each reason one was not, for the summary, and
    collects the problems of one fixture at a time."""

    def __init__(self, explained=None, crashes=None):
        self.counts = collections.Counter()
        # {fixture: (exit code, reason, optional)}: MediaInfo ends on the fixture with that code (negative: by that signal), with the
        # evidence. With optional, a build that does not end that way is no problem: the crash is undefined behavior of the library.
        self.crashes = crashes or {}
        self.crashed = set()
        # {fixture: {field: reason}}: MediaInfo's own reading of a field on a fixture, with the evidence.
        self.explained = explained or {}
        self.used = set()
        self.problems = []
        self.name = None

    def start(self, name):
        self.name = name
        self.problems = []
        return self.problems

    def read(self, path, full=False):
        """MediaInfo's tracks of the fixture (read()), or None for a fixture in `crashes` that ends as stated. Any other
        failure, and a stated crash that does not happen (unless it is optional), is a problem: an absent format is no
        agreement with a crash."""
        expected = self.crashes.get(self.name)
        try:
            tracks = read(path, full)
        except Failed as failure:
            if expected is not None and failure.returncode == expected[0]:
                self.crashed.add(self.name)
                self.counts[("exit code", f"skipped: {expected[1]}")] += 1
                return None
            self.problems.append(f"{failure}" + (f", expected {expected[0]}" if expected else ""))
            return None
        if expected is not None:
            self.crashed.add(self.name)
            if not expected[2]:
                self.problems.append(f"MediaInfo no longer ends with {expected[0]}; drop it from the expected crashes")
        return tracks

    def compare(self, field, actual, expected):
        """MediaInfo's actual value must equal the expected one, unless the fixture's difference is
        explained, in which case it must still differ."""
        reason = self.explained.get(self.name, {}).get(field)
        if reason is not None:
            self.used.add((self.name, field))
            if actual == expected:
                self.problems.append(f"MediaInfo's {field} {actual!r} now agrees; drop it from the explained differences")
            else:
                self.counts[(field, f"skipped: {reason}")] += 1
            return
        self.counts[(field, "compared" if expected is not None else "compared, absent on both" if actual is None else "compared, expected absent")] += 1
        if actual != expected:
            self.problems.append(f"MediaInfo's {field} is {actual!r}, expected {expected!r}")

    def skip(self, field, reason):
        self.counts[(field, f"skipped: {reason}")] += 1

    def unused(self):
        """Explained differences on fixtures or fields that were never compared."""
        return [f"{name}: the explained difference in {field} was not compared" for name, fields in sorted(self.explained.items())
                for field in fields if (name, field) not in self.used] + [f"{name}: the expected crash was not read" for name in sorted(self.crashes)
                                                                           if name not in self.crashed]

    def print(self, out=sys.stderr):
        fields = sorted({field for field, _ in self.counts})
        for field in fields:
            for (f, how), count in sorted(self.counts.items(), key=lambda c: (-c[1], c[0][1])):
                if f == field:
                    print(f"MediaInfo {field}: {count} fixtures, {how}", file=out)
