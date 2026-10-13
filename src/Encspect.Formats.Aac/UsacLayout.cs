using Encspect.Core;

namespace Encspect.Formats.Aac;

// The shape of a UsacConfig read to its end (ISO/IEC 23003-3 5.2, Tables 13 to 17): its elements
// in order, as "SCE", "CPE", "LFE" or "EXT" with the extension element type ("EXT3"), whether an
// audio pre-roll element (ID_EXT_ELE_AUDIOPREROLL, Table 18) comes first with an empty
// configuration, no default length and no fragmentation, whether a channel element uses harmonic
// SBR or PVC, and the types of its configuration extensions (Table 19). Config is the UsacConfig
// node.
internal sealed record UsacLayout(Node Config, IReadOnlyList<string> Elements, bool PlainPreRollFirst, bool HarmonicSbrOrPvc,
    IReadOnlyList<long> ConfigExtensions);
