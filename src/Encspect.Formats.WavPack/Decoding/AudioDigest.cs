using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.WavPack.Decoding;

// The MD5 of the decoded audio, in the layout the stored one was worked out over: wvunpack's (cli/wvunpack.c unpack_audio and
// unpack_dsd_audio) with the file's qualify mode (wavpack.h QMODE_*): interleaved samples of the stream's bytes per sample,
// little-endian and signed except 8-bit audio, which is unsigned, unless the mode says big-endian, signed bytes or unsigned
// words, with the channels reordered to the source's order where the mode says so. DSD is the bytes as they are, or, for the
// layout of DSF files, blocks of 4,096 bytes of each channel in turn, with the bits of each byte reversed when the mode says
// least significant bit first, the last block padded with zeros.
internal sealed class AudioDigest
{
    public const int QmodeBigEndian = 0x1;
    public const int QmodeSignedBytes = 0x2;
    public const int QmodeUnsignedWords = 0x4;
    public const int QmodeReorderedChannels = 0x8;
    public const int QmodeDsdLsbFirst = 0x10;
    public const int QmodeDsdInBlocks = 0x40;

    private const int DsdBlock = 4096;
    private const int ChunkBytes = 1 << 16;

    private readonly Md5 _md5 = new();
    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly int _qmode;
    private readonly bool _isDsd;
    private readonly int[]? _order;
    private byte[] _bytes = [];
    private byte[] _dsdBlock = [];
    private int _dsdFill;

    public AudioDigest(int channels, int bytesPerSample, int qmode, bool isDsd, byte[]? order)
    {
        _channels = channels;
        _bytesPerSample = bytesPerSample;
        _qmode = qmode;
        _isDsd = isDsd;
        _order = order is null ? null : [.. order.Take(channels).Select(i => (int)i)];
        if (isDsd && (qmode & QmodeDsdInBlocks) != 0)
        {
            _dsdBlock = new byte[channels * DsdBlock];
        }
    }

    // The channels of a group, planar with a stride of samples; channel c of the output is plane Source(c).
    public void Append(ReadOnlySpan<int> planes, int samples)
    {
        if (_isDsd)
        {
            AppendDsd(planes, samples);
            return;
        }

        int perSample = _channels * _bytesPerSample;
        int chunk = Math.Max(1, ChunkBytes / perSample);
        if (_bytes.Length < chunk * perSample)
        {
            _bytes = new byte[chunk * perSample];
        }

        bool isUnsigned = (_qmode & QmodeUnsignedWords) != 0 || (_bytesPerSample == 1 && (_qmode & QmodeSignedBytes) == 0);
        bool isBigEndian = (_qmode & QmodeBigEndian) != 0;
        for (int start = 0; start < samples; start += chunk)
        {
            int count = Math.Min(chunk, samples - start);
            int at = 0;
            for (int i = 0; i < count; i++)
            {
                for (int c = 0; c < _channels; c++)
                {
                    int plane = _order is not null && (uint)c < (uint)_order.Length ? _order[c] : c;
                    int value = planes[(plane * samples) + start + i];
                    if (isUnsigned)
                    {
                        value = unchecked(value + (int)(0x80u << (8 * (_bytesPerSample - 1))));
                    }

                    for (int b = 0; b < _bytesPerSample; b++)
                    {
                        int shift = isBigEndian ? 8 * (_bytesPerSample - 1 - b) : 8 * b;
                        _bytes[at++] = (byte)(value >> shift);
                    }
                }
            }

            _md5.AppendData(_bytes.AsSpan(0, at));
        }
    }

    private void AppendDsd(ReadOnlySpan<int> planes, int samples)
    {
        if (_dsdBlock.Length == 0)
        {
            int chunk = Math.Max(1, ChunkBytes / _channels);
            if (_bytes.Length < chunk * _channels)
            {
                _bytes = new byte[chunk * _channels];
            }

            for (int start = 0; start < samples; start += chunk)
            {
                int count = Math.Min(chunk, samples - start);
                int at = 0;
                for (int i = 0; i < count; i++)
                {
                    for (int c = 0; c < _channels; c++)
                    {
                        int plane = _order is not null && (uint)c < (uint)_order.Length ? _order[c] : c;
                        _bytes[at++] = (byte)planes[(plane * samples) + start + i];
                    }
                }

                _md5.AppendData(_bytes.AsSpan(0, at));
            }

            return;
        }

        bool isLsbFirst = (_qmode & QmodeDsdLsbFirst) != 0;
        for (int i = 0; i < samples; i++)
        {
            for (int c = 0; c < _channels; c++)
            {
                int plane = _order is not null && (uint)c < (uint)_order.Length ? _order[c] : c;
                byte value = (byte)planes[(plane * samples) + i];
                _dsdBlock[(c * DsdBlock) + _dsdFill] = isLsbFirst ? ReverseBits(value) : value;
            }

            if (++_dsdFill == DsdBlock)
            {
                _md5.AppendData(_dsdBlock);
                _dsdFill = 0;
            }
        }
    }

    // The hash of everything appended; a partial last block of DSD blocks is padded with zeros first.
    public void Finish(Span<byte> destination)
    {
        if (_dsdFill > 0)
        {
            for (int c = 0; c < _channels; c++)
            {
                _dsdBlock.AsSpan((c * DsdBlock) + _dsdFill, DsdBlock - _dsdFill).Clear();
            }

            _md5.AppendData(_dsdBlock);
            _dsdFill = 0;
        }

        _md5.GetHashAndReset(destination);
    }

    private static byte ReverseBits(byte value)
    {
        int v = value;
        v = ((v & 0xF0) >> 4) | ((v & 0x0F) << 4);
        v = ((v & 0xCC) >> 2) | ((v & 0x33) << 2);
        v = ((v & 0xAA) >> 1) | ((v & 0x55) << 1);
        return (byte)v;
    }
}
