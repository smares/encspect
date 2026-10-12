/* Prints what libmpcdec (musepack_src r495) decodes from each frame of a Musepack SV7 stream: the
 * frame's bits, the subbands it uses, the mid/side flags of its coded subbands and the subband
 * channels it fills with noise. Built by musepack_tool.py against the unchanged library sources, so
 * crosscheck_musepack_fixtures.py can compare the analyzer's per-frame figures with the decoder's.
 *
 * Output, one line per frame: "<bits> <used subbands> <mid/side mask in hex> <noise channels>". */

#include <stdio.h>
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
