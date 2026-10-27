using Encspect.Core;

namespace Encspect.Formats.Wav;

// The hand-over of a PCM data chunk to the decoder "pcm" (docs/design/spectral-analysis.md, "WAV, Wave64 and AIFF: a PCM decoder"). A session is started only for
// samples PcmCoding can describe, so data that is not PCM, or whose layout the file states inconsistently, is reported as it was before.
internal static class PcmRoute
{
    // The coding of the first data chunk's samples, or null where there is none to describe: a coding that does not store samples as they are, a width that
    // the coding does not have (the findings wav.float-bits and wav.companded-bits say so), a block alignment that is not the channels times the width, samples
    // spread over the pieces of a wavl list, or samples that hold another stream (IEC 61937 bursts, DTS).
    public static PcmCoding? CodingOf(State s, WaveFormat format)
    {
        if (s.Burst is not null || s.CarriedCodec is not null || s.WavlList is not null || s.SilentSamples != 0 || s.Data is null || format.Channels < 1)
        {
            return null;
        }

        int bytes = format.BytesPerSample;
        long frameBytes = (long)bytes * format.Channels;
        if (bytes < 1 || (format.BlockAlign != 0 && format.BlockAlign != frameBytes))
        {
            return null;
        }

        int bits = bytes * 8;
        PcmEncoding? encoding = format.CodingTag switch
        {
            FormatTags.Pcm => bits == 8 ? PcmEncoding.UnsignedInteger : PcmEncoding.SignedInteger,
            FormatTags.IeeeFloat when bits is 32 or 64 && format.Bits == bits => PcmEncoding.Float,
            FormatTags.ALaw when format.Bits == 8 => PcmEncoding.ALaw,
            FormatTags.MuLaw when format.Bits == 8 => PcmEncoding.MuLaw,
            _ => null,
        };
        if (encoding is null)
        {
            return null;
        }

        return new PcmCoding
        {
            Encoding = encoding.Value,
            IsBigEndian = s.IsBigEndian,
            ContainerBits = bits,
            SampleRate = (int)Math.Min(format.SampleRate, int.MaxValue),
            ChannelLayout = DataChunks.StreamLayout(format),
        };
    }

    // Starts the session, feeds it the samples and finishes it with no checksum (PCM stores none); null where there is no coding.
    public static StreamVerification? Run(State s, WaveFormat format, DataChunk data)
    {
        if (CodingOf(s, format) is not { } coding)
        {
            return null;
        }

        long frameBytes = coding.FrameBytes(format.Channels);
        long frames = data.Available / frameBytes;
        IDecodeSession session = s.Context.StartDecoding(PcmCoding.CodecId, new DecodeSetup
        {
            Stream = data.Node,
            Channels = format.Channels,
            BitsPerSample = coding.OutputBits,
            Samples = frames,
            Pcm = coding,
        });
        if (session.IsDecoding)
        {
            PcmFeed.Run(session, s.Reader.Slice(data.Start, data.Available), data.Start, frames, frameBytes, data.Node.Name);
        }

        return session.Finish([]);
    }
}
