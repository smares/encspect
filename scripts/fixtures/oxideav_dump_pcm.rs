//! Decodes an E-AC-3 file access unit by access unit and writes the interleaved S16LE PCM: dump_pcm <in> <out.raw>
use oxideav_ac3::eac3::{Eac3DecoderState, decode_eac3_packet};
use std::io::Write;

fn main() {
    let path = std::env::args().nth(1).unwrap();
    let out = std::env::args().nth(2).unwrap();
    let data = std::fs::read(&path).unwrap();
    // frames
    let mut frames = Vec::new();
    let mut off = 0usize;
    while off + 6 <= data.len() {
        if data[off] != 0x0B || data[off + 1] != 0x77 { off += 1; continue; }
        let frmsiz = (((data[off + 2] & 0x07) as u32) << 8) | data[off + 3] as u32;
        let flen = ((frmsiz + 1) * 2) as usize;
        if off + flen > data.len() { break; }
        let strmtyp = (data[off + 2] >> 6) & 3;
        frames.push((off, flen, strmtyp));
        off += flen;
    }
    // units: an independent frame (strmtyp 0 or 2) with the dependent frames (1) after it
    let mut units: Vec<(usize, usize)> = Vec::new();
    for &(o, l, t) in &frames {
        if t == 1 && !units.is_empty() { units.last_mut().unwrap().1 = o + l; } else { units.push((o, o + l)); }
    }
    let mut state = Eac3DecoderState::default();
    let mut file = std::fs::File::create(&out).unwrap();
    let mut channels = 0; let mut rate = 0; let mut ok = 0; let mut bad = 0;
    for (s, e) in units {
        match decode_eac3_packet(&mut state, &data[s..e]) {
            Ok(f) => { channels = f.channels; rate = f.sample_rate; ok += 1; file.write_all(&f.pcm_s16le).unwrap(); }
            Err(err) => { bad += 1; if bad < 4 { eprintln!("err {err}"); } }
        }
    }
    println!("units ok={ok} bad={bad} channels={channels} rate={rate}");
}
