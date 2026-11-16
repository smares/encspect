/* Decodes the Speex streams of an Ogg file with libspeex 1.2.1 built with FIXED_POINT, the way speexdec.c does (process_header, the frame loop with the stereo
 * request handler and speex_decode_stereo_int), but without speexdec's trimming and without its output formats: it writes what the decoder returns, at the
 * coded stage, for the cross-check of the analyzer's Speex decoder (scripts/fixtures/crosscheck_speex_pcm.py).
 *
 *     speex_pcm FILE OUTPUT-PREFIX
 *
 * For each Speex stream k of the file (a logical stream whose first packet starts with "Speex   "), in the order its first page comes:
 *   - OUTPUT-PREFIX.s<k>.pcm   the samples of every frame the decoder returned 0 for, interleaved 16-bit little-endian, 1 channel or 2 (speexdec decodes in stereo
 *                              whenever the header's channel count is not 1);
 *   - OUTPUT-PREFIX.s<k>.packets   one line per audio packet: the number of frames it decoded, the code the frame that stopped it returned (0 where none did) and the packet's bytes;
 *   - a line on standard output: stream K channels C rate R mode M frame-size F frames-per-packet N extra-headers E.
 *
 * Copyright (c) 2002-2008 Jean-Marc Valin and Xiph.org Foundation, whose speexdec.c this program follows in its use of libspeex (BSD 3-clause license, in
 * THIRD-PARTY-NOTICES.md); nothing of its getopt code is used.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ogg/ogg.h>
#include <speex/speex.h>
#include <speex/speex_header.h>
#include <speex/speex_stereo.h>
#include <speex/speex_callbacks.h>

#define MAX_STREAMS 64

typedef struct {
   ogg_stream_state os;
   int serial;
   int active;
   int packet_count;
   int extra_headers;
   void *st;
   SpeexBits bits;
   SpeexStereoState stereo;
   SpeexHeader *header;
   int frame_size;
   int nframes;
   int channels;
   FILE *pcm;
   FILE *packets;
   spx_int16_t output[640 * 2];
} Stream;

static Stream streams[MAX_STREAMS];
static int stream_count = 0;
static int speex_streams = 0;
static const char *prefix;

static int open_decoder(Stream *s, ogg_packet *op, int index)
{
   const SpeexMode *mode;
   SpeexCallback callback;
   spx_int32_t enh = 1, rate;
   char name[1024];
   SpeexStereoState init = SPEEX_STEREO_STATE_INIT;

   s->header = speex_packet_to_header((char *)op->packet, op->bytes);
   if (!s->header || s->header->mode >= SPEEX_NB_MODES || s->header->mode < 0)
      return -1;
   mode = speex_lib_get_mode(s->header->mode);
   if (s->header->speex_version_id > 1 || mode->bitstream_version != s->header->mode_bitstream_version)
      return -1;
   s->st = speex_decoder_init(mode);
   if (!s->st)
      return -1;
   speex_decoder_ctl(s->st, SPEEX_SET_ENH, &enh);
   speex_decoder_ctl(s->st, SPEEX_GET_FRAME_SIZE, &s->frame_size);
   rate = s->header->rate;
   speex_decoder_ctl(s->st, SPEEX_SET_SAMPLING_RATE, &rate);
   s->nframes = s->header->frames_per_packet;
   if (s->nframes == 0)
      s->nframes = 1;
   s->channels = s->header->nb_channels;
   s->stereo = init;
   if (s->channels != 1) {
      s->channels = 2;
      callback.callback_id = SPEEX_INBAND_STEREO;
      callback.func = speex_std_stereo_request_handler;
      callback.data = &s->stereo;
      speex_decoder_ctl(s->st, SPEEX_SET_HANDLER, &callback);
   }
   s->extra_headers = s->header->extra_headers;
   speex_bits_init(&s->bits);
   snprintf(name, sizeof name, "%s.s%d.pcm", prefix, index);
   s->pcm = fopen(name, "wb");
   snprintf(name, sizeof name, "%s.s%d.packets", prefix, index);
   s->packets = fopen(name, "wb");
   printf("stream %d channels %d rate %d mode %d frame-size %d frames-per-packet %d extra-headers %d\n", index, s->channels, s->header->rate,
          s->header->mode, s->frame_size, s->nframes, s->extra_headers);
   return 0;
}

static void decode_packet(Stream *s, ogg_packet *op)
{
   spx_int16_t *output = s->output;
   int j, decoded = 0, code = 0;

   speex_bits_read_from(&s->bits, (char *)op->packet, op->bytes);
   for (j = 0; j != s->nframes; j++) {
      int ret = speex_decode_int(s->st, &s->bits, output);
      if (ret == -1) {
         code = -1;
         break;
      }
      if (ret == -2) {
         code = -2;
         break;
      }
      if (speex_bits_remaining(&s->bits) < 0) {
         code = -3;
         break;
      }
      if (s->channels == 2)
         speex_decode_stereo_int(output, s->frame_size, &s->stereo);
      {
         int i;
         for (i = 0; i < s->frame_size * s->channels; i++) {
            unsigned char b[2];
            b[0] = (unsigned char)(output[i] & 0xFF);
            b[1] = (unsigned char)((output[i] >> 8) & 0xFF);
            fwrite(b, 1, 2, s->pcm);
         }
      }
      decoded++;
   }
   fprintf(s->packets, "%d %d %ld\n", decoded, code, (long)op->bytes);
}

int main(int argc, char **argv)
{
   FILE *in;
   ogg_sync_state oy;
   ogg_page og;
   char *buffer;
   int bytes, i;

   if (argc != 3) {
      fprintf(stderr, "usage: speex_pcm FILE OUTPUT-PREFIX\n");
      return 2;
   }
   prefix = argv[2];
   in = fopen(argv[1], "rb");
   if (!in) {
      fprintf(stderr, "cannot open %s\n", argv[1]);
      return 2;
   }
   ogg_sync_init(&oy);
   for (;;) {
      while (ogg_sync_pageout(&oy, &og) == 1) {
         int serial = ogg_page_serialno(&og);
         Stream *s = NULL;
         ogg_packet op;
         for (i = 0; i < stream_count; i++)
            if (streams[i].serial == serial && streams[i].active >= 0) {
               s = &streams[i];
               break;
            }
         if (!s && ogg_page_bos(&og) && stream_count < MAX_STREAMS) {
            s = &streams[stream_count++];
            memset(s, 0, sizeof *s);
            s->serial = serial;
            ogg_stream_init(&s->os, serial);
            s->active = 0;
         }
         if (!s)
            continue;
         ogg_stream_pagein(&s->os, &og);
         while (ogg_stream_packetout(&s->os, &op) == 1) {
            if (s->active < 0)
               continue;
            if (s->packet_count == 0) {
               if (op.bytes >= 8 && memcmp(op.packet, "Speex   ", 8) == 0 && open_decoder(s, &op, speex_streams) == 0) {
                  speex_streams++;
                  s->active = 1;
               } else {
                  s->active = -1;
               }
            } else if (s->active == 1 && s->packet_count > 1 + s->extra_headers) {
               decode_packet(s, &op);
            }
            s->packet_count++;
         }
      }
      buffer = ogg_sync_buffer(&oy, 4096);
      bytes = (int)fread(buffer, 1, 4096, in);
      if (bytes <= 0)
         break;
      ogg_sync_wrote(&oy, bytes);
   }
   for (i = 0; i < stream_count; i++) {
      if (streams[i].pcm)
         fclose(streams[i].pcm);
      if (streams[i].packets)
         fclose(streams[i].packets);
   }
   return 0;
}
