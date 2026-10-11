using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// Encoders known from what their frames have in common, for streams without a tag or signature
// that names them. Each rule comes from encodes of known signals with that encoder and holds for
// all of them (make_external_mpeg_fixtures.py), so a match is low confidence, and an encoder that
// shares the traits is not ruled out.
public sealed partial class MpegAudioParser
{
    // A short stream has too few frames to show an encoder's habits.
    private const int MinTraitFrames = 20;

    private static Node? TraitEncoder(RegionState region)
    {
        List<Entry> layer3 = region.Entries.Where(e => IsAudio(e) && e.SideInfo is not null).ToList();
        if (layer3.Count >= MinTraitFrames)
        {
            Layer3SideInfo[] infos = layer3.Select(e => Layer3SideInfo.Read(e.SideInfo!, new MpegHeader(e.Header))).ToArray();
            var granules = infos.SelectMany(i => i.All.Select(g => g.Info)).ToList();
            bool scaling = granules.Any(g => g.ScalefacScale.Value == 1);
            bool scfsi = infos.Any(i => i.Scfsi.Any(f => f.Value != 0));
            Node span = new("Evidence", layer3[0].Offset, layer3[^1].End - layer3[0].Offset);

            if (IsShine(region, infos, granules, scaling))
            {
                return TraitNode("Shine", "No frame uses the bit reservoir, short blocks, scalefactor scaling or ancillary data, as Shine writes them");
            }

            // Helix sets both bits by default; LAME, Shine and Fraunhofer's encoders set neither.
            if (layer3.All(e => new MpegHeader(e.Header) is { IsCopyrighted: true, IsOriginal: true }) && scfsi && scaling)
            {
                return TraitNode("Helix MP3 Encoder", "Copyright and original bits set in every frame, with scfsi and scalefactor scaling, as the Helix encoder writes them without its tag");
            }

            // scfsi only exists in MPEG-1.
            if (new MpegHeader(layer3[0].Header).IsMpeg1 && scaling && !scfsi)
            {
                return TraitNode("Fraunhofer", "Scalefactor scaling but no scfsi in any frame, as Fraunhofer's encoders write them");
            }

            Node TraitNode(string name, string evidence)
            {
                Node node = new("Encoder", span.Offset, span.Length, name);
                node.Add("Confidence", span.Offset, span.Length, "Low");
                node.Add("Evidence", span.Offset, span.Length, evidence);
                return node;
            }
        }

        List<Entry> layer2 = region.Entries.Where(e => IsAudio(e) && e.Layer2 is not null).ToList();
        if (layer2.Count >= MinTraitFrames && layer2.Select(e => e.Layer2!.UndefinedScalefactor).FirstOrDefault(n => n is not null) is { } undefined)
        {
            Node node = new("Encoder", undefined.Offset, undefined.Length, "TwoLAME");
            node.Add("Confidence", undefined.Offset, undefined.Length, "Low");
            node.Add("Evidence", undefined.Offset, undefined.Length, "Scalefactor index 63, which TwoLAME writes and Table B.1 of ISO/IEC 11172-3 leaves undefined");
            return node;
        }

        return null;
    }

    // Shine 3.1.1 never points main_data_begin back, codes every granule with long blocks and
    // without scalefactor scaling, and fills each frame with its main data.
    private static bool IsShine(RegionState region, Layer3SideInfo[] infos, List<Layer3Granule> granules, bool scaling) =>
        infos.All(i => i.MainDataBegin.Value == 0) && granules.All(g => !g.IsSwitched) && !scaling && region.Ancillary is { Bytes: 0 };
}
