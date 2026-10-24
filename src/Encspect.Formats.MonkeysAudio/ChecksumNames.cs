namespace Encspect.Formats.MonkeysAudio;

// The names the checksums of a Monkey's Audio stream are declared with (StreamVerification.Checksums), which the decoder's results find their
// tally by.
internal static class ChecksumNames
{
    public const string DescriptorMd5 = "descriptor MD5";
    public const string FrameCrc = "frame CRC-32";
}
