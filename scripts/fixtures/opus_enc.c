/*
 * Encodes a 16-bit PCM WAV file (44-byte header, as make_external_vorbis_opus_fixtures.py writes it) to an Ogg Opus file with libopus 1.5.2's encoder (BSD-3-Clause),
 * for the fixtures whose coded syntax the other encoders do not write: stereo SILK frames of 40 and 60 ms with in-band forward error correction, and the like.
 *
 *     opus_enc INPUT.wav OUTPUT.opus SERIAL application bitrate frame_ms fec bandwidth mode
 *
 *     application: voip, audio or lowdelay; frame_ms: 5, 10, 20, 40 or 60; fec: 0 or 1 (with an expected loss of 20%);
 *     bandwidth: nb, mb, wb, swb or fb (the highest the encoder may use); mode: auto, silk, hybrid or celt (what the encoder is forced to).
 *
 *     cc -O1 -ffp-contract=off -o opus_enc opus_enc.c -lopus -logg -lm
 */
#include <ogg/ogg.h>
#include <opus/opus.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define OPUS_SET_FORCE_MODE_REQUEST 11002

static void put32(unsigned char *p, uint32_t v) { p[0] = v; p[1] = v >> 8; p[2] = v >> 16; p[3] = v >> 24; }

static void write_packet(ogg_stream_state *os, FILE *out, unsigned char *data, long bytes, ogg_int64_t granule, long number, int eos, int flush)
{
    ogg_packet op = {data, bytes, number == 0, eos, granule, number};
    ogg_page og;
    ogg_stream_packetin(os, &op);
    while ((flush ? ogg_stream_flush(os, &og) : ogg_stream_pageout(os, &og)) > 0)
    {
        fwrite(og.header, 1, og.header_len, out);
        fwrite(og.body, 1, og.body_len, out);
    }
}

int main(int argc, char **argv)
{
    if (argc != 10)
    {
        fprintf(stderr, "usage: opus_enc INPUT.wav OUTPUT.opus SERIAL application bitrate frame_ms fec bandwidth mode\n");
        return 2;
    }
    FILE *in = fopen(argv[1], "rb");
    FILE *out = fopen(argv[2], "wb");
    if (!in || !out) return 2;
    unsigned char wav[44];
    if (fread(wav, 1, 44, in) != 44) return 2;
    int channels = wav[22] | (wav[23] << 8);
    int rate = wav[24] | (wav[25] << 8) | (wav[26] << 16) | ((int)wav[27] << 24);
    int application = !strcmp(argv[4], "voip") ? OPUS_APPLICATION_VOIP : !strcmp(argv[4], "lowdelay") ? OPUS_APPLICATION_RESTRICTED_LOWDELAY : OPUS_APPLICATION_AUDIO;
    int bitrate = atoi(argv[5]);
    int frame_ms10 = (int)(atof(argv[6]) * 10 + 0.5);
    int frame = rate / 1000 * frame_ms10 / 10;
    int bandwidth = !strcmp(argv[8], "nb") ? OPUS_BANDWIDTH_NARROWBAND : !strcmp(argv[8], "mb") ? OPUS_BANDWIDTH_MEDIUMBAND : !strcmp(argv[8], "wb") ? OPUS_BANDWIDTH_WIDEBAND
        : !strcmp(argv[8], "swb") ? OPUS_BANDWIDTH_SUPERWIDEBAND : OPUS_BANDWIDTH_FULLBAND;
    int error = 0;
    OpusEncoder *encoder = opus_encoder_create(rate, channels, application, &error);
    if (!encoder) return 2;
    opus_encoder_ctl(encoder, OPUS_SET_BITRATE(bitrate));
    opus_encoder_ctl(encoder, OPUS_SET_INBAND_FEC(atoi(argv[7])));
    opus_encoder_ctl(encoder, OPUS_SET_PACKET_LOSS_PERC(atoi(argv[7]) ? 20 : 0));
    opus_encoder_ctl(encoder, OPUS_SET_MAX_BANDWIDTH(bandwidth));
    opus_encoder_ctl(encoder, OPUS_SET_BANDWIDTH(bandwidth));
    if (!strcmp(argv[9], "silk")) opus_encoder_ctl(encoder, OPUS_SET_FORCE_MODE_REQUEST, (opus_int32)1000);
    else if (!strcmp(argv[9], "hybrid")) opus_encoder_ctl(encoder, OPUS_SET_FORCE_MODE_REQUEST, (opus_int32)1001);
    else if (!strcmp(argv[9], "celt")) opus_encoder_ctl(encoder, OPUS_SET_FORCE_MODE_REQUEST, (opus_int32)1002);
    opus_int32 lookahead = 0;
    opus_encoder_ctl(encoder, OPUS_GET_LOOKAHEAD(&lookahead));
    int preskip = (int)((long)lookahead * 48000 / rate);
    long samples = 0, capacity = 1 << 20;
    int16_t *pcm = malloc(capacity * sizeof(int16_t) * channels);
    int16_t chunk[4096];
    size_t got;
    while ((got = fread(chunk, sizeof(int16_t), 4096, in)) > 0)
    {
        if ((samples + 4096) * channels > capacity * channels) { capacity *= 2; pcm = realloc(pcm, capacity * sizeof(int16_t) * channels); }
        memcpy(pcm + samples * channels, chunk, got * sizeof(int16_t));
        samples += got / channels;
    }
    long padded = ((samples + lookahead + frame - 1) / frame) * frame;
    pcm = realloc(pcm, (padded + frame) * sizeof(int16_t) * channels);
    memset(pcm + samples * channels, 0, (padded - samples) * sizeof(int16_t) * channels);

    ogg_stream_state os;
    ogg_stream_init(&os, atoi(argv[3]));
    unsigned char head[19] = "OpusHead";
    head[8] = 1; head[9] = channels; head[10] = preskip & 0xFF; head[11] = preskip >> 8;
    put32(head + 12, rate); head[16] = head[17] = 0; head[18] = 0;
    write_packet(&os, out, head, 19, 0, 0, 0, 1);
    const char *vendor = opus_get_version_string();
    unsigned char tags[256];
    memcpy(tags, "OpusTags", 8);
    put32(tags + 8, (uint32_t)strlen(vendor));
    memcpy(tags + 12, vendor, strlen(vendor));
    put32(tags + 12 + strlen(vendor), 0);
    write_packet(&os, out, tags, 16 + (long)strlen(vendor), 0, 1, 0, 1);
    unsigned char packet[4000];
    long number = 2;
    for (long at = 0; at < padded; at += frame)
    {
        int bytes = opus_encode(encoder, pcm + at * channels, frame, packet, sizeof packet);
        if (bytes < 0) return 1;
        int last = at + frame >= padded;
        ogg_int64_t granule = (ogg_int64_t)(at + frame) * 48000 / rate;
        if (last) granule = (ogg_int64_t)samples * 48000 / rate + preskip;
        write_packet(&os, out, packet, bytes, granule, number++, last, last);
    }
    fclose(out);
    return 0;
}
