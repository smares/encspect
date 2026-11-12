using Encspect.Core;
using Encspect.Formats.Vorbis;
using Encspect.Host;

namespace Encspect.Tests;

// Helpers for the Vorbis tests: the packets of an Ogg file's first stream, a bit writer in Vorbis's order, and a sink that keeps what a decoder writes.
internal static class VorbisStreams
{
    // The packets of the first logical stream of an Ogg file, whole (a packet that spans pages is joined).
    public static List<byte[]> Packets(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        var packets = new List<byte[]>();
        var current = new List<byte>();
        uint? serial = null;
        int position = 0;
        while (position + 27 <= data.Length && data.AsSpan(position, 4).SequenceEqual("OggS"u8))
        {
            uint page = BitConverter.ToUInt32(data, position + 14);
            serial ??= page;
            int segments = data[position + 26];
            int body = position + 27 + segments;
            for (int i = 0; i < segments && body <= data.Length; i++)
            {
                int length = Math.Min(data[position + 27 + i], data.Length - body);
                if (page == serial)
                {
                    current.AddRange(data.AsSpan(body, length).ToArray());
                    if (data[position + 27 + i] < 255)
                    {
                        packets.Add([.. current]);
                        current.Clear();
                    }
                }

                body += length;
            }

            position = body;
        }

        return packets;
    }

    // An Ogg file of the packets, one audio packet to a page, granule positions the counted samples; pages named in lost are written and then removed from the file,
    // so that the next page follows a gap.
    public static byte[] Ogg(IReadOnlyList<byte[]> packets, IReadOnlyList<long> positions, IReadOnlySet<int>? lost = null)
    {
        var builder = new OggBuilder().Page(1, 0, 2, packets[0]).Page(1, 0, 0, packets[1], packets[2]);
        var removed = new List<(int From, int To)>();
        for (int i = 3; i < packets.Count; i++)
        {
            int from = builder.Length;
            builder.Page(1, positions[i - 3], i == packets.Count - 1 ? (byte)4 : (byte)0, packets[i]);
            if (lost?.Contains(i) == true)
            {
                removed.Add((from, builder.Length));
            }
        }

        byte[] bytes = builder.Bytes;
        foreach ((int from, int to) in Enumerable.Reverse(removed))
        {
            bytes = [.. bytes[..from], .. bytes[to..]];
        }

        return bytes;
    }

    // The samples each packet returns, from the block sizes the packets' modes name (the count the parser keeps), and their running total.
    public static long[] Positions(IReadOnlyList<byte[]> packets)
    {
        Identification id = Identification.Read(packets[0])!;
        SetupModel setup = SetupModel.Read(packets[2], id.Channels, CancellationToken.None)!;
        var positions = new long[packets.Count - 3];
        long total = 0;
        int previous = -1;
        for (int i = 3; i < packets.Count; i++)
        {
            byte[] packet = packets[i];
            if (packet.Length > 0 && (packet[0] & 1) == 0)
            {
                int mode = (packet[0] >> 1) & ((1 << setup.ModeBits) - 1);
                int size = setup.ModeIsLong[mode] ? id.Blocksize1 : id.Blocksize0;
                total += previous < 0 ? 0 : (previous / 4) + (size / 4);
                previous = size;
            }

            positions[i - 3] = total;
        }

        return positions;
    }

    public sealed class RecordingSink : IPcmSink
    {
        private readonly List<(long Position, float[][] Channels)> _blocks = [];

        public IReadOnlyList<(long Position, float[][] Channels)> Blocks => _blocks;

        public long Samples => _blocks.Sum(b => (long)b.Channels[0].Length);

        public void Write(PcmBlock block)
        {
            var channels = new float[block.Channels][];
            for (int c = 0; c < block.Channels; c++)
            {
                channels[c] = block.Floats.Slice(c * block.Stride, block.Length).ToArray();
            }

            _blocks.Add((block.Position, channels));
        }

        public void End(long samples)
        {
        }
    }

    public static (Report Report, RecordingSink Sink) Decode(byte[] file, string name = "test.ogg")
    {
        var sink = new RecordingSink();
        using var stream = new MemoryStream(file);
        Report report = Analyzer.CreateDefault().Analyze(name, stream, new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => sink } });
        return (report, sink);
    }

    // The decoded samples of channel 0 by position.
    public static Dictionary<long, float> ByPosition(RecordingSink sink, int channel = 0)
    {
        var map = new Dictionary<long, float>();
        foreach ((long position, float[][] channels) in sink.Blocks)
        {
            for (int i = 0; i < channels[channel].Length; i++)
            {
                map[position + i] = channels[channel][i];
            }
        }

        return map;
    }

    // Least significant bit first, as Vorbis packs its fields.
    public sealed class Bits
    {
        private readonly List<bool> _bits = [];

        public Bits Put(long value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }

            return this;
        }

        public byte[] Bytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    bytes[i / 8] |= (byte)(1 << (i % 8));
                }
            }

            return bytes;
        }
    }
}
