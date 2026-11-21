// Derived from libwavpack 5.6.0 (unpack3.c unpack_samples3), Copyright (c) 1998 - 2013 Conifer Software, Copyright (c) 1998 - 2022 David Bryant, under the BSD 3-clause license.
// The license text is in THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;

namespace Encspect.Formats.WavPack.Decoding;

// The sample loops of unpack3.c's unpack_samples3, one for each mode the header selects: version 3 lossless and lossy or hybrid, each in mono and stereo, and versions
// 1 and 2. Each decodes up to count samples of each channel into the planes of output (channel c from c * stride on) and returns how many it decoded: fewer where the
// bitstream ends (_isEof) or, in a stream still being fed, where fewer than Margin bytes are left (the caller waits for the next bytes). Integer arithmetic wraps as the
// C code's does on the machines it was written for. The muting of libwavpack (ATTEMPT_ERROR_MUTING) is not done: no sample leaves the analyzer but through the measurements.
internal sealed partial class LegacyStreamDecoder
{
    private const int MaxTerm = 8;

    private sealed class Pass
    {
        public int Term;
        public int WeightA;
        public int WeightB;
        public readonly int[] SamplesA = new int[MaxTerm];
        public readonly int[] SamplesB = new int[MaxTerm];
    }

    // The decorrelation terms of the modes (unpack3.c): each list is read from its end.
    private static readonly sbyte[] ExtremeTerms = [1, 1, 1, 2, 4, -1, 1, 2, 3, 6, -2, 8, 5, 7, 4, 1, 2, 3];
    private static readonly sbyte[] DefaultTerms = [1, 1, 1, -1, 2, 1, -2];
    private static readonly sbyte[] SimpleTerms = [1, 1, 1, 1];

    private Pass[] _passes = [];

    private void InitPasses()
    {
        var list = new List<Pass>();
        if ((_flags & LegacyHeader.ExtremeDecorrFlag) != 0)
        {
            for (int ti = 0; ti < ExtremeTerms.Length; ti++)
            {
                sbyte term = ExtremeTerms[ExtremeTerms.Length - ti - 1];
                if (term > 0 || (_flags & LegacyHeader.CrossDecorrFlag) != 0)
                {
                    list.Add(new Pass { Term = term });
                }
            }
        }
        else if ((_flags & LegacyHeader.NewDecorrFlag) != 0)
        {
            for (int ti = 0; ti < DefaultTerms.Length; ti++)
            {
                sbyte term = DefaultTerms[DefaultTerms.Length - ti - 1];
                if (term > 0 || (_flags & LegacyHeader.CrossDecorrFlag) != 0)
                {
                    list.Add(new Pass { Term = term });
                }
            }
        }
        else
        {
            for (int ti = 0; ti < SimpleTerms.Length; ti++)
            {
                list.Add(new Pass { Term = SimpleTerms[SimpleTerms.Length - ti - 1] });
            }
        }

        _passes = [.. list];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ApplyWeight(int bits, int weight, int sample) => ((weight * sample) + (1 << (bits - 1))) >> bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateWeight(int bits, ref int weight, int source, int result, int minWeight)
    {
        if (source != 0 && result != 0)
        {
            if ((source ^ result) >= 0)
            {
                if (weight++ == 1 << bits)
                {
                    weight--;
                }
            }
            else if (weight-- == minWeight)
            {
                weight++;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ApplyWeight24(int weight, int sample) => ((((sample & 0xffff) * weight) >> 7) + (((sample & ~0xffff) >> 7) * weight) + 1) >> 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateWeight2(ref int weight, int source, int result, int minWeight)
    {
        if (source != 0 && result != 0)
        {
            if ((source ^ result) >= 0)
            {
                if (weight++ == 256)
                {
                    weight--;
                }
            }
            else if (weight-- == minWeight)
            {
                weight++;
            }
        }
    }

    // The sample as the output takes it in the lossy modes: held to the range of the source's samples.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Clamp(int sample) => sample < _minValue ? _minShifted : sample > _maxValue ? _maxShifted : sample << _shift;

    private int Run(int count, Span<int> output, int stride)
    {
        if (_version == 3)
        {
            if (_bits == 0)
            {
                return _isMono ? LosslessMono(count, output) : LosslessStereo(count, output, stride);
            }

            return _isMono ? LossyMono(count, output) : LossyStereo(count, output, stride);
        }

        return _isMono ? Version2Mono(count, output) : Version2Stereo(count, output, stride);
    }

    // Reads the extra bits of a sample over 20 bits (4 bits of a channel's sample, 8 of two): run-length coded by the number of repeats.
    private int ExtraBits(int width)
    {
        if (_extraBitsCount < 8 || GetBit() == 0)
        {
            int extra = (int)GetBits(width);
            if (extra != _lastExtraBits)
            {
                _lastExtraBits = extra;
                _extraBitsCount = 0;
            }
            else
            {
                ++_extraBitsCount;
            }
        }

        return _lastExtraBits;
    }

    private int LosslessMono(int count, Span<int> output)
    {
        int flags = _flags;
        int shift = _shift;
        int crc = _crc;
        int m = _m;
        int i = 0;
        ref int s0 = ref _sample[0];
        ref int s1 = ref _sample[1];
        if ((flags & LegacyHeader.FastFlag) != 0)
        {
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt)
                {
                    break;
                }

                int word = GetWord3(0);
                if (word == WordEof)
                {
                    _isEof = true;
                    break;
                }

                s0 += s1 += word;
                if ((flags & LegacyHeader.Over20Flag) != 0)
                {
                    int temp = (int)GetBits(4);
                    crc = (crc * 3) + (temp = (temp & 0xf) + (s0 << 4));
                    output[i] = temp;
                }
                else
                {
                    crc = (crc * 3) + s0;
                    output[i] = s0 << shift;
                }
            }
        }
        else if ((flags & LegacyHeader.HighFlag) != 0)
        {
            bool isNew = (flags & LegacyHeader.NewHighFlag) != 0;
            bool isExtreme = (flags & LegacyHeader.ExtremeDecorrFlag) != 0;
            int bits = isExtreme ? 9 : 8;
            Pass[] passes = _passes;
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt)
                {
                    break;
                }

                int word = isNew ? GetWord1(0) : GetOldWord1(0);
                if (word == WordEof)
                {
                    _isEof = true;
                    break;
                }

                foreach (Pass dpp in passes)
                {
                    int sam = dpp.SamplesA[m];
                    int temp = ApplyWeight(bits, dpp.WeightA, sam) + word;
                    UpdateWeight(bits, ref dpp.WeightA, sam, word, _minWeight);
                    dpp.SamplesA[(m + dpp.Term) & (MaxTerm - 1)] = word = temp;
                }

                m = (m + 1) & (MaxTerm - 1);
                if ((flags & LegacyHeader.Over20Flag) != 0)
                {
                    int temp = ExtraBits(4) + (word << 4);
                    crc = (crc * 3) + temp;
                    output[i] = temp;
                }
                else
                {
                    crc = (crc * 3) + word;
                    output[i] = word << shift;
                }
            }
        }
        else
        {
            int weight = _weight[0];
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt)
                {
                    break;
                }

                int word = GetWord3(0);
                if (word == WordEof)
                {
                    _isEof = true;
                    break;
                }

                int temp = s0 + (((s1 * weight) + 128) >> 8) + word;
                if ((s1 >= 0) == (word > 0))
                {
                    if (weight++ == 256)
                    {
                        weight--;
                    }
                }
                else if (weight-- == 0)
                {
                    weight++;
                }

                s0 += s1 = temp - s0;
                if ((flags & LegacyHeader.Over20Flag) != 0)
                {
                    temp = ExtraBits(4) + (s0 << 4);
                    crc = (crc * 3) + temp;
                    output[i] = temp;
                }
                else
                {
                    crc = (crc * 3) + s0;
                    output[i] = s0 << shift;
                }
            }

            _weight[0] = weight;
        }

        _crc = crc;
        _m = m;
        return i;
    }

    private int LosslessStereo(int count, Span<int> output, int stride)
    {
        int flags = _flags;
        int shift = _shift;
        int crc = _crc;
        int m = _m;
        int i = 0;
        int leftLevel = _leftLevel;
        int rightLevel = _rightLevel;
        int sumLevel = _sumLevel;
        int diffLevel = _diffLevel;
        ref int l0 = ref _sample[0];
        ref int l1 = ref _sample[1];
        ref int r0 = ref _sample[2];
        ref int r1 = ref _sample[3];
        bool isOver20 = (flags & LegacyHeader.Over20Flag) != 0;
        if ((flags & LegacyHeader.FastFlag) != 0)
        {
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt)
                {
                    break;
                }

                int word = GetWord3(0);
                if (word == WordEof)
                {
                    _isEof = true;
                    break;
                }

                int diff;
                int sum = (word << 1) | ((diff = GetWord3(1)) & 1);
                if (isOver20)
                {
                    l0 += l1 += (sum + diff) >> 1;
                    r0 += r1 += (sum - diff) >> 1;
                    int temp = (int)GetBits(8);
                    int left = (l0 << 4) + ((temp >> 4) & 0xf);
                    int right = (r0 << 4) + (temp & 0xf);
                    crc = (crc * 3) + left;
                    crc = (crc * 3) + right;
                    output[i] = left;
                    output[stride + i] = right;
                }
                else
                {
                    l1 += (sum + diff) >> 1;
                    r1 += (sum - diff) >> 1;
                    crc = (crc * 3) + (l0 += l1);
                    crc = (crc * 3) + (r0 += r1);
                    output[i] = l0 << shift;
                    output[stride + i] = r0 << shift;
                }
            }
        }
        else if ((flags & LegacyHeader.HighFlag) != 0)
        {
            bool isCross = (flags & LegacyHeader.CrossDecorrFlag) != 0;
            bool isNew = (flags & LegacyHeader.NewHighFlag) != 0;
            bool isJoint = (flags & LegacyHeader.JointStereoFlag) != 0;
            bool isExtreme = (flags & LegacyHeader.ExtremeDecorrFlag) != 0;
            int bits = isExtreme ? 9 : 8;
            Pass[] passes = _passes;
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt)
                {
                    break;
                }

                int sum = 0;
                int diff = 0;
                int left;
                int right;
                if (isCross)
                {
                    left = GetWord1(0);
                    if (left == WordEof)
                    {
                        _isEof = true;
                        break;
                    }

                    right = GetWord1(1);
                }
                else
                {
                    int readWord;
                    int nextWord;
                    if (isNew)
                    {
                        readWord = GetWord1(0);
                        if (readWord == WordEof)
                        {
                            _isEof = true;
                            break;
                        }

                        nextWord = GetWord1(1);
                        if (rightLevel > leftLevel)
                        {
                            if (leftLevel + rightLevel < sumLevel + diffLevel && rightLevel < diffLevel)
                            {
                                sum = (right = readWord) + (left = nextWord);
                                diff = left - right;
                            }
                            else
                            {
                                diff = readWord;
                                if (sumLevel < leftLevel)
                                {
                                    sum = (nextWord << 1) | (diff & 1);
                                    left = (sum + diff) >> 1;
                                    right = (sum - diff) >> 1;
                                }
                                else
                                {
                                    sum = nextWord + (right = (left = nextWord) - diff);
                                }
                            }
                        }
                        else if (leftLevel + rightLevel < sumLevel + diffLevel && leftLevel < diffLevel)
                        {
                            sum = (left = readWord) + (right = nextWord);
                            diff = left - right;
                        }
                        else
                        {
                            diff = readWord;
                            if (sumLevel < rightLevel)
                            {
                                sum = (nextWord << 1) | (diff & 1);
                                left = (sum + diff) >> 1;
                                right = (sum - diff) >> 1;
                            }
                            else
                            {
                                sum = (left = diff + (right = nextWord)) + nextWord;
                            }
                        }
                    }
                    else
                    {
                        readWord = GetOldWord1(0);
                        if (readWord == WordEof)
                        {
                            _isEof = true;
                            break;
                        }

                        nextWord = GetOldWord1(1);
                        if (sumLevel <= rightLevel && sumLevel <= leftLevel)
                        {
                            sum = (nextWord << 1) | (readWord & 1);
                            left = (sum + readWord) >> 1;
                            right = (sum - readWord) >> 1;
                        }
                        else if (leftLevel <= rightLevel)
                        {
                            sum = nextWord + (right = (left = nextWord) - readWord);
                        }
                        else
                        {
                            sum = nextWord + (left = readWord + (right = nextWord));
                        }

                        diff = left - right;
                    }

                    sumLevel = (int)((sumLevel - (sumLevel >> 8)) + Math.Abs((long)(sum >> 1)));
                    leftLevel = (int)((leftLevel - (leftLevel >> 8)) + Math.Abs((long)left));
                    rightLevel = (int)((rightLevel - (rightLevel >> 8)) + Math.Abs((long)right));
                    diffLevel = (int)((diffLevel - (diffLevel >> 8)) + Math.Abs((long)diff));
                    if (isJoint)
                    {
                        left = diff;
                        right = sum >> 1;
                    }
                }

                foreach (Pass dpp in passes)
                {
                    int left2;
                    int right2;
                    if (dpp.Term > 0)
                    {
                        int samA = dpp.SamplesA[m];
                        int samB = dpp.SamplesB[m];
                        int k = (m + dpp.Term) & (MaxTerm - 1);
                        left2 = ApplyWeight(bits, dpp.WeightA, samA) + left;
                        right2 = ApplyWeight(bits, dpp.WeightB, samB) + right;
                        UpdateWeight(bits, ref dpp.WeightA, samA, left, _minWeight);
                        UpdateWeight(bits, ref dpp.WeightB, samB, right, _minWeight);
                        dpp.SamplesA[k] = left = left2;
                        dpp.SamplesB[k] = right = right2;
                    }
                    else if (dpp.Term == -1)
                    {
                        left2 = left + ApplyWeight(bits, dpp.WeightA, dpp.SamplesA[0]);
                        UpdateWeight(bits, ref dpp.WeightA, dpp.SamplesA[0], left, _minWeight);
                        left = left2;
                        right2 = right + ApplyWeight(bits, dpp.WeightB, left);
                        UpdateWeight(bits, ref dpp.WeightB, left, right, _minWeight);
                        dpp.SamplesA[0] = right = right2;
                    }
                    else
                    {
                        right2 = right + ApplyWeight(bits, dpp.WeightA, dpp.SamplesA[0]);
                        UpdateWeight(bits, ref dpp.WeightA, dpp.SamplesA[0], right, _minWeight);
                        right = right2;
                        left2 = left + ApplyWeight(bits, dpp.WeightB, right);
                        UpdateWeight(bits, ref dpp.WeightB, right, left, _minWeight);
                        dpp.SamplesA[0] = left = left2;
                    }
                }

                m = (m + 1) & (MaxTerm - 1);
                if (isJoint)
                {
                    sum = (right << 1) | ((diff = left) & 1);
                    right = (sum - diff) >> 1;
                    left = (sum + diff) >> 1;
                }

                if (isOver20)
                {
                    int extra = ExtraBits(8);
                    left = (left << 4) + (extra >> 4);
                    right = (right << 4) + (extra & 0xf);
                    crc = (crc * 3) + left;
                    crc = (crc * 3) + right;
                    output[i] = left;
                    output[stride + i] = right;
                }
                else
                {
                    crc = (crc * 9) + (left * 3) + right;
                    output[i] = left << shift;
                    output[stride + i] = right << shift;
                }
            }
        }
        else
        {
            int weightL = _weight[0];
            int weightR = _weight[1];
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt)
                {
                    break;
                }

                int word = GetWord3(0);
                if (word == WordEof)
                {
                    _isEof = true;
                    break;
                }

                int sum;
                int left;
                int right;
                if (sumLevel <= rightLevel && sumLevel <= leftLevel)
                {
                    sum = (GetWord3(1) << 1) | (word & 1);
                    left = (sum + word) >> 1;
                    right = (sum - word) >> 1;
                }
                else if (leftLevel <= rightLevel)
                {
                    right = (left = GetWord3(1)) - word;
                    sum = left + right;
                }
                else
                {
                    left = word + (right = GetWord3(1));
                    sum = right + left;
                }

                sumLevel = (int)((sumLevel - (sumLevel >> 8)) + Math.Abs((long)(sum >> 1)));
                leftLevel = (int)((leftLevel - (leftLevel >> 8)) + Math.Abs((long)left));
                rightLevel = (int)((rightLevel - (rightLevel >> 8)) + Math.Abs((long)right));

                int left2 = l0 + (((l1 * weightL) + 128) >> 8) + left;
                int right2 = r0 + (((r1 * weightR) + 128) >> 8) + right;
                if ((l1 >= 0) == (left > 0))
                {
                    if (weightL++ == 256)
                    {
                        weightL--;
                    }
                }
                else if (weightL-- == 0)
                {
                    weightL++;
                }

                if ((r1 >= 0) == (right > 0))
                {
                    if (weightR++ == 256)
                    {
                        weightR--;
                    }
                }
                else if (weightR-- == 0)
                {
                    weightR++;
                }

                l0 += l1 = left2 - l0;
                r0 += r1 = right2 - r0;
                if (isOver20)
                {
                    int extra = ExtraBits(8);
                    left2 = (l0 << 4) + (extra >> 4);
                    right2 = (r0 << 4) + (extra & 0xf);
                    crc = (crc * 3) + left2;
                    crc = (crc * 3) + right2;
                    output[i] = left2;
                    output[stride + i] = right2;
                }
                else
                {
                    crc = (crc * 9) + (l0 * 3) + r0;
                    output[i] = l0 << shift;
                    output[stride + i] = r0 << shift;
                }
            }

            _weight[0] = weightL;
            _weight[1] = weightR;
        }

        _leftLevel = leftLevel;
        _rightLevel = rightLevel;
        _sumLevel = sumLevel;
        _diffLevel = diffLevel;
        _crc = crc;
        _m = m;
        return i;
    }

    private int LossyMono(int count, Span<int> output)
    {
        int flags = _flags;
        int crc = _crc;
        int m = _m;
        int i = 0;
        ref int s0 = ref _sample[0];
        ref int s1 = ref _sample[1];
        if ((flags & LegacyHeader.FastFlag) != 0)
        {
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt)
                {
                    break;
                }

                int word = GetWord3(0);
                if (word == WordEof)
                {
                    _isEof = true;
                    break;
                }

                crc = (crc * 3) + (s0 += s1 += word);
                output[i] = Clamp(s0);
            }
        }
        else if ((flags & (LegacyHeader.HighFlag | LegacyHeader.NewHighFlag)) != 0)
        {
            bool isNew = (flags & LegacyHeader.NewHighFlag) != 0;
            bool isCorrected = HasCorrection;
            bool isShaped = (flags & LegacyHeader.LossyShapeFlag) != 0;
            Pass[] passes = _passes;
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt || IsCorrectionStarved())
                {
                    break;
                }

                int word = isNew ? GetWord4(0) : GetWord3(0);
                if (word == WordEof)
                {
                    _isEof = true;
                    break;
                }

                foreach (Pass dpp in passes)
                {
                    int sam = dpp.SamplesA[m];
                    int temp = ApplyWeight24(dpp.WeightA, sam) + word;
                    UpdateWeight2(ref dpp.WeightA, sam, word, _minWeight);
                    dpp.SamplesA[(m + dpp.Term) & (MaxTerm - 1)] = word = temp;
                }

                m = (m + 1) & (MaxTerm - 1);
                if (isCorrected)
                {
                    word += isShaped ? _correction[0] + _error[0] : _correction[0];
                    if (isShaped)
                    {
                        _error[0] = -_correction[0];
                    }

                    crc = (crc * 3) + word;
                    output[i] = word << _shift;
                }
                else
                {
                    crc = (crc * 3) + word;
                    output[i] = Clamp(word);
                }
            }
        }
        else
        {
            int weight = _weight[0];
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt)
                {
                    break;
                }

                int word = GetWord3(0);
                if (word == WordEof)
                {
                    _isEof = true;
                    break;
                }

                int newSample = s0 + (((s1 * weight) + 128) >> 8) + word;
                if ((s1 >= 0) == (word > 0))
                {
                    if (weight++ == 256)
                    {
                        weight--;
                    }
                }
                else if (weight-- == 0)
                {
                    weight++;
                }

                s1 = newSample - s0;
                crc = (crc * 3) + (s0 = newSample);
                output[i] = Clamp(s0);
            }

            _weight[0] = weight;
        }

        _crc = crc;
        _m = m;
        return i;
    }

    private int LossyStereo(int count, Span<int> output, int stride)
    {
        int flags = _flags;
        int crc = _crc;
        int m = _m;
        int i = 0;
        ref int l0 = ref _sample[0];
        ref int l1 = ref _sample[1];
        ref int r0 = ref _sample[2];
        ref int r1 = ref _sample[3];
        if ((flags & LegacyHeader.FastFlag) != 0)
        {
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt)
                {
                    break;
                }

                int word = GetWord3(0);
                if (word == WordEof)
                {
                    _isEof = true;
                    break;
                }

                crc = (crc * 3) + (l0 += l1 += word);
                output[i] = Clamp(l0);
                crc = (crc * 3) + (r0 += r1 += GetWord3(1));
                output[stride + i] = Clamp(r0);
            }
        }
        else if ((flags & (LegacyHeader.HighFlag | LegacyHeader.NewHighFlag)) != 0)
        {
            bool isNew = (flags & LegacyHeader.NewHighFlag) != 0;
            bool isJoint = (flags & LegacyHeader.JointStereoFlag) != 0;
            bool isCorrected = HasCorrection;
            bool isShaped = (flags & LegacyHeader.LossyShapeFlag) != 0;
            Pass[] passes = _passes;
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt || IsCorrectionStarved())
                {
                    break;
                }

                int left;
                int right;
                if (isNew)
                {
                    left = GetWord4(0);
                    right = GetWord4(1);
                }
                else
                {
                    left = GetWord3(0);
                    right = GetWord3(1);
                }

                if (left == WordEof)
                {
                    _isEof = true;
                    break;
                }

                foreach (Pass dpp in passes)
                {
                    int samA = dpp.SamplesA[m];
                    int samB = dpp.SamplesB[m];
                    int k = (m + dpp.Term) & (MaxTerm - 1);
                    int left2 = ApplyWeight24(dpp.WeightA, samA) + left;
                    UpdateWeight2(ref dpp.WeightA, samA, left, _minWeight);
                    dpp.SamplesA[k] = left = left2;
                    int right2 = ApplyWeight24(dpp.WeightB, samB) + right;
                    UpdateWeight2(ref dpp.WeightB, samB, right, _minWeight);
                    dpp.SamplesB[k] = right = right2;
                }

                m = (m + 1) & (MaxTerm - 1);
                if (isCorrected)
                {
                    if (isShaped)
                    {
                        left += _correction[0] + _error[0];
                        right += _correction[1] + _error[1];
                        _error[0] = -_correction[0];
                        _error[1] = -_correction[1];
                    }
                    else
                    {
                        left += _correction[0];
                        right += _correction[1];
                    }
                }

                if (isJoint)
                {
                    int sum;
                    int diff;
                    right = ((sum = (right << 1) | (left & 1)) - (diff = left)) >> 1;
                    left = (sum + diff) >> 1;
                }

                crc = (crc * 9) + (left * 3) + right;
                if (isCorrected)
                {
                    output[i] = left << _shift;
                    output[stride + i] = right << _shift;
                }
                else
                {
                    output[i] = Clamp(left);
                    output[stride + i] = Clamp(right);
                }
            }
        }
        else
        {
            int weightL = _weight[0];
            int weightR = _weight[1];
            for (; i < count; i++)
            {
                if (_end - _pos < _stopAt)
                {
                    break;
                }

                int word = GetWord3(0);
                if (word == WordEof)
                {
                    _isEof = true;
                    break;
                }

                int newSample = l0 + (((l1 * weightL) + 128) >> 8) + word;
                if ((l1 >= 0) == (word > 0))
                {
                    if (weightL++ == 256)
                    {
                        weightL--;
                    }
                }
                else if (weightL-- == 0)
                {
                    weightL++;
                }

                l1 = newSample - l0;
                crc = (crc * 3) + (l0 = newSample);

                word = GetWord3(1);
                newSample = r0 + (((r1 * weightR) + 128) >> 8) + word;
                if ((r1 >= 0) == (word > 0))
                {
                    if (weightR++ == 256)
                    {
                        weightR--;
                    }
                }
                else if (weightR-- == 0)
                {
                    weightR++;
                }

                r1 = newSample - r0;
                crc = (crc * 3) + (r0 = newSample);
                output[i] = Clamp(l0);
                output[stride + i] = Clamp(r0);
            }

            _weight[0] = weightL;
            _weight[1] = weightR;
        }

        _crc = crc;
        _m = m;
        return i;
    }

    // Versions 1 and 2 (and version 1's mono files, which 3.97's decoder reads and libwavpack's does not): sum and difference stereo over the fixed predictor, with the
    // lossy limit's clamp written back into the state.
    private int Version2Mono(int count, Span<int> output)
    {
        int shift = _shift;
        int i = 0;
        ref int s0 = ref _sample[0];
        ref int s1 = ref _sample[1];
        for (; i < count; i++)
        {
            if (_end - _pos < _stopAt)
            {
                break;
            }

            int word = GetWord2(0);
            if (word == WordEof)
            {
                _isEof = true;
                break;
            }

            s0 += s1 += word;
            if (_bits != 0)
            {
                s0 = s0 < _minValue ? _minValue : s0 > _maxValue ? _maxValue : s0;
            }

            output[i] = s0 << shift;
        }

        return i;
    }

    private int Version2Stereo(int count, Span<int> output, int stride)
    {
        int shift = _shift;
        int i = 0;
        ref int l0 = ref _sample[0];
        ref int l1 = ref _sample[1];
        ref int r0 = ref _sample[2];
        ref int r1 = ref _sample[3];
        for (; i < count; i++)
        {
            if (_end - _pos < _stopAt)
            {
                break;
            }

            int word = GetWord2(0);
            if (word == WordEof)
            {
                _isEof = true;
                break;
            }

            int diff;
            int sum = (word << 1) | ((diff = GetWord2(1)) & 1);
            l0 += l1 += (sum + diff) >> 1;
            r0 += r1 += (sum - diff) >> 1;
            if (_bits != 0)
            {
                l0 = l0 < _minValue ? _minValue : l0 > _maxValue ? _maxValue : l0;
                r0 = r0 < _minValue ? _minValue : r0 > _maxValue ? _maxValue : r0;
            }

            output[i] = l0 << shift;
            output[stride + i] = r0 << shift;
        }

        return i;
    }
}
