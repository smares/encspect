#!/usr/bin/env python3
"""Writes MP4 fixtures of encrypted tracks with real muxers into tests/fixtures/mp4: ffmpeg 6.1's Common Encryption writer (cenc-aes-ctr, movenccenc.c) for AAC, AC-3,
E-AC-3, MPEG audio, ALAC, FLAC and Opus, whole and fragmented, and GPAC's MP4Box 2.2 (-crypt) for the four schemes cenc, cens, cbc1 and cbcs, with pssh boxes.

The keys protect nothing: every file is encrypted with the test key 000102030405060708090a0b0c0d0e0f under the key ID 101112131415161718191a1b1c1d1e1f, from 1.5 seconds
of a 440 Hz tone (and 660 Hz in the second channel). Only the key ID is written to the file (the tenc box and the pssh boxes), so the files can be read without any secret.

Each <name>.expected.json manifest has the command, the tools' versions and what the analyzer must report, mostly from independent tools at the time of encoding:

- "boxes": the boxes MP4Box -diso lists, as path and size (the offset is null: MP4Box gives none).
- "samples": the samples of the encrypted track, which ffprobe lists as packets (offset and size), or for the codings whose parsers merge ciphertext (AC-3, E-AC-3, MPEG
  audio, FLAC, Opus), the sizes MP4Box -dtsx lists (offset null). "units" is empty: the analyzer reads no frame of an encrypted track.
- "streams": the codec, rate, channels, samples and the protection (scheme, encryption, DRM systems) that the command gives.
- "fields": the values MP4Box reads from the movie, track and media headers, and the fields the command gives (the scheme, the KID, the pssh boxes).
- "findings" and "crosscheck" as in make_protection_fixtures.py.

    python3 scripts/fixtures/make_external_protection_fixtures.py --manifests-only [name ...]

rewrites the manifests from the committed files without the tools.

Needs ffmpeg 6.1, MP4Box (gpac 2.2) and mediainfo for the cross-check:

    apt-get install ffmpeg gpac
"""

import json
import pathlib
import re
import subprocess
import sys
import tempfile

from crosscheck_mp4_fixtures import ffprobe_packets, mp4box_boxes, mp4box_fields, mp4box_sample_sizes

ROOT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures"
OUT = ROOT / "mp4"
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"
GPAC_VERSION = "GPAC version 2.2.1"
GPAC_TOOL = "GPAC-2.2.1-rev2.2.1+dfsg1-3.1build2"
LAVC = "FFmpeg (Lavc60.31.102)"
KEY = "000102030405060708090a0b0c0d0e0f"
KID = "101112131415161718191a1b1c1d1e1f"
KID_TEXT = "10111213-1415-1617-1819-1a1b1c1d1e1f"
CONSTANT_IV = "202122232425262728292a2b2c2d2e2f"
WIDEVINE = "edef8ba979d64acea3c827dcd51d21ed"
PLAYREADY = "9a04f079984042 86ab92e65be0885f95".replace(" ", "")
STSD = "MP4/moov/trak/mdia/minf/stbl/stsd"
ENCA = STSD + "/enca"
SINF = ENCA + "/sinf"
TENC = SINF + "/schi/tenc"
MOOV_AFTER = ["mp4.moov-after-mdat", "not optimized for streaming"]
ENCRYPTED = ["mp4.samples-encrypted", "are encrypted"]
CODEC_UNPARSED = "mp4.codec-not-analyzed"
CENC = "Common Encryption, AES-CTR (cenc)"
SCHEMES = {"cenc": CENC, "cens": "Common Encryption, AES-CTR with pattern (cens)", "cbc1": "Common Encryption, AES-CBC (cbc1)",
           "cbcs": "Common Encryption, AES-CBC with pattern (cbcs)"}
SYSTEM_NAMES = {WIDEVINE: "Widevine Content Protection", PLAYREADY: "Microsoft PlayReady"}
FFMPEG_CENC = ["-encryption_scheme", "cenc-aes-ctr", "-encryption_key", KEY, "-encryption_kid", KID]
FRAGMENTS = ["-movflags", "frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "500000"]


def crypt_file(scheme, track_options, pssh=()):
    """The GPAC crypt file (https://wiki.gpac.io/xmlformats/Common-Encryption) of one track: the scheme's type, pssh boxes and the key."""
    types = {"cenc": "CENC AES-CTR", "cens": "CENC AES-CTR Pattern", "cbc1": "CENC AES-CBC", "cbcs": "CENC AES-CBC Pattern"}
    systems = "".join(
        f'<DRMInfo type="pssh" version="{version}">\\n<BS ID128="0x{system}"/>\\n' + (f'<BS bits="32" value="1"/>\\n<BS ID128="0x{KID}"/>\\n' if version else "")
        + f'<BS bits="32" value="{len(data)}"/>\\n<BS data="0x{data.hex()}"/>\\n</DRMInfo>\\n' for system, version, data in pssh)
    return (f'<GPACDRM type="{types[scheme]}">\\n{systems}<CrypTrack trackID="1" IsEncrypted="1" {track_options} saiSavedBox="senc">\\n'
            f'<key KID="0x{KID}" value="0x{KEY}"/>\\n</CrypTrack>\\n</GPACDRM>\\n')


def ffmpeg(name, extension, codec_options, codec, rate, parsed=True, fragmented=False):
    """A file ffmpeg's Common Encryption writer makes; AAC is the one coding whose samples ffmpeg's demuxer lists as packets one by one."""
    command = ["ffmpeg", "-v", "error", "-y", "-i", "{in}", *codec_options, *FFMPEG_CENC, *(FRAGMENTS if fragmented else []), "{out}"]
    findings = [ENCRYPTED] + ([] if fragmented else [MOOV_AFTER]) + ([] if parsed else [[CODEC_UNPARSED, f"protected {UNPARSED_FORMAT[codec]}"]])
    return dict(name=name, extension=extension, command=command, codec=codec, rate=rate, scheme="cenc", systems=[], parsed=parsed, findings=findings, pssh=[], options='IV_size="8"')


def mp4box(scheme, options, pssh, name=None, clear=False):
    """A file MP4Box encrypts with a crypt file; clear is its selectiveType Clear, which signals the encryption but leaves every sample in the clear, with a sample group.
    The tenc box still protects by default, so the analyzer reports the track as encrypted and reads no frame: it does not prove sample by sample that an encrypted track is partly clear."""
    findings = [ENCRYPTED]
    return dict(name=name or f"mp4box-{scheme}-aac", extension=".m4a", command=["MP4Box", "-quiet", "-crypt", "{crypt}", "{clear}", "-out", "{out}"], codec="AAC LC", rate=44100,
                scheme=scheme, systems=[SYSTEM_NAMES[system] for system, _, _ in pssh], parsed=True, findings=findings, pssh=pssh, options=options, is_clear=clear,
                crypt=crypt_file(scheme, options + (' selectiveType="Clear"' if clear else ""), pssh))


# The codings whose parsers ffmpeg runs on the samples, so that its packets are no longer the samples.
REPACKETIZED = {"AC-3", "E-AC-3", "Apple Lossless"}
# The sample entry's format of the codings no codec parser reads, as the finding names it.
UNPARSED_FORMAT = {"Apple Lossless": "alac", "FLAC": "fLaC", "Opus": "Opus"}
FIXTURES = [
    ffmpeg("ffmpeg-cenc-aac", ".m4a", ["-c:a", "aac", "-b:a", "128k"], "AAC LC", 44100),
    ffmpeg("ffmpeg-cenc-aac-fragmented", ".mp4", ["-c:a", "aac", "-b:a", "128k"], "AAC LC", 44100, fragmented=True),
    ffmpeg("ffmpeg-cenc-ac3", ".mp4", ["-c:a", "ac3", "-b:a", "192k"], "AC-3", 44100),
    ffmpeg("ffmpeg-cenc-eac3", ".mp4", ["-c:a", "eac3", "-b:a", "192k"], "E-AC-3", 44100),
    ffmpeg("ffmpeg-cenc-mp3", ".mp4", ["-c:a", "libmp3lame", "-b:a", "128k"], "MPEG-1 Audio", 44100),
    ffmpeg("ffmpeg-cenc-alac", ".m4a", ["-c:a", "alac"], "Apple Lossless", 44100, parsed=False),
    ffmpeg("ffmpeg-cenc-flac", ".mp4", ["-c:a", "flac", "-strict", "-2"], "FLAC", 44100, parsed=False),
    ffmpeg("ffmpeg-cenc-opus", ".mp4", ["-c:a", "libopus", "-b:a", "96k", "-strict", "-2"], "Opus", 48000, parsed=False),
    mp4box("cenc", 'IV_size="8"', [(WIDEVINE, 1, bytes.fromhex("08011210" + KID)), (PLAYREADY, 0, b"<WRMHEADER>test</WRMHEADER>")]),
    mp4box("cens", 'IV_size="8" crypt_byte_block="1" skip_byte_block="9"', []),
    mp4box("cbc1", 'IV_size="16"', []),
    mp4box("cbcs", f'IV_size="0" constant_IV_size="16" constant_IV="0x{CONSTANT_IV}" crypt_byte_block="1" skip_byte_block="9"', [(WIDEVINE, 0, b"mp4box")]),
    mp4box("cenc", 'IV_size="8"', [(WIDEVINE, 0, b"mp4box")], name="mp4box-cenc-clear-samples", clear=True),
]


def check_versions():
    ffmpeg_version = subprocess.run(["ffmpeg", "-version"], capture_output=True, text=True).stdout.splitlines()[0]
    if not ffmpeg_version.startswith(FFMPEG_VERSION):
        sys.exit(f"{FFMPEG_VERSION} is required, found {ffmpeg_version}")
    gpac = subprocess.run(["MP4Box", "-version"], capture_output=True, text=True)
    if GPAC_VERSION not in gpac.stdout + gpac.stderr:
        sys.exit(f"{GPAC_VERSION} is required")


def write_tone(path):
    sources = ["-f", "lavfi", "-i", "sine=f=440:d=1.5:r=44100", "-f", "lavfi", "-i", "sine=f=660:d=1.5:r=44100", "-filter_complex", "amerge"]
    subprocess.run(["ffmpeg", "-v", "error", "-y", *sources, "-ac", "2", "-c:a", "pcm_s16le", str(path)], check=True)


def duration_samples(fields, rate):
    """The samples the analyzer derives from the media header: its duration in seconds, times the rate."""
    duration = fields.get("MP4/moov/trak/mdia/mdhd/Duration")
    scale = fields.get("MP4/moov/trak/mdia/mdhd/Timescale")
    if not duration or not scale:
        return 0
    return round(int(duration.split()[0]) / int(scale.split()[0]) * rate)


def mp4box_tenc(path):
    """The attributes of the track encryption box in MP4Box's dump: its reading of the tenc box, which the manifest's fields repeat."""
    with tempfile.TemporaryDirectory() as directory:
        out = pathlib.Path(directory) / "dump.xml"
        subprocess.run(["MP4Box", "-quiet", "-diso", str(path), "-out", str(out)], capture_output=True)
        text = out.read_text(encoding="utf-8", errors="replace")
    match = re.search(r"<TrackEncryptionBox\s([^>]*)>", text)
    return dict(re.findall(r'(\w+)="([^"]*)"', match.group(1)))


def manifest_for(entry, path):
    data = path.read_bytes()
    boxes = [[p, None, s] for p, s in mp4box_boxes(path)]
    # MP4Box reads the sample entry as an MPEG audio entry (mp4a) whatever its format; the analyzer's node is the entry's own, enca.
    fields = {k.replace("/stsd/mp4a/", "/stsd/enca/"): v for k, v in mp4box_fields(path).items()}
    tenc = mp4box_tenc(path)
    fields.update({SINF + "/schm/Scheme Type": entry["scheme"], TENC + "/Default KID": KID_TEXT, TENC + "/Default IsProtected": "true" if tenc["isEncrypted"] == "1" else "false",
                   TENC + "/Default Per Sample IV Size": f"{tenc.get('IV_size', '0')} bytes"})
    if "crypt_byte_block" in tenc:
        crypt, skip = int(tenc["crypt_byte_block"]), int(tenc["skip_byte_block"])
        fields[TENC + "/Default Crypt Byte Block"] = f"{crypt} {'block' if crypt == 1 else 'blocks'}"
        fields[TENC + "/Default Skip Byte Block"] = f"{skip} {'block' if skip == 1 else 'blocks'}"
    if "constant_IV" in tenc:
        fields[TENC + "/Default Constant IV"] = tenc["constant_IV"][2:].lower()
    for index, (system, _, _) in enumerate(entry["pssh"]):
        fields["MP4/moov/pssh" + (f"[{index}]" if index else "")] = SYSTEM_NAMES[system]
    # AAC is the coding whose samples ffmpeg lists as packets one by one; for the others MP4Box lists the samples (without their offsets).
    # ffmpeg 6.1 reads no packet of the fragmented file it wrote itself ("saio atom found without saiz").
    is_aac = entry["codec"] == "AAC LC"
    listed = is_aac and "fragmented" not in entry["name"]
    packets = ffprobe_packets(path) if listed else [[None, size] for size in mp4box_sample_sizes(path)]
    is_clear = entry.get("is_clear", False)
    stream = {"codec": entry["codec"], "sample_rate": entry["rate"], "channels": 2, "channel_mode": "Stereo" if is_aac else None,
              "channel_layout": ["front left", "front right"] if is_aac else [],
              "samples": duration_samples(fields, entry["rate"]), "is_vbr": False,
              "protection": {"scheme": SCHEMES[entry["scheme"]], "is_encrypted": True, "systems": entry["systems"]}}
    if entry["command"][0] == "MP4Box":
        # With no frame to read, the only evidence is the writer's own tool atom, a claim of low confidence as in any file whose frames name nothing.
        stream["encoder"] = [GPAC_TOOL, "Low"]
    crosscheck = {"ffprobe": listed, "mp4box": True, "mp4box_samples": not listed, "faad": None,
                  "encryption": {"ffprobe": (None if "fragmented" in entry["name"] or entry["codec"] in REPACKETIZED else True), "mediainfo": "Encrypted"},
                  "skip": {"faad": "faad2 reads clear AAC only and rejects the encrypted sample entry."}}
    if is_clear:
        crosscheck["skip"]["faad_trace"] = "faad2 rejects the protected sample entry."
        crosscheck["skip"]["clear_samples"] = "the samples are in the clear (MP4Box's selectiveType Clear), and ffmpeg could read them; the analyzer reports the track as encrypted because its tenc box protects by default"
    if entry["name"] == "mp4box-cbc1-aac":
        # ffmpeg's AAC decoder reads the first access unit, here AES-CBC ciphertext, and takes a program config element from it, by chance.
        crosscheck["ffprobe_streams"] = False
        crosscheck["skip"]["ffprobe_streams"] = "the first access unit is ciphertext, and ffmpeg's AAC decoder finds a program config element of 27 channels in it"
    if entry["codec"] in REPACKETIZED:
        crosscheck["skip"]["ffprobe_encryption"] = "ffmpeg's parser for the coding makes packets of its own, which carry no encryption info"
    if not listed:
        crosscheck["skip"]["ffprobe"] = ("ffmpeg 6.1 reads no packet of the fragmented file it wrote (\"saio atom found without saiz\")" if "fragmented" in entry["name"] else
                                         "ffmpeg's parser for the coding merges the random-looking samples into packets of its own, or the coding has none") + "; MP4Box lists the samples instead."
    return {
        "description": f"{' '.join(entry['command'])} on 1.5 s of a 440/660 Hz tone, with the test key and key ID of this script.",
        "encoder": {"command": entry["command"]},
        "length": len(data), "format": "MP4", "boxes": boxes, "streams": [stream], "units": [], "samples": packets,
        "fields": fields, "findings": entry["findings"], "crosscheck": crosscheck,
    }


def run(entry, values):
    command = [arg.format(**values) for arg in entry["command"]]
    subprocess.run(command, check=True, capture_output=True)


def main(names):
    if names[:1] == ["--manifests-only"]:
        for entry in FIXTURES:
            if names[1:] and entry["name"] not in names[1:]:
                continue
            path = OUT / (entry["name"] + entry["extension"])
            (OUT / (entry["name"] + ".expected.json")).write_text(json.dumps(manifest_for(entry, path), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        return
    check_versions()
    with tempfile.TemporaryDirectory() as directory:
        temp = pathlib.Path(directory)
        tone = temp / "tone.wav"
        write_tone(tone)
        clear = temp / "clear.m4a"
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(tone), "-c:a", "aac", "-b:a", "128k", str(clear)], check=True)
        for entry in FIXTURES:
            if names and entry["name"] not in names:
                continue
            path = OUT / (entry["name"] + entry["extension"])
            path.unlink(missing_ok=True)
            crypt = temp / (entry["name"] + ".xml")
            if "crypt" in entry:
                crypt.write_text(entry["crypt"], encoding="utf-8")
            run(entry, {"in": str(tone), "clear": str(clear), "crypt": str(crypt), "out": str(path)})
            (OUT / (entry["name"] + ".expected.json")).write_text(json.dumps(manifest_for(entry, path), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            print(f"{path.name}: {len(path.read_bytes())} bytes")


if __name__ == "__main__":
    main(sys.argv[1:])
