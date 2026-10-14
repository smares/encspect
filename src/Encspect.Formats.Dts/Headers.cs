using Encspect.Core;

namespace Encspect.Formats.Dts;

// The first 12 bytes of a core frame in the bit-packed stream: everything sync and the frame's
// length need (ETSI TS 102 114 5.4.2, Table 5-1): the sync word and the fields up to HFLAG, which
// ends at bit 88.
internal readonly struct CoreHeader(UInt128 raw)
{
    public const int Bytes = 12;
    public const int SyncWord = 0x7FFE8001;
    public const int MinimumPackedLength = 96;
    public const int MinimumBlocks = 6;

    public UInt128 Raw => raw;

    public bool IsNormalFrame => Field(32, 1) == 1;

    public int DeficitSamples => Field(33, 5);

    public bool HasCrc => Field(38, 1) == 1;

    // NBLKS + 1: the number of blocks of 32 PCM samples per channel (5.4.2, NBLKS).
    public int Blocks => Field(39, 7) + 1;

    // FSIZE + 1: the frame's size in bytes of the bit-packed stream, extension data included.
    public int PackedLength => Field(46, 14) + 1;

    public int Amode => Field(60, 6);

    public int Sfreq => Field(66, 4);

    public int Rate => Field(70, 5);

    public bool FixedBit => Field(75, 1) == 1;

    public bool HasDynamicRange => Field(76, 1) == 1;

    public bool HasTimeStamp => Field(77, 1) == 1;

    public bool HasAuxiliaryData => Field(78, 1) == 1;

    public bool IsHdcd => Field(79, 1) == 1;

    public int ExtensionAudioId => Field(80, 3);

    public bool HasExtendedAudio => Field(83, 1) == 1;

    public bool SyncPerSubsubframe => Field(84, 1) == 1;

    public int Lff => Field(85, 2);

    public bool HasPredictorHistory => Field(87, 1) == 1;

    public int Samples => Blocks * 32;

    // Table 5-5; 0 for the codes the table marks invalid.
    public int SampleRate => Tables.SampleRate(Sfreq);

    // The bytes of the frame in the file in the form given.
    public int FileLength(StreamForm form) => Forms.FileLength(PackedLength, form);

    // What a header must hold to start a frame in a chain: a frame of at least 96 bytes (FSIZE 95 to 16,383, Table 5-1's text),
    // at least 6 blocks (NBLKS 5 to 127) and a sampling frequency Table 5-5 lists. Every other field is checked
    // and reported once the frame is in sync.
    public bool IsValid => PackedLength >= MinimumPackedLength && Blocks >= MinimumBlocks && SampleRate != 0;

    // Reads the header of the frame at the start of file bytes, or null where the sync word, the form or the
    // fields do not make one.
    public static (CoreHeader Header, StreamForm Form)? Read(ReadOnlySpan<byte> file)
    {
        if (ReadUnchecked(file) is { } read && read.Header.IsValid)
        {
            return read;
        }

        return null;
    }

    // The header at the start of file bytes whether or not its fields make a frame.
    public static (CoreHeader Header, StreamForm Form)? ReadUnchecked(ReadOnlySpan<byte> file)
    {
        if (Forms.Detect(file) is not { } form)
        {
            return null;
        }

        // 12 packed bytes are 96 bits: 7 words of a 14-bit form hold 98.
        int needed = form.Is14Bit() ? 14 : Bytes;
        if (file.Length < needed)
        {
            return null;
        }

        Span<byte> packed = stackalloc byte[16];
        Forms.Unpack(file[..needed], form, packed);
        UInt128 raw = 0;
        for (int i = 0; i < Bytes; i++)
        {
            raw = (raw << 8) | packed[i];
        }

        return (new CoreHeader(raw), form);
    }

    // Why the fields do not make a frame: the first one that is wrong.
    public string? InvalidReason => PackedLength < MinimumPackedLength ? $"FSIZE is {PackedLength - 1}, below the 95 that 5.4.2 allows"
        : Blocks < MinimumBlocks ? $"NBLKS is {Blocks - 1}, below the 5 that 5.4.2 allows"
        : SampleRate == 0 ? $"SFREQ {Sfreq} is invalid (Table 5-5)"
        : null;

    private int Field(int start, int width) => (int)((raw >> (96 - start - width)) & (((UInt128)1 << width) - 1));
}

// The first bytes of an extension substream (7.5.2, Table 7-2): the sync word 0x64582025, the user
// defined bits, the substream index, the header's size type and the two sizes, which are all that is
// needed to step over the substream: 10 bytes.
internal readonly struct ExssHeader(ulong high, int low)
{
    public const int Bytes = 10;
    public const uint SyncWord = 0x64582025;

    // Fewer bytes cannot hold the fields every header has (sync word, the sizes and flags, one asset's size and
    // descriptor, and the CRC), and FFmpeg's probe asks for as many (dts_probe: hdr_size < 16).
    public const int MinimumHeaderSize = 16;

    public const int MaximumHeaderSize = 4096;

    public int UserBits => (int)((high >> 24) & 0xFF);

    public int Index => (int)((high >> 22) & 3);

    public bool IsWide => ((high >> 21) & 1) != 0;

    public int HeaderSize => (int)(IsWide ? (high >> 9) & 0xFFF : (high >> 13) & 0xFF) + 1;

    public int TotalSize => IsWide
        ? (int)((((high & 0x1FF) << 11) | (uint)(low >> 5)) & 0xFFFFF) + 1
        : (int)(((high & 0x1FFF) << 3) | (uint)(low >> 13)) + 1;

    public bool IsValid => HeaderSize >= MinimumHeaderSize && TotalSize >= HeaderSize;

    public static ExssHeader? Read(ReadOnlySpan<byte> packed)
    {
        if (packed.Length < Bytes || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packed) != SyncWord)
        {
            return null;
        }

        ulong high = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(packed);
        int low = (packed[8] << 8) | packed[9];
        var header = new ExssHeader(high, low);
        return header.IsValid ? header : null;
    }
}

internal enum ItemKind : byte
{
    Core,
    Exss,
}

// One thing that starts at a sync word: a core frame or an extension substream, with what the chain
// of frames compares. Length is the item's bytes in the file.
internal readonly record struct Item(ItemKind Kind, StreamForm Form, int Length, CoreHeader Core, ExssHeader Exss)
{
    public bool IsCore => Kind == ItemKind.Core;

    public int SampleRate => IsCore ? Core.SampleRate : 0;

    // Core frames of a stream share their form and sampling frequency (Annex E.4: duration, sampling
    // frequency, channel arrangement and extensions stay constant for seamless playback); an extension
    // substream goes with whatever it follows, though only the 16-bit forms hold one.
    public bool Continues(Item previous)
    {
        if (IsCore && previous.IsCore)
        {
            return Form == previous.Form && Core.Sfreq == previous.Core.Sfreq;
        }

        if (!IsCore)
        {
            return !previous.IsCore || !previous.Form.Is14Bit();
        }

        return true;
    }
}

// How a unit lies in the file: its core frame, if it has one, and the extension substreams after it.
internal sealed class UnitLayout
{
    public const int MaxExtensionSubstreams = 4;

    public StreamForm Form { get; set; }

    // The core frame's bytes in the file (what the frame's header says, or fewer where the data ends or a new
    // chain starts inside it); 0 without a core frame.
    public int CoreLength { get; set; }

    // The frame's bytes in the file by its header, which a cut or truncated frame does not reach.
    public int CoreDeclaredLength { get; set; }

    public bool IsCut { get; set; }

    public bool IsTruncated { get; set; }

    public List<ExssRef> Substreams { get; } = [];

    // The core extension the analysis found, which the node pass reads back.
    public FoundExtension? Extension { get; set; }

    public long Offset { get; set; }

    // The unit's bytes in the file.
    public int Length { get; set; }
}

// An extension substream of a unit: where it starts relative to the unit, its length (nuExtSSFsize) and its index.
internal readonly record struct ExssRef(int Start, int Length, int Index, int Available);

// A core extension found in a frame: its kind, where its sync word lies in the unit and whether its size field gives the distance
// itself, which 6.4.2 accepts for legacy streams.
internal readonly record struct FoundExtension(CoreExtension Kind, int Byte, bool IsLegacySize);

// Where the analysis found the bytes: the file, or a packet a container handed over.
internal interface ISource
{
    // Copies bytes from the file offset into buffer, as many as there are; returns how many.
    int Read(long offset, Span<byte> buffer);
}
