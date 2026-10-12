/* Prints, for every AC-3 frame of a raw file, where liba52 (0.7.4, Ubuntu's liba52-0.7.4-dev) ends
 * each audio block: the bit position in the frame after a52_block() has read the block's mantissas.
 * The position comes from the decoder state's bitstream fields, which a52.h declares. Frames liba52
 * rejects print "rejected". Used by crosscheck_ac3_fixtures.py.
 *
 *     cc -O2 -o a52_blocks a52_blocks.c -la52 -lm && ./a52_blocks file.ac3
 */
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <a52dec/a52.h>

static long position(a52_state_t *state, uint8_t *frame)
{
    return ((long)((uint8_t *)state->buffer_start - frame)) * 8 - (long)state->bits_left;
}

int main(int argc, char **argv)
{
    if (argc != 2) {
        fprintf(stderr, "usage: a52_blocks file.ac3\n");
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
    if (fread(data, 1, size, file) != (size_t)size) {
        return 2;
    }
    fclose(file);
    a52_state_t *state = a52_init(0);
    /* An aligned copy of each frame, so the bitstream's word reads start at the frame. */
    uint8_t *frame = aligned_alloc(16, 8192);
    long offset = 0;
    while (offset + 7 <= size) {
        int flags, rate, bitrate;
        int length = a52_syncinfo(data + offset, &flags, &rate, &bitrate);
        if (length == 0) {
            offset++;
            continue;
        }
        if (offset + length > size) {
            break;
        }
        memset(frame, 0, 8192);
        memcpy(frame, data + offset, length);
        sample_t level = 1, bias = 0;
        flags |= A52_ADJUST_LEVEL;
        printf("%ld %d", offset, length);
        if (a52_frame(state, frame, &flags, &level, bias)) {
            printf(" rejected\n");
        } else {
            for (int block = 0; block < 6; block++) {
                if (a52_block(state)) {
                    printf(" error");
                    break;
                }
                printf(" %ld", position(state, frame));
            }
            printf("\n");
        }
        offset += length;
    }
    a52_free(state);
    return 0;
}
