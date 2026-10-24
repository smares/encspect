using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// How the decoder reaches the records of the stream it decodes: through the stream's node, which is what the session's setup names. The decoder uses
// them to point a finding at a node the tree holds (Nodes.LosslessCheckFor), which only the parser knows, as the tree lists the detail of some units
// only. The table holds the records as long as the node lives.
internal static class DecodeLink
{
    private static readonly ConditionalWeakTable<Node, Records> Streams = new();

    public static void Register(Node stream, Records records) => Streams.AddOrUpdate(stream, records);

    public static Records? Find(Node stream) => Streams.TryGetValue(stream, out Records? records) ? records : null;
}
