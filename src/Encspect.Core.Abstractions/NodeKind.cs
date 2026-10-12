namespace Encspect.Core;

// What part of a file a node stands for, so front ends find tags, streams, frames and statistics
// without knowing the names a parser gives them. Most nodes are details.
public enum NodeKind
{
    Detail,

    // A tag, wherever it sits: ID3, APE, Lyrics3, and later a tag chunk inside a container.
    Tag,

    // A stretch of a format's audio data, such as an MPEG Audio region between tags.
    Stream,

    // One frame or packet of audio data.
    Frame,

    // The side information of a frame: Layer III side information, Layer I and II bit allocation,
    // scfsi and scalefactors.
    SideInfo,

    // Figures computed over many frames.
    Statistics,
}
