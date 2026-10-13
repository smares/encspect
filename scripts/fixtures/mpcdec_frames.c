/* Prints what libmpcdec (musepack_src r495) decodes from each frame of a Musepack SV7 stream: the
 * frame's bits, the subbands it uses, the mid/side flags of its coded subbands and the subband
 * channels it fills with noise. Built by musepack_tool.py against the unchanged library sources, so
 * crosscheck_musepack_fixtures.py can compare the analyzer's per-frame figures with the decoder's.
 *
 * Output, one line per frame: "<bits> <used subbands> <mid/side mask in hex> <noise channels>".
 *
 * Built with -DLIBMPCDEC_126 against libmpcdec 1.2.6 instead, it reads stream versions 4 to 6. */

#include <stdio.h>

#ifndef LIBMPCDEC_126
#include <mpc/mpcdec.h>
#include "internal.h"
#include "decoder.h"

int main(int argc, char **argv)
{
	mpc_reader reader;
	mpc_demux *demux;
	mpc_streaminfo si;
	MPC_SAMPLE_FORMAT buffer[MPC_DECODER_BUFFER_LENGTH];

	if (argc != 2 || mpc_reader_init_stdio(&reader, argv[1]) != MPC_STATUS_OK)
		return 2;
	demux = mpc_demux_init(&reader);
	if (demux == 0)
		return 3;
	mpc_demux_get_info(demux, &si);
	for (;;) {
		mpc_frame_info frame;
		mpc_decoder *d = demux->d;
		int band, used = 0, noise = 0;
		unsigned mask = 0;

		frame.buffer = buffer;
		if (mpc_demux_decode(demux, &frame) != MPC_STATUS_OK || frame.bits == -1)
			break;
		for (band = 0; band <= (int) si.max_band && band < 32; band++) {
			if (d->Res_L[band] != 0 || d->Res_R[band] != 0) {
				used = band + 1;
				if (si.ms && d->MS_Flag[band])
					mask |= 1u << band;
			}
			noise += (d->Res_L[band] == -1) + (d->Res_R[band] == -1);
		}
		printf("%u %d %x %d\n", (unsigned) frame.bits, used, mask, noise);
	}
	mpc_demux_exit(demux);
	mpc_reader_exit_stdio(&reader);
	return 0;
}

#else
/* With -DLIBMPCDEC_126, against libmpcdec 1.2.6, which still decodes stream versions 4 to 6: its
 * mpc_streaminfo_read refuses any stream without "MP+" before it reaches their branch, so the
 * header is read here with its own SV4-6 reader (streaminfo_read_header_sv6, static, hence the
 * #include). It prints the header's verdict and the stream fields, then one line per frame as
 * above, with "invalid" where the decoder's bits differ from the frame's length, and the samples
 * it outputs. */

#include <string.h>
#include "../src/streaminfo.c"

int main(int argc, char **argv)
{
	FILE *file;
	mpc_reader_file reader;
	mpc_streaminfo si;
	mpc_decoder d;
	mpc_uint32_t header[8];
	MPC_SAMPLE_FORMAT buffer[MPC_DECODER_BUFFER_LENGTH];
	mpc_int32_t status;
	long total = 0;

	if (argc != 2 || (file = fopen(argv[1], "rb")) == 0)
		return 2;
	memset(header, 0, sizeof header);
	if (fread(header, 4, 8, file) < 2)
		return 3;
	fseek(file, 0, SEEK_SET);
	mpc_reader_setup_file_reader(&reader, file);
	memset(&si, 0, sizeof si);
	status = streaminfo_read_header_sv6(&si, header);
	printf("header %d version %u frames %u bitrate %d is %d ms %d max_band %d block %d\n", (int) status, (unsigned) si.stream_version,
	       (unsigned) si.frames, (int) si.bitrate, (int) si.is, (int) si.ms, (int) si.max_band, (int) si.block_size);
	if (status != ERROR_CODE_OK)
		return 0;
	mpc_decoder_setup(&d, &reader.reader);
	mpc_decoder_initialize(&d, &si);
	for (;;) {
		mpc_uint32_t frames = 0, bits = 0, samples;
		int band, used = 0;
		unsigned mask = 0;

		samples = mpc_decoder_decode(&d, buffer, &frames, &bits);
		if (samples == 0)
			break;
		if (samples == (mpc_uint32_t) -1) {
			printf("invalid\n");
			break;
		}
		for (band = 0; band <= (int) si.max_band && band < 32; band++) {
			if (d.Res_L[band] != 0 || d.Res_R[band] != 0) {
				used = band + 1;
				if (si.ms && d.MS_Flag[band])
					mask |= 1u << band;
			}
		}
		total += samples;
		printf("%u %d %x 0\n", (unsigned) bits, used, mask);
	}
	printf("samples %ld\n", total);
	return 0;
}
#endif
