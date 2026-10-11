#!/usr/bin/env python3
"""Checks the analyzer on output of encoders whose licences allow private evaluation only:
Fraunhofer's l3enc 2.60 and 2.61 (shareware) and the demos of its mp3enc 3.0 and 3.1 (evaluation
only, no public evaluation without Fraunhofer's consent), and, with --mp3pro, Thomson's mp3PRO
Audio Player 1.1 with its 64 kbps encoder ("solely for personal use to evaluate mp3PRO
technology"). Run by hand, never in CI: nothing they write is kept or committed, and the results
are printed, not stored. The project's owner uses them for this personal, non-commercial project
on that basis.

Each encoder encodes this project's own signals into a temporary directory; the analyzer must name
"Fraunhofer" with low confidence from the frames alone, as its trait rule says, and mp3guessenc
must name a Fraunhofer encoder, unless every frame fails its CRC, which mp3guessenc skips frames
for. The exit status is 1 when either does not.

The encoders are 1998 Linux builds linked against libc5, which Debian's archive still keeps: its
libc5 and ld.so packages are unpacked next to them and patchelf points them at that loader. The
mp3enc demos need address space layout randomization off (setarch -R) to allocate memory. l3enc
2.71 and 2.72 are time-limited trials that have expired, and are not used; unregistered l3enc only
encodes 44100 Hz at its default 112 kbps.

The mp3PRO encoder only has a window, driven here with xdotool on a virtual display: its encoder
window takes the source from its file dialog, and the Original File Length option is ticked. Its
output must be mp3PRO with an OFL block whose delay and length mp3guessenc reads the same, and
whose samples, counted at twice the core's rate, fit the stream.

Usage: python3 scripts/fixtures/check_restricted_encoders.py [--mp3pro] src/Encspect.Cli/bin/Release/net10.0/encspect
Needs: patchelf, setarch, and what crosscheck_mpeg_fixtures.py needs to build mp3guessenc; with
--mp3pro also Wine (32-bit), Xvfb, xdotool, 7z and unshield.
"""

import json
import os
import pathlib
import subprocess
import sys
import tarfile
import tempfile
import time

import downloads
import guessenc_tool
from crosscheck_mpeg_fixtures import guessenc_guess, guessenc_ofl
from make_external_mpeg_fixtures import write_tone

RAREWARES = "https://www.rarewares.org/rrw/files/fhg/"
DEBIAN = "http://archive.debian.org/debian/dists/slink/main/binary-i386/"

# Archive, SHA-256, the encoder's path inside it.
ENCODERS = {
    "l3enc 2.60": ("l3enc260.linux.tar.gz", "3771a769fba06bcd14b7f4bfbf9d896c7d56d298c84e25fe70662fce9a3461e2", "l3v260.linux/l3enc"),
    "l3enc 2.61": ("l3enc261.linux.tar.gz", "6b218b3f5bef5657cdb3cd3034bf3fb3a3bc97b16afbee55aa764e9bf442045b", "l3v261.linux/l3enc"),
    "mp3enc 3.0 demo": ("mp3encdemo_linux.tgz", "cbfd7eefde5e18de5c5cc8d4ab8a7f2c2469d3525406d00629c96d62d701b47f", "mp3enc30/mp3encdemo"),
    "mp3enc 3.1 demo": ("mp3encdemo_3_1_linux.tgz", "6c961ded5dde14959572b2751d93805707f5f2e1c271a6ad3099c69a160d3943", "mp3enc31/mp3encdemo31"),
}
RUNTIME = {
    "base/ldso_1.9.10-1.deb": "f30aae62c2c67c610035efdf87ac150370971a317e88fcba9cabe3713c6e1e9b",
    "oldlibs/libc5_5.4.46-3.deb": "ea3ebac7ec40186769974f22d2e4a16fb013aac01fb92a064da2a673bc7d4f83",
}

# Signal, sample rate, channels, and the arguments each family takes.
CASES = [
    ("music", 44100, 2, [], ["-br", "128000"]),
    ("music", 44100, 1, [], ["-br", "64000"]),
    ("music", 48000, 2, None, ["-br", "160000", "-no-is"]),
    ("music", 32000, 2, None, ["-br", "96000", "-crc"]),
]


def fetch(directory):
    """Downloads and unpacks the encoders and the libc5 runtime, checked against their SHA-256."""
    runtime = directory / "runtime"
    for name, digest in RUNTIME.items():
        path = directory / pathlib.Path(name).name
        downloads.fetch([DEBIAN + name], digest, path)
        subprocess.run(["dpkg-deb", "-x", str(path), str(runtime)], check=True)
    loader = runtime / "lib" / "ld-linux.so.1"
    binaries = {}
    for label, (archive, digest, member) in ENCODERS.items():
        path = directory / archive
        downloads.fetch([RAREWARES + archive], digest, path)
        target = directory / label.replace(" ", "-")
        target.mkdir()
        with tarfile.open(path) as tar:
            tar.extractall(target, filter="data")
        binary = target / member
        subprocess.run(["patchelf", "--set-interpreter", str(loader), str(binary)], check=True)
        binaries[label] = binary
    return binaries, runtime / "lib"


def encode(label, binary, libraries, source, path, arguments):
    environment = dict(os.environ, LD_LIBRARY_PATH=str(libraries))
    if label.startswith("l3enc"):
        # The shareware asks whether to enter a registration code.
        command = ["setarch", "-R", str(binary), str(source), str(path), *arguments]
        answer = "N\n\n\n"
    else:
        command = ["setarch", "-R", str(binary), "-if", str(source), "-of", str(path), *arguments]
        answer = "\n"
    subprocess.run(command, input=answer, capture_output=True, text=True, env=environment, timeout=300)
    return path.exists() and path.stat().st_size > 0


def analyze(cli, path):
    """The encoder the analyzer names with its confidence, and whether every frame fails its CRC."""
    report = json.loads(subprocess.run([cli, "--json", str(path)], capture_output=True, text=True, check=True).stdout)[0]
    frames = sum(1 for region in report["root"]["children"] for node in region.get("children", []) if node["name"] == "Frame")
    failing = next((int(f["message"].split()[0]) for f in report["findings"] if f["code"] == "mpeg.crc-mismatch"), 0)
    for region in report["root"]["children"]:
        for node in region.get("children", []):
            if node["name"] == "Encoder":
                confidence = next(c["value"] for c in node["children"] if c["name"] == "Confidence")
                return (node["value"], confidence), failing == frames
    return (None, None), failing == frames


MP3PRO_URL = "https://www.rarewares.org/rrw/files/mpeg/mp3ProAudioPlayer11.exe"
MP3PRO_SHA256 = "4d81f5139d4b58f036981b9a063dea46e6a35c76785cf2cf6b1e5fd75bc40430"
MP3PRO_PLAYER_SHA256 = "6ea991e1b098a95da68ff4bd299b7db49b00025ae90c91bb21264681c5c3767f"
# Where to click, from each window's top left corner, at the player's fixed size.
PLAYER_ENCODE = (301, 125)
DIALOG_NO = (188, 108)
ENCODER_BROWSE = (430, 82)
ENCODER_OFL = (321, 262)
ENCODER_START = (52, 21)


def xdotool(*arguments, display):
    return subprocess.run(["xdotool", *arguments], capture_output=True, text=True, env=dict(os.environ, DISPLAY=display)).stdout


def window(name, display, timeout=30):
    for _ in range(timeout):
        found = xdotool("search", "--name", f"^{name}$", display=display).split()
        if found:
            return found[0]
        time.sleep(1)
    return None


def click(window_id, offset, display):
    geometry = dict(line.split("=") for line in xdotool("getwindowgeometry", "--shell", window_id, display=display).split())
    xdotool("mousemove", str(int(geometry["X"]) + offset[0]), str(int(geometry["Y"]) + offset[1]), "click", "1", display=display)
    time.sleep(2)


def mp3pro_encode(directory, source):
    """Installs the player's files without running its installer, and encodes source with the
    Original File Length option through its encoder window; returns the output file."""
    installer = directory / "mp3ProAudioPlayer11.exe"
    downloads.fetch([MP3PRO_URL], MP3PRO_SHA256, installer)
    unpacked = directory / "mp3pro"
    subprocess.run(["7z", "x", "-y", f"-o{unpacked}", str(installer)], capture_output=True, check=True)
    subprocess.run(["unshield", "-d", str(unpacked / "files"), "x", str(unpacked / "data1.cab")], capture_output=True, check=True)
    player = unpacked / "files" / "Program_Executable_Files" / "mp3PROplayer.exe"
    if downloads.sha256(player) != MP3PRO_PLAYER_SHA256:
        raise SystemExit(f"mp3PROplayer.exe has SHA-256 {downloads.sha256(player)}")
    display = ":71"
    server = subprocess.Popen(["Xvfb", display, "-screen", "0", "1024x768x24"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    environment = dict(os.environ, DISPLAY=display, WINEPREFIX=str(directory / "wine"), WINEDEBUG="-all")
    try:
        subprocess.Popen(["wine", str(player)], env=environment, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        # A new Wine prefix takes a while to start, and the player draws its skin after mapping
        # its windows, so clicks are repeated until they take.
        if dialog := window("Associate File Types", display, 120):
            click(dialog, DIALOG_NO, display)
        player = window("THOMSON mp3PRO Audio Player", display)
        encoder = None
        for _ in range(10):
            time.sleep(2)
            click(player, PLAYER_ENCODE, display)
            if encoder := window("THOMSON mp3PRO Encoder", display, 3):
                break
        if encoder is None or player is None:
            raise SystemExit("the mp3PRO player did not open its encoder")
        time.sleep(2)
        click(encoder, ENCODER_BROWSE, display)
        if window("Select Source File", display) is None:
            raise SystemExit("the mp3PRO encoder did not open its file dialog")
        xdotool("type", "--delay", "20", "Z:" + str(source).replace("/", "\\"), display=display)
        xdotool("key", "Return", display=display)
        time.sleep(3)
        click(encoder, ENCODER_OFL, display)
        click(encoder, ENCODER_START, display)
        output = source.with_suffix(".mp3")
        size = -1
        for _ in range(60):
            time.sleep(2)
            if output.exists() and output.stat().st_size == size:
                return output
            size = output.stat().st_size if output.exists() else -1
        raise SystemExit("the mp3PRO encoder wrote nothing")
    finally:
        subprocess.run(["wineserver", "-k"], env=environment, capture_output=True)
        server.terminate()


def check_mp3pro(cli, directory):
    source = directory / "mp3pro-music.wav"
    write_tone(source, 44100, 2, signal="music")
    path = mp3pro_encode(directory, source)
    report = json.loads(subprocess.run([cli, "--json", str(path)], capture_output=True, text=True, check=True).stdout)[0]
    region = next(r for r in report["root"]["children"] if r["name"] == "MPEG Audio")
    nodes = {n["name"]: n for n in region.get("children", [])}
    ofl = nodes.get("Original File Length")
    fields = {c["name"]: c["value"] for c in ofl["children"]} if ofl else {}
    expected = guessenc_ofl(path)
    good = (nodes.get("Enhancement", {}).get("value") == "mp3PRO" and ofl is not None and expected is not None
            and fields["Delay"] == f"{expected['delay']} samples" and fields["Original Length"] == f"{expected['length']} samples"
            and not any(f["code"] == "mpeg.ofl-exceeds-stream" for f in report["findings"]))
    print(f"mp3PRO Audio Player 1.1 with OFL on {source.stem}: enhancement {nodes.get('Enhancement', {}).get('value')}, "
          f"OFL {fields or None}, mp3guessenc OFL {expected}{'' if good else '  <- unexpected'}")
    return not good


def main():
    mp3pro = "--mp3pro" in sys.argv[1:]
    arguments = [a for a in sys.argv[1:] if a != "--mp3pro"]
    if len(arguments) != 1:
        sys.exit(__doc__)
    cli = arguments[0]
    guessenc_tool.binary()
    problems = 0
    with tempfile.TemporaryDirectory() as temp:
        directory = pathlib.Path(temp)
        binaries, libraries = fetch(directory)
        for signal, rate, channels, l3enc_arguments, mp3enc_arguments in CASES:
            source = directory / f"{signal}-{rate}-{channels}.wav"
            write_tone(source, rate, channels, signal=signal)
            for label, binary in binaries.items():
                arguments = l3enc_arguments if label.startswith("l3enc") else mp3enc_arguments
                if arguments is None:
                    continue
                path = directory / f"{label.replace(' ', '-')}-{source.stem}.mp3"
                case = f"{label} {' '.join(arguments) or '(defaults)'} on {source.stem}"
                if not encode(label, binary, libraries, source, path, arguments):
                    problems += 1
                    print(f"{case}: no output")
                    continue
                (ours, crc_failing), guess = analyze(cli, path), guessenc_guess(path)
                good = ours == ("Fraunhofer", "Low") and (crc_failing or guess is not None and guess.startswith("FhG"))
                problems += not good
                note = ", every frame fails its CRC" if crc_failing else ""
                print(f"{case}: analyzer {ours[0]} ({ours[1]}), mp3guessenc {guess}{note}{'' if good else '  <- unexpected'}")
        if mp3pro:
            problems += check_mp3pro(cli, directory)
    print(f"{problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
