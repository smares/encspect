/*
 * Decodes the first Vorbis stream of an Ogg file with libvorbis (the system's, 1.3.7) to 32-bit floats, full scale 1.0, as the reference of the PCM cross-check of the
 * analyzer's decoder (scripts/fixtures/crosscheck_vorbis_pcm.py). libogg splits the file into pages and packets; each audio packet is given to libvorbis alone, with
 * granulepos -1 and e_o_s 0 as the official-decoder harness does (scripts/fixtures/ogg_decode.c, "direct"), so that libvorbis trims nothing: the output is at the coded
 * stage, and its first block returns nothing, as the analyzer's.
 *
 *     cc -O1 -ffp-contract=off -o vorbis_pcm vorbis_pcm.c -lvorbis -logg -lm
 *     vorbis_pcm INPUT.ogg OUTPUT.f32   (one JSON object per packet on standard output)
 *
 * OUTPUT is the samples every packet returns, interleaved, in file order. Per packet its number in the stream (the three headers are 0 to 2), its size, what libvorbis said
 * of it (the return of vorbis_synthesis, 0 for audio it decoded; "hole" for a gap libogg reports) and the samples per channel it returned. A packet libvorbis refuses
 * returns nothing and changes nothing, as the analyzer reads it (the packet is not a block).
 */
#include <ogg/ogg.h>
#include <vorbis/codec.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main(int argc, char **argv)
{
    if (argc != 3)
    {
        fprintf(stderr, "usage: vorbis_pcm INPUT.ogg OUTPUT.f32\n");
        return 2;
    }
    FILE *in = fopen(argv[1], "rb");
    FILE *out = fopen(argv[2], "wb");
    if (!in || !out)
    {
        fprintf(stderr, "cannot open the files\n");
        return 2;
    }
    ogg_sync_state sync;
    ogg_stream_state stream;
    ogg_page page;
    ogg_packet packet;
    vorbis_info info;
    vorbis_comment comment;
    vorbis_dsp_state dsp;
    vorbis_block block;
    int streaming = 0, headers = 0, ready = 0;
    long number = 0;
    ogg_sync_init(&sync);
    vorbis_info_init(&info);
    vorbis_comment_init(&comment);
    for (;;)
    {
        char *buffer = ogg_sync_buffer(&sync, 4096);
        size_t read = fread(buffer, 1, 4096, in);
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
                if (headers < 3)
                {
                    int status = vorbis_synthesis_headerin(&info, &comment, &packet);
                    printf("{\"packet\":%ld,\"bytes\":%ld,\"status\":%d,\"samples\":0}\n", number++, packet.bytes, status);
                    if (status < 0)
                    {
                        return 1;
                    }
                    if (++headers == 3)
                    {
                        if (vorbis_synthesis_init(&dsp, &info) != 0 || vorbis_block_init(&dsp, &block) != 0)
                        {
                            fprintf(stderr, "libvorbis does not accept the stream\n");
                            return 1;
                        }
                        ready = 1;
                    }
                    continue;
                }
                packet.granulepos = -1;
                packet.e_o_s = 0;
                int status = vorbis_synthesis(&block, &packet);
                long returned = 0;
                if (status == 0)
                {
                    vorbis_synthesis_blockin(&dsp, &block);
                    float **pcm;
                    int count;
                    while ((count = vorbis_synthesis_pcmout(&dsp, &pcm)) > 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            for (int c = 0; c < info.channels; c++)
                            {
                                fwrite(&pcm[c][i], sizeof(float), 1, out);
                            }
                        }
                        returned += count;
                        vorbis_synthesis_read(&dsp, count);
                    }
                }
                printf("{\"packet\":%ld,\"bytes\":%ld,\"status\":%d,\"samples\":%ld}\n", number++, packet.bytes, status, returned);
                fflush(stdout);
            }
        }
        if (read == 0)
        {
            break;
        }
    }
    if (!ready)
    {
        fprintf(stderr, "no Vorbis stream\n");
        return 1;
    }
    printf("{\"channels\":%d,\"rate\":%ld}\n", info.channels, info.rate);
    return 0;
}
