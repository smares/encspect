using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Encspect.Core;

// MD5 (RFC 1321). System.Security.Cryptography's MD5 goes to the system's OpenSSL on Linux, which refuses MD5 in FIPS
// mode, so verification would fail on such a system. The ID3 parser's SHA256.HashData takes the same path, but
// SHA-256 is approved in FIPS mode. One instance hashes one message at a time and is not thread-safe.
public sealed class Md5
{
    public const int HashLength = 16;

    private readonly byte[] _buffer = new byte[64];
    private int _buffered;
    private long _length;
    private uint _a = 0x67452301;
    private uint _b = 0xefcdab89;
    private uint _c = 0x98badcfe;
    private uint _d = 0x10325476;

    public void AppendData(ReadOnlySpan<byte> data)
    {
        _length += data.Length;

        if (_buffered > 0)
        {
            int take = Math.Min(64 - _buffered, data.Length);
            data[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            data = data[take..];
            if (_buffered < 64)
            {
                return;
            }

            Transform(_buffer);
            _buffered = 0;
        }

        while (data.Length >= 64)
        {
            Transform(data);
            data = data[64..];
        }

        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    // The 16 bytes of the hash of everything appended; the instance then starts a new message.
    public void GetHashAndReset(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, HashLength, nameof(destination));

        long bits = _length * 8;
        Span<byte> padding = stackalloc byte[72];
        padding.Clear();
        padding[0] = 0x80;
        int paddingLength = _buffered < 56 ? 56 - _buffered : 120 - _buffered;
        BinaryPrimitives.WriteInt64LittleEndian(padding[paddingLength..], bits);
        AppendData(padding[..(paddingLength + 8)]);

        BinaryPrimitives.WriteUInt32LittleEndian(destination, _a);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], _b);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], _c);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], _d);

        _buffered = 0;
        _length = 0;
        (_a, _b, _c, _d) = (0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint F(uint x, uint y, uint z) => z ^ (x & (y ^ z));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint G(uint x, uint y, uint z) => y ^ (z & (x ^ y));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint H(uint x, uint y, uint z) => x ^ y ^ z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint I(uint x, uint y, uint z) => y ^ (x | ~z);

    // The 64 steps of RFC 1321, section 3.4, unrolled: step i adds the constant floor(2^32 * abs(sin(i + 1))).
    private void Transform(ReadOnlySpan<byte> block)
    {
        uint x0 = BinaryPrimitives.ReadUInt32LittleEndian(block[0..]);
        uint x1 = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        uint x2 = BinaryPrimitives.ReadUInt32LittleEndian(block[8..]);
        uint x3 = BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
        uint x4 = BinaryPrimitives.ReadUInt32LittleEndian(block[16..]);
        uint x5 = BinaryPrimitives.ReadUInt32LittleEndian(block[20..]);
        uint x6 = BinaryPrimitives.ReadUInt32LittleEndian(block[24..]);
        uint x7 = BinaryPrimitives.ReadUInt32LittleEndian(block[28..]);
        uint x8 = BinaryPrimitives.ReadUInt32LittleEndian(block[32..]);
        uint x9 = BinaryPrimitives.ReadUInt32LittleEndian(block[36..]);
        uint x10 = BinaryPrimitives.ReadUInt32LittleEndian(block[40..]);
        uint x11 = BinaryPrimitives.ReadUInt32LittleEndian(block[44..]);
        uint x12 = BinaryPrimitives.ReadUInt32LittleEndian(block[48..]);
        uint x13 = BinaryPrimitives.ReadUInt32LittleEndian(block[52..]);
        uint x14 = BinaryPrimitives.ReadUInt32LittleEndian(block[56..]);
        uint x15 = BinaryPrimitives.ReadUInt32LittleEndian(block[60..]);

        uint a = _a;
        uint b = _b;
        uint c = _c;
        uint d = _d;

        a = b + BitOperations.RotateLeft(a + F(b, c, d) + 0xd76aa478 + x0, 7);
        d = a + BitOperations.RotateLeft(d + F(a, b, c) + 0xe8c7b756 + x1, 12);
        c = d + BitOperations.RotateLeft(c + F(d, a, b) + 0x242070db + x2, 17);
        b = c + BitOperations.RotateLeft(b + F(c, d, a) + 0xc1bdceee + x3, 22);
        a = b + BitOperations.RotateLeft(a + F(b, c, d) + 0xf57c0faf + x4, 7);
        d = a + BitOperations.RotateLeft(d + F(a, b, c) + 0x4787c62a + x5, 12);
        c = d + BitOperations.RotateLeft(c + F(d, a, b) + 0xa8304613 + x6, 17);
        b = c + BitOperations.RotateLeft(b + F(c, d, a) + 0xfd469501 + x7, 22);
        a = b + BitOperations.RotateLeft(a + F(b, c, d) + 0x698098d8 + x8, 7);
        d = a + BitOperations.RotateLeft(d + F(a, b, c) + 0x8b44f7af + x9, 12);
        c = d + BitOperations.RotateLeft(c + F(d, a, b) + 0xffff5bb1 + x10, 17);
        b = c + BitOperations.RotateLeft(b + F(c, d, a) + 0x895cd7be + x11, 22);
        a = b + BitOperations.RotateLeft(a + F(b, c, d) + 0x6b901122 + x12, 7);
        d = a + BitOperations.RotateLeft(d + F(a, b, c) + 0xfd987193 + x13, 12);
        c = d + BitOperations.RotateLeft(c + F(d, a, b) + 0xa679438e + x14, 17);
        b = c + BitOperations.RotateLeft(b + F(c, d, a) + 0x49b40821 + x15, 22);
        a = b + BitOperations.RotateLeft(a + G(b, c, d) + 0xf61e2562 + x1, 5);
        d = a + BitOperations.RotateLeft(d + G(a, b, c) + 0xc040b340 + x6, 9);
        c = d + BitOperations.RotateLeft(c + G(d, a, b) + 0x265e5a51 + x11, 14);
        b = c + BitOperations.RotateLeft(b + G(c, d, a) + 0xe9b6c7aa + x0, 20);
        a = b + BitOperations.RotateLeft(a + G(b, c, d) + 0xd62f105d + x5, 5);
        d = a + BitOperations.RotateLeft(d + G(a, b, c) + 0x02441453 + x10, 9);
        c = d + BitOperations.RotateLeft(c + G(d, a, b) + 0xd8a1e681 + x15, 14);
        b = c + BitOperations.RotateLeft(b + G(c, d, a) + 0xe7d3fbc8 + x4, 20);
        a = b + BitOperations.RotateLeft(a + G(b, c, d) + 0x21e1cde6 + x9, 5);
        d = a + BitOperations.RotateLeft(d + G(a, b, c) + 0xc33707d6 + x14, 9);
        c = d + BitOperations.RotateLeft(c + G(d, a, b) + 0xf4d50d87 + x3, 14);
        b = c + BitOperations.RotateLeft(b + G(c, d, a) + 0x455a14ed + x8, 20);
        a = b + BitOperations.RotateLeft(a + G(b, c, d) + 0xa9e3e905 + x13, 5);
        d = a + BitOperations.RotateLeft(d + G(a, b, c) + 0xfcefa3f8 + x2, 9);
        c = d + BitOperations.RotateLeft(c + G(d, a, b) + 0x676f02d9 + x7, 14);
        b = c + BitOperations.RotateLeft(b + G(c, d, a) + 0x8d2a4c8a + x12, 20);
        a = b + BitOperations.RotateLeft(a + H(b, c, d) + 0xfffa3942 + x5, 4);
        d = a + BitOperations.RotateLeft(d + H(a, b, c) + 0x8771f681 + x8, 11);
        c = d + BitOperations.RotateLeft(c + H(d, a, b) + 0x6d9d6122 + x11, 16);
        b = c + BitOperations.RotateLeft(b + H(c, d, a) + 0xfde5380c + x14, 23);
        a = b + BitOperations.RotateLeft(a + H(b, c, d) + 0xa4beea44 + x1, 4);
        d = a + BitOperations.RotateLeft(d + H(a, b, c) + 0x4bdecfa9 + x4, 11);
        c = d + BitOperations.RotateLeft(c + H(d, a, b) + 0xf6bb4b60 + x7, 16);
        b = c + BitOperations.RotateLeft(b + H(c, d, a) + 0xbebfbc70 + x10, 23);
        a = b + BitOperations.RotateLeft(a + H(b, c, d) + 0x289b7ec6 + x13, 4);
        d = a + BitOperations.RotateLeft(d + H(a, b, c) + 0xeaa127fa + x0, 11);
        c = d + BitOperations.RotateLeft(c + H(d, a, b) + 0xd4ef3085 + x3, 16);
        b = c + BitOperations.RotateLeft(b + H(c, d, a) + 0x04881d05 + x6, 23);
        a = b + BitOperations.RotateLeft(a + H(b, c, d) + 0xd9d4d039 + x9, 4);
        d = a + BitOperations.RotateLeft(d + H(a, b, c) + 0xe6db99e5 + x12, 11);
        c = d + BitOperations.RotateLeft(c + H(d, a, b) + 0x1fa27cf8 + x15, 16);
        b = c + BitOperations.RotateLeft(b + H(c, d, a) + 0xc4ac5665 + x2, 23);
        a = b + BitOperations.RotateLeft(a + I(b, c, d) + 0xf4292244 + x0, 6);
        d = a + BitOperations.RotateLeft(d + I(a, b, c) + 0x432aff97 + x7, 10);
        c = d + BitOperations.RotateLeft(c + I(d, a, b) + 0xab9423a7 + x14, 15);
        b = c + BitOperations.RotateLeft(b + I(c, d, a) + 0xfc93a039 + x5, 21);
        a = b + BitOperations.RotateLeft(a + I(b, c, d) + 0x655b59c3 + x12, 6);
        d = a + BitOperations.RotateLeft(d + I(a, b, c) + 0x8f0ccc92 + x3, 10);
        c = d + BitOperations.RotateLeft(c + I(d, a, b) + 0xffeff47d + x10, 15);
        b = c + BitOperations.RotateLeft(b + I(c, d, a) + 0x85845dd1 + x1, 21);
        a = b + BitOperations.RotateLeft(a + I(b, c, d) + 0x6fa87e4f + x8, 6);
        d = a + BitOperations.RotateLeft(d + I(a, b, c) + 0xfe2ce6e0 + x15, 10);
        c = d + BitOperations.RotateLeft(c + I(d, a, b) + 0xa3014314 + x6, 15);
        b = c + BitOperations.RotateLeft(b + I(c, d, a) + 0x4e0811a1 + x13, 21);
        a = b + BitOperations.RotateLeft(a + I(b, c, d) + 0xf7537e82 + x4, 6);
        d = a + BitOperations.RotateLeft(d + I(a, b, c) + 0xbd3af235 + x11, 10);
        c = d + BitOperations.RotateLeft(c + I(d, a, b) + 0x2ad7d2bb + x2, 15);
        b = c + BitOperations.RotateLeft(b + I(c, d, a) + 0xeb86d391 + x9, 21);

        _a += a;
        _b += b;
        _c += c;
        _d += d;
    }
}
