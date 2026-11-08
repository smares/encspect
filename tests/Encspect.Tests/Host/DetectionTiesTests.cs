using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Inputs built in code on which two formats reach the same class, so that the order of the parser
// list would decide if the evidence did not: each is detected as the evidence says, in the default
// order and in five shuffled ones. The fixtures of DetectionOrderTests hold one such case, the
// look-alike ADTS chain in E-AC-3 frames; these are the others the classes allow.
public class DetectionTiesTests
{
    private static readonly int[] Seeds = [3, 17, 101, 2026, 31337];

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));

    // Bytes that start no format's sync word: none is 0x0B, 0x1F, 0x58, 0x64, 0x7F, 0xFE or 0xFF.
    private static byte[] Junk(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(0x80 | (i % 0x40)))];

    private static SniffResult Sniff(IFormatParser parser, byte[] data) => parser.Sniff(new SniffContext(new ByteReader(new MemoryStream(data))));

    private static IFormatParser Parser(string name) => DefaultParsers.Formats.Single(p => p.Name == name);

    public static TheoryData<string> Cases() => ["wavpack-block-cut-short-then-mpeg", "musepack-sv5-with-ogg-pages", "ac3-before-dts", "dts-before-ac3"];

    private static (byte[] Data, string Expected) Build(string name)
    {
        switch (name)
        {
            case "wavpack-block-cut-short-then-mpeg":
                {
                    // A WavPack block header whose block runs past the end of the data (class 90), followed by MPEG frames (class 90): the
                    // header is first.
                    byte[] wavpack = Fixture("wavpack/built-ape-and-id3v1.wv");
                    byte[] mp3 = Fixture("mpeg/external-lame-cbr128-joint.mp3");
                    int sync = Array.FindIndex(mp3, i => i != 0);
                    while (mp3[sync] != 0xFF || (mp3[sync + 1] & 0xE0) != 0xE0)
                    {
                        sync++;
                    }

                    byte[] data = [.. wavpack[..32], .. mp3[sync..(sync + 1700)]];
                    Assert.Equal(90, Sniff(Parser("WavPack"), data).Class);
                    Assert.Equal(90, Sniff(Parser("MPEG Audio"), data).Class);
                    return (data, "WavPack");
                }

            case "musepack-sv5-with-ogg-pages":
                {
                    // A stream of version 5 of two units (class 60) whose first unit holds two Ogg pages (class 60 further in).
                    byte[] ogg = Fixture("ogg/external-speexenc-nb.spx");
                    int third = 0;
                    for (int pages = 0, at = 0; pages < 3; pages++)
                    {
                        third = at = ogg.AsSpan(at + 1).IndexOf("OggS"u8) is var found and >= 0 ? at + 1 + found : ogg.Length;
                    }

                    byte[] pages2 = ogg[..third];
                    const int payloadStart = 16;
                    int firstBits = ((payloadStart + pages2.Length + 12) * 8) - 84;
                    int secondBits = 12;
                    long totalBits = 84 + firstBits + 20 + secondBits;
                    totalBits = (totalBits + 31) / 32 * 32;
                    secondBits = (int)(totalBits - (84 + firstBits + 20));
                    var words = new uint[totalBits / 32];
                    Put(words, 0, 9, 0);
                    Put(words, 11, 10, 5);
                    Put(words, 21, 5, 31);
                    Put(words, 26, 6, 1);
                    Put(words, 32, 32, 2);
                    Put(words, 64, 20, (ulong)firstBits);
                    Put(words, 84 + firstBits, 20, (ulong)secondBits);
                    byte[] data = new byte[totalBits / 8];
                    for (int word = 0; word < words.Length; word++)
                    {
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(word * 4), words[word]);
                    }

                    pages2.CopyTo(data, payloadStart);
                    Assert.Equal((60, 0), (Sniff(Parser("Musepack"), data).Class, Sniff(Parser("Musepack"), data).Offset));
                    Assert.Equal((60, payloadStart), (Sniff(Parser("Ogg"), data).Class, Sniff(Parser("Ogg"), data).Offset));
                    return (data, "Musepack");
                }

            case "ac3-before-dts":
                {
                    byte[] data = [.. Junk(30), .. Fixture("ac3/bsi-fields.ac3"), .. Junk(30), .. Fixture("dts/14be-extension-missing.dts")];
                    Assert.Equal((50, 50), (Sniff(Parser("AC-3"), data).Class, Sniff(Parser("DTS"), data).Class));
                    return (data, "AC-3");
                }

            default:
                {
                    byte[] data = [.. Junk(30), .. Fixture("dts/14be-extension-missing.dts"), .. Junk(30), .. Fixture("ac3/bsi-fields.ac3")];
                    Assert.Equal((50, 50), (Sniff(Parser("AC-3"), data).Class, Sniff(Parser("DTS"), data).Class));
                    return (data, "DTS");
                }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheEvidenceDecidesWhateverTheOrderOfTheParsers(string name)
    {
        (byte[] data, string expected) = Build(name);

        Assert.Equal(expected, DetectionOrderTests.Detect(DefaultParsers.Formats, data, ".bin"));
        foreach (int seed in Seeds)
        {
            IFormatParser[] shuffled = [.. DefaultParsers.Formats];
            new Random(seed).Shuffle(shuffled);
            Assert.Equal(expected, DetectionOrderTests.Detect(shuffled, data, ".bin"));
        }
    }

    // The stream with its start cut off: the look-alike chain starts first, the real one passes its CRCs.
    [Theory]
    [InlineData(101)]
    [InlineData(110)]
    [InlineData(128)]
    public void ACutEac3FileIsAc3InEveryOrder(int cut)
    {
        byte[] data = Fixture("ac3/junk-adts-in-frames.eac3")[cut..];

        Assert.Equal("AC-3", DetectionOrderTests.Detect(DefaultParsers.Formats, data, ".eac3"));
        foreach (int seed in Seeds)
        {
            IFormatParser[] shuffled = [.. DefaultParsers.Formats];
            new Random(seed).Shuffle(shuffled);
            Assert.Equal("AC-3", DetectionOrderTests.Detect(shuffled, data, ".eac3"));
        }
    }

    // Writes value into the words as the Musepack stream versions 4 to 6 read them: 32-bit little-endian words, most significant bit first.
    private static void Put(uint[] words, long bit, int length, ulong value)
    {
        for (int i = 0; i < length; i++)
        {
            long position = bit + i;
            uint mask = 1u << (31 - (int)(position % 32));
            if (((value >> (length - 1 - i)) & 1) != 0)
            {
                words[position / 32] |= mask;
            }
        }
    }
}
