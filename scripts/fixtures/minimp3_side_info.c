/*
 * Dumps the Layer III side information of every frame minimp3 decodes, one JSON object per line,
 * as an independent reference for the analyzer's side-info parser and bit reservoir.
 *
 * minimp3 (github.com/lieff/minimp3, CC0) is included with its implementation, so its own
 * static side-info reader, L3_read_side_info, and its decoder state are used as they are.
 * crosscheck_mpeg_fixtures.py downloads the pinned commit, checks its SHA-256 and builds this:
 *
 *     cc -O1 -o minimp3_side_info minimp3_side_info.c -lm
 *
 * Per frame: the file offset, the bytes minimp3 has in its bit reservoir before the frame
 * (capped at 511), main_data_begin (-1 when minimp3 rejects the side information), and for
 * each granule and channel the fields minimp3 keeps as read. It rewrites a few fields, so those
 * are left out where they do not hold the coded value: region counts and table_select[2] with
 * window switching, subblock gains without it, preflag outside MPEG-1, and scfsi, which it only
 * holds for granule 1 of MPEG-1 streams without short blocks.
 */
#define MINIMP3_IMPLEMENTATION
#include "minimp3.h"
#include <stdio.h>

static void dump_side_info(const uint8_t *hdr, int frame_bytes, int reservoir, long offset)
{
    bs_t bs;
    L3_gr_info_t gr[4];
    int channels = HDR_IS_MONO(hdr) ? 1 : 2;
    int granules = HDR_TEST_MPEG1(hdr) ? 2 : 1;
    int i, has_short = 0, main_data_begin;

    bs_init(&bs, hdr + HDR_SIZE, frame_bytes - HDR_SIZE);
    if (HDR_IS_CRC(hdr))
        get_bits(&bs, 16);
    main_data_begin = L3_read_side_info(&bs, gr, hdr);

    printf("{\"offset\":%ld,\"reservoir\":%d,\"main_data_begin\":%d", offset, reservoir, main_data_begin);
    if (main_data_begin >= 0)
    {
        for (i = 0; i < granules * channels; i++)
            has_short |= gr[i].block_type == SHORT_BLOCK_TYPE;
        printf(",\"granules\":[");
        for (i = 0; i < granules * channels; i++)
        {
            const L3_gr_info_t *g = &gr[i];
            int switched = g->block_type != 0;
            printf("%s{\"part2_3_length\":%d,\"big_values\":%d,\"global_gain\":%d,\"scalefac_compress\":%d,"
                   "\"block_type\":%d,\"scalefac_scale\":%d,\"count1_table\":%d,\"table_select\":[%d,%d",
                   i ? "," : "", g->part_23_length, g->big_values, g->global_gain, g->scalefac_compress,
                   g->block_type, g->scalefac_scale, g->count1_table, g->table_select[0], g->table_select[1]);
            if (switched)
                printf("],\"mixed_block_flag\":%d,\"subblock_gain\":[%d,%d,%d]", g->mixed_block_flag,
                       g->subblock_gain[0], g->subblock_gain[1], g->subblock_gain[2]);
            else
                printf(",%d],\"region0_count\":%d,\"region1_count\":%d", g->table_select[2], g->region_count[0],
                       g->region_count[1]);
            if (HDR_TEST_MPEG1(hdr))
                printf(",\"preflag\":%d", g->preflag);
            if (HDR_TEST_MPEG1(hdr) && !has_short && i >= channels)
                printf(",\"scfsi\":%d", g->scfsi);
            printf("}");
        }
        printf("]");
    }
    printf("}\n");
}

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
        fprintf(stderr, "usage: minimp3_side_info file.mp3\n");
        return 2;
    }
    size = (long)fread(data, 1, sizeof(data), file);
    fclose(file);

    memset(&dec, 0, sizeof(dec));
    while (position < size)
    {
        const uint8_t *next = data + position;
        long remaining = size - position;
        /* The same test mp3dec_decode_frame makes: unless the frame continues the stream, the
           decoder resets and its reservoir is empty. */
        int continues = dec.header[0] == 0xFF && remaining > 4 && hdr_compare(dec.header, next);
        if (continues)
        {
            int frame_size = hdr_frame_bytes(next, dec.free_format_bytes) + hdr_padding(next);
            continues = frame_size == remaining || (frame_size + HDR_SIZE <= remaining && hdr_compare(next, next + frame_size));
        }
        int reservoir = continues ? dec.reserv : 0;

        info.layer = 0;
        mp3dec_decode_frame(&dec, next, (int)remaining, pcm, &info);
        if (!info.frame_bytes)
            break;
        if (info.layer == 3)
            dump_side_info(next + info.frame_offset, info.frame_bytes - info.frame_offset, reservoir,
                           position + info.frame_offset);
        position += info.frame_bytes;
    }
    return 0;
}
