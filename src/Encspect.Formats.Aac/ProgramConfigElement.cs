using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Aac;

// program_config_element (ISO/IEC 14496-3 4.4.1.1, Table 4.2): the channel layout a channel
// configuration of 0 leaves to it, in an AudioSpecificConfig or at the start of a raw data block.
internal static class ProgramConfigElement
{
    // Reads the element from its instance tag on (the ID is the caller's) and returns its channels:
    // one per single channel element, two per channel pair, plus the LFE elements. anchor is the bit
    // the comment's byte alignment counts from.
    public static int Read(BitFields f, Node node, int anchor, Action<Severity, string, string, Node> report)
    {
        f.Read(node, "Element Instance Tag", 4, Explain.PceTag);
        f.Read(node, "Object Type", 2, Explain.PceObjectType, v => $"{v} ({AacTables.ObjectTypeName((int)v + 1)})");
        f.Read(node, "Sample Rate", 4, Explain.PceSampleRate, v => AacTables.SampleRate((int)v) is { } rate ? NodeValue.Integer(rate, "Hz") : $"{v} (reserved)");
        int front = (int)f.Read(node, "Front Channel Elements", 4, Explain.FrontElements);
        int side = (int)f.Read(node, "Side Channel Elements", 4, Explain.SideElements);
        int back = (int)f.Read(node, "Back Channel Elements", 4, Explain.BackElements);
        int lfe = (int)f.Read(node, "LFE Channel Elements", 2, Explain.LfeElements);
        int data = (int)f.Read(node, "Associated Data Elements", 3, Explain.DataElements);
        int coupling = (int)f.Read(node, "Valid Coupling Channel Elements", 4, Explain.CouplingElements);
        if (f.Flag(node, "HasMonoMixdown", Explain.HasMonoMixdown))
        {
            f.Read(node, "Mono Mixdown Element", 4, Explain.MonoMixdown);
        }

        if (f.Flag(node, "HasStereoMixdown", Explain.HasStereoMixdown))
        {
            f.Read(node, "Stereo Mixdown Element", 4, Explain.StereoMixdown);
        }

        if (f.Flag(node, "HasMatrixMixdown", Explain.HasMatrixMixdown))
        {
            f.Read(node, "Matrix Mixdown Index", 2, Explain.MatrixMixdown);
            f.Flag(node, "HasPseudoSurround", Explain.PseudoSurround);
        }

        int channels = lfe;
        foreach ((string position, int count) in new[] { ("Front", front), ("Side", side), ("Back", back) })
        {
            for (int i = 0; i < count; i++)
            {
                int start = f.Position;
                var element = new Node("Element", 0, 0);
                bool isPair = f.Flag(element, "IsChannelPair", Explain.IsChannelPair);
                f.Read(element, "Tag", 4, Explain.ElementTag);
                channels += isPair ? 2 : 1;
                node.Add(f.Span($"{position} Element", start, isPair ? "CPE" : "SCE", Explain.PceChannelElement, element));
            }
        }

        for (int i = 0; i < lfe; i++)
        {
            f.Read(node, "LFE Element Tag", 4, Explain.LfeTag);
        }

        for (int i = 0; i < data; i++)
        {
            f.Read(node, "Associated Data Element Tag", 4, Explain.DataTag);
        }

        for (int i = 0; i < coupling; i++)
        {
            int start = f.Position;
            var element = new Node("Element", 0, 0);
            f.Flag(element, "IsIndependentlySwitched", Explain.IndependentlySwitched);
            f.Read(element, "Tag", 4, Explain.ElementTag);
            node.Add(f.Span("Coupling Element", start, null, Explain.CouplingElement, element));
        }

        f.Align(anchor);
        int length = (int)f.Read(node, "Comment Length", 8, Explain.CommentLength, v => NodeValue.Integer(v, "bytes"));
        if (length > 0)
        {
            int start = f.Position;
            var comment = new byte[length];
            for (int i = 0; i < length; i++)
            {
                comment[i] = (byte)f.Take(8);
            }

            node.Add(f.Span("Comment", start, Encoding.Latin1.GetString(comment), Explain.Comment));
        }

        if (channels == 0)
        {
            report(Severity.Warning, "aac.pce-without-channels", "The program config element defines no channels.", node);
        }

        return channels;
    }
}
