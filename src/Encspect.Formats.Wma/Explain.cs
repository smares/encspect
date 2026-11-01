namespace Encspect.Formats.Wma;

// What each Windows Media Audio node is. Constants only, shared by every node of their kind. The frames of
// Professional and Voice are not read, so the nodes of those streams describe only what the container states.
internal static class Explain
{
    public const string EncryptedHeaderOnlyStream = "The stream as the container and the codec-specific data describe it: its packets are encrypted and their frames are not read.";
    public const string HeaderOnlyStream = "The stream as the container and the codec-specific data describe it: its packets are counted and their frames are not read.";
    public const string Statistics = "Figures over the stream's packets, counted from the container.";
    public const string StatPackets = "How many packets the stream has.";
    public const string StatPacketBytes = "The bytes of all the packets.";
    public const string StatBlockAlignment = "The size of each packet, the container's block alignment.";
    public const string StatSmallestPacket = "The fewest bytes a packet takes.";
    public const string StatLargestPacket = "The most bytes a packet takes.";
    public const string StatNominalBitrate = "The average bytes per second the container states for the stream (the WAVEFORMATEX's nAvgBytesPerSec), as a bitrate. For a variable bitrate stream it can differ much from the bitrate the packets take.";
    public const string StatAverageBitrate = "The bytes of all the packets divided by the stream's duration, as a bitrate. It is the figure the stream reports as its average bitrate.";
    public const string StatContainerSamples = "The length of the stream as the container states it, in samples per channel: the File Properties Object's play duration less the preroll (section 3.2 of the ASF specification), at the stream's sampling rate and rounded to the nearest sample, halves up. The send duration is not used. It is the header's statement and is not counted from the audio, so it can differ from the samples the stream codes.";
    public const string StatContainerDuration = "The length of the stream as the container states it, the play duration less the preroll.";

    public const string Encoder = "Which program encoded the stream, as the analyzer concludes from the evidence below.";
    public const string Confidence = "How sure the naming is: high when the file carries the encoder's own trait, medium for a pattern of the encoder's, low when only a claim in the metadata names it.";
    public const string Evidence = "What points to the encoder: a trait of the stream, or an attribute of the file that names it, with its offset in the file where it has one.";

    public const string WriterEvidence = "What points to the writer: the attribute that names it or the header layout that is its habit, with its offset in the file.";
}
