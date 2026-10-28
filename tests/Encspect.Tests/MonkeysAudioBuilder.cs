using System.Buffers.Binary;
using System.Security.Cryptography;
using Encspect.Core;

namespace Encspect.Tests;

// Takes the frames of a Monkey's Audio file of the current format apart, in the order of the stream they are coded in (the bytes of every aligned word
// are reversed in the file), and puts frames together again into a file with a descriptor, header and seek table that are right, so a test changes
// one thing and nothing else. A frame decodes by itself, so a file of copies of one frame is a valid file of as much audio as the copies make.
internal static class MonkeysAudioBuilder
{
    public const int DescriptorLength = 52;

    public const int HeaderLength = 24;

    public static string FixturePath(string name) => Path.Combine(Fixtures.FixtureDirectory, "monkeys-audio", name);

    // A fixture's frames in stream order, with its header.
    public static (byte[] Header, byte[] HeaderData, List<byte[]> Frames) Take(string name)
    {
        byte[] file = File.ReadAllBytes(FixturePath(name));
        uint seekBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16));
        int headerData = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20));
        long frameBytes = (long)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(24));
        int dataStart = DescriptorLength + HeaderLength + (int)seekBytes + headerData;
        byte[] header = file[DescriptorLength..(DescriptorLength + HeaderLength)];
        var frames = new List<byte[]>();
        int count = (int)(seekBytes / 4);
        for (int f = 0; f < count; f++)
        {
            long start = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(DescriptorLength + HeaderLength + (4 * f))) - dataStart;
            long end = f + 1 < count ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(DescriptorLength + HeaderLength + (4 * (f + 1)))) - dataStart : frameBytes;
            var frame = new byte[end - start];
            for (long j = start; j < end; j++)
            {
                frame[j - start] = file[dataStart + ((j >> 2) << 2) + 3 - (j & 3)];
            }

            frames.Add(frame);
        }

        return (header, file[(DescriptorLength + HeaderLength + (int)seekBytes)..dataStart], frames);
    }

    // The file of frames, each of blocksPerFrame samples but the last, which has finalBlocks, in the format of the header given (its counts are replaced).
    public static byte[] Build(byte[] header, byte[] headerData, IReadOnlyList<byte[]> frames, uint blocksPerFrame, uint finalBlocks, ushort? level = null)
    {
        byte[] ownHeader = [.. header];
        BinaryPrimitives.WriteUInt32LittleEndian(ownHeader.AsSpan(4), blocksPerFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(ownHeader.AsSpan(8), finalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(ownHeader.AsSpan(12), (uint)frames.Count);
        if (level is { } l)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(ownHeader.AsSpan(0), l);
        }

        int dataStart = DescriptorLength + HeaderLength + (4 * frames.Count) + headerData.Length;
        long total = frames.Sum(f => (long)f.Length);
        var body = new byte[(total + 3) / 4 * 4];
        var seek = new byte[4 * frames.Count];
        long position = 0;
        for (int f = 0; f < frames.Count; f++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(seek.AsSpan(4 * f), (uint)(dataStart + position));
            foreach (byte b in frames[f])
            {
                body[(int)((position >> 2 << 2) + 3 - (position & 3))] = b;
                position++;
            }
        }

        var descriptor = new byte[DescriptorLength];
        "MAC "u8.CopyTo(descriptor);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(4), 3990);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(6), 13420);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(8), DescriptorLength);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(12), HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(16), (uint)seek.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(20), (uint)headerData.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(24), (ulong)body.Length);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(headerData);
        md5.AppendData(body);
        md5.AppendData(ownHeader);
        md5.AppendData(seek);
        md5.GetHashAndReset().CopyTo(descriptor.AsSpan(36));
        return [.. descriptor, .. ownHeader, .. seek, .. headerData, .. body];
    }

    // The frame (index) of a fixture, copied times over, as a file of frames of blocksPerFrame samples.
    public static byte[] Repeat(string name, int frame, int times, uint blocksPerFrame, ushort? level = null)
    {
        (byte[] header, byte[] headerData, List<byte[]> frames) = Take(name);
        return Build(header, headerData, [.. Enumerable.Repeat(frames[frame], times)], blocksPerFrame, blocksPerFrame, level);
    }

    // The samples of a fixture's frame.
    public static uint BlocksOf(string name, int frame)
    {
        byte[] header = Take(name).Header;
        uint perFrame = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint final = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        return frame == count - 1 ? final : perFrame;
    }

    // A fixture of the format before version 3.98 (one header of 32 bytes, an optional peak level and element count, the seek table, the frames), taken apart: its header
    // up to the seek table, and its frames in the order of the stream. Versions up to 3.80, whose frames follow each other bit by bit, are not taken.
    public static (byte[] Header, int Version, List<byte[]> Frames) TakeOld(string name)
    {
        byte[] file = File.ReadAllBytes(FixturePath(name));
        int version = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));
        Assert.True(version > 3800 && version < 3980, $"{name} is of version {version}.");
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8));
        int headerBytes = 32 + ((flags & 4) != 0 ? 4 : 0) + ((flags & 16) != 0 ? 4 : 0);
        uint headerData = (flags & 32) != 0 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16));
        uint terminating = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20));
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24));
        int seekAt = headerBytes + (int)headerData;
        int dataStart = seekAt + (4 * count);
        long dataEnd = file.Length - terminating;
        var frames = new List<byte[]>();
        for (int f = 0; f < count; f++)
        {
            long start = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(seekAt + (4 * f))) - dataStart;
            long end = f + 1 < count ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(seekAt + (4 * (f + 1)))) - dataStart : dataEnd - dataStart;
            var frame = new byte[end - start];
            for (long j = start; j < end; j++)
            {
                frame[j - start] = file[dataStart + ((j >> 2) << 2) + 3 - (j & 3)];
            }

            frames.Add(frame);
        }

        return (file[..headerBytes], version, frames);
    }

    // The samples of a frame of a file of the format before version 3.98, the whole frame but the last of a file.
    public static uint OldBlocksPerFrame(int version, int level) => version >= 3900 || (version >= 3800 && level == 4000) ? 73728u : 9216u;

    // A file of the format before 3.98 of the frames given, all of OldBlocksPerFrame samples but the last, which has finalBlocks.
    public static byte[] BuildOld(byte[] header, IReadOnlyList<byte[]> frames, uint finalBlocks)
    {
        byte[] own = [.. header];
        BinaryPrimitives.WriteUInt32LittleEndian(own.AsSpan(24), (uint)frames.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(own.AsSpan(28), finalBlocks);
        int dataStart = own.Length + (4 * frames.Count);
        long total = frames.Sum(f => (long)f.Length);
        var body = new byte[(total + 3) / 4 * 4];
        var seek = new byte[4 * frames.Count];
        long position = 0;
        for (int f = 0; f < frames.Count; f++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(seek.AsSpan(4 * f), (uint)(dataStart + position));
            foreach (byte b in frames[f])
            {
                body[(int)((position >> 2 << 2) + 3 - (position & 3))] = b;
                position++;
            }
        }

        return [.. own, .. seek, .. body];
    }

    // A frame of digital silence in two channels of 16 bits: the CRC of the zero samples with the bit that flags the special codes, and the code for
    // both channels silent (APEDecompressCore.cpp). It has no coded samples, and it decodes.
    public static byte[] SilentFrame(int blocks)
    {
        uint crc = Crc32.Finish(Crc32.Update(Crc32.Initial, new byte[blocks * 4])) >> 1;
        var frame = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(frame, 0x80000000 | crc);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), 3);
        return frame;
    }

    // count frames of 64 samples of silence in stereo at 44.1 kHz and 16 bits: eight bytes each.
    public static byte[] Silence(int count)
    {
        (byte[] header, byte[] headerData, _) = Take("external-level-normal-16-stereo.ape");
        byte[] frame = SilentFrame(64);
        return Build(header, headerData, [.. Enumerable.Repeat(frame, count)], 64, 64);
    }
}
