"""Independent reference model of Encspect's treatment of Matroska ContentEncodings.

Sources: RFC 9559 5.1.4.1.31 to 5.1.4.1.31.12 (Tables 21-27), section 14, the project's
docs/formats/matroska.md and Topics/matroska-content-encryption.md, plus the policy
decisions given with the task. Written without reading the C# implementation.

Rules
 1. Defaults (RFC 5.1.4.1.31.2/.3/.4/.6/.9): order 0, scope 1, type 0, ContentCompAlgo 0
    (zlib), ContentEncAlgo 0 (not encrypted, Table 24). None means the element is absent.
 2. Ties: if two encodings share an order (after defaulting), nothing is trusted: frames_read
    and codec_private_read are False. Protected (scheme "Matroska ContentEncryption",
    is_encrypted True) iff any encoding has type 1 (policy; a ContentEncryption under type 0
    is ignored, RFC 5.1.4.1.31.8). Otherwise not protected.
 3. Without ties, encodings are processed from the highest order down (RFC .2).
 4. Scope is a set of bits (Table 21): 1 = frames, 2 = CodecPrivate, 4 = next encoding.
    Other bits are ignored; scope 0 covers nothing. The CodecPrivate counts as covered only
    if the track has one.
 5. Hiding: an encoding with bit 4 makes the next lower-order encoding "hidden"; hiding
    chains through consecutive bit-4 encodings: the encoding right below a bit-4 encoding is
    hidden (hidden[j] = scope[j-1] & 4), so a hidden encoding with bit 4 hides the next one.
    A hidden encoding's settings (algorithm, compression algorithm) are not read; its own
    scope and type (outside ContentCompression/ContentEncryption) still are. The root of a
    run is the nearest visible encoding above it.
 6. Visible type 1 with a ContentEncryption algo != 0: the covered parts are encrypted.
    Algo 0 (explicit or default): clear, no effect. Type 1 with no ContentEncryption element:
    encrypted by an unknown algorithm (bare scheme).
 7. Visible type 0: ContentCompAlgo 0 (zlib, also the default and the value when the
    ContentCompression element is missing) or 3 (header stripping, with or without settings)
    is undone for any codec. Any other algorithm: the covered parts are not read (not
    encrypted). Types the RFC does not define (2 or higher): not read, not protected.
 8. Hidden encoding E under root R, with P the hidden encodings between them (checked in
    this order):
      a. any encoding of P has type 1: E's parts are encrypted by an unknown algorithm;
      b. else R a real encryption (rule 6): E's parts are encrypted, labeled as R. This
         names R's algorithm even for frames that R's own scope does not cover. Table 21
         says bit 4 concerns the next encoding's settings, not the frames; the scheme
         names the encryption involved, here the one whose ciphertext hides the settings
         needed to read the frames;
      c. else (R compression or algo 0): E type 1 -> encrypted, unknown algorithm; E type 0
         -> not encrypted.
    In every case E's parts are not read.
 9. Protected iff some part (frames, or an existing CodecPrivate) ends up encrypted. An
    encryption that covers nothing itself and hides only encodings that cover nothing gives
    no protection.
10. is_encrypted is True iff the frames are encrypted (CodecPrivate only: False). Not
    protected: scheme and is_encrypted are None.
11. Scheme is that of the first effective encryption in processing order (the outermost):
    "Matroska ContentEncryption" + ", " + name: 1 DES, 2 3DES, 3 Twofish, 4 Blowfish,
    5 AES; AES with AESSettings and cipher mode 1 / 2 is AES-CTR / AES-CBC, with any other
    mode "AES, mode N", without AESSettings or mode "AES"; other values "algorithm N". The
    bare scheme only when no algorithm can be trusted (hidden, or no ContentEncryption).
    The AES settings are ignored for algorithms other than AES.
12. frames_read: frames neither encrypted nor unreadable; and for A_OPUS/A_VORBIS also the
    existing CodecPrivate neither encrypted nor unreadable (they need it for setup).
13. codec_private_read: has_codec_private and the CodecPrivate neither encrypted nor
    unreadable.
"""

SCHEME = "Matroska ContentEncryption"
_ALGO_NAMES = {1: "DES", 2: "3DES", 3: "Twofish", 4: "Blowfish"}
_AES_MODES = {1: "AES-CTR", 2: "AES-CBC"}
_OPAQUE_CODECS = ("A_OPUS", "A_VORBIS")
_UNKNOWN = object()  # label of an unknown algorithm


def _scheme(label):
    return SCHEME if label is _UNKNOWN or not label else SCHEME + ", " + label


def _norm(e):
    return {
        "order": 0 if e.get("order") is None else e["order"],
        "scope": 1 if e.get("scope") is None else e["scope"],
        "type": 0 if e.get("type") is None else e["type"],
        "compression": e.get("compression"),
        "encryption": e.get("encryption"),
    }


def _parts(scope, has_cp):
    return bool(scope & 1), bool(scope & 2) and has_cp


def _enc_label(enc):
    """Label of a real encryption: None for algo 0, a name, or _UNKNOWN."""
    if enc is None:
        return _UNKNOWN  # type 1 without ContentEncryption: cannot say it is clear
    algo = 0 if enc.get("algo") is None else enc["algo"]
    if algo == 0:
        return None
    if algo in _ALGO_NAMES:
        return _ALGO_NAMES[algo]
    if algo == 5:
        mode = enc.get("aes_mode")
        if enc.get("has_aes_settings") and mode is not None:
            return _AES_MODES.get(mode, "AES, mode %d" % mode)
        return "AES"
    return "algorithm %d" % algo


def _undoable(e):
    c = e["compression"]
    algo = 0 if c is None or c.get("algo") is None else c["algo"]
    return algo in (0, 3)


def expected(track):
    codec = track["codec"]
    has_cp = bool(track["has_codec_private"])
    encs = [_norm(e) for e in track.get("encodings", [])]
    orders = [e["order"] for e in encs]
    if len(set(orders)) != len(orders):
        prot = any(e["type"] == 1 for e in encs)
        return {"protected": prot, "scheme": SCHEME if prot else None,
                "is_encrypted": True if prot else None,
                "frames_read": False, "codec_private_read": False}

    encs.sort(key=lambda e: -e["order"])
    # per part: [encrypted, unreadable]
    st = {"f": [False, False], "c": [False, False]}
    events = []  # labels of effective encryptions, outermost first

    def apply(e, encrypted, label, unreadable):
        f, c = _parts(e["scope"], has_cp)
        for on, key in ((f, "f"), (c, "c")):
            if on:
                if encrypted:
                    st[key][0] = True
                if unreadable:
                    st[key][1] = True
        if encrypted and (f or c):
            events.append(label)

    root = None
    root_label = None  # label if root is a real encryption
    hidden_enc_seen = False
    for j, e in enumerate(encs):
        hidden = j > 0 and bool(encs[j - 1]["scope"] & 4)
        if not hidden:
            root, hidden_enc_seen = e, False
            root_label = None
            if e["type"] == 1:
                lab = _enc_label(e["encryption"])
                if lab is not None:
                    root_label = lab
                    apply(e, True, lab, False)
            elif e["type"] == 0:
                if not _undoable(e):
                    apply(e, False, None, True)
            else:
                apply(e, False, None, True)
        else:
            if hidden_enc_seen:
                lab, enc = _UNKNOWN, True
            elif root_label is not None:
                lab, enc = root_label, True
            elif e["type"] == 1:
                lab, enc = _UNKNOWN, True
            else:
                lab, enc = None, False
            apply(e, enc, lab, True)
            if e["type"] == 1:
                hidden_enc_seen = True

    protected = bool(events)
    frames_enc, frames_bad = st["f"]
    cp_enc, cp_bad = st["c"]
    frames_read = not frames_enc and not frames_bad
    if codec in _OPAQUE_CODECS and (cp_enc or cp_bad):
        frames_read = False
    return {
        "protected": protected,
        "scheme": _scheme(events[0]) if protected else None,
        "is_encrypted": frames_enc if protected else None,
        "frames_read": frames_read,
        "codec_private_read": has_cp and not cp_enc and not cp_bad,
    }
