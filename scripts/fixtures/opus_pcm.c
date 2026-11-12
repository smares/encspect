/*
 * Decodes an Opus stream with libopus 1.5.2 (BSD-3-Clause, built without OSCE, deep PLC and DRED, which are off by default) to 32-bit floats, full scale 1.0, as the reference of
 * the PCM and range coder cross-check of the analyzer's decoder (scripts/fixtures/crosscheck_opus_pcm.py). Two inputs:
 *
 *     opus_pcm ogg INPUT.opus OUTPUT.f32     the first Opus stream of an Ogg file; libogg splits the file into packets, each is given to
 *                                            opus_multistream_decode_float alone (no trimming, no pre-skip: the output is at the coded stage), with the header's output gain
 *     opus_pcm bit CHANNELS INPUT.bit OUTPUT.f32   the opus_demo files of RFC 6716's and RFC 8251's test vectors: per packet a big-endian length and the encoder's final range
 *
 * One JSON object per packet on standard output: its number, its size, the samples per channel libopus returned (negative for an error code) and the final range coder state
 * (the exclusive or of the streams' states for a multistream packet; for the bit files also the state the encoder stored). A last line states the channels.
 *
 *     cc -O1 -ffp-contract=off -o opus_pcm opus_pcm.c -lopus -logg -lm
 *
 * The libopus it links is the one the caller built from the pinned release; the script scripts/fixtures/opus_tool.py builds it.
 */
#include <ogg/ogg.h>
#include <opus/opus_multistream.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MAX_FRAME 5760

static float *buffer;

static int emit(OpusMSDecoder *decoder, FILE *out, long number, const unsigned char *data, long bytes, int channels, long stored)
{
    int samples = opus_multistream_decode_float(decoder, bytes > 0 ? data : NULL, (opus_int32)bytes, buffer, MAX_FRAME, 0);
    opus_uint32 range = 0;
    opus_multistream_decoder_ctl(decoder, OPUS_GET_FINAL_RANGE(&range));
    if (samples > 0)
    {
        fwrite(buffer, sizeof(float), (size_t)samples * channels, out);
    }
    if (stored >= 0)
    {
        printf("{\"packet\":%ld,\"bytes\":%ld,\"samples\":%d,\"range\":%u,\"stored\":%ld}\n", number, bytes, samples, range, stored);
    }
    else
    {
        printf("{\"packet\":%ld,\"bytes\":%ld,\"samples\":%d,\"range\":%u}\n", number, bytes, samples, range);
    }
    return samples;
}

static int run_bit(int channels, const char *path, FILE *out)
{
    FILE *in = fopen(path, "rb");
    if (!in)
    {
        return 2;
    }
    int error = 0;
    OpusMSDecoder *decoder;
    unsigned char mapping[2] = {0, 1};
    decoder = opus_multistream_decoder_create(48000, channels, 1, channels == 2 ? 1 : 0, mapping, &error);
    if (!decoder)
    {
        fprintf(stderr, "cannot create the decoder: %d\n", error);
        return 2;
    }
    buffer = malloc(sizeof(float) * MAX_FRAME * channels);
    unsigned char header[8];
    unsigned char *data = malloc(1 << 16);
    long number = 0;
    while (fread(header, 1, 8, in) == 8)
    {
        long bytes = ((long)header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        long stored = ((long)header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
        if (bytes > (1 << 16) || fread(data, 1, bytes, in) != (size_t)bytes)
        {
            break;
        }
        emit(decoder, out, number++, data, bytes, channels, stored);
    }
    printf("{\"channels\":%d}\n", channels);
    return 0;
}

static int run_ogg(const char *path, FILE *out)
{
    FILE *in = fopen(path, "rb");
    if (!in)
    {
        return 2;
    }
    ogg_sync_state sync;
    ogg_stream_state stream;
    ogg_page page;
    ogg_packet packet;
    int streaming = 0, headers = 0, channels = 0;
    long number = 0;
    OpusMSDecoder *decoder = NULL;
    ogg_sync_init(&sync);
    for (;;)
    {
        char *chunk = ogg_sync_buffer(&sync, 4096);
        size_t read = fread(chunk, 1, 4096, in);
        ogg_sync_wrote(&sync, (long)read);
        while (ogg_sync_pageout(&sync, &page) == 1)
        {
            if (!streaming)
            {
                ogg_stream_init(&stream, ogg_page_serialno(&page));
                streaming = 1;
            }
            else if (ogg_page_serialno(&page) != stream.serialno)
            {
                continue;
            }
            ogg_stream_pagein(&stream, &page);
            for (;;)
            {
                int got = ogg_stream_packetout(&stream, &packet);
                if (got == 0)
                {
                    break;
                }
                if (got < 0)
                {
                    printf("{\"packet\":%ld,\"status\":\"hole\"}\n", number);
                    continue;
                }
                if (headers == 0)
                {
                    const unsigned char *h = packet.packet;
                    if (packet.bytes < 19 || memcmp(h, "OpusHead", 8) != 0)
                    {
                        fprintf(stderr, "no OpusHead\n");
                        return 1;
                    }
                    channels = h[9];
                    int gain = (short)(h[16] | (h[17] << 8));
                    int family = h[18];
                    int streams = 1, coupled = channels == 2 ? 1 : 0;
                    unsigned char mapping[255] = {0, 1};
                    if (family != 0)
                    {
                        if (packet.bytes < 21 + channels)
                        {
                            fprintf(stderr, "short OpusHead\n");
                            return 1;
                        }
                        streams = h[19];
                        coupled = h[20];
                        memcpy(mapping, h + 21, channels);
                    }
                    int error = 0;
                    decoder = opus_multistream_decoder_create(48000, channels, streams, coupled, mapping, &error);
                    if (!decoder)
                    {
                        fprintf(stderr, "libopus does not accept the stream: %d\n", error);
                        return 1;
                    }
                    opus_multistream_decoder_ctl(decoder, OPUS_SET_GAIN(gain));
                    buffer = malloc(sizeof(float) * MAX_FRAME * channels);
                    headers = 1;
                    printf("{\"packet\":%ld,\"bytes\":%ld,\"samples\":0}\n", number++, packet.bytes);
                    continue;
                }
                if (headers == 1)
                {
                    headers = 2;
                    printf("{\"packet\":%ld,\"bytes\":%ld,\"samples\":0}\n", number++, packet.bytes);
                    continue;
                }
                emit(decoder, out, number++, packet.packet, packet.bytes, channels, -1);
            }
        }
        if (read == 0)
        {
            break;
        }
    }
    if (!decoder)
    {
        fprintf(stderr, "no Opus stream\n");
        return 1;
    }
    printf("{\"channels\":%d}\n", channels);
    return 0;
}

int main(int argc, char **argv)
{
    if (argc == 4 && strcmp(argv[1], "ogg") == 0)
    {
        FILE *out = fopen(argv[3], "wb");
        return out ? run_ogg(argv[2], out) : 2;
    }
    if (argc == 5 && strcmp(argv[1], "bit") == 0)
    {
        FILE *out = fopen(argv[4], "wb");
        return out ? run_bit(atoi(argv[2]), argv[3], out) : 2;
    }
    fprintf(stderr, "usage: opus_pcm ogg INPUT.opus OUTPUT.f32 | opus_pcm bit CHANNELS INPUT.bit OUTPUT.f32\n");
    return 2;
}
