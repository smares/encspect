/*
 * Prints, for every Layer III frame minimp3 decodes, the end of the coded lines of each granule and channel as minimp3's own
 * Huffman loop leaves them, with the scalefactor band widths it used: an independent reference for the analyzer's "Coded Bandwidth".
 * minimp3 (CC0) is included with its implementation after minimp3_coded.py has patched a copy of its header: L3_huffman records where
 * its loop ends (the line after the last quadruple it decoded, and two more where the loop stops between a quadruple's pairs, the
 * convention the analyzer's walk follows), the block type and the band table. No other line of minimp3 changes.
 *
 *     cc -O1 -o minimp3_coded minimp3_coded.c -lm
 */
#define MINIMP3_IMPLEMENTATION
#include "minimp3.h"
#include <stdio.h>

int main(int argc, char **argv)
{
    static uint8_t data[64 << 20];
    static mp3d_sample_t pcm[MINIMP3_MAX_SAMPLES_PER_FRAME];
    mp3dec_t dec;
    mp3dec_frame_info_t info = {0};
    FILE *file;
    long size, position = 0;

    if (argc != 2 || !(file = fopen(argv[1], "rb")))
    {
        fprintf(stderr, "usage: minimp3_coded file.mp3\n");
        return 2;
    }
    size = (long)fread(data, 1, sizeof(data), file);
    fclose(file);

    memset(&dec, 0, sizeof(dec));
    while (position < size)
    {
        g_coded_n = 0;
        info.layer = 0;
        mp3dec_decode_frame(&dec, data + position, (int)(size - position), pcm, &info);
        if (!info.frame_bytes)
            break;
        if (info.layer == 3)
        {
            int i, k;
            printf("{\"offset\":%ld,\"hz\":%d,\"granules\":[", position + info.frame_offset, info.hz);
            for (i = 0; i < g_coded_n; i++)
            {
                printf("%s{\"end\":%d,\"block_type\":%d,\"long_bands\":%d,\"widths\":[", i ? "," : "", g_coded[i].end, g_coded[i].block_type, g_coded[i].n_long_sfb);
                for (k = 0; g_coded[i].sfbtab[k]; k++)
                    printf("%s%d", k ? "," : "", g_coded[i].sfbtab[k]);
                printf("]}");
            }
            printf("]}\n");
        }
        position += info.frame_bytes;
    }
    return 0;
}
