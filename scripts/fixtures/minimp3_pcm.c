/*
 * Decodes an MPEG audio file with minimp3 (github.com/lieff/minimp3, CC0) to 32-bit floats, full scale 1.0, as the reference of the PCM cross-check of the analyzer's
 * decoder (scripts/fixtures/crosscheck_mpeg_pcm.py): it follows the loop of minimp3's own example, so the frames it finds are the ones minimp3 decodes.
 *
 *     cc -O1 -ffp-contract=off -I third_party/minimp3 -o minimp3_pcm minimp3_pcm.c -lm
 *     minimp3_pcm INPUT OUTPUT.f32   (one JSON object per frame on standard output)
 *
 * OUTPUT is the samples of every frame it decodes, interleaved, in file order. Per frame the offset of the frame in the file, its length, the samples per
 * channel it decoded (0 for a frame it skipped: no main data), the channels, the layer and the sample rate.
 */
#define MINIMP3_IMPLEMENTATION
#define MINIMP3_FLOAT_OUTPUT
#include "minimp3.h"
#include <stdio.h>

int main(int argc, char **argv)
{
    if (argc != 3)
    {
        fprintf(stderr, "usage: minimp3_pcm INPUT OUTPUT.f32\n");
        return 2;
    }
    FILE *in = fopen(argv[1], "rb");
    FILE *out = fopen(argv[2], "wb");
    if (!in || !out)
    {
        fprintf(stderr, "cannot open the files\n");
        return 2;
    }
    fseek(in, 0, SEEK_END);
    long size = ftell(in);
    fseek(in, 0, SEEK_SET);
    uint8_t *data = (uint8_t *)malloc(size > 0 ? size : 1);
    if (fread(data, 1, size, in) != (size_t)size)
    {
        fprintf(stderr, "cannot read the input\n");
        return 2;
    }

    static mp3dec_t dec;
    static float pcm[MINIMP3_MAX_SAMPLES_PER_FRAME];
    mp3dec_frame_info_t info;
    mp3dec_init(&dec);
    long position = 0;
    while (position < size)
    {
        int samples = mp3dec_decode_frame(&dec, data + position, (int)(size - position), pcm, &info);
        if (info.frame_bytes == 0)
            break;
        printf("{\"offset\":%ld,\"bytes\":%d,\"samples\":%d,\"channels\":%d,\"layer\":%d,\"hz\":%d}\n", position + info.frame_offset, info.frame_bytes - info.frame_offset,
               samples, info.channels, info.layer, info.hz);
        if (samples)
            fwrite(pcm, sizeof(float), (size_t)samples * info.channels, out);
        position += info.frame_bytes;
    }
    fclose(out);
    return 0;
}
