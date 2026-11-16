namespace Encspect.Formats.Musepack;

// What the parser tells the decoder about a stream (DecodeSetup.Configuration): the stream version, the highest subband index the stream header allows, whether it allows
// mid/side stereo, the channel count and the sample rate, in 8 bytes, and whether libmpcdec refuses the stream (the decoder then opens none).
internal readonly record struct MusepackConfiguration(int Version, int MaxBand, bool MidSide, int Channels, int SampleRate, bool IsRefused = false)
{
    public const int Length = 8;

    public byte[] ToBytes()
    {
        var bytes = new byte[Length];
        bytes[0] = (byte)Version;
        bytes[1] = (byte)MaxBand;
        bytes[2] = (byte)((MidSide ? 1 : 0) | (IsRefused ? 2 : 0));
        bytes[3] = (byte)Channels;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), SampleRate);
        return bytes;
    }

    public static bool TryRead(ReadOnlySpan<byte> bytes, out MusepackConfiguration configuration)
    {
        configuration = default;
        if (bytes.Length < Length || bytes[0] is < 4 or > 8 || bytes[1] > 32)
        {
            return false;
        }

        configuration = new MusepackConfiguration(bytes[0], bytes[1], (bytes[2] & 1) != 0, bytes[3], System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]), (bytes[2] & 2) != 0);
        return configuration.SampleRate > 0 || configuration.IsRefused;
    }
}
