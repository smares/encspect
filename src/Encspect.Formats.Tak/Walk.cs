using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Tak;

// The walk over the metadata objects (takStream.pas, "MetaData"): after the stream ID each object is a header of 4 bytes (a 7-bit ID, a reserved bit, a 24-bit size), the data and
// the CRC-24 that ends it, which the size counts; the End object (ID 0) has neither. The walk ends at the End object, or, where it is missing, at the first frame: a frame's sync word
// 0xA0FF is stored as the bytes FF A0, which read as an object header give ID 127 with the reserved bit set and a size made of the 24 bits after that byte (A0 and the next two bytes), so a frame is tested first, and only a header whose
// CRC-24 holds counts. It never stops on a signature alone.
internal static class Walk
{
    private const int ReadChunk = 1 << 16;

    // Reads the objects into the records. With a state it also reads the stream info, the encoder info, the MD5, the wave data and the CRC-24 of every object; without one (the sniff's
    // DeclaredEnd) it reads the objects and the last frame info.
    public static void Run(Records r, ByteReader reader, State? s)
    {
        r.Start = reader.Start;
        r.DataEnd = reader.End;
        r.IsSignaturePresent = reader.Length >= Layout.SignatureLength && reader.MatchesAt(reader.Start, Layout.Signature);
        if (!r.IsSignaturePresent)
        {
            return;
        }

        long position = reader.Start + Layout.SignatureLength;
        Span<byte> head = stackalloc byte[Layout.ObjectHeaderLength];
        while (true)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
            if (reader.End - position < Layout.ObjectHeaderLength)
            {
                r.IsMetadataCut = true;
                r.LeftoverOffset = position;
                return;
            }

            int frame = Layout.FrameHeaderLength(reader, position);
            if (frame > 0)
            {
                r.AudioStart = position;
                r.IsEndMissing = true;
                r.FirstFrameHeaderLength = frame;
                return;
            }

            if (r.Objects.Count >= Records.MaxObjects)
            {
                r.IsLimitReached = true;
                r.LeftoverOffset = position;
                return;
            }

            reader.Position = position;
            reader.Read(head);
            Layout.ReadObjectHeader(BinaryPrimitives.ReadUInt32LittleEndian(head), out int id, out bool isReserved, out int size);
            long dataAt = position + Layout.ObjectHeaderLength;
            if (id == Layout.End)
            {
                var end = new ObjectRecord { Id = id, IsReserved = isReserved, Offset = position, Size = size, Held = 0, IsSizeValid = size == 0 };
                r.Objects.Add(end);
                r.AudioStart = dataAt;
                r.FirstFrameHeaderLength = Layout.FrameHeaderLength(reader, dataAt);
                return;
            }

            long held = Math.Min(size, reader.End - dataAt);
            var o = new ObjectRecord { Id = id, IsReserved = isReserved, Offset = position, Size = size, Held = held };
            r.Objects.Add(o);
            if (o.IsCut)
            {
                r.IsMetadataCut = true;
                return;
            }

            Read(r, reader, s, o);
            position = dataAt + size;
        }
    }

    private static void Read(Records r, ByteReader reader, State? s, ObjectRecord o)
    {
        long size = o.Size;
        long at = o.DataOffset;
        bool isDetailed = s is not null;
        if (Layout.HasCrc(o.Id) && size >= Layout.CrcLength && (isDetailed || o.Id == Layout.LastFrameInfo))
        {
            o.IsCompared = true;
            reader.Position = at;
            uint crc = Layout.Crc24Initial;
            byte[] buffer = new byte[(int)Math.Min(ReadChunk, Math.Max(1, size))];
            for (long left = size - Layout.CrcLength; left > 0;)
            {
                int n = (int)Math.Min(left, buffer.Length);
                reader.Read(buffer.AsSpan(0, n));
                crc = Layout.Crc24Update(crc, buffer.AsSpan(0, n));
                left -= n;
            }

            Span<byte> stored = stackalloc byte[Layout.CrcLength];
            reader.Read(stored);
            o.StoredCrc = (uint)(stored[0] | (stored[1] << 8) | (stored[2] << 16));
            o.ComputedCrc = crc;
        }

        switch (o.Id)
        {
            case Layout.StreamInfo:
                o.IsSizeValid = size is >= Layout.StreamInfoSizeMin and <= Layout.StreamInfoSizeMax;
                if (isDetailed && r.StreamInfoObject is null)
                {
                    r.StreamInfoObject = o;
                    if (o.IsSizeValid)
                    {
                        var bits = new Layout.LsbBits(reader.ReadBytesAt(at, (int)(size - Layout.CrcLength)));
                        r.Stream = StreamInfoFields.Read(ref bits);
                    }
                }

                break;
            case Layout.EncoderInfo:
                o.IsSizeValid = size == Layout.EncoderInfoSize;
                if (isDetailed && o.IsSizeValid && r.EncoderInfoObject is null)
                {
                    byte[] bytes = reader.ReadBytesAt(at, 4);
                    r.EncoderInfoObject = o;
                    r.EncoderVersion = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
                    r.Preset = bytes[3] & 15;
                    r.Evaluation = (bytes[3] >> 4) & 3;
                    r.EncoderReserved = bytes[3] >> 6;
                }

                break;
            case Layout.Md5:
                o.IsSizeValid = size == Layout.Md5Size;
                if (isDetailed && o.IsSizeValid && r.Md5Object is null)
                {
                    r.Md5Object = o;
                    r.Md5 = reader.ReadBytesAt(at, 16);
                }

                break;
            case Layout.LastFrameInfo:
                o.IsSizeValid = size == Layout.LastFrameInfoSize;
                if (o.IsSizeValid && r.LastFrameObject is null)
                {
                    byte[] bytes = reader.ReadBytesAt(at, 8);
                    r.LastFrameObject = o;
                    r.LastFramePosition = bytes[0] | ((long)bytes[1] << 8) | ((long)bytes[2] << 16) | ((long)bytes[3] << 24) | ((long)bytes[4] << 32);
                    r.LastFrameSize = bytes[5] | (bytes[6] << 8) | (bytes[7] << 16);
                }

                break;
            case Layout.WaveData:
                ReadWaveData(r, reader, s, o);
                break;
        }
    }

    // The wave data: a header size and a footer size of 3 bytes each, the header bytes, the footer bytes and the CRC-24.
    private static void ReadWaveData(Records r, ByteReader reader, State? s, ObjectRecord o)
    {
        if (o.Size < Layout.WaveDataFixed)
        {
            o.IsSizeValid = false;
            return;
        }

        byte[] sizes = reader.ReadBytesAt(o.DataOffset, 6);
        o.HeaderSize = sizes[0] | (sizes[1] << 8) | (sizes[2] << 16);
        o.FooterSize = sizes[3] | (sizes[4] << 8) | (sizes[5] << 16);
        o.IsSizeValid = o.Size == Layout.WaveDataFixed + o.HeaderSize + o.FooterSize;
        if (s is null || r.WaveDataObject is not null)
        {
            return;
        }

        r.WaveDataObject = o;
        if (!o.IsSizeValid)
        {
            return;
        }

        long headerAt = o.DataOffset + 6;
        if (o.HeaderSize > 0)
        {
            o.WavHeader = HeaderData.Analyze(s, reader, headerAt, o.HeaderSize, isHeader: true, isRiffFile: false);
        }

        if (o.FooterSize > 0)
        {
            bool isRiffFile = o.WavHeader?.Container is WavHeader.RiffContainer or WavHeader.Rf64Container or WavHeader.Bw64Container;
            o.WavFooter = HeaderData.Analyze(s, reader, headerAt + o.HeaderSize, o.FooterSize, isHeader: false, isRiffFile);
        }
    }
}
