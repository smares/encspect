/* Decodes an ADTS file with fdk-aac's decoder and prints each access unit's result, so the CRC verdicts
   (AAC_DEC_CRC_ERROR) of a decoder that checks ADTS CRCs can be compared with the analyzer's (fdk_tool.py). */
#include <stdio.h>
#include <stdlib.h>
#include "fdk-aac/aacdecoder_lib.h"

int main(int argc, char **argv) {
    if (argc != 2) {
        fprintf(stderr, "usage: fdk_crc file.aac\n");
        return 2;
    }
    FILE *file = fopen(argv[1], "rb");
    if (!file) {
        perror(argv[1]);
        return 2;
    }
    fseek(file, 0, SEEK_END);
    long length = ftell(file);
    fseek(file, 0, SEEK_SET);
    UCHAR *data = malloc(length);
    if (fread(data, 1, length, file) != (size_t)length) {
        return 2;
    }
    fclose(file);
    HANDLE_AACDECODER decoder = aacDecoder_Open(TT_MP4_ADTS, 1);
    static INT_PCM pcm[8 * 4096];
    UINT valid = length;
    int unit = 0;
    for (;;) {
        UCHAR *position = data + (length - valid);
        UINT size = valid;
        if (valid > 0) {
            aacDecoder_Fill(decoder, &position, &size, &valid);
        }
        AAC_DECODER_ERROR error = aacDecoder_DecodeFrame(decoder, pcm, sizeof(pcm) / sizeof(pcm[0]), 0);
        if (error == AAC_DEC_NOT_ENOUGH_BITS) {
            if (valid == 0) {
                break;
            }
            continue;
        }
        printf("unit %d: 0x%04X%s\n", unit++, error, error == AAC_DEC_CRC_ERROR ? " CRC error" : "");
    }
    aacDecoder_Close(decoder);
    free(data);
    return 0;
}
