"""Tests of the reference model of Matroska ContentEncodings by hand-worked cases: python3 scripts/ci/test_model.py"""
import os
import sys
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures"))
from matroska_encodings_model import expected

def enc(order=0, scope=1, type=0, comp=None, crypt=None):
    return {"order": order, "scope": scope, "type": type, "compression": comp, "encryption": crypt}
def zlib(): return {"algo": 0, "has_settings": False}
def strip(): return {"algo": 3, "has_settings": True}
def aes(mode=1, settings=True):
    return {"algo": 5, "aes_mode": mode, "has_aes_settings": settings, "has_key_id": False}
def alg(a): return {"algo": a, "aes_mode": None, "has_aes_settings": False, "has_key_id": False}
def tr(encs, codec="A_OPUS", cp=True):
    return expected({"codec": codec, "has_codec_private": cp, "encodings": encs})

S = "Matroska ContentEncryption"
CASES = []
def case(f): CASES.append(f); return f

@case
def no_encodings():  # RFC 5.1.4.1.31: ContentEncodings optional
    assert tr([]) == dict(protected=False, scheme=None, is_encrypted=None, frames_read=True, codec_private_read=True)
@case
def no_codec_private():  # no CodecPrivate, nothing to read
    assert tr([], cp=False)["codec_private_read"] is False
@case
def all_defaults_missing_compression_is_zlib():  # RFC .6 default 0 (decision 13)
    r = tr([enc(None, None, None, comp=None)]); assert r["frames_read"] is True
@case
def zlib_opus():  # docs: zlib undone for Opus
    r = tr([enc(comp=zlib())]); assert r["frames_read"] and r["codec_private_read"] and not r["protected"]
@case
def zlib_algo_default_is_zlib():  # RFC .6 default 0
    r = tr([enc(comp={"algo": None, "has_settings": False})]); assert r["frames_read"]
@case
def strip_vorbis():  # Table 23 value 3, undone for Vorbis
    assert tr([enc(comp=strip())], "A_VORBIS")["frames_read"]
@case
def zlib_aac_undone():  # decision 12: undo does not depend on the codec
    r = tr([enc(comp=zlib())], "A_AAC"); assert r["frames_read"] and r["codec_private_read"] and not r["protected"]
@case
def strip_pcm_undone():  # decision 11/12
    assert tr([enc(comp=strip())], "A_PCM/INT/LIT")["frames_read"]
@case
def strip_without_settings_undoable():  # decision 14
    assert tr([enc(comp={"algo": 3, "has_settings": False})], "A_AAC")["frames_read"]
@case
def zlib_on_aac_codec_private_undone():  # decision 12
    r = tr([enc(scope=3, comp=zlib())], "A_AAC"); assert r["frames_read"] and r["codec_private_read"]
@case
def bzlib_aac_codec_private_not_read():
    r = tr([enc(scope=2, comp={"algo": 1, "has_settings": False})], "A_AAC"); assert r["frames_read"] and not r["codec_private_read"]
@case
def bzlib_not_read():  # docs: bzlib not undone
    assert not tr([enc(comp={"algo": 1, "has_settings": False})])["frames_read"]
@case
def lzo_not_read():
    assert not tr([enc(comp={"algo": 2, "has_settings": False})])["frames_read"]
@case
def unknown_comp_algo_not_read():  # Table 54 allows 4+
    assert not tr([enc(comp={"algo": 9, "has_settings": False})])["frames_read"]
@case
def cp_only_compression_opus():  # scope 2 zlib: frames clear, cp undone
    r = tr([enc(scope=2, comp=zlib())]); assert r["frames_read"] and r["codec_private_read"]
@case
def cp_only_bzlib_opus_blocks_frames():  # Opus needs CodecPrivate for setup
    r = tr([enc(scope=2, comp={"algo": 1, "has_settings": False})]); assert not r["frames_read"] and not r["codec_private_read"]
@case
def aes_ctr():  # docs scheme example
    r = tr([enc(type=1, crypt=aes(1))]); assert r == dict(protected=True, scheme=S+", AES-CTR", is_encrypted=True, frames_read=False, codec_private_read=True)
@case
def aes_cbc():
    assert tr([enc(type=1, crypt=aes(2))])["scheme"] == S+", AES-CBC"
@case
def aes_unknown_mode():  # decision 4
    assert tr([enc(type=1, crypt=aes(3))])["scheme"] == S+", AES, mode 3"
    assert tr([enc(type=1, crypt=aes(7))])["scheme"] == S+", AES, mode 7"
@case
def aes_no_settings():  # decision 4
    assert tr([enc(type=1, crypt=aes(None, False))])["scheme"] == S+", AES"
    assert tr([enc(type=1, crypt=aes(None, True))])["scheme"] == S+", AES"
@case
def des_3des_names():  # Table 24
    assert tr([enc(type=1, crypt=alg(2))])["scheme"] == S+", 3DES"
    assert tr([enc(type=1, crypt=alg(1))])["scheme"] == S+", DES"
    assert tr([enc(type=1, crypt=alg(3))])["scheme"] == S+", Twofish"
    assert tr([enc(type=1, crypt=alg(4))])["scheme"] == S+", Blowfish"
@case
def unknown_algo_by_number():  # decision 3
    assert tr([enc(type=1, crypt=alg(6))])["scheme"] == S+", algorithm 6"
    assert tr([enc(type=1, crypt=alg(200))])["scheme"] == S+", algorithm 200"
@case
def cipher_mode_ignored_for_3des():  # Table 25/27
    c = alg(2); c.update(aes_mode=1, has_aes_settings=True)
    assert tr([enc(type=1, crypt=c)])["scheme"] == S+", 3DES"
@case
def algo_zero_clear():  # Table 24: not encrypted
    r = tr([enc(type=1, crypt=alg(0))]); assert not r["protected"] and r["frames_read"]
@case
def algo_default_clear():  # .9 default 0
    r = tr([enc(type=1, crypt=alg(None))]); assert not r["protected"] and r["frames_read"]
@case
def type1_without_encryption_element():  # modeled conservatively as unknown algorithm
    r = tr([enc(type=1, crypt=None)]); assert r["scheme"] == S and r["is_encrypted"] and not r["frames_read"]
@case
def encryption_under_type0_ignored():  # RFC .8 MUST be ignored
    r = tr([enc(type=0, comp=zlib(), crypt=aes())]); assert not r["protected"] and r["frames_read"]
@case
def cp_only_encryption():  # docs: protected, samples not encrypted; Opus frames not analyzed
    r = tr([enc(scope=2, type=1, crypt=aes(1))]); assert r == dict(protected=True, scheme=S+", AES-CTR", is_encrypted=False, frames_read=False, codec_private_read=False)
@case
def cp_only_encryption_aac_frames_clear():  # docs: other codecs routed without configuration
    r = tr([enc(scope=2, type=1, crypt=aes(1))], "A_AAC"); assert r["frames_read"] and not r["codec_private_read"] and r["is_encrypted"] is False
@case
def scope2_without_codec_private_nothing_to_hide():  # decision 10
    r = tr([enc(scope=2, type=1, crypt=aes(1))], "A_AAC", cp=False)
    assert r == dict(protected=False, scheme=None, is_encrypted=None, frames_read=True, codec_private_read=False)
    r = tr([enc(scope=2, type=1, crypt=aes(1))], "A_OPUS", cp=False); assert r["frames_read"] and not r["protected"]
@case
def scope3_without_codec_private():  # decision 10: frames still encrypted
    r = tr([enc(scope=3, type=1, crypt=aes(1))], "A_AAC", cp=False); assert r["protected"] and r["is_encrypted"]
@case
def scope3_both():
    r = tr([enc(scope=3, type=1, crypt=aes(1))], "A_AAC"); assert r["is_encrypted"] and not r["frames_read"] and not r["codec_private_read"]
@case
def covers_nothing():  # docs: encryption-covers-nothing, no protection
    r = tr([enc(scope=4, type=1, crypt=aes(1))]); assert not r["protected"] and r["frames_read"]
@case
def compress_over_encrypt_chain():  # orders: zlib 1 over AES 0; encrypted frames never read
    r = tr([enc(order=0, type=1, crypt=aes(2)), enc(order=1, comp=zlib())]); assert r["scheme"] == S+", AES-CBC" and not r["frames_read"]
@case
def order_independent_of_file_order():
    a = [enc(order=1, type=1, crypt=alg(2)), enc(order=0, type=1, crypt=aes(1))]
    assert tr(a) == tr(a[::-1]) and tr(a)["scheme"] == S+", 3DES"  # outermost = highest order
@case
def hidden_encryption_under_encryption():  # docs: parts counted as encrypted by hiding encryption
    r = tr([enc(order=1, scope=4, type=1, crypt=alg(2)), enc(order=0, scope=1, type=1, crypt=aes(1))])
    assert r["scheme"] == S+", 3DES" and r["is_encrypted"] and not r["frames_read"]
@case
def hidden_compression_under_encryption_cp():
    r = tr([enc(order=1, scope=4, type=1, crypt=aes(1)), enc(order=0, scope=2, comp=zlib())])
    assert r["protected"] and r["scheme"] == S+", AES-CTR" and r["is_encrypted"] is False and r["frames_read"] is False
@case
def hidden_encryption_under_compression():  # docs: unknown algorithm
    r = tr([enc(order=1, scope=4, comp=zlib()), enc(order=0, scope=1, type=1, crypt=aes(1))])
    assert r["protected"] and r["scheme"] == S and r["is_encrypted"] and not r["frames_read"]
@case
def hidden_compression_under_compression():  # not read, not protected
    r = tr([enc(order=1, scope=4, comp=zlib()), enc(order=0, scope=1, comp=zlib())])
    assert not r["protected"] and not r["frames_read"]
@case
def run_of_bit4_hides_all():  # docs: every encoding after the first of a run
    r = tr([enc(order=2, scope=5, type=1, crypt=alg(2)), enc(order=1, scope=4, comp=zlib()), enc(order=0, scope=2, comp=zlib())], "A_AAC")
    assert r["scheme"] == S+", 3DES" and r["is_encrypted"] and not r["codec_private_read"]
@case
def after_hidden_encryption_unknown():  # docs: encoding after an encryption hidden in chain
    r = tr([enc(order=2, scope=4, type=1, crypt=alg(2)), enc(order=1, scope=4, type=1, crypt=alg(1)), enc(order=0, scope=2, comp=zlib())], "A_AAC")
    assert r["protected"] and not r["codec_private_read"] and r["is_encrypted"] is False
@case
def tie_with_encryption():  # policy
    r = tr([enc(order=0, type=1, crypt=aes(1)), enc(order=0, comp=zlib())]); assert r == dict(protected=True, scheme=S, is_encrypted=True, frames_read=False, codec_private_read=False)
@case
def tie_defaults_absent_orders():  # two absent orders default to 0 and tie
    r = tr([enc(order=None, comp=zlib()), enc(order=0, comp=zlib())]); assert r == dict(protected=False, scheme=None, is_encrypted=None, frames_read=False, codec_private_read=False)
@case
def tie_ignores_type0_encryption_element():  # policy: iff type 1
    r = tr([enc(order=3, type=0, comp=zlib(), crypt=aes()), enc(order=3, type=0, comp=zlib())]); assert not r["protected"]
@case
def tie_type1_algo0_still_protected():  # policy: any type 1
    assert tr([enc(order=1, type=1, crypt=alg(0)), enc(order=1, comp=zlib())])["protected"]
@case
def unknown_type_not_read():  # docs silent; conservative
    r = tr([enc(type=2)]); assert not r["frames_read"] and not r["protected"]
@case
def unknown_scope_bits_ignored():  # decision 7
    r = tr([enc(scope=8, type=1, crypt=aes(1))]); assert not r["protected"] and r["frames_read"] and r["codec_private_read"]
    r = tr([enc(scope=9, type=1, crypt=aes(1))]); assert r["is_encrypted"] and r["codec_private_read"]
@case
def scope_zero_covers_nothing():  # decision 7
    r = tr([enc(scope=0, type=1, crypt=aes(1))]); assert not r["protected"] and r["frames_read"] and r["codec_private_read"]
    r = tr([enc(scope=0, comp={"algo": 1, "has_settings": False})]); assert r["frames_read"]
@case
def hiding_encryption_over_nothing_not_protected():  # decision 9
    r = tr([enc(order=1, scope=4, type=1, crypt=aes(1)), enc(order=0, scope=0, comp=zlib())])
    assert not r["protected"] and r["frames_read"]
    r = tr([enc(order=1, scope=4, type=1, crypt=aes(1)), enc(order=0, scope=2, comp=zlib())], cp=False)
    assert not r["protected"] and r["frames_read"]
@case
def hidden_encryption_algo_hidden_names_by_root():  # decision 8
    r = tr([enc(order=1, scope=4, type=1, crypt=alg(6)), enc(order=0, scope=1, type=1, crypt=alg(2))])
    assert r["scheme"] == S+", algorithm 6"
@case
def undefined_type_before_encryption():  # decision 6
    r = tr([enc(order=1, type=2), enc(order=0, type=1, crypt=aes(1))]); assert r["is_encrypted"] and r["scheme"] == S+", AES-CTR"
@case
def missing_encryption_element_hidden_label():  # decision 5
    r = tr([enc(order=1, scope=3, type=1, crypt=None)], "A_AAC"); assert r["scheme"] == S and r["is_encrypted"]

if __name__ == "__main__":
    for f in CASES:
        f()
    print("ok", len(CASES), "cases")
