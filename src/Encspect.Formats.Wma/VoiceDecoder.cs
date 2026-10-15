namespace Encspect.Formats.Wma;

// What carries from one frame to the next, as the fields of FFmpeg's decoder do (wmavoice.c: last_pitch_val and
// last_acb_type): the pitch that a pitch-adaptive window frame interpolates from.
internal struct VoiceState()
{
    public int LastPitch = 40;
    public int LastAdaptiveCodebook;
}

// Reads a superframe of Windows Media Audio Voice as far as its size needs, as wmavoice.c synth_superframe and synth_frame do:
// the header, the LSPs, each frame's type, pitch and excitation fields, and the additional data. The audio is not rebuilt, so
// the excitation's fields are read only to be skipped, apart from the few that decide how wide later ones are.
internal sealed class VoiceDecoder(VoiceParameters parameters)
{
    private static readonly (uint Code, int Length)[] FrameTypeCodes = BuildCodes();

    // wmavoice.c: ff_vlc_init_from_lengths: the codes follow from the lengths as the running total of 2^-length.
    private static (uint, int)[] BuildCodes()
    {
        byte[] lengths = VoiceTables.FrameTypeCodeLengths;
        var codes = new (uint, int)[lengths.Length];
        ulong code = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            codes[i] = ((uint)(code >> (32 - lengths[i])), lengths[i]);
            code += 1UL << (32 - lengths[i]);
        }

        return codes;
    }

    private readonly VoiceParameters _p = parameters;

    // Reads the superframe that starts at the reader's position; the bit positions it records count from there. The frames
    // are written to the three records given. A status other than Ok says why it stopped; the record then holds what was read.
    public VoiceStatus Read(Bits bits, bool residual, ref VoiceState state, ref VoiceSuperframeRecord record, Span<VoiceFrameRecord> frames)
    {
        int origin = bits.Position;
        record.FrameCount = 0;
        if (bits.ReadBit() == 0)
        {
            return Stop(bits, origin, ref record, VoiceStatus.Music);
        }

        int samples = VoiceParameters.SuperframeSamples;
        if (bits.ReadBit() != 0)
        {
            record.Flags |= VoiceSuperframeRecord.HasSampleCount;
            samples = (int)bits.Read(12);
            record.Samples = (ushort)samples;
            if (samples > VoiceParameters.SuperframeSamples)
            {
                return Stop(bits, origin, ref record, VoiceStatus.SampleCount);
            }
        }

        record.Samples = (ushort)samples;
        if (residual)
        {
            record.Flags |= VoiceSuperframeRecord.ResidualLsps;
            bits.Skip(_p.ResidualLspBits);
        }

        record.HeaderBits = bits.Position - origin;
        for (int n = 0; n < VoiceParameters.FramesPerSuperframe; n++)
        {
            frames[n] = default;
            VoiceStatus status = ReadFrame(bits, residual, origin, ref state, ref frames[n]);
            if (status != VoiceStatus.Ok)
            {
                return Stop(bits, origin, ref record, status);
            }

            record.FrameCount++;
        }

        record.FramesEnd = bits.Position - origin;

        // wmavoice.c: the "statistics" the decoder skips: a flag, then 4 bits that count 10-bit groups less one.
        if (bits.ReadBit() != 0)
        {
            record.Flags |= VoiceSuperframeRecord.HasAdditionalData;
            int groups = (int)bits.Read(4) + 1;
            record.ExtraBits = (ushort)(10 * groups);
            bits.Skip(10 * groups);
        }

        record.Bits = bits.Position - origin;
        return bits.IsOverrun ? VoiceStatus.Overrun : VoiceStatus.Ok;
    }

    // Data that ran out is the cause when it ran out before the error showed: a decoder reads zeros there.
    private static VoiceStatus Stop(Bits bits, int origin, ref VoiceSuperframeRecord record, VoiceStatus status)
    {
        record.Bits = bits.Position - origin;
        record.FramesEnd = record.Bits;
        return bits.IsOverrun ? VoiceStatus.Overrun : status;
    }

    private VoiceStatus ReadFrame(Bits bits, bool residual, int origin, ref VoiceState state, ref VoiceFrameRecord frame)
    {
        frame.StartBit = bits.Position - origin;
        if (!residual)
        {
            bits.Skip(_p.IndependentLspBits);
        }

        int typeStart = bits.Position;
        uint window = bits.Peek(14);
        int slot = -1;
        for (int i = 0; i < FrameTypeCodes.Length; i++)
        {
            (uint code, int length) = FrameTypeCodes[i];
            if (window >> (14 - length) == code)
            {
                slot = i;
                bits.Skip(length);
                break;
            }
        }

        frame.TypeBits = (byte)(bits.Position - typeStart);
        int type = slot >= 0 ? _p.FrameTypeTable[slot] : -1;
        if (type < 0)
        {
            frame.EndBit = bits.Position - origin;
            return VoiceStatus.FrameType;
        }

        frame.Type = (byte)type;
        byte[] descriptor = VoiceTables.FrameDescriptors[type];
        int blocks = descriptor[0];
        int logBlocks = descriptor[1];
        int adaptive = descriptor[2];
        int fixedCodebook = descriptor[3];
        int pairs = descriptor[4];

        Span<int> pitch = stackalloc int[8];
        int current = 0;
        if (adaptive == 1)
        {
            // A pitch per frame, which the blocks' pitches are interpolated from the last frame's to.
            int blocksTwice = blocks << 1;
            current = Math.Min(_p.MinPitch + (int)bits.Read(_p.PitchBits), _p.MaxPitch - 1);
            if (state.LastAdaptiveCodebook == 0 || 20 * Math.Abs(current - state.LastPitch) > current + state.LastPitch)
            {
                state.LastPitch = current;
            }

            for (int n = 0; n < blocks; n++)
            {
                int factor = (n * 2) + 1;
                pitch[n] = ((factor * current) + ((blocksTwice - factor) * state.LastPitch) + blocks) >> (logBlocks + 1);
            }
        }

        int windowPulses = 0;
        bool windowExtended = false;
        if (fixedCodebook == 0)
        {
            frame.Parameter = (ushort)bits.Read(8);
        }
        else if (fixedCodebook == 2)
        {
            // wmavoice.c: aw_parse_coords: where the first pulse of the pitch-adaptive window lies decides how many blocks have pulses at the pitch lag.
            int index = (int)bits.Read(6);
            if (index >= 54)
            {
                windowExtended = true;
                index += ((index - 54) * 3) + (int)bits.Read(2);
            }

            frame.Parameter = (ushort)index;

            // The pitch is at least 1 for any configuration FFmpeg accepts; a lag of less would loop or divide by zero there.
            int lag = Math.Max(1, pitch[0]);
            int offset = VoiceTables.StartOffsets[index];
            while (offset < 0)
            {
                offset += lag;
            }

            windowPulses = (lag - 1 + (VoiceParameters.FrameSamples / 2) - offset) / lag;
            frame.Flags |= (byte)((windowExtended ? VoiceFrameRecord.WindowExtended : 0) | (windowPulses > 0 ? VoiceFrameRecord.WindowPulsesInFirstBlock : 0));
        }

        int lastBlockPitch = 0;
        for (int n = 0; n < blocks; n++)
        {
            if (adaptive == 2)
            {
                // A pitch per block: the first absolute, the others as differences from the one before.
                int blockPitch = n == 0
                    ? (int)bits.Read(_p.BlockPitchBits)
                    : lastBlockPitch - _p.BlockDeltaPitchHalfRange + (int)bits.Read(_p.BlockDeltaPitchBits);
                lastBlockPitch = Clip(blockPitch, _p.BlockDeltaPitchHalfRange, _p.BlockPitchRange - _p.BlockDeltaPitchHalfRange);
                int[] table = _p.BlockConversion;
                int first = (table[1] - table[0]) << 2;
                int second = (table[2] - table[1]) << 1;
                int third = table[3] - table[2] + 1;
                int doubled;
                if (blockPitch < first)
                {
                    doubled = (table[0] << 2) + blockPitch;
                }
                else
                {
                    blockPitch -= first;
                    if (blockPitch < second)
                    {
                        doubled = (table[1] << 2) + (blockPitch << 1);
                    }
                    else
                    {
                        blockPitch -= second;
                        doubled = blockPitch < third ? (table[2] + blockPitch) << 2 : table[3] << 2;
                    }
                }

                pitch[n] = doubled >> 2;
            }

            ReadBlock(bits, n, logBlocks, adaptive, fixedCodebook, pairs, windowExtended, windowPulses > 0);
        }

        state.LastAdaptiveCodebook = adaptive;
        state.LastPitch = adaptive == 0 ? 0 : adaptive == 1 ? current : pitch[blocks - 1];
        frame.Pitch = (ushort)Math.Min(ushort.MaxValue, Math.Max(0, state.LastPitch));
        frame.EndBit = bits.Position - origin;
        return VoiceStatus.Ok;
    }

    // libavutil: av_clip, which gives the minimum when the minimum is above the maximum.
    private static int Clip(int value, int minimum, int maximum) => value < minimum ? minimum : value > maximum ? maximum : value;

    // The bits of a block's excitation: skipped, since the gains and positions are not reported.
    private static void ReadBlock(Bits bits, int block, int logBlocks, int adaptive, int fixedCodebook, int pairs, bool extended, bool pulsesInFirstBlock)
    {
        if (adaptive == 0)
        {
            // The hardcoded codebook: a start in the standard codebook and a gain; silence reads nothing per block.
            if (fixedCodebook == 1)
            {
                bits.Skip(8 + 6);
            }

            return;
        }

        if (fixedCodebook == 2)
        {
            // wmavoice.c: aw_pulse_set1 and aw_pulse_set2, then the 7-bit gain below.
            bits.Skip(12 - (extended && block == 0 ? 2 : 0));
            bits.Skip(pulsesInFirstBlock ? 5 - (2 * block) : 4);
            bits.Skip(1);
        }
        else
        {
            // Five pulse vectors, each with a sign and a position, and a second position for those that hold a pair.
            int positionBits = 5 - logBlocks;
            bits.Skip(5 + (5 * positionBits) + (pairs * positionBits));
        }

        bits.Skip(7);
    }
}
