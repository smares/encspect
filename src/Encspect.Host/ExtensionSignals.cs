using Encspect.Core;

namespace Encspect.Host;

// The file name's extension (SniffContext.ExtensionHint: lower case, no dot) as a second, independent signal for a claim a parser makes from the bytes
// (docs/architecture.md, "The extension as a second signal"). The parsers never see the file name, so the host applies the extension after the parse, as it
// combines the processor claims, and only through the typed StreamClaim: the extension of the lossy part of a pair raises that claim from low to medium
// confidence and adds itself to its evidence; where the bytes speak against the claim (it is held unasserted) the host adds an Info finding and no claim. The
// extension never drives format detection and never makes anything lossless. A stream or standard input has no name, and the claim keeps the bytes' confidence.
internal static class ExtensionSignals
{
    // The extensions that name the lossy part of a pair: OptimFROG DualStream's. WavPack's hybrid files make no such claim.
    private static readonly HashSet<string> LossyPartExtensions = ["ofs"];

    public static void Apply(string? extension, ParseContext context)
    {
        if (extension is null || !LossyPartExtensions.Contains(extension))
        {
            return;
        }

        foreach (StreamInfo stream in context.Streams)
        {
            foreach (StreamClaim claim in stream.Claims.Where(c => c.Kind == StreamClaim.LossyPart))
            {
                if (claim.IsAsserted)
                {
                    claim.Raise(Confidence.Medium, $"the file name ends in .{extension}, the extension of DualStream files");
                }
                else
                {
                    context.AddFinding(Severity.Info, "optimfrog.extension-differs",
                        $"The file name ends in .{extension}, the extension of DualStream files, but {claim.Evidence}; no claim is made, and the extension decides nothing.", stream.Nodes[0]);
                }
            }
        }
    }
}
