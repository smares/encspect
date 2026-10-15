"""EMDF containers and their object audio payloads for the E-AC-3 fixtures, written as ETSI TS 102 366
V1.4.1 Annex H (the container, H.2.1.2 to H.2.1.4) and ETSI TS 103 420 V1.2.1 lay them out:

- the container: emdf_sync (0x5838 and the length of what follows), emdf_version, key_id, payloads
  each with emdf_payload_id, emdf_payload_config and emdf_payload_size, an end marker (payload ID 0)
  and emdf_protection;
- payload 14, joint object coding: joc_header (6.2.2) and the first fields of joc_info (6.2.3), then
  filler for the per-object parameters, which no fixture codes;
- payload 11, object audio metadata: oa_md_version_bits and object_count_bits (5.5.2), then filler.

The bodies after the fields the analyzer reads are zero bytes: the fixtures check the container and
the header fields, not decodable objects, and a decoder that reads them as parameters finds none.
"""

from mp4_boxes import Bits

SYNC = 0x5838
OAMD = 11
JOC = 14


def variable_bits(b, value, n):
    """variable_bits(n) of H.2.1.2.1: groups of n bits, each followed by read_more."""
    groups = 1
    offset = 0
    while value >= offset + (1 << (n * groups)):
        offset += 1 << (n * groups)
        groups += 1
    encoded = value - offset
    for g in range(groups - 1, -1, -1):
        b.put((encoded >> (g * n)) & ((1 << n) - 1), n)
        b.put(1 if g else 0, 1)


def payload_config(b, offset=None, duration=None, group=None, codecdata=None, discard=0, aligned=1, create=0, remove=0, priority=0, proc=0):
    """emdf_payload_config of H.2.1.3. The default is TS 103 420 Table 56 as Dolby's own file writes it:
    no duration, no codec data (the table lists codecdatae as 1, H.2.2.3.7 and the file as 0), the
    payload kept, frame aligned, priority 0; container() adds a group ID."""
    b.put(offset is not None, 1)
    if offset is not None:
        b.put(offset, 11).put(0, 1)
    b.put(duration is not None, 1)
    if duration is not None:
        variable_bits(b, duration, 11)
    b.put(group is not None, 1)
    if group is not None:
        variable_bits(b, group, 2)
    b.put(codecdata is not None, 1)
    if codecdata is not None:
        b.put(codecdata, 8)
    b.put(discard, 1)
    if not discard:
        frame_aligned = 0
        if offset is None:
            frame_aligned = aligned
            b.put(aligned, 1)
            if aligned:
                b.put(create, 1).put(remove, 1)
        if offset is not None or frame_aligned:
            b.put(priority, 5).put(proc, 2)


def joc_payload(objects=15, dmx=1, ext=0, clip=(3, 12), sequence=1, size=96, header_only=False):
    """joc_header and the first joc_info fields (objects = joc_num_objects), zero filler to size bytes."""
    b = Bits().put(dmx, 3).put(objects - 1, 6).put(ext, 3)
    if not header_only:
        b.put(clip[0], 3).put(clip[1], 5).put(sequence, 10)
    data = b.align().bytes()
    return data + bytes(max(0, size - len(data)))


def oamd_payload(objects=15, version=0, size=40, lfe=False):
    """oa_md_version_bits and object_count_bits (objects = object_count), then a program_assignment
    of dynamic objects only (5.5.3: b_dyn_object_only_program 1, b_lfe_present lfe); with an LFE
    channel one of the objects is that channel, as in Dolby's own file (16 objects, 15 dynamic);
    zero filler to size bytes."""
    b = Bits()
    b.put(min(version, 3), 2)
    if version >= 3:
        b.put(version - 3, 3)
    if objects - 1 >= 31:
        b.put(31, 5).put(objects - 1 - 31, 7)
    else:
        b.put(objects - 1, 5)
    b.put(1, 1).put(1 if lfe else 0, 1)
    data = b.align().bytes()
    return data + bytes(max(0, size - len(data)))


def container(payloads, version=0, key=0, protection=(1, 0), protection_value=0xA5, group=0, config=None, length=None, pad_bits=0, end_marker=True,
              trailing=b""):
    """An emdf_sync and emdf_container. payloads: (id, bytes) pairs, each written with config (default
    payload_config with groupid group; a dict of payload_config's options or a per-payload list).
    protection: (protection_length_primary, protection_length_secondary) codes of H.2.2.4.
    length overrides the emdf_container_length; pad_bits sets the unused bits of the last byte;
    end_marker False leaves out the payload ID 0; trailing is appended after the container's bytes
    (inside the length unless length says otherwise)."""
    b = Bits()
    b.put(min(version, 3), 2)
    if version >= 3:
        variable_bits(b, version - 3, 2)
    b.put(min(key, 7), 3)
    if key >= 7:
        variable_bits(b, key - 7, 3)
    for index, (pid, data) in enumerate(payloads):
        if pid >= 31:
            b.put(31, 5)
            variable_bits(b, pid - 31, 5)
        else:
            b.put(pid, 5)
        options = config[index] if isinstance(config, list) else config
        payload_config(b, **(options if options is not None else {"group": group}))
        variable_bits(b, len(data), 8)
        for byte in data:
            b.put(byte, 8)
    if end_marker:
        b.put(0, 5)
    primary, secondary = protection
    b.put(primary, 2).put(secondary, 2)
    for code in (primary, secondary):
        bits = {0: 0, 1: 8, 2: 32, 3: 128}[code]
        for i in range(bits // 8):
            b.put(protection_value ^ i, 8)
    if b.count % 8:
        b.put(pad_bits & ((1 << (8 - b.count % 8)) - 1), 8 - b.count % 8)
    body = b.bytes() + trailing
    size = len(body) if length is None else length
    return SYNC.to_bytes(2, "big") + size.to_bytes(2, "big") + body


def object_audio(objects=15, dmx=1, size=96, **options):
    """What an E-AC-3 stream with object audio carries: an OAMD and a JOC payload in one container."""
    return container([(OAMD, oamd_payload(objects)), (JOC, joc_payload(objects, dmx, size=size))], **options)


def atmos_container():
    """The container of Dolby's JOC test file (DD+ Online Delivery Kit v1.4.1): an OAMD payload of 53 bytes
    for 16 objects with an LFE channel, a JOC payload of 320 bytes for 15 objects with the 90 degree
    phase shifted 5.X downmix (joc_dmx_config_idx 3), clip gain bits 4 and 0, protection of 32 and 8 bits."""
    return container([(OAMD, oamd_payload(16, size=53, lfe=True)), (JOC, joc_payload(15, dmx=3, clip=(4, 0), size=320))], protection=(2, 1))


def addbsi(complexity, flag=1):
    """The two bytes of additional bit stream information of TS 103 420 8.3.1: seven reserved bits,
    flag_ec3_extension_type_a, complexity_index_type_a."""
    return bytes([flag & 1, complexity])


def cut(data, keep):
    """The container with its body cut to keep bytes and emdf_container_length set to them."""
    return data[:2] + keep.to_bytes(2, "big") + data[4:4 + keep]
