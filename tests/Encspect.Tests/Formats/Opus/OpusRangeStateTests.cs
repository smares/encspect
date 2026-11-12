using Encspect.Core;
using Encspect.Formats.Opus;

namespace Encspect.Tests;

// The final range coder state of every packet of RFC 6716's and RFC 8251's test vectors (RFC 6716, section 6: a conforming decoder ends each frame with the state the
// encoder ended with; the .bit files of opus_demo store it after each packet's length). The vectors are downloaded, never committed (their license is unverified,
// docs/design/lossy-decoders.md): ENCSPECT_OPUS_VECTORS names the folder that holds the .bit files, which `crosscheck_opus_pcm.py --bits FOLDER` writes: the test vectors (testvectorNN.bit,
// downloaded at a pinned SHA-256 by `opus_tool.fetch_vectors`), seeded random packets of every mode that libopus accepts (random-*.cN.bit), and the packets of the Ogg fixtures with libopus's
// final range of each (NAME.cN.bit, N the channels: the .bit format does not state them; the test vectors have 2). Without the variable the test does nothing.
public class OpusRangeStateTests
{
    [Fact]
    public void Every_packet_of_the_test_vectors_ends_in_the_state_the_encoder_stored()
    {
        if (Environment.GetEnvironmentVariable("ENCSPECT_OPUS_VECTORS") is not { Length: > 0 } folder)
        {
            return;
        }

        var problems = new List<string>();
        int packets = 0;
        foreach (string path in Directory.EnumerateFiles(folder, "*.bit").Order(StringComparer.Ordinal))
        {
            byte[] file = File.ReadAllBytes(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            int channels = stem.Length > 3 && stem[^3] == '.' && stem[^2] == 'c' ? stem[^1] - '0' : 2;
            var walker = new OpusWalker(disableInv: channels == 1);
            var writer = new IntWriter { IsEnabled = false };
            int position = 0;
            for (int number = 0; position + 8 <= file.Length; number++)
            {
                int length = (file[position] << 24) | (file[position + 1] << 16) | (file[position + 2] << 8) | file[position + 3];
                uint stored = (uint)((file[position + 4] << 24) | (file[position + 5] << 16) | (file[position + 6] << 8) | file[position + 7]);
                position += 8;
                ReadOnlySpan<byte> packet = file.AsSpan(position, length);
                position += length;
                var frames = new ChunkedList<ushort>();
                PartRecord part = Framing.Read(packet, false, frames, out _);
                if (part.Status != PartStatus.Valid)
                {
                    problems.Add($"{Path.GetFileName(path)} packet {number}: framing refused");
                    continue;
                }

                uint last = 0;
                int at = part.Header;
                for (int f = 0; f < part.Frames; f++)
                {
                    int frameLength = frames[part.FirstFrame + f];
                    writer.Clear();
                    FrameFacts facts = walker.Walk(writer, packet.Slice(at, frameLength), Toc.Mode(part.TocByte), Toc.Bandwidth(part.TocByte), Toc.FrameSamples(part.TocByte), Toc.IsStereo(part.TocByte) ? 2 : 1);
                    at += frameLength;
                    last = facts.FinalRange;
                }

                packets++;
                if (last != stored)
                {
                    problems.Add($"{Path.GetFileName(path)} packet {number}: {last:X8}, the encoder stored {stored:X8}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(20)));
        Assert.True(packets > 0, "The folder holds no test vector.");
    }
}
