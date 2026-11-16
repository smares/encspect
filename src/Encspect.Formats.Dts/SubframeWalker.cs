using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Dts;

// The entropy pass over the subframes of a core frame (ETSI TS 102 114 5.5 and 5.6, with the pseudo-code of C.2): each subframe's side information, the vector quantized
// high-frequency subbands, the LFE samples and the samples of every other subband in its coding (plain, Huffman or block code). Run in every analysis, it finds the figures the
// reports need and the damage the headers cannot show; where a decode session lends buffers it also stores the values in the layout of LentLayout. An instance belongs to one
// analysis and serves one frame at a time.
internal sealed class SubframeWalker
{
    private const int Dsync = 0xFFFF;

    private static readonly int[] BlockLevels = [3, 5, 7, 9, 13, 17, 25];
    private static readonly int[] BlockBits = [7, 10, 12, 13, 15, 17, 19];
    private static readonly int[] BlockPower = [81, 625, 2401, 6561, 28561, 83521, 390625];

    // The Huffman books of each quantizer ABITS 1 to 10 (Table 5-26), by SEL.
    private static readonly string[][] QuantizerBooks =
    [
        ["A3"],
        ["A5", "B5", "C5"],
        ["A7", "B7", "C7"],
        ["A9", "B9", "C9"],
        ["A13", "B13", "C13"],
        ["A17", "B17", "C17", "D17", "E17", "F17", "G17"],
        ["A25", "B25", "C25", "D25", "E25", "F25", "G25"],
        ["A33", "B33", "C33", "D33", "E33", "F33", "G33"],
        ["A65", "B65", "C65", "D65", "E65", "F65", "G65"],
        ["A129", "B129", "C129", "D129", "E129", "F129", "G129"],
    ];

    private static readonly string[] TransientNames = ["A4", "B4", "C4", "D4"];
    private static readonly string[] ScaleNames = ["SA129", "SB129", "SC129", "SD129", "SE129"];
    private static readonly string[] AbitsNames = ["A12", "B12", "C12", "D12", "E12"];

    // The books of the tables above, resolved once.
    private static readonly HuffmanBook[][] Quantizer = [.. QuantizerBooks.Select(row => row.Select(HuffmanBook.Get).ToArray())];
    private static readonly HuffmanBook[] TransientBooks = [.. TransientNames.Select(HuffmanBook.Get)];
    private static readonly HuffmanBook[] ScaleBooks = [.. ScaleNames.Select(HuffmanBook.Get)];
    private static readonly HuffmanBook[] AbitsBooks = [.. AbitsNames.Select(HuffmanBook.Get)];

    private readonly int[][] _abits = NewRows(CoreAudioHeader.MaxChannels, 32);
    private readonly int[][] _pmode = NewRows(CoreAudioHeader.MaxChannels, 32);
    private readonly int[][] _pvq = NewRows(CoreAudioHeader.MaxChannels, 32);
    private readonly int[][] _tmode = NewRows(CoreAudioHeader.MaxChannels, 32);
    private readonly int[][] _scale0 = NewRows(CoreAudioHeader.MaxChannels, 32);
    private readonly int[][] _scale1 = NewRows(CoreAudioHeader.MaxChannels, 32);
    private readonly int[][] _join = NewRows(CoreAudioHeader.MaxChannels, 32);
    private readonly int[] _joinShuff = new int[CoreAudioHeader.MaxChannels];
    private readonly int[] _samples = new int[8];

    private byte[] _data = [];
    private int _pos;
    private int _limit;
    private bool _isOverrun;

    private static int[][] NewRows(int rows, int columns) => [.. Enumerable.Range(0, rows).Select(_ => new int[columns])];

    // Walks the subframes whose bits start at startBit of data (the frame's bytes in the 16-bit big endian form, with at least 8 spare bytes after them) and end at endBit. lent
    // holds the planes to fill, or is empty.
    public SubframeWalk Walk(byte[] data, int startBit, int endBit, CoreAudioHeader a, SampleBuffers lent, CancellationToken cancellation)
    {
        var walk = new SubframeWalk();
        _data = data;
        _pos = startBit;
        _limit = endBit;
        _isOverrun = false;
        int channels = a.Channels;
        if (channels is < 1 or > CoreAudioHeader.MaxChannels || a.Lff == 3)
        {
            Fail(walk, WalkProblem.Invalid, a.Lff == 3 ? "LFF 3 is reserved" : $"{channels} primary channels", 0);
            return walk;
        }

        if (!lent.IsEmpty)
        {
            StoreHeader(lent, a);
        }

        for (int sf = 0; sf < a.Subframes; sf++)
        {
            cancellation.ThrowIfCancellationRequested();
            int start = _pos;
            if (!Subframe(walk, a, sf, lent) || walk.Problem != WalkProblem.None)
            {
                if (walk.Problem == WalkProblem.None)
                {
                    Fail(walk, WalkProblem.Overrun, "the data ends inside it", sf);
                    walk.ProblemBit = start;
                }

                return walk;
            }

            walk.Subframes++;
        }

        return walk;
    }

    private void Fail(SubframeWalk walk, WalkProblem problem, string detail, int subframe)
    {

        walk.Problem = problem;
        walk.ProblemDetail = detail;
        walk.ProblemSubframe = subframe;
        walk.ProblemBit = _pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool Subframe(SubframeWalk walk, CoreAudioHeader a, int sf, SampleBuffers lent)
    {
        int channels = a.Channels;
        int ssc = (int)Read(2) + 1;
        int psc = (int)Read(3);

        if (psc != 0)
        {
            Fail(walk, WalkProblem.Partial, $"PSC is {psc}, a partial subsubframe", sf);
            return false;
        }

        bool store = !lent.IsEmpty;
        // PMODE, then the vectors of the prediction coefficients.
        for (int ch = 0; ch < channels; ch++)
        {
            for (int n = 0; n < a.Subs[ch]; n++)
            {
                _pmode[ch][n] = (int)Read(1);
            }

            Array.Clear(_pmode[ch], a.Subs[ch], 32 - a.Subs[ch]);
        }

        for (int ch = 0; ch < channels; ch++)
        {
            Array.Clear(_pvq[ch]);
            for (int n = 0; n < a.Subs[ch]; n++)
            {
                if (_pmode[ch][n] != 0)
                {
                    _pvq[ch][n] = (int)Read(12);
                }
            }
        }

        // Bit allocation of the subbands that are not vector quantized.
        for (int ch = 0; ch < channels; ch++)
        {
            Array.Clear(_abits[ch]);
            int vq = Math.Min(a.VqSub[ch], 32);
            int select = a.Bhuff[ch];
            if (select > 6)
            {
                Fail(walk, WalkProblem.Invalid, "BHUFF 7 is invalid", sf);
                return false;
            }

            for (int n = 0; n < vq; n++)
            {
                int abits = select switch
                {
                    5 => (int)Read(4),
                    6 => (int)Read(5),
                    _ => AbitsOfSymbol(AbitsBooks[select]),
                };
                if (abits > 26)
                {
                    Fail(walk, WalkProblem.Invalid, $"ABITS {abits} is invalid (Table 5-26)", sf);
                    return false;
                }

                _abits[ch][n] = abits;
            }
        }

        // Transient modes.
        for (int ch = 0; ch < channels; ch++)
        {
            Array.Clear(_tmode[ch]);
            if (ssc > 1)
            {
                HuffmanBook book = TransientBooks[a.Thuff[ch]];
                int vq = Math.Min(a.VqSub[ch], 32);
                for (int n = 0; n < vq; n++)
                {
                    if (_abits[ch][n] > 0)
                    {
                        _tmode[ch][n] = Symbol(book);
                    }
                }
            }
        }

        // Scale factors.
        for (int ch = 0; ch < channels; ch++)
        {
            Array.Clear(_scale0[ch]);
            Array.Clear(_scale1[ch]);
            int select = a.Shuff[ch];
            if (select > 6)
            {
                Fail(walk, WalkProblem.Invalid, "SHUFF 7 is invalid", sf);
                return false;
            }

            int sum = 0;
            int vq = Math.Min(a.VqSub[ch], 32);
            for (int n = 0; n < vq; n++)
            {
                if (_abits[ch][n] <= 0)
                {
                    continue;
                }

                if (!ScaleFactor(walk, select, ref sum, out _scale0[ch][n], sf))
                {
                    return false;
                }

                if (_tmode[ch][n] > 0 && !ScaleFactor(walk, select, ref sum, out _scale1[ch][n], sf))
                {
                    return false;
                }
            }

            for (int n = vq; n < a.Subs[ch]; n++)
            {
                if (!ScaleFactor(walk, select, ref sum, out _scale0[ch][n], sf))
                {
                    return false;
                }
            }
        }

        // Joint intensity coding.
        for (int ch = 0; ch < channels; ch++)
        {
            Array.Clear(_join[ch]);
            _joinShuff[ch] = 0;
            if (a.Joinx[ch] > 0)
            {
                _joinShuff[ch] = (int)Read(3);
            }
        }

        for (int ch = 0; ch < channels; ch++)
        {
            int source = a.Joinx[ch] - 1;
            if (source < 0 || source >= channels)
            {
                continue;
            }

            int select = _joinShuff[ch];
            if (select > 6)
            {
                Fail(walk, WalkProblem.Invalid, "JOIN_SHUFF 7 is invalid", sf);
                return false;
            }

            for (int n = a.Subs[ch]; n < a.Subs[source]; n++)
            {
                int index = select switch
                {
                    5 => SignExtend((int)Read(6), 6),
                    6 => SignExtend((int)Read(7), 7),
                    _ => Symbol(ScaleBooks[select]),
                };
                index += 64;
                if (index is < 0 or > 128)
                {
                    Fail(walk, WalkProblem.Invalid, $"the joint scale factor index {index - 64} is outside the table (D.3)", sf);
                    return false;
                }

                _join[ch][n] = BitConverter.SingleToInt32Bits((float)SubframeTables.JointScale[index]);
            }
        }

        int range = -1;
        if (a.HasDynamicRange)
        {
            range = (int)Read(8);
        }

        if (a.HasCrc)
        {
            Read(16);
        }

        // The side information is complete: the figures that need only it.
        int transients = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            int vq = Math.Min(a.VqSub[ch], 32);
            for (int n = 0; n < vq; n++)
            {
                if (_abits[ch][n] > 0)
                {
                    walk.Subbands = Math.Max(walk.Subbands, n + 1);
                    transients += _tmode[ch][n] > 0 ? 1 : 0;
                }
            }

            walk.Subbands = Math.Max(walk.Subbands, a.Subs[ch]);
            int source = a.Joinx[ch] - 1;
            if (source >= 0 && source < channels)
            {
                walk.Subbands = Math.Max(walk.Subbands, a.Subs[source]);
            }
        }

        if (transients > 0)
        {
            walk.TransientMask |= 1 << sf;
            walk.Transients += transients;
        }

        // High-frequency VQ addresses.
        if (store)
        {
            StoreSide(lent, a, sf, ssc, range);
        }

        for (int ch = 0; ch < channels; ch++)
        {
            int vq = Math.Min(a.VqSub[ch], 32);
            for (int n = vq; n < a.Subs[ch]; n++)
            {
                int index = (int)Read(10);
                if (store)
                {
                    lent.Channel(ch)[LentLayout.SamplesAt(sf, n)] = index;
                }
            }
        }

        // The LFE channel.
        if (a.Lff > 0)
        {
            int count = 2 * a.Lff * ssc;
            for (int i = 0; i < count; i++)
            {
                int value = SignExtend((int)Read(8), 8);
                if (store)
                {
                    lent.Channel(channels)[LentLayout.LfeAt(sf) + i] = value;
                }
            }

            int scaleIndex = (int)Read(8);
            if (store)
            {
                lent.Channel(channels)[LentLayout.Subframe(sf) + 3] = scaleIndex;
                lent.Channel(channels)[LentLayout.Subframe(sf)] = ssc;
            }
        }

        if (_isOverrun)
        {
            return false;
        }

        // The subsubframes.
        for (int sub = 0; sub < ssc; sub++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int vq = Math.Min(a.VqSub[ch], 32);
                for (int n = 0; n < vq; n++)
                {
                    int abits = _abits[ch][n];
                    if (abits == 0)
                    {
                        continue;
                    }

                    if (!Samples(walk, a, ch, abits, sf))
                    {
                        return false;
                    }

                    if (store)
                    {
                        Span<int> plane = lent.Channel(ch);
                        int at = LentLayout.SamplesAt(sf, n) + (sub * 8);
                        for (int m = 0; m < 8; m++)
                        {
                            plane[at + m] = _samples[m];
                        }
                    }
                }
            }

            if (_isOverrun)
            {
                return false;
            }

            if (sub == ssc - 1 || a.SyncPerSubsubframe)
            {
                int start = _pos;
                if (Read(16) != Dsync)
                {
                    walk.ProblemBit = start;
                    Fail(walk, WalkProblem.Sync, sub == ssc - 1 ? "the subframe does not end with the word 0xFFFF" : $"subsubframe {sub + 1} does not end with the word 0xFFFF", sf);
                    walk.ProblemBit = start;
                    return false;
                }
            }
        }

        walk.Samples += ssc * 8;
        return !_isOverrun;
    }

    // The quantization indexes of one subband's subsubframe in _samples.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool Samples(SubframeWalk walk, CoreAudioHeader a, int ch, int abits, int sf)
    {
        if (abits >= 11)
        {
            int bits = abits - 3;
            for (int m = 0; m < 8; m++)
            {
                _samples[m] = SignExtend((int)Read(bits), bits);
            }

            return true;
        }

        string[] books = QuantizerBooks[abits - 1];
        int sel = a.Sel[ch][abits - 1];
        if (sel < books.Length)
        {
            HuffmanBook book = Quantizer[abits - 1][sel];
            for (int m = 0; m < 8; m++)
            {
                _samples[m] = Symbol(book);
            }

            return true;
        }

        if (abits <= 7)
        {
            // The block code: 4 samples in each of 2 codes of BlockBits(abits) bits, element k of a code the remainder of the code divided by the levels, k times (C.3.2).
            int kind = abits <= 1 ? 0 : abits - 1;
            int levels = BlockLevels[kind];
            int offset = (levels - 1) / 2;
            for (int block = 0; block < 2; block++)
            {
                int code = (int)Read(BlockBits[kind]);
                if (code >= BlockPower[kind])
                {
                    Fail(walk, WalkProblem.Invalid, $"a block code of {code} is beyond the {BlockPower[kind]} codes of {levels} levels (C.3.2)", sf);
                    return false;
                }

                for (int k = 0; k < 4; k++)
                {
                    _samples[(block * 4) + k] = (code % levels) - offset;
                    code /= levels;
                }
            }

            return true;
        }

        // No further encoding: two's complement of abits - 3 bits.
        int width = abits - 3;
        for (int m = 0; m < 8; m++)
        {
            _samples[m] = SignExtend((int)Read(width), width);
        }

        return true;
    }

    // One scale factor index (5.5.1): a Huffman code of the difference to the sum so far, or a linear code of the index itself; the table's value, or 0 for an index it marks invalid.
    private bool ScaleFactor(SubframeWalk walk, int select, ref int sum, out int value, int sf)
    {
        int code = select switch
        {
            5 => (int)Read(6),
            6 => (int)Read(7),
            _ => Symbol(ScaleBooks[select]),
        };
        sum = select < 5 ? sum + code : code;
        ReadOnlySpan<int> table = select == 6 ? SubframeTables.ScaleFactor7 : SubframeTables.ScaleFactor6;
        if ((uint)sum >= (uint)table.Length || table[sum] < 0)
        {
            Fail(walk, WalkProblem.Invalid, $"the scale factor index {sum} is invalid (D.1)", sf);
            value = 0;
            return false;
        }

        value = table[sum];
        return true;
    }

    // The frame's header values in every plane (LentLayout).
    private static void StoreHeader(SampleBuffers lent, CoreAudioHeader a)
    {
        int planes = a.Channels + (a.Lff > 0 ? 1 : 0);
        for (int plane = 0; plane < Math.Min(planes, lent.Channels); plane++)
        {
            Span<int> p = lent.Channel(plane);
            bool isPrimary = plane < a.Channels;
            int source = isPrimary ? a.Joinx[plane] - 1 : -1;
            p[0] = a.Channels;
            p[1] = a.Subframes;
            p[2] = isPrimary ? a.Subs[plane] : 0;
            p[3] = isPrimary ? a.VqSub[plane] : 0;
            p[4] = isPrimary ? a.Joinx[plane] : 0;
            p[5] = a.IsPerfectReconstruction ? 1 : 0;
            p[6] = a.IsSumDifferenceFront ? 1 : 0;
            p[7] = a.IsSumDifferenceSurround ? 1 : 0;
            p[8] = a.Amode;
            p[9] = a.Lff;
            p[10] = a.HasPredictorHistory ? 1 : 0;
            p[11] = a.Sfreq;
            p[12] = a.Rate;
            p[13] = source >= 0 && source < a.Channels ? a.Subs[source] : 0;
            p[14] = a.Lff > 0 ? 1 : 0;
            p[15] = 0;
        }
    }

    private void StoreSide(SampleBuffers lent, CoreAudioHeader a, int sf, int ssc, int range)
    {
        int channels = a.Channels;
        for (int ch = 0; ch < channels; ch++)
        {
            Span<int> plane = lent.Channel(ch);
            int header = LentLayout.Subframe(sf);
            plane[header] = ssc;
            plane[header + 1] = 0;
            plane[header + 2] = range;
            plane[header + 3] = -1;
            int vq = Math.Min(a.VqSub[ch], 32);
            int source = a.Joinx[ch] - 1;
            for (int n = 0; n < 32; n++)
            {
                int meta = LentLayout.MetaAt(sf, n);
                int abits = n < vq ? _abits[ch][n] : n < a.Subs[ch] ? LentLayout.Vq : source >= 0 && source < channels && n < a.Subs[source] ? LentLayout.Joint : 0;
                plane[meta] = abits;
                plane[meta + 1] = _pmode[ch][n];
                plane[meta + 2] = _pvq[ch][n];
                plane[meta + 3] = _tmode[ch][n];
                plane[meta + 4] = _scale0[ch][n];
                plane[meta + 5] = _scale1[ch][n];
                plane[meta + 6] = _join[ch][n];
                plane[meta + 7] = abits is > 0 and <= 10 ? a.Adj[ch][abits - 1] : 0;
            }
        }
    }

    // A symbol of the book at the next bits.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Symbol(HuffmanBook book)
    {
        int symbol = book.Decode(Peek32(), out int length);
        _pos += length;
        if (_pos > _limit)
        {
            _isOverrun = true;
        }

        return symbol;
    }

    // The quantizer index of a Huffman-coded ABITS: the books of D.5.6 list ABITS 1 to 12, which are the values (ABITS 0 has no code in them).
    private int AbitsOfSymbol(HuffmanBook book) => Symbol(book);

    private static int SignExtend(int value, int bits) => (value << (32 - bits)) >> (32 - bits);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private uint Peek32()
    {
        int index = _pos >> 3;
        if (index + 8 > _data.Length)
        {
            _isOverrun = true;
            return 0;
        }

        ulong window = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(index));
        return (uint)(window >> (32 - (_pos & 7)));
    }

    // Up to 24 bits.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private uint Read(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        uint value = Peek32() >> (32 - count);
        _pos += count;
        if (_pos > _limit)
        {
            _isOverrun = true;
        }

        return value;
    }
}
