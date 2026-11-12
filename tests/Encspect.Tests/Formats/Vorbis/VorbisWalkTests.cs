using Encspect.Core;
using Encspect.Formats.Vorbis;

namespace Encspect.Tests;

// The setup model and the walk of the audio packets (the entropy pass): codebooks as the specification assigns their codewords, libvorbis's refusals, and the
// figures the walk gives, which the manifests' independent Python walk (scripts/fixtures/vorbis_walk.py) holds for every fixture.
public class VorbisWalkTests
{
    private static Codebook? Book(params int[] lengths)
    {
        var bits = new VorbisStreams.Bits().Put(0x564342, 24).Put(1, 16).Put(lengths.Length, 24).Put(0, 1).Put(0, 1);
        foreach (int length in lengths)
        {
            bits.Put(length - 1, 5);
        }

        bits.Put(0, 4);
        byte[] data = bits.Bytes();
        var reader = new PacketBits(data);
        return Codebook.Read(ref reader, 1 << 20, CancellationToken.None, out _);
    }

    private static int Decode(Codebook book, params int[] codeBits)
    {
        // The bits of a codeword in the order they are read: the first bit is the most significant bit of the codeword.
        var bits = new VorbisStreams.Bits();
        foreach (int bit in codeBits)
        {
            bits.Put(bit, 1);
        }

        byte[] data = bits.Bytes();
        var reader = new PacketBits(data.AsSpan(0, (codeBits.Length + 7) / 8));
        return book.Decode(ref reader);
    }

    // Vorbis I specification, section 3.2.1: the example with the lengths 2, 4, 4, 4, 4, 2, 3, 3 gets the codewords 00, 0100, 0101, 0110, 0111, 10, 110 and 111.
    [Fact]
    public void Codewords_are_assigned_to_the_entries_in_order_the_leftmost_free_one_of_each_length()
    {
        Codebook book = Book(2, 4, 4, 4, 4, 2, 3, 3)!;

        Assert.Equal(0, Decode(book, 0, 0));
        Assert.Equal(1, Decode(book, 0, 1, 0, 0));
        Assert.Equal(2, Decode(book, 0, 1, 0, 1));
        Assert.Equal(3, Decode(book, 0, 1, 1, 0));
        Assert.Equal(4, Decode(book, 0, 1, 1, 1));
        Assert.Equal(5, Decode(book, 1, 0));
        Assert.Equal(6, Decode(book, 1, 1, 0));
        Assert.Equal(7, Decode(book, 1, 1, 1));
    }

    [Fact]
    public void A_code_that_is_not_a_complete_prefix_code_is_refused_but_a_single_entry_of_length_one()
    {
        Assert.Null(Book(1, 1, 1));
        Assert.Null(Book(2, 2, 2));
        Assert.Null(Book(1, 2));
        Assert.NotNull(Book(1));
        Assert.Null(Book(2));
    }

    [Fact]
    public void A_single_entry_book_decodes_from_either_bit_and_an_empty_packet_is_the_end()
    {
        Codebook book = Book(1)!;

        Assert.Equal(0, Decode(book, 0));
        Assert.Equal(0, Decode(book, 1));
        var empty = new PacketBits([]);
        Assert.Equal(-1, book.Decode(ref empty));
    }

    // Lengths 1 to 9 and two of 10 bits: the entries 9 and 10 are 1111111110 and 1111111111. With eight bits left, eight ones are the start of one of them, not a codeword.
    [Fact]
    public void A_codeword_cut_by_the_end_of_the_packet_is_the_end_of_the_packet()
    {
        Codebook book = Book(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 10)!;

        var cut = new PacketBits([0xFF]);
        Assert.Equal(-1, book.Decode(ref cut));
        var nine = new PacketBits([0xFF, 0x00]);
        Assert.Equal(8, book.Decode(ref nine));
        var ten9 = new PacketBits([0xFF, 0x01]);
        Assert.Equal(9, book.Decode(ref ten9));
        var ten = new PacketBits([0xFF, 0x03]);
        Assert.Equal(10, book.Decode(ref ten));
        var short1 = new PacketBits([0x00]);
        Assert.Equal(0, book.Decode(ref short1));
    }

    [Fact]
    public void Reading_past_the_end_of_a_packet_gives_minus_one_and_ends_it()
    {
        var reader = new PacketBits([0xFF, 0x0F]);

        Assert.Equal(0xFF, reader.Read(8));
        Assert.Equal(0xF, reader.Read(4));
        Assert.Equal(-1, reader.Read(5));
        Assert.Equal(-1, reader.Read(1));
        Assert.Equal(0, reader.Read(0));
    }

    private static IEnumerable<string> VorbisFiles() =>
        Directory.EnumerateFiles(Path.Combine(Fixtures.FixtureDirectory, "ogg"), "*.ogg").Select(p => "ogg/" + Path.GetFileName(p)).Where(n => n.Contains("vorbis", StringComparison.Ordinal) || n.Contains("oggenc", StringComparison.Ordinal) || n.Contains("aotuv", StringComparison.Ordinal) || n.Contains("ffmpeg-libvorbis", StringComparison.Ordinal) || n.Contains("ffmpeg-vorbis", StringComparison.Ordinal)).Order(StringComparer.Ordinal);

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (string name in VorbisFiles())
        {
            data.Add(name);
        }

        return data;
    }

    // The walk of the header reports what libvorbis refuses as warnings; the model must refuse exactly the setups those findings describe, or the decoder would decode a
    // stream libvorbis refuses (or refuse one it decodes).
    [Theory]
    [MemberData(nameof(Files))]
    public void The_model_refuses_the_setup_headers_the_header_walk_warns_about(string name)
    {
        List<byte[]> packets = VorbisStreams.Packets(name);
        if (packets.Count < 3 || Identification.Read(packets[0]) is not { IsUsable: true } id || packets[2].Length < 8 || packets[2][0] != 5)
        {
            return;
        }

        SetupResult walk = SetupDecoder.Decode(new MappedBytes(packets[2], 0), id.Channels, CancellationToken.None);
        bool refused = walk.Problems.Any(p => p.Severity == Severity.Warning);
        SetupModel? model = SetupModel.Read(packets[2], id.Channels, CancellationToken.None);

        Assert.Equal(refused, model is null);
    }

    // Every packet of every fixture walks without an exception, into a buffer of the size MaxInts states, and the facts do not depend on whether it is stored.
    [Theory]
    [MemberData(nameof(Files))]
    public void The_walk_stays_in_the_buffer_it_is_given_and_gives_the_same_facts_without_one(string name)
    {
        List<byte[]> packets = VorbisStreams.Packets(name);
        if (packets.Count < 4 || Identification.Read(packets[0]) is not { IsUsable: true } id || SetupModel.Read(packets[2], id.Channels, CancellationToken.None) is not { } model)
        {
            return;
        }

        var walker = new PacketWalker(model, id.Blocksize0, id.Blocksize1);
        foreach (byte[] packet in packets.Skip(3))
        {
            WalkFacts bare = walker.Walk(packet, default);
            if (!bare.IsAudio || bare.IsBadMode)
            {
                continue;
            }

            int[] buffer = new int[walker.MaxInts(bare.Mode)];
            WalkFacts stored = walker.Walk(packet, buffer);

            Assert.Equal(bare, stored);
            Assert.True(stored.Used <= buffer.Length, $"{stored.Used} ints of {buffer.Length}");
            Assert.Equal(stored.Used, buffer[LentLayout.Used]);
        }
    }

    // Damage in the data of a packet never raises an exception: every prefix of every packet of an encoder's file, and bits flipped in the middle of the packets.
    [Fact]
    public void A_packet_cut_anywhere_or_with_its_bits_flipped_walks_without_an_exception()
    {
        List<byte[]> packets = VorbisStreams.Packets("ogg/external-oggenc-q5.ogg");
        Identification id = Identification.Read(packets[0])!;
        SetupModel model = SetupModel.Read(packets[2], id.Channels, CancellationToken.None)!;
        var walker = new PacketWalker(model, id.Blocksize0, id.Blocksize1);
        var random = new Random(7);
        foreach (byte[] packet in packets.Skip(3).Take(30))
        {
            for (int length = 0; length <= packet.Length; length += Math.Max(1, packet.Length / 40))
            {
                Walk(walker, packet.AsSpan(0, length));
            }

            for (int round = 0; round < 40; round++)
            {
                byte[] damaged = [.. packet];
                for (int flips = 0; flips < 1 + (round % 4); flips++)
                {
                    damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
                }

                Walk(walker, damaged);
            }
        }
    }

    private static void Walk(PacketWalker walker, ReadOnlySpan<byte> packet)
    {
        WalkFacts bare = walker.Walk(packet, default);
        if (bare.IsAudio && !bare.IsBadMode)
        {
            WalkFacts stored = walker.Walk(packet, new int[walker.MaxInts(bare.Mode)]);
            Assert.Equal(bare, stored);
        }
    }

    // A silent packet (the minimal codec's floors are all unused) codes nothing; an encoder's packets code up to a lowpass below half the sample rate.
    [Fact]
    public void A_stream_with_every_floor_unused_has_no_coded_bandwidth_and_an_encoders_stream_has_one_below_the_nyquist_frequency()
    {
        Report silent = Fixtures.AnalyzeMpeg("ogg/vorbis-minimal-mono.ogg");
        Report encoded = Fixtures.AnalyzeMpeg("ogg/external-oggenc-q5.ogg");

        Assert.Equal("0 Hz", Find(silent.Root, "Highest")?.Value?.ToString());
        string[] hertz = ["Lowest", "Most Common", "Highest"];
        int[] values = [.. hertz.Select(n => int.Parse(Find(encoded.Root, n, "Coded Bandwidth")!.Value!.ToString()!.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture))];
        Assert.True(values[0] <= values[1] && values[1] <= values[2]);
        Assert.InRange(values[2], 1, 22050);
    }

    private static Node? Find(Node node, string name, string? parent = null) =>
        (parent is null || node.Name == parent) && node.Children.FirstOrDefault(c => c.Name == name) is { } found ? found : node.Children.Select(c => Find(c, name, parent)).FirstOrDefault(n => n is not null);
}
