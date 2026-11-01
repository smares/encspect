/* The direct path for Opus and Vorbis in Ogg (docs/design/official-decoder-crosschecks.md): libogg splits the file into pages
   and packets, and libopus or libvorbis decodes each packet alone. One flushed JSON line per packet, so a crash or a stall
   leaves what was done. The decoders are used as black boxes and are not linked into Encspect.

     ogg_decode <mode> <file>

   modes:
     opus            libopus, opus_multistream_decode per packet (mapping families 0, 1, 2 and 3: the multistream decoder for 0, 1 and 2, the projection decoder for 3)
     vorbis          libvorbis with granulepos -1 and e_o_s 0 on every audio packet, so libvorbis trims nothing (the coded stage)
     vorbis-granule  libvorbis with the packets as libogg hands them, granule positions and end of stream included (the trimmed stage)

   libogg drops a page that fails its CRC: the skipped bytes are written as "skip" lines and a hole in the packets as "hole". */
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ogg/ogg.h>
#include <opus/opus_multistream.h>
#include <opus/opus_projection.h>
#include <vorbis/codec.h>

static int is_opus(const ogg_packet *p) { return p->bytes >= 8 && !memcmp(p->packet, "OpusHead", 8); }
static int is_vorbis(const ogg_packet *p) { return p->bytes >= 7 && p->packet[0] == 1 && !memcmp(p->packet + 1, "vorbis", 6); }

int main(int argc, char **argv) {
    if (argc != 3) {
        fprintf(stderr, "usage: ogg_decode opus|vorbis|vorbis-granule file\n");
        return 2;
    }
    const char *mode = argv[1];
    int want_opus = !strcmp(mode, "opus");
    int keep_granule = !strcmp(mode, "vorbis-granule");
    FILE *file = fopen(argv[2], "rb");
    if (!file) {
        perror(argv[2]);
        return 2;
    }
    printf("{\"type\":\"env\",\"mode\":\"%s\",\"libvorbis\":\"%s\",\"libopus\":\"%s\"}\n", mode, vorbis_version_string(), opus_get_version_string());
    fflush(stdout);

    ogg_sync_state sync;
    ogg_stream_state stream;
    int have_stream = 0;
    ogg_sync_init(&sync);
    OpusMSDecoder *opus = NULL;
    OpusProjectionDecoder *projection = NULL;
    const char *stopped = "end";
    int opus_channels = 0, opus_head_seen = 0;
    int header_packets = 0;
    vorbis_info vi;
    vorbis_comment vc;
    vorbis_dsp_state vd;
    vorbis_block vb;
    int vorbis_ready = 0;
    vorbis_info_init(&vi);
    vorbis_comment_init(&vc);
    long long offset = 0, packet_index = 0, coded = 0, packets_decoded = 0, holes = 0, skipped_bytes = 0, pages = 0;
    long long streams = 0;
    static float opus_pcm[5760 * 255];
    int eof = 0;
    while (!eof) {
        char *buffer = ogg_sync_buffer(&sync, 4096);
        size_t got = fread(buffer, 1, 4096, file);
        ogg_sync_wrote(&sync, (long)got);
        if (got == 0) {
            eof = 1;
            if (ferror(file)) {
                stopped = "error";
            }
        }
        for (;;) {
            ogg_page page;
            long n = ogg_sync_pageseek(&sync, &page);
            if (n == 0) {
                break;
            }
            if (n < 0) {
                printf("{\"type\":\"skip\",\"offset\":%lld,\"bytes\":%ld}\n", offset, -n);
                fflush(stdout);
                offset += -n;
                skipped_bytes += -n;
                continue;
            }
            long long page_offset = offset;
            offset += n;
            pages++;
            if (ogg_page_bos(&page)) {
                if (have_stream) {
                    ogg_stream_clear(&stream);
                }
                ogg_stream_init(&stream, ogg_page_serialno(&page));
                have_stream = 1;
                streams++;
                header_packets = 0;
                opus_head_seen = 0;
                if (vorbis_ready) {
                    vorbis_block_clear(&vb);
                    vorbis_dsp_clear(&vd);
                    vorbis_info_clear(&vi);
                    vorbis_comment_clear(&vc);
                    vorbis_info_init(&vi);
                    vorbis_comment_init(&vc);
                    vorbis_ready = 0;
                }
                if (opus) {
                    opus_multistream_decoder_destroy(opus);
                    opus = NULL;
                }
                if (projection) {
                    opus_projection_decoder_destroy(projection);
                    projection = NULL;
                }
                printf("{\"type\":\"stream\",\"offset\":%lld,\"serial\":%d}\n", page_offset, ogg_page_serialno(&page));
            }
            if (!have_stream || ogg_stream_pagein(&stream, &page) < 0) {
                printf("{\"type\":\"pagein\",\"offset\":%lld,\"error\":true}\n", page_offset);
                fflush(stdout);
                continue;
            }
            for (;;) {
                ogg_packet op;
                int r = ogg_stream_packetout(&stream, &op);
                if (r == 0) {
                    break;
                }
                if (r < 0) {
                    holes++;
                    printf("{\"type\":\"hole\",\"page\":%lld,\"pageOffset\":%lld}\n", pages, page_offset);
                    fflush(stdout);
                    continue;
                }
                long long index = packet_index++;
                printf("{\"type\":\"packet\",\"index\":%lld,\"pageOffset\":%lld,\"bytes\":%ld,\"granulepos\":%" PRId64 ",\"eos\":%ld", index, page_offset,
                       op.bytes, (int64_t)op.granulepos, op.e_o_s);
                if (want_opus) {
                    if (!opus_head_seen) {
                        opus_head_seen = 1;
                        if (!is_opus(&op) || op.bytes < 19) {
                            printf(",\"result\":\"not an OpusHead\"}\n");
                        } else {
                            const unsigned char *h = op.packet;
                            int channels = h[9], family = h[18];
                            unsigned char mapping[256];
                            int nstreams = 0, coupled = 0, err = 0, supported = 1;
                            const char *why = "";
                            if (channels < 1 || channels > 255) {
                                supported = 0;
                                why = "channel count";
                            } else if (family == 0) {
                                if (channels > 2) {
                                    supported = 0;
                                    why = "family 0 with more than 2 channels";
                                } else {
                                    nstreams = 1;
                                    coupled = channels == 2;
                                    mapping[0] = 0;
                                    mapping[1] = 1;
                                }
                            } else if ((family == 1 || family == 2) && op.bytes >= 21 + channels) {
                                nstreams = h[19];
                                coupled = h[20];
                                memcpy(mapping, h + 21, channels);
                            } else if (family == 3 && op.bytes >= 21 + 2 * channels * (h[19] + h[20])) {
                                nstreams = h[19];
                                coupled = h[20];
                                projection = opus_projection_decoder_create(48000, channels, nstreams, coupled, (unsigned char *)h + 21, 2 * channels * (nstreams + coupled), &err);
                            } else {
                                supported = 0;
                                why = family == 255 ? "mapping family 255 (undefined by RFC 7845)" : "head too short for its mapping or an unknown family";
                            }
                            if (!supported) {
                                printf(",\"result\":\"not decoded: %s\",\"family\":%d,\"channels\":%d}\n", why, family, channels);
                            } else {
                                if (family != 3) {
                                    opus = opus_multistream_decoder_create(48000, channels, nstreams, coupled, mapping, &err);
                                }
                                opus_channels = channels;
                                printf(",\"result\":\"head\",\"channels\":%d,\"family\":%d,\"preSkip\":%d,\"created\":%d}\n", channels, family, h[10] | (h[11] << 8), err);
                            }
                        }
                    } else if (op.bytes >= 8 && !memcmp(op.packet, "OpusTags", 8)) {
                        printf(",\"result\":\"tags\"}\n");
                    } else if (opus || projection) {
                        int nb = opus_packet_get_nb_samples(op.packet, (opus_int32)op.bytes, 48000);
                        int frames = opus ? opus_multistream_decode_float(opus, op.packet, (opus_int32)op.bytes, opus_pcm, 5760, 0)
                                          : opus_projection_decode_float(projection, op.packet, (opus_int32)op.bytes, opus_pcm, 5760, 0);
                        if (frames >= 0) {
                            coded += frames;
                            packets_decoded++;
                            printf(",\"result\":\"decoded\",\"samples\":%d,\"tocSamples\":%d,\"channels\":%d}\n", frames, nb, opus_channels);
                        } else {
                            printf(",\"result\":\"error\",\"error\":%d,\"tocSamples\":%d}\n", frames, nb);
                        }
                    } else {
                        printf(",\"result\":\"no decoder\"}\n");
                    }
                } else {
                    if (header_packets < 3 && !vorbis_ready) {
                        if (header_packets == 0 && !is_vorbis(&op)) {
                            printf(",\"result\":\"not a Vorbis identification header\"}\n");
                        } else {
                            int err = vorbis_synthesis_headerin(&vi, &vc, &op);
                            header_packets++;
                            if (header_packets == 3 && err == 0) {
                                err = vorbis_synthesis_init(&vd, &vi);
                                if (err == 0) {
                                    vorbis_block_init(&vd, &vb);
                                    vorbis_ready = 1;
                                }
                            }
                            printf(",\"result\":\"header\",\"error\":%d%s}\n", err, header_packets == 1 && err == 0 ? ",\"detail\":\"identification\"" : "");
                        }
                    } else if (vorbis_ready) {
                        if (!keep_granule) {
                            op.granulepos = -1;
                            op.e_o_s = 0;
                        }
                        int err = vorbis_synthesis(&vb, &op);
                        long out = 0;
                        if (err == 0) {
                            vorbis_synthesis_blockin(&vd, &vb);
                            float **pcm;
                            int got_samples;
                            while ((got_samples = vorbis_synthesis_pcmout(&vd, &pcm)) > 0) {
                                out += got_samples;
                                vorbis_synthesis_read(&vd, got_samples);
                            }
                            coded += out;
                            packets_decoded++;
                            printf(",\"result\":\"decoded\",\"samples\":%ld,\"channels\":%d,\"rate\":%ld}\n", out, vi.channels, vi.rate);
                        } else {
                            printf(",\"result\":\"error\",\"error\":%d}\n", err);
                        }
                    } else {
                        printf(",\"result\":\"no decoder\"}\n");
                    }
                }
                fflush(stdout);
            }
        }
    }
    if (opus) {
        opus_multistream_decoder_destroy(opus);
    }
    if (projection) {
        opus_projection_decoder_destroy(projection);
    }
    if (vorbis_ready) {
        vorbis_block_clear(&vb);
        vorbis_dsp_clear(&vd);
    }
    vorbis_comment_clear(&vc);
    vorbis_info_clear(&vi);
    if (have_stream) {
        ogg_stream_clear(&stream);
    }
    ogg_sync_clear(&sync);
    fclose(file);
    printf("{\"type\":\"end\",\"stoppedBy\":\"%s\",\"pages\":%lld,\"streams\":%lld,\"packets\":%lld,\"decodedPackets\":%lld,\"holes\":%lld,\"skippedBytes\":%lld,\"codedSamples\":%lld}\n",
           stopped, pages, streams, packet_index, packets_decoded, holes, skipped_bytes, coded);
    fflush(stdout);
    return 0;
}
