using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio versions 1 and 2 (the format tags 0x0160 and 0x0161). Read at header level until the
// frames are read again by a clean-room rewrite: the stream is described by what the container and the
// type-specific data state, and its packets are counted.
public sealed class WmaStandardParser : ICodecParser
{
    public const string CodecId = "wma";

    // WMA_TYPE_SPECIFIC_DATA after the WAVEFORMATEX (ASF specification, section 11.1.1): a DWORD, a WORD and a DWORD.
    private const int TypeSpecificSize = 10;

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string name = context.Configuration?.Format?.FormatTag == 0x0160 ? "Windows Media Audio 1" : "Windows Media Audio 2";
        WmaHeaderOnly.Parse(context, name, Topics.WmaStandard, "wma.standard-not-read",
            "The frames of Windows Media Audio versions 1 and 2 are not read: the stream is described by what the container and the type-specific data state, and its packets are counted.", AddConfiguration);
    }

    // The specification defines the layout for the format tag 0x0161 only; the data of version 1 is left as the container's bytes.
    private static void AddConfiguration(CodecConfiguration configuration, FindingCollector findings)
    {
        if (configuration.Format?.FormatTag != 0x0161 || configuration.Data.Length < TypeSpecificSize)
        {
            return;
        }

        Node holder = configuration.Parent;
        MappedBytes data = configuration.Data;
        ReadOnlySpan<byte> bytes = data.Span;
        long offset = data.FileStart;
        holder.Add("Samples Per Block", offset, 4, NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(bytes), "samples", "sample"), StandardExplain.SamplesPerBlock, Topics.WmaStandard);
        holder.Add("Encode Options", offset + 4, 2, NodeValue.Hex(BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]), 4), StandardExplain.EncodeOptions, Topics.WmaStandard);
        holder.Add("Super Block Align", offset + 6, 4, NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..])), StandardExplain.SuperBlockAlign, Topics.WmaStandard);
        if (bytes.Length > TypeSpecificSize)
        {
            holder.Add("Extra Data", offset + TypeSpecificSize, bytes.Length - TypeSpecificSize, NodeValue.Integer(bytes.Length - TypeSpecificSize, "bytes", "byte"), StandardExplain.ExtraData);
        }
    }
}

internal static class StandardExplain
{
    public const string SamplesPerBlock = "The dwSamplesPerBlock field of the type-specific data of Windows Media Audio (ASF specification, section 11.1.1). The specification gives no further meaning.";
    public const string EncodeOptions = "The wEncodeOptions field of the type-specific data of Windows Media Audio (ASF specification, section 11.1.1), shown as the 16-bit value. The specification gives no meaning to its bits.";
    public const string SuperBlockAlign = "The dwSuperBlockAlign field of the type-specific data of Windows Media Audio (ASF specification, section 11.1.1). The specification gives no further meaning.";
    public const string ExtraData = "Bytes after the fields that section 11.1.1 of the ASF specification defines.";
}
