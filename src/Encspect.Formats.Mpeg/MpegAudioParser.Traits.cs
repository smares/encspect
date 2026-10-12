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
        Layer3Traits traits = region.Traits;
        if (traits.Frames >= MinTraitFrames && traits.First is { } first)
        {
            (bool scaling, bool scfsi, bool switching) = (traits.UsesScaling, traits.UsesScfsi, traits.UsesSwitching);
            Node span = new("Evidence", traits.Start, traits.End - traits.Start);

            if (IsShine(region, traits))
            {
                return TraitNode("Shine", "No frame uses the bit reservoir, short blocks, scalefactor scaling or ancillary data, as Shine writes them");
            }

            // Helix sets both bits by default; LAME, Shine and Fraunhofer's encoders set neither.
            if (traits.AreAllCopyrightedOriginals && scfsi && scaling)
            {
                return TraitNode("Helix MP3 Encoder", "Copyright and original bits set in every frame, with scfsi and scalefactor scaling, as the Helix encoder writes them without its tag");
            }

            // mp3guessenc: "the Xing encoder never uses short blocks"; the Helix encoder that grew out of
            // it does, as do LAME, Fraunhofer's encoders and BladeEnc, even at the onset of a tone.
            if (!switching && !scfsi && traits.AreAllOriginals)
            {
                return TraitNode("Xing", "No short blocks or scfsi in any frame and the original bit in every one, as Xing's encoder writes them");
            }

            // BladeEnc codes plain stereo or mono at a constant bitrate, MPEG-1 only, never uses
            // scalefactor scaling, and leaves ancillary data at most in its last frame (mp3guessenc).
            // 0.80 and 0.81 also drain an overflowing reservoir into ancillary data mid-stream, as the
            // ISO dist10 encoder does, which leaves those encodes unnamed.
            if (first.IsMpeg1 && switching && !scaling && traits.AreAllPlainAtFirstBitrate
                && region.Ancillary is { } ancillary && ancillary.Bytes == ancillary.LastSegmentBytes)
            {
                return TraitNode("BladeEnc", "Short blocks without scalefactor scaling, plain stereo or mono at one bitrate, and ancillary data at most in the last frame, as BladeEnc writes them");
            }

            // scfsi only exists in MPEG-1.
            if (first.IsMpeg1 && scaling && !scfsi && switching)
            {
                return TraitNode("Fraunhofer", "Scalefactor scaling and short blocks but no scfsi in any frame, as Fraunhofer's encoders write them");
            }

            Node TraitNode(string name, string evidence)
            {
                Node node = new("Encoder", span.Offset, span.Length, name);
                node.Add("Confidence", span.Offset, span.Length, "Low");
                node.Add("Evidence", span.Offset, span.Length, evidence);
                return node;
            }
        }

        // TwoLAME and Xing's tompg both write it.
        if (region.Layer2Frames >= MinTraitFrames && region.UndefinedScalefactor is { } undefined)
        {
            Node node = new("Encoder", undefined.Offset, undefined.Length, "TwoLAME or Xing");
            node.Add("Confidence", undefined.Offset, undefined.Length, "Low");
            node.Add("Evidence", undefined.Offset, undefined.Length, "Scalefactor index 63, which TwoLAME and Xing's encoder write and Table B.1 of ISO/IEC 11172-3 leaves undefined");
            return node;
        }

        return null;
    }

    // Shine 3.1.1 never points main_data_begin back, codes every granule with long blocks and
    // without scalefactor scaling, and fills each frame with its main data.
    private static bool IsShine(RegionState region, Layer3Traits traits) =>
        traits.AreAllMainDataBeginsZero && !traits.UsesSwitching && !traits.UsesScaling && region.Ancillary is { Bytes: 0 };

    // What a region's complete Layer III audio frames have in common, gathered while they are read,
    // so their side information is decoded once and never kept.
    private sealed class Layer3Traits
    {
        public int Frames { get; private set; }

        public long Start { get; private set; }

        public long End { get; private set; }

        public MpegHeader? First { get; private set; }

        public bool UsesScaling { get; private set; }

        public bool UsesScfsi { get; private set; }

        public bool UsesSwitching { get; private set; }

        public bool AreAllMainDataBeginsZero { get; private set; } = true;

        public bool AreAllCopyrightedOriginals { get; private set; } = true;

        public bool AreAllOriginals { get; private set; } = true;

        // Stereo or mono, at the first frame's bitrate.
        public bool AreAllPlainAtFirstBitrate { get; private set; } = true;

        public void Add(Layer3SideInfo info, long start, long end)
        {
            MpegHeader header = info.Header;
            First ??= header;
            Start = Frames == 0 ? start : Start;
            End = end;
            Frames++;
            foreach (Layer3Granule[] row in info.Granules)
            {
                foreach (Layer3Granule g in row)
                {
                    UsesScaling |= g.ScalefacScale.Value == 1;
                    UsesSwitching |= g.IsSwitched;
                }
            }

            foreach (BitField scfsi in info.Scfsi)
            {
                UsesScfsi |= scfsi.Value != 0;
            }

            AreAllMainDataBeginsZero &= info.MainDataBegin.Value == 0;
            AreAllCopyrightedOriginals &= header is { IsCopyrighted: true, IsOriginal: true };
            AreAllOriginals &= header.IsOriginal;
            AreAllPlainAtFirstBitrate &= header.ChannelModeBits is 0 or 3 && header.BitrateIndex == First.Value.BitrateIndex;
        }
    }
}
