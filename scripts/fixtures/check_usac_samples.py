#!/usr/bin/env python3
"""Checks tests/conformance/usac-samples.json against xHE-AAC (USAC) files that other encoders wrote.

The analyzer names exhale from the layout of a UsacConfig (AacEncoder.cs): an audio pre-roll element
first, then only channel elements with exhale's fixed settings and no fill element, and one loudness
info configuration extension. That only means something where other encoders lay it out differently,
which their own files show: four of Fraunhofer's demonstration files, eight ISO/IEC 23003-3
conformance streams in FFmpeg's test suite and one exhale file from that suite. They are downloaded into a cache directory and checked
against their SHA-256 rather than committed.

The manifest is ours. Per file it records who wrote it, the elements of its UsacDecoderConfig ("SCE",
"CPE", "LFE", "EXT" with its extension element type, "CPE+MPS212" for a channel pair element with MPEG
Surround 2-1-2 stereo), the types of its configuration extensions, the encoder and the writer with the confidence
the analyzer must report, and its warnings and errors ("findings").

Without --analyzer, the elements and extensions are read with a UsacConfig reader written here from
ISO/IEC 23003-3 5.2, apart from the analyzer, out of the DecoderSpecificInfo that MP4Box -diso
(GPAC 2.2) lists; Mps212Config() follows ffmpeg's and fdk-aac's reading of it. The reader must end
within the configuration's last byte. With --analyzer, the analyzer's report on every file (encspect
--ndjson) must show the same elements and extensions, the encoder, the writer and the findings.

Needs curl and MP4Box. Usage:

    python3 scripts/fixtures/check_usac_samples.py [cache directory]
    python3 scripts/fixtures/check_usac_samples.py --analyzer path/to/encspect [cache directory]
"""

import json
import pathlib
import re
import subprocess
import sys
import urllib.parse

import downloads
from conformance_mpeg_audio import child

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "usac-samples.json"


def addresses(sample):
    """The sample's own address, then the private repository's copy (the Wayback Machine could not be
    reached when the samples were pinned)."""
    return [sample["url"], downloads.private(f"usac/{urllib.parse.quote(sample['file'])}")]


def fetch(manifest, cache):
    cache.mkdir(parents=True, exist_ok=True)
    for sample in manifest["samples"]:
        downloads.fetch(addresses(sample), sample["sha256"], cache / sample["file"])


class Bits:
    def __init__(self, data):
        self.data = data
        self.position = 0

    def read(self, count):
        value = 0
        for _ in range(count):
            byte = self.data[self.position >> 3]
            value = (value << 1) | ((byte >> (7 - (self.position & 7))) & 1)
            self.position += 1
        return value

    def escaped(self, first, second, third):
        """escapedValue() (ISO/IEC 23003-3 5.2, Table 15)."""
        value = self.read(first)
        if value == (1 << first) - 1:
            more = self.read(second)
            value += more
            if more == (1 << second) - 1:
                value += self.read(third)
        return value


def usac_layout(config):
    """The elements and configuration extension types of an AudioSpecificConfig of object type 42."""
    bits = Bits(config)
    object_type = bits.read(5)
    if object_type == 31:
        object_type = 32 + bits.read(6)
    if object_type != 42:
        raise ValueError(f"audio object type {object_type}, not 42")
    if bits.read(4) == 15:
        bits.read(24)
    bits.read(4)
    # UsacConfig (Table 13).
    if bits.read(5) == 31:
        bits.read(24)
    sbr_ratio = {0: 0, 1: 0, 2: 2, 3: 3, 4: 1}[bits.read(3)]
    if bits.read(5) == 0:
        for _ in range(bits.escaped(5, 8, 16)):
            bits.read(5)
    elements = []
    # UsacDecoderConfig (Table 16) with the element configurations of Tables 17, 18 and 22.
    for _ in range(bits.escaped(4, 8, 16) + 1):
        kind = bits.read(2)
        if kind in (0, 1):
            bits.read(2)  # tw_mdct, noiseFilling
            if sbr_ratio:
                bits.read(3)  # harmonicSBR, bs_interTes, bs_pvc
                bits.read(8)  # dflt_start_freq, dflt_stop_freq
                extra1, extra2 = bits.read(1), bits.read(1)
                bits.read(5 if extra1 else 0)
                bits.read(6 if extra2 else 0)
                if kind == 1:
                    stereo_config_index = bits.read(2)
                    if stereo_config_index:
                        mps212_config(bits, stereo_config_index)
                        elements.append("CPE+MPS212")
                        continue
            elements.append("SCE" if kind == 0 else "CPE")
        elif kind == 2:
            elements.append("LFE")
        else:
            extension = bits.escaped(4, 8, 16)
            length = bits.escaped(4, 8, 16)
            if bits.read(1):
                bits.escaped(8, 16, 0)
            bits.read(1)
            # UsacExtElementConfig() reads no configuration for a fill or audio pre-roll element.
            if extension not in (0, 3):
                bits.read(8 * length)
            elements.append(f"EXT{extension}")
    extensions = []
    if bits.read(1):
        for _ in range(bits.escaped(2, 4, 8) + 1):
            extensions.append(bits.escaped(4, 8, 16))
            bits.read(8 * bits.escaped(4, 8, 16))
    if not 0 <= len(config) * 8 - bits.position < 8:
        raise ValueError(f"the UsacConfig ends at bit {bits.position} of the configuration's {len(config) * 8}")
    return elements, extensions


def mps212_config(bits, stereo_config_index):
    """Mps212Config() (ISO/IEC 23003-3 5.2), as ffmpeg's decode_usac_element_pair and fdk-aac's
    SpatialDecParseMps212Config read it."""
    bits.read(3)  # bsFreqRes
    bits.read(3)  # bsFixedGainDMX
    shaping = bits.read(2)  # bsTempShapeConfig
    bits.read(2)  # bsDecorrConfig
    bits.read(1)  # bsHighRateMode
    bits.read(1)  # bsPhaseCoding
    if bits.read(1):  # bsOttBandsPhasePresent
        bits.read(5)
    if stereo_config_index > 1:
        bits.read(5)  # bsResidualBands
        bits.read(1)  # bsPseudoLr
    if shaping == 2:
        bits.read(1)  # bsEnvQuantMode


def mp4box_config(path):
    """The DecoderSpecificInfo of the file's first track, as MP4Box -diso lists it."""
    dump = subprocess.run(["MP4Box", "-std", "-diso", str(path)], capture_output=True, check=True).stdout.decode("latin-1")
    found = re.search(r'<DecoderSpecificInfo[^>]*src="data:application/octet-string,([^"]*)"', dump)
    if not found:
        raise ValueError("MP4Box lists no DecoderSpecificInfo")
    return urllib.parse.unquote_to_bytes(found.group(1))


def tool_problems(path, sample):
    elements, extensions = usac_layout(mp4box_config(path))
    problems = []
    if elements != sample["elements"]:
        problems.append(f"the UsacConfig that MP4Box lists holds the elements {elements}")
    if extensions != sample["config_extensions"]:
        problems.append(f"the UsacConfig that MP4Box lists holds the configuration extensions {extensions}")
    return problems


def analyzer_layout(report):
    """The elements and configuration extension types the analyzer's UsacConfig node shows."""
    config = next((n for n in walk_all(report["root"]) if n["name"] == "UsacConfig"), None)
    if config is None:
        return None, None
    elements = []
    decoder = child(config, "UsacDecoderConfig")
    for element in (decoder or {}).get("children", []):
        if element["name"] != "Element":
            continue
        fields = {c["name"]: c.get("value") for c in element.get("children", [])}
        if element["value"] == "EXT":
            elements.append(f"EXT{str(fields['Extension Element Type']).split()[0]}")
        elif fields.get("Stereo Config Index", 0):
            elements.append("CPE+MPS212")
        else:
            elements.append(element["value"])
    extensions = child(config, "UsacConfigExtension")
    types = [int(str(c["value"]).split()[0]) for c in (extensions or {}).get("children", []) if c["name"] == "Extension Type"]
    return elements, types


def walk_all(node):
    yield node
    for c in node.get("children", []):
        yield from walk_all(c)


def analyzer_problems(analyzer, path, sample):
    report = json.loads(subprocess.run([analyzer, "--ndjson", str(path)], capture_output=True, text=True, check=True).stdout)
    problems = []
    if report.get("schemaVersion") != 1:
        problems.append(f"the analyzer writes report schema {report.get('schemaVersion')}, this script reads 1")
    elements, extensions = analyzer_layout(report)
    if (elements, extensions) != (sample["elements"], sample["config_extensions"]):
        problems.append(f"the analyzer shows the elements {elements} and configuration extensions {extensions}")
    streams = report.get("streams", [])
    encoder = [streams[0]["encoder"], streams[0].get("encoderConfidence")] if streams and streams[0].get("encoder") else None
    if encoder != sample["analyzer_encoder"]:
        problems.append(f"the analyzer names the encoder {encoder}, the manifest {sample['analyzer_encoder']}")
    writer = [streams[0]["writer"], streams[0].get("writerConfidence")] if streams and streams[0].get("writer") else None
    if writer != sample["analyzer_writer"]:
        problems.append(f"the analyzer names the writer {writer}, the manifest {sample['analyzer_writer']}")
    serious = sorted(f["code"] for f in report["findings"] if f["severity"] != "Info")
    if serious != sample.get("findings", []):
        problems.append(f"the analyzer reports {serious}")
    return problems


def main():
    arguments = sys.argv[1:]
    analyzer = None
    if arguments[:1] == ["--analyzer"]:
        analyzer, arguments = arguments[1], arguments[2:]
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "usac")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if not manifest["samples"]:
        raise SystemExit(f"{MANIFEST} lists no samples")
    fetch(manifest, cache)
    problems = 0
    for sample in manifest["samples"]:
        path = cache / sample["file"]
        found = analyzer_problems(analyzer, path, sample) if analyzer else tool_problems(path, sample)
        for problem in found:
            problems += 1
            print(f"{sample['file']}: {problem}")
    what = "Analyzed" if analyzer else "Checked"
    print(f"{what} {len(manifest['samples'])} USAC samples, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
