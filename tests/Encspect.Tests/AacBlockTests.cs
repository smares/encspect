using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Host;

namespace Encspect.Tests;

// The Huffman codebooks and band tables the raw data block reader uses, and the nodes a report
// rebuilds from what it keeps of each block.
public class AacBlockTests
{
    public static TheoryData<string> Tables => new(TableList().Select(t => t.Name));

    // Every codebook of ISO/IEC 14496-3 is a complete prefix code: the Kraft sum of its lengths is
    // exactly 1 and no codeword starts another.
    [Theory]
    [MemberData(nameof(Tables))]
    public void CodebookIsCompletePrefixCode(string name)
    {
        (_, uint[] codes, byte[] lengths) = TableList().Single(t => t.Name == name);
        Assert.Equal(codes.Length, lengths.Length);
        int longest = lengths.Max();
        Assert.True(longest <= 20);
        long kraft = lengths.Sum(l => 1L << (longest - l));
        Assert.Equal(1L << longest, kraft);
        for (int i = 0; i < codes.Length; i++)
        {
            Assert.True(codes[i] >> lengths[i] == 0, $"Codeword {i} has more bits than its length.");
            for (int j = 0; j < codes.Length; j++)
            {
                if (i != j && lengths[i] <= lengths[j])
                {
                    Assert.False(codes[j] >> (lengths[j] - lengths[i]) == codes[i], $"Codeword {i} starts codeword {j}.");
                }
            }
        }
    }

    // The lookup tables decode every codeword to its index and read exactly its bits, whatever
    // follows it.
    [Theory]
    [MemberData(nameof(Tables))]
    public void LookupDecodesEveryCodeword(string name)
    {
        (_, uint[] codes, byte[] lengths) = TableList().Single(t => t.Name == name);
        var huffman = new Huffman(codes, lengths);
        for (int i = 0; i < codes.Length; i++)
        {
            foreach (bool ones in new[] { false, true })
            {
                byte[] data = new byte[8];
                if (ones)
                {
                    Array.Fill(data, (byte)0xFF);
                }

                for (int bit = 0; bit < lengths[i]; bit++)
                {
                    int value = (int)(codes[i] >> (lengths[i] - 1 - bit)) & 1;
                    data[bit / 8] = (byte)((data[bit / 8] & ~(0x80 >> (bit % 8))) | (value << (7 - (bit % 8))));
                }

                var bits = new AacBits(data, 0, 64);
                Assert.Equal(i, huffman.Decode(ref bits));
                Assert.Equal(lengths[i], bits.Position);
            }
        }
    }

    // Entries of the specification's tables (ISO/IEC 14496-3 Annex 4.A and 4.5.4): the codeword of
    // a scalefactor difference of 0 (index 60) is the single bit 0, the codeword of four zero lines
    // in codebook 1 (index 40) too, and codebook 11's pair (0, 0) is 0000 among its 17 by 17 pairs;
    // the scalefactor bands of a long window at 44.1 and 48 kHz number 49, of a short
    // one 14, and both end at the window's last line.
    [Fact]
    public void TablesHoldSpecificationEntries()
    {
        Assert.Equal((0u, (byte)1), (HuffmanData.ScalefactorCodes[60], HuffmanData.ScalefactorBits[60]));
        Assert.Equal((0, (byte)1), (HuffmanData.Codes1[40], HuffmanData.Bits1[40]));
        Assert.Equal((0, (byte)4), (HuffmanData.Codes11[0], HuffmanData.Bits11[0]));
        Assert.Equal(17 * 17, HuffmanData.Codes11.Length);
        Assert.Equal(121, HuffmanData.ScalefactorCodes.Length);
        Assert.Equal(49, BandTables.NumLong[3]);
        Assert.Equal(49, BandTables.NumLong[4]);
        Assert.Equal(14, BandTables.NumShort[4]);
        Assert.Equal(1024, BandTables.Long[4][BandTables.NumLong[4]]);
        Assert.Equal(128, BandTables.Short[4][BandTables.NumShort[4]]);
        Assert.Equal(960, BandTables.Long960[4][BandTables.NumLong960[4]]);
        Assert.Equal(120, BandTables.Short120[4][BandTables.NumShort120[4]]);
    }

    public static TheoryData<string> AdtsFixtures => new(
        Fixtures.Mp4FixtureNames().Where(n => n.EndsWith(".aac", StringComparison.Ordinal) && !n.Contains("corrupt", StringComparison.Ordinal)));

    // A report keeps each raw data block without its Huffman-coded data and rebuilds its nodes from
    // that; they must equal the nodes read from the block's own bytes, field by field. Where the
    // parametric stereo extension lies in SBR data takes the SBR header of an earlier block to find,
    // so a block read alone does not show it.
    [Theory]
    [MemberData(nameof(AdtsFixtures))]
    public void KeptBlocksRebuildTheirNodes(string name)
    {
        string path = Path.Combine(Fixtures.TagDirectory, "..", name);
        byte[] file = File.ReadAllBytes(path);
        Report report = Analyzer.CreateDefault().Analyze(path, new MemoryStream(file));
        int compared = 0;
        foreach (Node frame in Frames(report.Root))
        {
            if (frame.Length < 7)
            {
                continue;
            }

            var header = AdtsHeader.Read(file.AsSpan((int)frame.Offset, 7));
            if (!StreamSetup.CanRead(header.Config) || header.ChannelConfiguration == 0)
            {
                continue;
            }

            foreach (Node block in frame.Children.Where(c => c.Name == "Raw Data Block"))
            {
                byte[] bytes = file[(int)block.Offset..(int)block.End];
                List<Node> read = new BlockReader(new StreamSetup(header.Config), null)
                    .Nodes(bytes, 0, bytes.Length * 8, new UnitBytes(block.Offset, bytes.Length, null), out BlockError? error);
                if (error is null)
                {
                    Assert.Equal(Flatten(read), Flatten([.. block.Children.Where(c => c.Name != "Trailing Bytes")]));
                    compared++;
                }
            }
        }

        Assert.True(compared > 0 || name.Contains("pce", StringComparison.Ordinal) || name.Contains("adif", StringComparison.Ordinal), $"No block of {name} was compared.");
    }

    private static IEnumerable<Node> Frames(Node node)
    {
        if (node.Kind == NodeKind.Frame && node.Name == "Frame")
        {
            yield return node;
            yield break;
        }

        foreach (Node child in node.Children)
        {
            foreach (Node frame in Frames(child))
            {
                yield return frame;
            }
        }
    }

    private static List<string> Flatten(IReadOnlyList<Node> nodes)
    {
        var lines = new List<string>();
        void Walk(Node node, int depth)
        {
            lines.Add($"{depth} {node.Name} {node.Offset} {node.Length} {node.BitOffset} {node.BitLength} {node.Value} {node.Description}");
            foreach (Node child in node.Children.Where(c => c.Name != "Parametric Stereo Extension"))
            {
                Walk(child, depth + 1);
            }
        }

        foreach (Node node in nodes)
        {
            Walk(node, 0);
        }

        return lines;
    }

    private static List<(string Name, uint[] Codes, byte[] Lengths)> TableList() =>
    [
        ("spectral 1", Wide(HuffmanData.Codes1), HuffmanData.Bits1),
        ("spectral 2", Wide(HuffmanData.Codes2), HuffmanData.Bits2),
        ("spectral 3", Wide(HuffmanData.Codes3), HuffmanData.Bits3),
        ("spectral 4", Wide(HuffmanData.Codes4), HuffmanData.Bits4),
        ("spectral 5", Wide(HuffmanData.Codes5), HuffmanData.Bits5),
        ("spectral 6", Wide(HuffmanData.Codes6), HuffmanData.Bits6),
        ("spectral 7", Wide(HuffmanData.Codes7), HuffmanData.Bits7),
        ("spectral 8", Wide(HuffmanData.Codes8), HuffmanData.Bits8),
        ("spectral 9", Wide(HuffmanData.Codes9), HuffmanData.Bits9),
        ("spectral 10", Wide(HuffmanData.Codes10), HuffmanData.Bits10),
        ("spectral 11", Wide(HuffmanData.Codes11), HuffmanData.Bits11),
        ("scalefactor", HuffmanData.ScalefactorCodes, HuffmanData.ScalefactorBits),
        ("SBR envelope 1.5 dB time", HuffmanData.THuffmanEnv15Codes, HuffmanData.THuffmanEnv15Bits),
        ("SBR envelope 1.5 dB frequency", HuffmanData.FHuffmanEnv15Codes, HuffmanData.FHuffmanEnv15Bits),
        ("SBR balance 1.5 dB time", HuffmanData.THuffmanEnvBal15Codes, HuffmanData.THuffmanEnvBal15Bits),
        ("SBR balance 1.5 dB frequency", HuffmanData.FHuffmanEnvBal15Codes, HuffmanData.FHuffmanEnvBal15Bits),
        ("SBR envelope 3 dB time", HuffmanData.THuffmanEnv30Codes, HuffmanData.THuffmanEnv30Bits),
        ("SBR envelope 3 dB frequency", HuffmanData.FHuffmanEnv30Codes, HuffmanData.FHuffmanEnv30Bits),
        ("SBR balance 3 dB time", HuffmanData.THuffmanEnvBal30Codes, HuffmanData.THuffmanEnvBal30Bits),
        ("SBR balance 3 dB frequency", HuffmanData.FHuffmanEnvBal30Codes, HuffmanData.FHuffmanEnvBal30Bits),
        ("SBR noise time", HuffmanData.THuffmanNoise30Codes, HuffmanData.THuffmanNoise30Bits),
        ("SBR noise balance time", HuffmanData.THuffmanNoiseBal30Codes, HuffmanData.THuffmanNoiseBal30Bits),
    ];

    private static uint[] Wide(ushort[] codes) => [.. codes.Select(c => (uint)c)];
}
