/* The main program of a52_trace_tool.py: runs liba52 0.7.4, built from its original source with the trace patch, over the AC-3 frames of a
 * raw file and prints, on standard output, a line "@@FRAME <offset> <length> <acmod>" for every frame (followed by " rejected" when
 * a52_frame refuses it) and, from the patched a52_block(), a line "@@BLK ..." for every audio block it decodes. Frames are read in order;
 * a block liba52 cannot decode ends the frame with "@@ERROR".
 *
 *     cc -O2 -I<source>/include -o a52_trace a52_trace.c <source>/liba52/*.c -lm
 */
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "a52.h"

int main(int argc, char **argv)
{
    if (argc != 2) {
        fprintf(stderr, "usage: a52_trace file.ac3\n");
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
        printf("@@FRAME %ld %d %d", offset, length, (flags & A52_CHANNEL_MASK));
        if (a52_frame(state, frame, &flags, &level, bias)) {
            printf(" rejected\n");
        } else {
            printf("\n");
            for (int block = 0; block < 6; block++) {
                if (a52_block(state)) {
                    printf("@@ERROR\n");
                    break;
                }
            }
        }
        offset += length;
    }
    a52_free(state);
    return 0;
}
