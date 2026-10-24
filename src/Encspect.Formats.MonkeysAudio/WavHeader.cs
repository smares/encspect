using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

// What kind of file the stored header data begins like, to name it in the report.
internal static class WavHeader
{
    public static string? Container(ByteReader reader, long at, long length)
    {
        if (length < 12 || at + 12 > reader.End)
        {
            return null;
        }

        byte[] head = reader.ReadBytesAt(at, 12);
        ReadOnlySpan<byte> id = head.AsSpan(0, 4);
        ReadOnlySpan<byte> kind = head.AsSpan(8, 4);
        if ((id.SequenceEqual("RIFF"u8) || id.SequenceEqual("RF64"u8)) && kind.SequenceEqual("WAVE"u8))
        {
            return id.SequenceEqual("RF64"u8) ? "an RF64 WAVE header" : "a RIFF WAVE header";
        }

        if (id.SequenceEqual("FORM"u8))
        {
            return "an AIFF header";
        }

        return id.SequenceEqual("caff"u8) ? "a CAF header" : null;
    }
}
