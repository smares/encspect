#!/usr/bin/env python3
"""Checks the analyzer's FAAC 2.x rule, the layout of the SBR headers and data that FAAC's HE-AAC encoder
writes, against encoders (see AacEncoder, FaacSbr and the encoder detection article):

- FAAC 2.0, 2.1 and 2.2 (faac_tool.py) encode HE-AAC v1 over every sample rate FAAC accepts for it (32000 to
  96000 Hz), mono and stereo, and bitrates from 16 to 128 kbps, as ADTS at every rate and as MP4 at 44.1 kHz only:
  the analyzer must name every file "FAAC" with medium confidence. The sweep is mono and stereo: the three
  releases also encode 5.1 at 44.1 kHz, where 2.2 is named and 2.0 and 2.1 are not, since they write one SBR
  payload typed as a channel pair element after the whole frame (channels.c, BuildFrame), which a 5.1 stream has
  no element for (aac.sbr-without-channel-element).
- fdkaac 1.0.0 with libfdk-aac 4.0.1 (fdk_tool.py), the only other HE-AAC encoder at hand, encodes HE-AAC (profile 5)
  and HE-AAC v2 (profile 29, stereo only) over FAAC's sample rates and the lower ones fdkaac accepts (FDK_RATES),
  mono and stereo, bitrates from 16 to 128 kbps and variable bitrate modes 1, 3 and 5, as ADTS and as MP4, and
  low complexity files of the same signals as ADTS: the analyzer must name none of its files FAAC. (Nero's,
  Apple's and Dolby's encoders were not at hand.) The MPEG-4 audio SBR conformance streams and Coding Technologies'
  aacPlus decoder check streams of FFmpeg's FATE suite, other encoders' HE-AAC and HE-AAC v2 files, are held
  against the rule by check_sbr_samples.py, which CI runs.
- Low complexity files of FAAC 2.0 to 2.2: no LC layout of 2.x is known, so they must not be named. fdkaac's must not be
  named FAAC either.

    python3 scripts/fixtures/check_faac_sbr.py [path of the encspect executable]

Takes about ten minutes; needs the tools of make_external_aac_fixtures.py. Not part of CI, which builds no encoders; the
fixtures it makes are the committed faac-2.*-he* ones. Prints a line per group and exits non-zero where a file is named
wrongly.
"""

import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

import faac_tool
import fdk_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
DEFAULT_ANALYZER = ROOT / "src" / "Encspect.Cli" / "bin" / "Release" / "net10.0" / "encspect"
FAAC_RATES = [32000, 44100, 48000, 64000, 88200, 96000]
FAAC_BITRATES = [16, 24, 32, 48, 64, 96, 128]
# Sample rates below FAAC's that only fdkaac is swept over; it is swept over FAAC_RATES too, and rejects the rates and bitrates it does not support.
FDK_RATES = [16000, 22050, 24000, 32000, 44100, 48000]
FDK_BITRATES = [16, 24, 32, 48, 64, 96, 128]


def write_signal(path, rate, channels):
    """Two seconds of pink noise with a 440 Hz tone, the second channel 2 dB quieter."""
    sources = ["-f", "lavfi", "-i", f"anoisesrc=d=2:c=pink:r={rate}:a=0.3:s=7", "-f", "lavfi", "-i", f"sine=f=440:d=2:r={rate}"]
    graph = "[0][1]amix=inputs=2:normalize=0" + (",asplit[a][b];[b]volume=0.8[c];[a][c]amerge" if channels == 2 else "")
    subprocess.run(["ffmpeg", "-v", "error", "-y", *sources, "-filter_complex", graph, "-ac", str(channels), "-c:a", "pcm_s16le", str(path)], check=True)


def write_surround(path, rate):
    """Two seconds of 5.1: pink noise in every channel, a different tone in each."""
    sources = [arg for frequency in (440, 660, 550, 330, 880, 110) for arg in ("-f", "lavfi", "-i", f"sine=f={frequency}:d=2:r={rate}")]
    sources += ["-f", "lavfi", "-i", f"anoisesrc=d=2:c=pink:r={rate}:a=0.2:s=7"]
    graph = "[6]asplit=6[n0][n1][n2][n3][n4][n5];" + "".join(f"[{i}][n{i}]amix=inputs=2:normalize=0[c{i}];" for i in range(6))
    graph += "".join(f"[c{i}]" for i in range(6)) + "amerge=inputs=6"
    subprocess.run(["ffmpeg", "-v", "error", "-y", *sources, "-filter_complex", graph, "-ac", "6", "-c:a", "pcm_s16le", str(path)], check=True)


def analyze(analyzer, directory):
    """{file name: (encoder, confidence)} of every file in directory."""
    result = subprocess.run([str(analyzer), "--ndjson", str(directory)], capture_output=True, text=True, check=True)
    named = {}
    for line in result.stdout.splitlines():
        report = json.loads(line)
        streams = report.get("streams") or [{}]
        named[pathlib.Path(report["source"]).name] = (streams[0].get("encoder"), streams[0].get("encoderConfidence"))
    return named


def report(group, named, expect):
    wrong = [f"{name}: {encoder} ({confidence})" for name, (encoder, confidence) in sorted(named.items()) if expect(encoder, confidence) is False]
    print(f"{group}: {len(named) - len(wrong)} of {len(named)} files as expected" + ("" if not wrong else "; " + "; ".join(wrong[:5])))
    return len(wrong)


def main():
    analyzer = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_ANALYZER
    failures = 0
    with tempfile.TemporaryDirectory(prefix="faac-sbr-") as temp:
        temp = pathlib.Path(temp)
        signals = {}
        for rate in sorted({*FAAC_RATES, *FDK_RATES}):
            for channels in (1, 2):
                signals[rate, channels] = temp / f"in-{rate}-{channels}.wav"
                write_signal(signals[rate, channels], rate, channels)

        write_surround(temp / "in-44100-6.wav", 44100)
        for version in ("2.0", "2.1", "2.2"):
            faac = faac_tool.build(version)
            out = temp / f"faac-{version}"
            out.mkdir()
            for (rate, channels), wav in signals.items():
                for bitrate in FAAC_BITRATES:
                    if rate not in FAAC_RATES:
                        continue
                    for extension in (".aac", ".m4a") if rate == 44100 else (".aac",):
                        target = out / f"he-{rate}-{channels}-{bitrate}{extension}"
                        subprocess.run([str(faac), "--object-type", "he-aac-v1", "-b", str(bitrate), "-o", str(target), str(wav)], check=True, capture_output=True)
            failures += report(f"FAAC {version} HE-AAC", analyze(analyzer, out), lambda e, c: (e or "").startswith("FAAC") and c == "Medium")
            surround = temp / f"faac-{version}-51"
            surround.mkdir()
            for extension in (".aac", ".m4a"):
                subprocess.run([str(faac), "--object-type", "he-aac-v1", "-b", "16", "-o", str(surround / f"he-51{extension}"), str(temp / "in-44100-6.wav")],
                               check=True, capture_output=True)
            lc = temp / f"faac-{version}-lc"
            lc.mkdir()
            for channels in (1, 2):
                subprocess.run([str(faac), "-o", str(lc / f"lc-{channels}.aac"), str(signals[44100, channels])], check=True, capture_output=True)
            # 2.x LC streams carry no text and no layout of their own: unnamed; the 1.x ones name themselves.
            failures += report(f"FAAC {version} 5.1 HE-AAC", analyze(analyzer, surround), lambda e, c: ((e or "").startswith("FAAC") and c == "Medium") == (version == "2.2"))
            failures += report(f"FAAC {version} LC", analyze(analyzer, lc), lambda e, c: (e is None) == version.startswith("2"))
            shutil.rmtree(faac.parent)

        fdkaac, library, checker = fdk_tool.build()
        out = temp / "fdkaac"
        out.mkdir()
        environment = {"LD_LIBRARY_PATH": str(library)}
        for (rate, channels), wav in signals.items():
            for bitrate in FDK_BITRATES:
                for profile in (5, 29):
                    if profile == 29 and channels == 1:
                        continue
                    for extension, flags in ((".aac", ["-f", "2"]), (".m4a", [])):
                        target = out / f"p{profile}-{rate}-{channels}-{bitrate}{extension}"
                        # Rates and bitrates the library rejects fail; the others are the sweep.
                        subprocess.run([str(fdkaac), "-S", "-p", str(profile), "-b", str(bitrate * 1000), *flags, "-o", str(target), str(wav)],
                                       capture_output=True, env=environment)
                for mode in (1, 3, 5):
                    target = out / f"vbr{mode}-{rate}-{channels}.aac"
                    subprocess.run([str(fdkaac), "-S", "-p", "5", "-m", str(mode), "-f", "2", "-o", str(target), str(wav)], capture_output=True, env=environment)
            lc = [str(fdkaac), "-S", "-p", "2", "-b", "128000", "-f", "2", "-o", str(out / f"lc-{rate}-{channels}.aac"), str(wav)]
            subprocess.run(lc, capture_output=True, env=environment)
        named = analyze(analyzer, out)
        failures += report("fdkaac HE-AAC and HE-AAC v2", {n: v for n, v in named.items() if not n.startswith("lc-")}, lambda e, c: (e or "").startswith("FAAC") is False)
        failures += report("fdkaac LC", {n: v for n, v in named.items() if n.startswith("lc-")}, lambda e, c: (e or "").startswith("FAAC") is False)
        shutil.rmtree(fdkaac.parents[1], ignore_errors=True)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
