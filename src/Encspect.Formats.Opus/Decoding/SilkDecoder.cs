using static Encspect.Formats.Opus.SilkMath;

namespace Encspect.Formats.Opus.Decoding;

// The SILK decoder of one Opus stream (silk_Decode of dec_API.c, stereo_MS_to_LR.c of libopus 1.5.2: BSD-3-Clause, notice in THIRD-PARTY-NOTICES.md): the frames of a SILK
// part from the record of the entropy pass, the mid/side to left/right conversion, and the resampling to 48 kHz. The output is 16 bits, exact.
internal sealed class SilkDecoder(int channelsApi)
{
    private readonly SilkChannel[] _channels = [new SilkChannel(), new SilkChannel()];
    private readonly short[] _predPrevQ13 = new short[2];
    private readonly short[] _sMid = new short[2];
    private readonly short[] _sSide = new short[2];
    private int _channelsInternal;
    private int _apiChannelsMemory;
    private int _prevDecodeOnlyMiddle;
    private int _lastChannelsInternal = 1;
    private int _lastFsKHz = 16;

    // silk_ResetDecoder.
    public void Reset()
    {
        _channels[0].Init();
        _channels[1].Init();
        Array.Clear(_predPrevQ13);
        Array.Clear(_sMid);
        Array.Clear(_sSide);
        _prevDecodeOnlyMiddle = 0;
    }

    // Decodes the frames of the SILK part at r's position; the samples at 48 kHz go to pcm[c] from at, and the return is how many per channel.
    public int DecodeFrames(ref IntReader r, short[][] pcm, int at)
    {
        int channels = r.Next();
        int fsKHz = r.Next();
        int nbSubfr = r.Next();
        int frames = r.Next();
        _lastChannelsInternal = channels;
        _lastFsKHz = fsKHz;
        int total = 0;
        for (int call = 0; call < frames; call++)
        {
            total += Call(ref r, false, call == 0, channels, fsKHz, nbSubfr, pcm, at + total);
        }

        return total;
    }

    // The concealment of a frame of 10 ms with the settings of the last frame (silk_Decode with a lost packet); the first samples go to pcm.
    public void Conceal(short[][] pcm, int at, int samples)
    {
        var none = new IntReader([]);
        short[][] scratch = [new short[480], new short[480]];
        Call(ref none, true, true, _lastChannelsInternal, _lastFsKHz, 2, scratch, 0);
        for (int c = 0; c < channelsApi; c++)
        {
            Array.Copy(scratch[c], 0, pcm[c], at, samples);
        }
    }

    private int Call(ref IntReader r, bool lost, bool first, int channelsInternal, int fsKHz, int nbSubfr, short[][] pcm, int at)
    {
        if (first)
        {
            for (int n = 0; n < channelsInternal; n++)
            {
                _channels[n].FramesDecoded = 0;
            }
        }

        if (channelsInternal > _channelsInternal)
        {
            _channels[1].Init();
        }

        bool stereoToMono = channelsInternal == 1 && _channelsInternal == 2 && fsKHz * 1000 == 1000 * _channels[0].FsKHz;
        if (_channels[0].FramesDecoded == 0)
        {
            for (int n = 0; n < channelsInternal; n++)
            {
                _channels[n].SetFs(fsKHz, nbSubfr, 48000);
            }
        }

        if (channelsApi == 2 && channelsInternal == 2 && (_apiChannelsMemory == 1 || _channelsInternal == 1))
        {
            Array.Clear(_predPrevQ13);
            Array.Clear(_sSide);
            _channels[1].Resampler.CopyFrom(_channels[0].Resampler);
        }

        _apiChannelsMemory = channelsApi;
        _channelsInternal = channelsInternal;
        Span<int> msPred = stackalloc int[2];
        int decodeOnlyMiddle = 0;
        if (channelsInternal == 2)
        {
            if (!lost)
            {
                msPred[0] = r.Next();
                msPred[1] = r.Next();
                decodeOnlyMiddle = r.Next();
            }
            else
            {
                msPred[0] = _predPrevQ13[0];
                msPred[1] = _predPrevQ13[1];
            }
        }

        if (channelsInternal == 2 && decodeOnlyMiddle == 0 && _prevDecodeOnlyMiddle == 1)
        {
            _channels[1].RestartAfterMidOnly();
        }

        int frameLength = _channels[0].FrameLength;
        short[][] tmp = [new short[frameLength + 2], new short[frameLength + 2]];
        bool hasSide = lost ? _prevDecodeOnlyMiddle == 0 : decodeOnlyMiddle == 0;
        for (int n = 0; n < channelsInternal; n++)
        {
            if (n == 0 || hasSide)
            {
                SilkChannel channel = _channels[n];
                int frameIndex = _channels[0].FramesDecoded - n;
                if (!lost)
                {
                    SilkIndices indices = SilkIndices.Read(ref r, out int condCoding);
                    channel.DecodeFrame(tmp[n], 2, false, indices, condCoding, r.Take(channel.FrameLength));
                }
                else
                {
                    channel.DecodeFrame(tmp[n], 2, true, new SilkIndices(), 0, default);
                }

                _ = frameIndex;
            }
            else
            {
                Array.Clear(tmp[n], 2, frameLength);
            }

            _channels[n].FramesDecoded++;
        }

        int fs = _channels[0].FsKHz;
        if (channelsApi == 2 && channelsInternal == 2)
        {
            StereoMsToLr(tmp[0], tmp[1], msPred, fs, frameLength);
        }
        else
        {
            Array.Copy(_sMid, 0, tmp[0], 0, 2);
            Array.Copy(tmp[0], frameLength, _sMid, 0, 2);
        }

        int nOut = frameLength * 48000 / (fs * 1000);
        for (int n = 0; n < Math.Min(channelsApi, channelsInternal); n++)
        {
            _channels[n].Resampler.Process(pcm[n], at, tmp[n], 1, frameLength);
        }

        if (channelsApi == 2 && channelsInternal == 1)
        {
            if (stereoToMono)
            {
                _channels[1].Resampler.Process(pcm[1], at, tmp[0], 1, frameLength);
            }
            else
            {
                Array.Copy(pcm[0], at, pcm[1], at, nOut);
            }
        }

        if (lost)
        {
            for (int i = 0; i < _channelsInternal; i++)
            {
                _channels[i].LastGainIndex = 10;
            }
        }
        else
        {
            _prevDecodeOnlyMiddle = decodeOnlyMiddle;
        }

        return nOut;
    }

    // silk_stereo_MS_to_LR (stereo_MS_to_LR.c): x1 and x2 hold two samples of history before the frame.
    private void StereoMsToLr(short[] x1, short[] x2, ReadOnlySpan<int> predQ13, int fsKHz, int frameLength)
    {
        Array.Copy(_sMid, 0, x1, 0, 2);
        Array.Copy(_sSide, 0, x2, 0, 2);
        Array.Copy(x1, frameLength, _sMid, 0, 2);
        Array.Copy(x2, frameLength, _sSide, 0, 2);
        int pred0Q13 = _predPrevQ13[0];
        int pred1Q13 = _predPrevQ13[1];
        int denomQ16 = (1 << 16) / (8 * fsKHz);
        int delta0Q13 = RshiftRound(Smulbb(predQ13[0] - _predPrevQ13[0], denomQ16), 16);
        int delta1Q13 = RshiftRound(Smulbb(predQ13[1] - _predPrevQ13[1], denomQ16), 16);
        int n;
        for (n = 0; n < 8 * fsKHz; n++)
        {
            pred0Q13 += delta0Q13;
            pred1Q13 += delta1Q13;
            int sum = (x1[n] + x1[n + 2] + (x1[n + 1] << 1)) << 9;
            sum = Smlawb(x2[n + 1] << 8, sum, pred0Q13);
            sum = Smlawb(sum, x1[n + 1] << 11, pred1Q13);
            x2[n + 1] = (short)Sat16(RshiftRound(sum, 8));
        }

        pred0Q13 = predQ13[0];
        pred1Q13 = predQ13[1];
        for (; n < frameLength; n++)
        {
            int sum = (x1[n] + x1[n + 2] + (x1[n + 1] << 1)) << 9;
            sum = Smlawb(x2[n + 1] << 8, sum, pred0Q13);
            sum = Smlawb(sum, x1[n + 1] << 11, pred1Q13);
            x2[n + 1] = (short)Sat16(RshiftRound(sum, 8));
        }

        _predPrevQ13[0] = (short)predQ13[0];
        _predPrevQ13[1] = (short)predQ13[1];
        for (n = 0; n < frameLength; n++)
        {
            int sum = x1[n + 1] + x2[n + 1];
            int diff = x1[n + 1] - x2[n + 1];
            x1[n + 1] = (short)Sat16(sum);
            x2[n + 1] = (short)Sat16(diff);
        }
    }
}
