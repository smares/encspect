namespace Encspect.Core;

// The content protection systems DASH-IF's registry lists by their system ID (the SystemID of a
// pssh box, ISO/IEC 23001-7, and the UUID of a ContentProtection element), copied with the
// registry's names (https://dashif.org/identifiers/content_protection/). scripts/fixtures/check_dashif_systems.py
// holds the page by its SHA-256 and fails when this table and the page differ.
public static class ProtectionSystems
{
    private static readonly Dictionary<Guid, string> Names = new()
    {
        [new("6dd8b3c3-45f4-4a68-bf3a-64168d01a4a6")] = "ABV DRM (MoDRM)",
        [new("f239e769-efa3-4850-9c16-a903c6932efb")] = "Adobe Primetime DRM version 4",
        [new("616c7469-6361-7374-2d50-726f74656374")] = "Alticast",
        [new("94ce86fb-07ff-4f43-adb8-93d2fa968ca2")] = "Apple FairPlay",
        [new("279fe473-512c-48fe-ade8-d176fee6b40f")] = "Arris Titanium",
        [new("3d5e6d35-9b9a-41e8-b843-dd3c6e72c42c")] = "ChinaDRM",
        [new("3ea8778f-7742-4bf9-b18b-e834b2acbd47")] = "Clear Key AES-128",
        [new("be58615b-19c4-4684-88b3-c8c57e99e957")] = "Clear Key SAMPLE-AES",
        [new("e2719d58-a985-b3c9-781a-b030af78d30e")] = "Clear Key DASH-IF",
        [new("644fe7b5-260f-4fad-949a-0762ffb054b4")] = "CMLA (OMA DRM)",
        [new("37c33258-7b99-4c7e-b15d-19af74482154")] = "Commscope Titanium V3",
        [new("45d481cb-8fe0-49c0-ada9-ab2d2455b2f2")] = "CoreCrypt",
        [new("dcf4e3e3-62f1-5818-7ba6-0a6fe33ff3dd")] = "DigiCAP SmartXess",
        [new("35bf197b-530e-42d7-8b65-1b4bf415070f")] = "DivX DRM Series 5",
        [new("80a6be7e-1448-4c37-9e70-d5aebe04c8d2")] = "Irdeto Content Protection",
        [new("5e629af5-38da-4063-8977-97ffbd9902d4")] = "Marlin Adaptive Streaming Simple Profile V1.0",
        [new("9a04f079-9840-4286-ab92-e65be0885f95")] = "Microsoft PlayReady",
        [new("6a99532d-869f-5922-9a91-113ab7b1e2f3")] = "MobiTV DRM",
        [new("adb41c24-2dbf-4a6d-958b-4457c0d27b95")] = "Nagra MediaAccess PRM 3.0",
        [new("66472929-a568-69b6-53a2-352f951475e7")] = "Nagra One-way hardware DRM",
        [new("1f83e1e8-6ee9-4f0d-ba2f-5ec4e3ed1a66")] = "SecureMedia",
        [new("992c46e6-c437-4899-b6a0-50fa91ad0e39")] = "SecureMedia SteelKnot",
        [new("a68129d3-575b-4f1a-9cba-3223846cf7c3")] = "Synamedia/Cisco/NDS VideoGuard DRM",
        [new("aa11967f-cc01-4a4a-8e99-c5d3dddfea2d")] = "Unitend DRM (UDRM)",
        [new("9a27dd82-fde2-4725-8cbc-4234aa06ec09")] = "Verimatrix VCAS",
        [new("b4413586-c58c-ffb0-94a5-d4896c1af6c3")] = "Viaccess-Orca DRM (VODRM)",
        [new("793b7956-9f94-4946-a942-23e7ef7e44b4")] = "VisionCrypt",
        [new("1077efec-c0b2-4d02-ace3-3c1e52e2fb4b")] = "W3C Common PSSH box",
        [new("edef8ba9-79d6-4ace-a3c8-27dcd51d21ed")] = "Widevine Content Protection",
    };

    // The registry's name for a system ID given in the order the bytes lie in a box (the UUID's text
    // order), or null where the registry does not list it.
    public static string? Name(Guid systemId) => Names.GetValueOrDefault(systemId);

    // The system ID as the registry writes it: lower-case hexadecimal in the 8-4-4-4-12 groups.
    public static string Text(Guid systemId) => systemId.ToString("D");

    // "Name" for a listed system, else the ID.
    public static string Describe(Guid systemId) => Name(systemId) ?? Text(systemId);

    // The ID of 16 bytes as a pssh box lays it out: a UUID, big-endian in its first three fields.
    public static Guid FromBytes(ReadOnlySpan<byte> bytes) => new(bytes, bigEndian: true);
}
