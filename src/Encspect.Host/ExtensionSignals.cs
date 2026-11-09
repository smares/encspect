using Encspect.Core;

namespace Encspect.Host;

// The file name's extension (SniffContext.ExtensionHint: lower case, no dot) as a second, independent signal for a claim a parser makes from the bytes (docs/formats/optimfrog.md, "DualStream"). The
// parsers never see the file name, so the host applies the extension after the parse, as it combines the processor claims. The extension never drives
// format detection and never makes anything lossless: it can raise a claim the bytes make from low to medium confidence, and where it says "lossy" and the
// bytes say lossless it adds an Info finding and no claim. A stream or standard input has no name, and the claim keeps the confidence of the bytes.
internal static class ExtensionSignals
{
    private const string OptimFrogCodec = "OptimFROG";
    private const string DualStreamExtension = "ofs";
    private const string ClaimNode = "Lossy Stream";

    public static void Apply(string? extension, ParseContext context)
    {
        if (!string.Equals(extension, DualStreamExtension, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (StreamInfo stream in context.Streams.Where(s => s.Codec == OptimFrogCodec))
        {
            Node node = stream.Nodes[0];
            Node? claim = node.Children.FirstOrDefault(c => c.Name == ClaimNode);
            if (claim is null)
            {
                context.AddFinding(Severity.Info, "optimfrog.extension-differs",
                    "The file name ends in .ofs, the extension of DualStream files, but the data blocks' reserved bytes are those of a lossless file in Encspect's test files; no claim is made, and the extension decides nothing.", node);
                continue;
            }

            Node? confidence = claim.Children.FirstOrDefault(c => c.Name == "Confidence");
            Node? evidence = claim.Children.FirstOrDefault(c => c.Name == "Evidence");
            if (confidence is not null && evidence is not null)
            {
                confidence.Value = "Medium";
                evidence.Value = $"{evidence.Value}, and the file name ends in .ofs, the extension of DualStream files";
            }
        }
    }
}
