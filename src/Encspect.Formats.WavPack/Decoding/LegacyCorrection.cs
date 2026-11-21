// Derived from libwavpack 5.6.0 (unpack3.c: get_word4's correction bits, and the WVC_FLAG paths of unpack_samples3), Copyright (c) 1998 - 2013 Conifer Software,
// Copyright (c) 1998 - 2022 David Bryant, under the BSD 3-clause license. The license text is in THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.WavPack.Decoding;

// The correction bitstream of a WavPack 3 hybrid file (docs/design/correction-files.md, "Version 3"): a second bitstream with no header, read bit by bit beside the
// main one. Its bytes come with each frame (CodedFrame.Correction) as the parser feeds them in step with the main bitstream's; like the main one it reads bytes of 0xFF
// past its end (bs_read3), which the decoder counts as the correction ending before the samples do.
internal sealed partial class LegacyStreamDecoder
{
    private byte[] _cinput = [];
    private int _cpos;
    private int _cend;
    private ulong _csr;
    private int _cbc;
    private long _cfetched;
    private int _cStopAt;
    private bool _isCorrectionLast;

    // The correction each word reader left for the sample being decoded (get_word4's correction argument) and the error the noise shaping carries from one sample to the
    // next (unpack3.h's dc.error).
    private readonly int[] _correction = new int[2];
    private readonly int[] _error = new int[2];

    // Measured, not designed (docs/design/decoders.md): the correction bits' reading, the shaping of the error and the sum beyond the lossy decode of a sample of one channel.
    internal const long WorkPerCorrectedSample = 24;

    // Whether the stream is decoded with its correction file (libwavpack's wvc_flag, WVC_FLAG in the flags).
    private bool HasCorrection => _info.HasCorrection;

    // Takes the frame's correction bytes after those left over; where they are the file's last, the bits past them are read as 0xFF.
    private void AppendCorrection(in CodedFrame frame)
    {
        ReadOnlySpan<byte> data = frame.Correction is { } mapped ? mapped.Span : [];
        _context.AddWork(checked(DecodeWork.PerCodedByte * data.Length));
        _isCorrectionLast = frame.Correction is null || frame.Correction.FileEnd >= _info.CorrectionEnd;
        int left = _cend - _cpos;
        if (_cinput.Length < left + data.Length)
        {
            byte[] grown = new byte[left + data.Length];
            Buffer.BlockCopy(_cinput, _cpos, grown, 0, left);
            _cinput = grown;
        }
        else
        {
            Buffer.BlockCopy(_cinput, _cpos, _cinput, 0, left);
        }

        data.CopyTo(_cinput.AsSpan(left));
        _cpos = 0;
        _cend = left + data.Length;
        _cStopAt = _isCorrectionLast ? 0 : Margin;
    }

    // Whether the correction bytes held do not suffice for the next sample, which waits for the next frame.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCorrectionStarved() => HasCorrection && _cend - _cpos < _cStopAt;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte NextCorrectionByte()
    {
        _cfetched++;
        return _cpos < _cend ? _cinput[_cpos++] : (byte)0xFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetCorrectionBit()
    {
        if (_cbc == 0)
        {
            _csr = NextCorrectionByte();
            _cbc = 8;
        }

        int bit = (int)(_csr & 1);
        _csr >>= 1;
        _cbc--;
        return bit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint GetCorrectionBits(int count)
    {
        while (count > _cbc)
        {
            _csr |= (ulong)NextCorrectionByte() << _cbc;
            _cbc += 8;
        }

        uint value = (uint)_csr & (uint)((1UL << count) - 1);
        _csr >>= count;
        _cbc -= count;
        return value;
    }

    // The correction of the value get_word4 just read, which lay in [low, high] with mid as its estimate: the position inside that range, in as many bits as it takes to
    // tell its values apart, the shorter codes first.
    private int ReadCorrection(int mid, int low, int high)
    {
        if (high == low)
        {
            return 0;
        }

        uint maxcode = (uint)(high - low);
        int bitcount = CountBits(maxcode);
        uint extras = (uint)((1L << bitcount) - maxcode - 1);
        uint avalue = GetCorrectionBits(bitcount - 1) & BitMask(bitcount - 1);
        if (avalue >= extras)
        {
            avalue = (avalue << 1) - extras;
            if (GetCorrectionBit() != 0)
            {
                ++avalue;
            }
        }

        int value = mid < 0 ? high - (int)avalue : (int)avalue + low;
        return value - mid;
    }

    // Where the correction bitstream ended, once the samples are all decoded: whether it ran out of bytes first, and how many of its bytes the last bit read lies in.
    private void ReportCorrection()
    {
        long length = _info.CorrectionEnd - _info.CorrectionAt;
        _info.IsCorrectionShort = _cfetched > length;
        _info.CorrectionRead = Math.Min(_cfetched, length);
    }
}
