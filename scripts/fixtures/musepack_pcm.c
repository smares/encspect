/* Writes the PCM libmpcdec decodes from a Musepack stream, for scripts/fixtures/crosscheck_musepack_pcm.py: floats at full scale 1.0, interleaved, little-endian, at the
 * stage the library delivers them, which is after the trimming the stream's syntax states (the synthesis filter's 481 samples, SV8's beginning silence, the stream's
 * length). Prints on standard output the position of the first sample at the coded stage (481 plus the beginning silence), the channels, the sample rate and the samples.
 *
 * Built by musepack_tool.py against libmpcdec of musepack_src r495 (stream versions 7 and 8), or with -DLIBMPCDEC_126 against libmpcdec 1.2.6 (versions 4 to 6, read by its own
 * decoder: the frames are decoded one at a time, the 481 delay samples and the end cut by the library's own trimming).
 *
 * usage: musepack_pcm FILE OUTPUT
 */

#include <stdio.h>
#include <string.h>

#ifndef LIBMPCDEC_126
#include <mpc/mpcdec.h>

int main(int argc, char **argv)
{
	mpc_reader reader;
	mpc_demux *demux;
	mpc_streaminfo si;
	MPC_SAMPLE_FORMAT buffer[MPC_DECODER_BUFFER_LENGTH];
	FILE *out;
	long total = 0;

	if (argc != 3 || mpc_reader_init_stdio(&reader, argv[1]) != MPC_STATUS_OK)
		return 2;
	demux = mpc_demux_init(&reader);
	if (demux == 0)
		return 3;
	mpc_demux_get_info(demux, &si);
	out = fopen(argv[2], "wb");
	if (out == 0)
		return 2;
	for (;;) {
		mpc_frame_info frame;

		frame.buffer = buffer;
		if (mpc_demux_decode(demux, &frame) != MPC_STATUS_OK || frame.bits == -1)
			break;
		fwrite(buffer, sizeof(float), frame.samples * si.channels, out);
		total += frame.samples;
	}
	fclose(out);
	printf("first %d channels %u rate %u samples %ld\n", 481 + (int) si.beg_silence, (unsigned) si.channels, (unsigned) si.sample_freq, total);
	mpc_demux_exit(demux);
	mpc_reader_exit_stdio(&reader);
	return 0;
}
#else
#include "../src/streaminfo.c"

int main(int argc, char **argv)
{
	FILE *file, *out;
	mpc_reader_file reader;
	mpc_streaminfo si;
	mpc_decoder d;
	mpc_uint32_t header[8];
	MPC_SAMPLE_FORMAT buffer[MPC_DECODER_BUFFER_LENGTH];
	mpc_int32_t status;
	long total = 0;

	if (argc != 3 || (file = fopen(argv[1], "rb")) == 0)
		return 2;
	memset(header, 0, sizeof header);
	if (fread(header, 4, 8, file) < 2)
		return 3;
	fseek(file, 0, SEEK_SET);
	mpc_reader_setup_file_reader(&reader, file);
	memset(&si, 0, sizeof si);
	status = streaminfo_read_header_sv6(&si, header);
	if (status != ERROR_CODE_OK)
		return 4;
	mpc_decoder_setup(&d, &reader.reader);
	mpc_decoder_initialize(&d, &si);
	out = fopen(argv[2], "wb");
	if (out == 0)
		return 2;
	for (;;) {
		mpc_uint32_t samples = mpc_decoder_decode(&d, buffer, 0, 0);

		if (samples == 0 || samples == (mpc_uint32_t) -1)
			break;
		fwrite(buffer, sizeof(float), samples * 2, out);
		total += samples;
	}
	fclose(out);
	printf("first 481 channels 2 rate %u samples %ld\n", (unsigned) 44100, total);
	return 0;
}
#endif
