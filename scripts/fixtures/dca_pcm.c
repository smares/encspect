/* Decodes a raw DTS core stream with libdca (0.0.7, Ubuntu's libdca-dev), through its public API only, and writes the samples as interleaved 32-bit floats in the stream's own channel
 * configuration (the flags the first frame states, DCA_ADJUST_LEVEL off, so no downmix; level 1 and bias 0, so the samples are libdca's own scale). The channels come in libdca's order,
 * which is printed on stderr as the number of channels and whether the LFE channel is the first (DCA_LFE). A frame libdca rejects is left out, and a "gap" line says so. Used by
 * crosscheck_dts_pcm.py.
 *
 *     cc -O2 -o dca_pcm dca_pcm.c -ldca && ./dca_pcm file.dts out.f32
 */
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dca.h>

static int channels_of(int flags)
{
    static const int count[] = {1, 2, 2, 2, 2, 3, 3, 4, 4, 5, 6};
    int mode = flags & DCA_CHANNEL_MASK;
    return (mode < 11 ? count[mode] : 0) + ((flags & DCA_LFE) ? 1 : 0);
}

int main(int argc, char **argv)
{
    if (argc != 3) {
        fprintf(stderr, "usage: dca_pcm file.dts out.f32\n");
        return 2;
    }
    FILE *file = fopen(argv[1], "rb");
    if (!file) {
        perror(argv[1]);
        return 2;
    }
    fseek(file, 0, SEEK_END);
    long size = ftell(file);
    fseek(file, 0, SEEK_SET);
    uint8_t *data = malloc(size + 4096);
    memset(data + size, 0, 4096);
    if (fread(data, 1, size, file) != (size_t)size) {
        return 2;
    }
    fclose(file);
    FILE *out = fopen(argv[2], "wb");
    dca_state_t *state = dca_init(0);
    uint8_t *frame = aligned_alloc(16, 32768);
    long offset = 0;
    int channels = 0;
    long frames = 0;
    while (offset + 14 <= size) {
        int flags, rate, bitrate, samples;
        int length = dca_syncinfo(state, data + offset, &flags, &rate, &bitrate, &samples);
        if (length == 0) {
            offset++;
            continue;
        }
        if (offset + length > size) {
            break;
        }
        memcpy(frame, data + offset, length);
        int request = flags & ~DCA_ADJUST_LEVEL;
        level_t level = 1;
        if (channels == 0) {
            channels = channels_of(flags);
            fprintf(stderr, "channels %d lfe_first %d flags %d rate %d\n", channels, (flags & DCA_LFE) != 0, flags, rate);
        }
        int ok = dca_frame(state, frame, &request, &level, 0) == 0 && channels_of(request) == channels;
        for (int block = 0; ok && block < dca_blocks_num(state); block++) {
            if (dca_block(state) != 0) {
                ok = 0;
                break;
            }
            sample_t *s = dca_samples(state);
            for (int i = 0; i < 256; i++) {
                for (int c = 0; c < channels; c++) {
                    float v = (float)s[c * 256 + i];
                    fwrite(&v, 4, 1, out);
                }
            }
        }
        if (!ok) {
            fprintf(stderr, "gap at %ld\n", offset);
        }
        frames++;
        offset += length;
    }
    fclose(out);
    fprintf(stderr, "frames %ld\n", frames);
    return 0;
}
