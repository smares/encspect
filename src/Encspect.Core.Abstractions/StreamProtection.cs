namespace Encspect.Core;

// How a container protects one stream: the scheme that encrypts or locks its data and the DRM
// systems the file names for getting the keys. A stream nothing protects has no StreamProtection.
// Decrypting is out of scope; this only says what the file declares.
public sealed record StreamProtection
{
    // The protection scheme in the project's wording, such as "Common Encryption, AES-CTR (cenc)"
    // or "Windows Media DRM version 1".
    public required string Scheme { get; init; }

    // True where the stream's data is encrypted, so the analyzer did not read its frames. False for a
    // stream a scheme covers whose samples are in the clear: Common Encryption with a default of
    // not protected (ISO/IEC 23001-7).
    public bool IsEncrypted { get; init; }

    // The DRM systems the file names, such as "Widevine" or "Microsoft PlayReady", in file order
    // without repeats; a system the registry does not name is its system ID. Empty where the file
    // names none.
    public IReadOnlyList<string> Systems { get; init; } = [];
}
