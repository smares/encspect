/* Prints what the Portable Musepack decoder (mpc.zip from Rarewares, (C) 1999-2002
 * Buschmann/Klemm/Piecha/Wolf, (C) 2003-2004 Peter Pawlowski) reads from a stream of version 4 to 6,
 * in the same lines as mpcdec_frames.c built with -DLIBMPCDEC_126, so crosscheck_musepack_fixtures.py
 * compares it with libmpcdec 1.2.6, its C port, line for line: the header's verdict (StreamInfo::ReadStreamInfo, which takes
 * a stream without "MP+" as version 4 to 6) and its fields, then one line per frame, "<bits> <used
 * subbands> <mid/side mask in hex> 0", "invalid" where the decoder's bits differ from the frame's
 * length, and the samples it outputs. Built by musepack_tool.py against the library sources with
 * their one portability patch.
 *
 * The decoder keeps the resolutions and mid/side flags it read in private members, so they are made
 * public for this file only; the class's layout is the same either way. */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define private public
#include "mpc_dec.h"
#undef private

class FileReader : public MPC_reader {
public:
	FileReader(FILE *file) : m_file(file)
	{
		fseek(m_file, 0, SEEK_END);
		m_size = ftell(m_file);
		fseek(m_file, 0, SEEK_SET);
	}
	size_t read(void *ptr, size_t size) { return fread(ptr, 1, size, m_file); }
	bool seek(int offset, int origin) { return !fseek(m_file, offset, origin); }
	int tell() { return (int) ftell(m_file); }
	int get_size() { return m_size; }
	bool canseek() { return true; }
private:
	FILE *m_file;
	int m_size;
};

int main(int argc, char **argv)
{
	FILE *file;
	static MPC_SAMPLE_FORMAT buffer[MPC_decoder::DecodeBufferLength];
	long total = 0;

	if (argc != 2 || (file = fopen(argv[1], "rb")) == 0)
		return 2;
	FileReader reader(file);
	StreamInfo info;
	int status = info.ReadStreamInfo(&reader);
	printf("header %d version %u frames %u bitrate %u is %u ms %u max_band %u block %u\n", status, info.simple.StreamVersion, info.simple.Frames,
	       info.simple.Bitrate, info.simple.IS, info.simple.MS, info.simple.MaxBand, info.simple.BlockSize);
	if (status != ERROR_CODE_OK)
		return 0;
	MPC_decoder *decoder = new MPC_decoder(&reader);
	if (!decoder->Initialize(&info))
		return 3;
	for (;;) {
		unsigned frames = 0, bits = 0;
		int band, used = 0;
		unsigned mask = 0;
		unsigned samples = decoder->Decode(buffer, &frames, &bits);

		if (samples == 0)
			break;
		if (samples == (unsigned) -1) {
			printf("invalid\n");
			break;
		}
		for (band = 0; band <= (int) info.simple.MaxBand && band < 32; band++) {
			if (decoder->Res_L[band] != 0 || decoder->Res_R[band] != 0) {
				used = band + 1;
				if (info.simple.MS && decoder->MS_Flag[band])
					mask |= 1u << band;
			}
		}
		total += samples;
		printf("%u %d %x 0\n", bits, used, mask);
	}
	printf("samples %ld\n", total);
	delete decoder;
	fclose(file);
	return 0;
}
