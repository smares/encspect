using System.Security.Cryptography;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// A sink that hashes the canonical layout of what a decoder writes (CanonicalPcm), as the manifests' decoded_pcm_md5 is, and keeps the stream's format.
internal sealed class PcmHashingSink : IPcmSink
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

    public long Samples { get; private set; }

    public int Bits { get; private set; }

    public SampleFormat Format { get; private set; }

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public int Declared { get; private set; }

    public IPcmSink Declare(DecodedStream stream)
    {
        Declared++;
        (Bits, Format, SampleRate, Channels) = (stream.Format.BitsPerSample, stream.Format.Format, stream.Format.SampleRate, stream.Format.Channels);
        return this;
    }

    public void Write(PcmBlock block)
    {
        var buffer = new byte[CanonicalPcm.ByteCount(block, Bits)];
        CanonicalPcm.Write(block, Bits, buffer);
        _hash.AppendData(buffer);
        Samples += block.Length;
    }

    public void End(long samples)
    {
    }

    public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());
}
