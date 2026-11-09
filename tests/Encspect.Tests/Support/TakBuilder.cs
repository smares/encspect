using System.Text;
using Encspect.Formats.Tak;

namespace Encspect.Tests;

// Builds TAK files object by object from takStream.pas and the departures docs/formats/tak.md lists (an object's size counts its CRC-24, the wave data has one CRC-24 at its end,
// the End object has neither a CRC-24 nor data, the padding has no CRC-24), so a test changes one thing and nothing else. The frames hold bytes in place of coded audio: the
// analyzer compares the CRC-24s of the metadata and does not read the frames' coding.
internal static class TakBuilder
{
    public static string FixturePath(string name) => Path.Combine(Fixtures.FixtureDirectory, "tak", name);

    public static byte[] Crc24(ReadOnlySpan<byte> data)
    {
        uint crc = Layout.Crc24(data);
        return [(byte)crc, (byte)(crc >> 8), (byte)(crc >> 16)];
    }

    // The bit structures of takStream.pas: items start at the least significant bits.
    public sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _position;

        public BitWriter Put(long value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_position / 8 >= _bytes.Count)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[_position / 8] |= (byte)(1 << (_position % 8));
                }

                _position++;
            }

            return this;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    // The stream info's bits. speakers adds the extension with a speaker assignment (one value per channel); validBits adds it without one.
    public static byte[] StreamInfoBits(int codec = 2, int profile = 2, int sizeType = 1, long samples = 44100, int rate = 44100, int bits = 16, int channels = 2, int dataType = 0,
        int[]? speakers = null, int? validBits = null, int? bitsCode = null)
    {
        BitWriter w = new BitWriter().Put(codec, 6).Put(profile, 4).Put(sizeType, 4).Put(samples & 0xFFFFFF, 24).Put(samples >> 24, 11).Put(dataType, 3).Put(rate - 6000, 18)
            .Put(bitsCode ?? (bits - 8), 5).Put(channels - 1, 4);
        bool hasExtension = speakers is not null || validBits is not null;
        w.Put(hasExtension ? 1 : 0, 1);
        if (hasExtension)
        {
            w.Put((validBits ?? bits) - 1, 5).Put(speakers is null ? 0 : 1, 1);
            foreach (int speaker in speakers ?? [])
            {
                w.Put(speaker, 6);
            }
        }

        return w.ToArray();
    }

    // An object: the header (ID, reserved bit, size), the data and, where crc is not null or the object has one, the CRC-24 that the size counts.
    public static byte[] Object(int id, byte[] data, bool hasCrc = true, uint? crc = null, bool reserved = false, int? size = null)
    {
        byte[] tail = hasCrc ? (crc is { } c ? [(byte)c, (byte)(c >> 8), (byte)(c >> 16)] : Crc24(data)) : [];
        int stated = size ?? (data.Length + tail.Length);
        uint header = (uint)id | (reserved ? 128u : 0) | ((uint)stated << 8);
        return [.. BitConverter.GetBytes(header), .. data, .. tail];
    }

    public static byte[] StreamInfo(int codec = 2, int profile = 2, int sizeType = 1, long samples = 44100, int rate = 44100, int bits = 16, int channels = 2, int dataType = 0,
        int[]? speakers = null, int? validBits = null, int? bitsCode = null, uint? crc = null) =>
        Object(1, StreamInfoBits(codec, profile, sizeType, samples, rate, bits, channels, dataType, speakers, validBits, bitsCode), crc: crc);

    // The version is the three digits, the major in the top byte.
    public static byte[] EncoderInfo(int major = 2, int minor = 3, int revision = 3, int preset = 2, int evaluation = 0, uint? crc = null) =>
        Object(4, [(byte)revision, (byte)minor, (byte)major, (byte)(preset | (evaluation << 4))], crc: crc);

    public static byte[] LastFrameInfo(long position, int size, uint? crc = null) =>
        Object(7, [.. BitConverter.GetBytes(position).AsSpan(0, 5).ToArray(), .. BitConverter.GetBytes(size).AsSpan(0, 3).ToArray()], crc: crc);

    public static byte[] WaveData(byte[]? header = null, byte[]? footer = null, uint? crc = null)
    {
        header ??= Wav();
        footer ??= [];
        byte[] data = [.. BitConverter.GetBytes(header.Length).AsSpan(0, 3).ToArray(), .. BitConverter.GetBytes(footer.Length).AsSpan(0, 3).ToArray(), .. header, .. footer];
        return Object(3, data, crc: crc);
    }

    public static byte[] Md5() => Object(6, Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());

    public static byte[] SeekTable(int length = 40) => Object(2, Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray());

    public static byte[] Padding(int length = 4) => Object(5, new byte[length], hasCrc: false);

    public static byte[] End() => [0, 0, 0, 0];

    // The 44-byte header of a WAVE file of 16-bit audio.
    public static byte[] Wav(int channels = 2, uint rate = 44100, int samples = 44100, byte[]? beforeData = null, byte[]? format = null)
    {
        format ??= OptimFrogBuilder.Format(channels, rate);
        return
        [
            .. "RIFF"u8, .. BitConverter.GetBytes(36 + (samples * channels * 2)), .. "WAVE"u8,
            .. "fmt "u8, .. BitConverter.GetBytes(format.Length), .. format,
            .. beforeData ?? [],
            .. "data"u8, .. BitConverter.GetBytes(samples * channels * 2),
        ];
    }

    // A frame: the header (sync, flags, frame number, the sample count of a last frame, and for an info frame the stream info) with its CRC-24, coded bytes and the CRC-24 over them.
    public static byte[] Frame(int number, int payload, bool isLast = false, bool isInfo = false, int samples = 1000, byte[]? streamInfoBits = null, uint? headerCrc = null, bool hasMetadata = false)
    {
        BitWriter w = new BitWriter().Put(0xA0FF, 16).Put(isLast ? 1 : 0, 1).Put(isInfo ? 1 : 0, 1).Put(hasMetadata ? 1 : 0, 1).Put(number, 21);
        if (isLast)
        {
            w.Put(samples - 1, 14).Put(0, 2);
        }

        if (isInfo)
        {
            byte[] info = streamInfoBits ?? StreamInfoBits();
            // The stream info's bits are a whole number of bytes except for the unused top bits of its last byte; the header is bit-exact, so the info is put in 1 bit at a time.
            var reader = new Layout.LsbBits(info);
            int used = StreamInfoFields.Read(ref reader)!.BitLength;
            for (int i = 0; i < used; i++)
            {
                w.Put((info[i / 8] >> (i % 8)) & 1, 1);
            }

            w.Put(0, 1).Put(0, 5);
        }

        byte[] header = w.ToArray();
        byte[] body = new byte[payload];
        uint state = (uint)(0x5EED + (number * 7919));
        for (int i = 0; i < payload; i++)
        {
            state = (state * 1103515245) + 12345;
            body[i] = (byte)(state >> 16);
        }

        return [.. header, .. headerCrc is { } c ? new[] { (byte)c, (byte)(c >> 8), (byte)(c >> 16) } : Crc24(header), .. body, .. Crc24(body)];
    }

    // The sync word followed by bytes that are no frame header.
    public static byte[] FalseSync() => [0xFF, 0xA0, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC];

    // A whole file: stream ID, stream info, last frame info, encoder info, wave data, End and two frames (the first an info frame), the last frame info stating the second.
    public static byte[] Standard(byte[]? streamInfo = null, byte[]? encoderInfo = null, byte[]? waveData = null, byte[]? lastFrameInfo = null, byte[]? extra = null, bool hasEnd = true, int payload = 300)
    {
        byte[] first = Frame(0, payload, isInfo: true);
        byte[] last = Frame(1, payload, isLast: true);
        return
        [
            .. "tBaK"u8, .. streamInfo ?? StreamInfo(), .. lastFrameInfo ?? LastFrameInfo(first.Length, last.Length), .. encoderInfo ?? EncoderInfo(), .. waveData ?? WaveData(),
            .. extra ?? [], .. hasEnd ? End() : [], .. first, .. last,
        ];
    }

    // The offset where the frames start in Standard() with the default objects.
    public static int AudioStart(params byte[][] objects) => 4 + objects.Sum(o => o.Length) + 4;

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
