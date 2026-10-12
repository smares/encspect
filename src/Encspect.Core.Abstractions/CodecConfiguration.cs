namespace Encspect.Core;

// What a container stores about a coding apart from its data, handed to the codec parser along
// with the data: MP4 keeps AAC's AudioSpecificConfig in the esds box inside moov, far from the
// samples in mdat. Data maps to the file, so the codec parser builds its nodes for it at the
// container's bytes, under Parent, the container's node that holds them; its other nodes go under
// the node the data is handed with.
public sealed record CodecConfiguration
{
    public CodecConfiguration(MappedBytes data, Node parent)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(parent);

        Data = data;
        Parent = parent;
    }

    // The configuration as the container stores it, such as MP4's DecoderSpecificInfo.
    public MappedBytes Data { get; }

    public Node Parent { get; }

    // What the container says about the bitrate: true for a variable bitrate, false for a constant
    // one, null when it says nothing (MP4's DecoderConfigDescriptor sets avgBitrate to 0 for a
    // variable bitrate, ISO/IEC 14496-1 7.2.6.6.2).
    public bool? IsVbr { get; init; }
}
