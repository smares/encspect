// Follows Apple's ALAC reference implementation (macosforge/alac, ALACMagicCookieDescription.txt, ALACAudioTypes.h and ALACDecoder.cpp, Init),
// Apache License 2.0: Copyright (c) 2011 Apple Inc. See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Alac;

// The magic cookie of an ALAC stream (ALACMagicCookieDescription.txt): the ALACSpecificConfig of 24 bytes and, optionally, the 24 bytes of the
// channel layout info, all big-endian. An older encoder's cookie wraps these in a format atom ("frma"), an "alac" atom header and a terminator
// atom; Apple's decoder skips the first two when they are there (ALACDecoder.cpp, Init). Every field is read once here; the nodes and the walk
// work from this record.
internal sealed class AlacCookie
{
    public const int ConfigLength = 24;
    public const int LayoutLength = 24;
    public const int AtomLength = 12;
    public const int TerminatorLength = 8;

    // The values Apple's encoder writes for the tuning parameters (ALACEncoder.cpp, GetConfig: PB0, MB0 and KB0).
    public const int StandardPb = 40;
    public const int StandardMb = 10;
    public const int StandardKb = 14;

    // The frame length the format's configuration may state: 2^20 samples per channel, the ceiling docs/design/decoders.md gives the decoder.
    public const uint MaxFrameLength = 1 << 20;

    // The channel layout tags ALAC defines (ALACAudioTypes.h): the number of channels is the low 16 bits.
    public const int MaxChannels = 8;

    // The atoms and the config as laid out in the cookie's bytes, as offsets into them; -1 where the cookie has none.
    public int FormatAtomAt { get; init; } = -1;

    public int ConfigAtomAt { get; init; } = -1;

    public int ConfigAt { get; init; }

    public int LayoutAt { get; init; } = -1;

    public int TerminatorAt { get; init; } = -1;

    public int ExtraAt { get; init; } = -1;

    public int ExtraLength { get; init; }

    public int Length { get; init; }

    // The format atom's size and format, and the config atom's size and version and flags.
    public uint FormatAtomSize { get; init; }

    public uint FormatAtomFormat { get; init; }

    public uint ConfigAtomSize { get; init; }

    public uint ConfigAtomFlags { get; init; }

    // Whether the cookie holds all 24 bytes of the config.
    public bool IsComplete { get; init; }

    public uint FrameLength { get; init; }

    public int CompatibleVersion { get; init; }

    public int BitDepth { get; init; }

    public int Pb { get; init; }

    public int Mb { get; init; }

    public int Kb { get; init; }

    public int Channels { get; init; }

    public int MaxRun { get; init; }

    public uint MaxFrameBytes { get; init; }

    public uint AverageBitRate { get; init; }

    public uint SampleRate { get; init; }

    // The channel layout info: its size and ID as stored, the version and flags, the layout tag, and the two reserved words, which Core Audio's
    // AudioChannelLayout gives as the channel bitmap and the number of channel descriptions.
    public bool HasLayout => LayoutAt >= 0;

    public uint LayoutSize { get; init; }

    public uint LayoutId { get; init; }

    public uint LayoutFlags { get; init; }

    public uint LayoutTag { get; init; }

    public uint LayoutBitmap { get; init; }

    public uint LayoutDescriptions { get; init; }

    // The frame length a frame without its own sample count has.
    public bool IsFrameLengthValid => FrameLength is > 0 and <= MaxFrameLength;

    // The bit depths Apple's encoder writes (ALACEncoder.cpp: 16, 20, 24 and 32).
    public bool IsBitDepthValid => BitDepth is 16 or 20 or 24 or 32;

    public bool AreChannelsValid => Channels is >= 1 and <= MaxChannels;

    // Whether the walk of a frame can follow the cookie: it needs the channels and a bit depth the sample widths can be worked out from.
    public bool CanWalk => IsComplete && Channels >= 1 && BitDepth is >= 1 and <= 32;

    // The channels of the eight layout tags ALAC defines, in the order of the stream's channels, as ALACAudioTypes.h lists them: its comment gives the
    // order of 3 channels as center, left, right, which is what the elements of Apple's encoder carry (a single channel element, then a pair), though
    // Core Audio's own tag 113 is left, right, center.
    private static readonly Dictionary<uint, string> LayoutTags = new()
    {
        [(100u << 16) | 1] = "C",
        [(101u << 16) | 2] = "L R",
        [(113u << 16) | 3] = "C L R",
        [(116u << 16) | 4] = "C L R Cs",
        [(120u << 16) | 5] = "C L R Ls Rs",
        [(124u << 16) | 6] = "C L R Ls Rs LFE",
        [(142u << 16) | 7] = "C L R Ls Rs Cs LFE",
        [(127u << 16) | 8] = "C Lc Rc L R Ls Rs LFE",
    };

    // The speakers the cookie names, one per channel in the stream's order: from the channel layout info where it has one that names the
    // channels (ALAC's own tags first, then Core Audio's), else mono and stereo as the description assumes ("If the channel layout is absent from the
    // cookie"); three channels and more without layout info have no specific designation.
    public IReadOnlyList<Speaker>? Layout
    {
        get
        {
            if (HasLayout)
            {
                IReadOnlyList<Speaker>? named = LayoutTags.TryGetValue(LayoutTag, out string? channels)
                    ? CoreAudioLayouts.FromAbbreviations(channels.Split(' '))
                    : CoreAudioLayouts.Of(LayoutTag, LayoutBitmap, null);
                return named is not null && named.Count == Channels ? named : null;
            }

            return Channels switch
            {
                1 => ChannelLayouts.Mono,
                2 => ChannelLayouts.Stereo,
                _ => null,
            };
        }
    }

    // Reads the cookie from the configuration the container stores. Never throws: a cookie that is too short gives what it holds.
    public static AlacCookie Read(ReadOnlySpan<byte> data)
    {
        int at = 0;
        int formatAtom = -1;
        uint formatSize = 0;
        uint formatType = 0;
        if (HasAtom(data, at, "frma"u8))
        {
            formatAtom = at;
            formatSize = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            formatType = data.Length >= at + AtomLength ? BinaryPrimitives.ReadUInt32BigEndian(data[(at + 8)..]) : 0;
            at += AtomLength;
        }

        int configAtom = -1;
        uint configSize = 0;
        uint configFlags = 0;
        if (HasAtom(data, at, "alac"u8))
        {
            configAtom = at;
            configSize = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            configFlags = data.Length >= at + AtomLength ? BinaryPrimitives.ReadUInt32BigEndian(data[(at + 8)..]) : 0;
            at += AtomLength;
        }

        int remaining = Math.Max(0, data.Length - at);
        bool isComplete = remaining >= ConfigLength;
        ReadOnlySpan<byte> config = isComplete ? data.Slice(at, ConfigLength) : default;
        int next = at + Math.Min(remaining, ConfigLength);
        int layout = -1;
        int terminator = -1;
        int extra = -1;
        int extraLength = 0;
        uint layoutSize = 0;
        uint layoutId = 0;
        uint layoutFlags = 0;
        uint layoutTag = 0;
        uint layoutBitmap = 0;
        uint layoutDescriptions = 0;
        if (isComplete)
        {
            if (data.Length - next >= LayoutLength && data.Slice(next + 4, 4).SequenceEqual("chan"u8))
            {
                layout = next;
                ReadOnlySpan<byte> info = data.Slice(next, LayoutLength);
                layoutSize = BinaryPrimitives.ReadUInt32BigEndian(info);
                layoutId = BinaryPrimitives.ReadUInt32BigEndian(info[4..]);
                layoutFlags = BinaryPrimitives.ReadUInt32BigEndian(info[8..]);
                layoutTag = BinaryPrimitives.ReadUInt32BigEndian(info[12..]);
                layoutBitmap = BinaryPrimitives.ReadUInt32BigEndian(info[16..]);
                layoutDescriptions = BinaryPrimitives.ReadUInt32BigEndian(info[20..]);
                next += LayoutLength;
            }

            // The terminator atom of the older cookie: a size of 8 and an ID of 0.
            if (data.Length - next >= TerminatorLength && BinaryPrimitives.ReadUInt32BigEndian(data[next..]) == TerminatorLength && BinaryPrimitives.ReadUInt32BigEndian(data[(next + 4)..]) == 0)
            {
                terminator = next;
                next += TerminatorLength;
            }

            if (data.Length > next)
            {
                extra = next;
                extraLength = data.Length - next;
            }
        }

        return new AlacCookie
        {
            Length = data.Length,
            FormatAtomAt = formatAtom,
            FormatAtomSize = formatSize,
            FormatAtomFormat = formatType,
            ConfigAtomAt = configAtom,
            ConfigAtomSize = configSize,
            ConfigAtomFlags = configFlags,
            ConfigAt = at,
            IsComplete = isComplete,
            FrameLength = isComplete ? BinaryPrimitives.ReadUInt32BigEndian(config) : 0,
            CompatibleVersion = isComplete ? config[4] : 0,
            BitDepth = isComplete ? config[5] : 0,
            Pb = isComplete ? config[6] : 0,
            Mb = isComplete ? config[7] : 0,
            Kb = isComplete ? config[8] : 0,
            Channels = isComplete ? config[9] : 0,
            MaxRun = isComplete ? BinaryPrimitives.ReadUInt16BigEndian(config[10..]) : 0,
            MaxFrameBytes = isComplete ? BinaryPrimitives.ReadUInt32BigEndian(config[12..]) : 0,
            AverageBitRate = isComplete ? BinaryPrimitives.ReadUInt32BigEndian(config[16..]) : 0,
            SampleRate = isComplete ? BinaryPrimitives.ReadUInt32BigEndian(config[20..]) : 0,
            LayoutAt = layout,
            LayoutSize = layoutSize,
            LayoutId = layoutId,
            LayoutFlags = layoutFlags,
            LayoutTag = layoutTag,
            LayoutBitmap = layoutBitmap,
            LayoutDescriptions = layoutDescriptions,
            TerminatorAt = terminator,
            ExtraAt = extra,
            ExtraLength = extraLength,
        };
    }

    // An atom of the four-character type at the offset, as Apple's decoder looks for one: its type is at bytes 4 to 7, and there is room for
    // its 12 bytes (the decoder does not check the length; a cookie that ends sooner has no such atom here).
    private static bool HasAtom(ReadOnlySpan<byte> data, int at, ReadOnlySpan<byte> type) =>
        data.Length >= at + AtomLength && data.Slice(at + 4, 4).SequenceEqual(type);
}
