using System.Buffers.Binary;
using System.Security.Cryptography;
using Encspect.Core;

namespace Encspect.Tests;

// Hashes what a decoder writes, as the .decode.json goldens of lossy fixtures hold it (pcmSha256, docs/design/lossy-decoders.md, decision 14): SHA-256 over
// the blocks' samples in the canonical layout (CanonicalPcm: interleaved, little-endian, floats as IEEE 754 singles), in stream order, with the new
// position as 8 little-endian bytes in front of the first block after a jump in positions, so that where a gap or a withheld span lies is pinned too. How
// a decoder cuts its blocks does not change the hash. The stream's format opens the hash, and a file with several streams needs a sink for each.
internal sealed class PcmHashSink : IPcmSink
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _bits;
    private long _next;
    private byte[] _buffer = [];

    public long Samples { get; private set; }

    // One sink hashes one stream; its format (rate, channels, bits, sample format) opens the hash.
    public void Format(PcmFormat format)
    {
        _bits = format.BitsPerSample;
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header, format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], format.BitsPerSample);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], (int)format.Format);
        _hash.AppendData(header);
    }

    public void Write(PcmBlock block)
    {
        if (block.Length == 0)
        {
            return;
        }

        if (block.Position != _next)
        {
            Span<byte> position = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(position, block.Position);
            _hash.AppendData(position);
        }

        int bytes = checked((int)CanonicalPcm.ByteCount(block, _bits));
        if (_buffer.Length < bytes)
        {
            _buffer = new byte[bytes];
        }

        CanonicalPcm.Write(block, _bits, _buffer);
        _hash.AppendData(_buffer.AsSpan(0, bytes));
        _next = block.Position + block.Length;
        Samples += block.Length;
    }

    public void End(long samples)
    {
    }

    public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());
}
