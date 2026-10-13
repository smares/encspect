namespace Encspect.Formats.Asf;

// The GUIDs of the ASF specification, revision 01.20.03, section 10, and the names it gives them.
// GUIDs lie in the file as Windows lays them out (the first three fields little-endian), which is
// how System.Guid reads 16 bytes.
internal static class AsfGuids
{
    public static readonly Guid Header = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
    public static readonly Guid Data = new("75B22636-668E-11CF-A6D9-00AA0062CE6C");
    public static readonly Guid SimpleIndex = new("33000890-E5B1-11CF-89F4-00A0C90349CB");
    public static readonly Guid Index = new("D6E229D3-35DA-11D1-9034-00A0C90349BE");
    public static readonly Guid MediaObjectIndex = new("FEB103F8-12AD-4C64-840F-2A1D2F7AD48C");
    public static readonly Guid TimecodeIndex = new("3CB73FD0-0C4A-4803-953D-EDF7B6228F0C");

    public static readonly Guid FileProperties = new("8CABDCA1-A947-11CF-8EE4-00C00C205365");
    public static readonly Guid StreamProperties = new("B7DC0791-A9B7-11CF-8EE6-00C00C205365");
    public static readonly Guid HeaderExtension = new("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
    public static readonly Guid CodecList = new("86D15240-311D-11D0-A3A4-00A0C90348F6");
    public static readonly Guid ScriptCommand = new("1EFB1A30-0B62-11D0-A39B-00A0C90348F6");
    public static readonly Guid Marker = new("F487CD01-A951-11CF-8EE6-00C00C205365");
    public static readonly Guid BitrateMutualExclusion = new("D6E229DC-35DA-11D1-9034-00A0C90349BE");
    public static readonly Guid ErrorCorrection = new("75B22635-668E-11CF-A6D9-00AA0062CE6C");
    public static readonly Guid ContentDescription = new("75B22633-668E-11CF-A6D9-00AA0062CE6C");
    public static readonly Guid ExtendedContentDescription = new("D2D0A440-E307-11D2-97F0-00A0C95EA850");
    public static readonly Guid ContentBranding = new("2211B3FA-BD23-11D2-B4B7-00A0C955FC6E");
    public static readonly Guid StreamBitrateProperties = new("7BF875CE-468D-11D1-8D82-006097C9A2B2");
    public static readonly Guid ContentEncryption = new("2211B3FB-BD23-11D2-B4B7-00A0C955FC6E");
    public static readonly Guid ExtendedContentEncryption = new("298AE614-2622-4C17-B935-DAE07EE9289C");
    public static readonly Guid DigitalSignature = new("2211B3FC-BD23-11D2-B4B7-00A0C955FC6E");
    public static readonly Guid Padding = new("1806D474-CADF-4509-A4BA-9AABCB96AAE8");

    public static readonly Guid ExtendedStreamProperties = new("14E6A5CB-C672-4332-8399-A96952065B5A");
    public static readonly Guid AdvancedMutualExclusion = new("A08649CF-4775-4670-8A16-6E35357566CD");
    public static readonly Guid GroupMutualExclusion = new("D1465A40-5A79-4338-B71B-E36B8FD6C249");
    public static readonly Guid StreamPrioritization = new("D4FED15B-88D3-454F-81F0-ED5C45999E24");
    public static readonly Guid BandwidthSharing = new("A69609E6-517B-11D2-B6AF-00C04FD908E9");
    public static readonly Guid LanguageList = new("7C4346A9-EFE0-4BFC-B229-393EDE415C85");
    public static readonly Guid Metadata = new("C5F8CBEA-5BAF-4877-8467-AA8C44FA4CCA");
    public static readonly Guid MetadataLibrary = new("44231C94-9498-49D1-A141-1D134E457054");
    public static readonly Guid IndexParameters = new("D6E229DF-35DA-11D1-9034-00A0C90349BE");
    public static readonly Guid MediaObjectIndexParameters = new("6B203BAD-3F11-48E4-ACA8-D7613DE2CFA7");
    public static readonly Guid TimecodeIndexParameters = new("F55E496D-9797-4B5D-8C8B-604DFE9BFB24");
    public static readonly Guid AdvancedContentEncryption = new("43058533-6981-49E6-9B74-AD12CB86D58C");

    // The Compatibility Object (section 4.12), which the Windows Media Format SDK's writer puts in every file of the project's fixtures. The
    // text of revision 01.20.03 prints the Header Object's GUID for it in section 10.3, a misprint: 26 bytes with
    // profile 2 and mode 1, as section 4.12 sets them, carry this GUID in all the SDK's files, and MediaInfoLib
    // (File_Wm_Elements.cpp, Header_HeaderExtension_Compatibility) names it so.
    public static readonly Guid Compatibility = new("26F18B5D-4584-47EC-9F5F-0E651F0452C9");

    // Not in the specification or in FFmpeg: MediaInfoLib (File_Wm_Elements.cpp) calls it Index Placeholder, and the
    // Windows Media Format SDK's writer puts it in the Header Extension Object of the constant bitrate files in the project's
    // fixtures (written on Windows Server 2025, wmvcore 12.0.26100), and in none of its variable bitrate or Lossless files. Readers ignore it.
    public static readonly Guid IndexPlaceholder = new("D9AADE20-7C17-4F9C-BC28-8555DD98E2A2");

    public static readonly Guid Reserved1 = new("ABD3D211-A9BA-11CF-8EE6-00C00C205365");
    public static readonly Guid Reserved2 = new("86D15241-311D-11D0-A3A4-00A0C90348F6");
    public static readonly Guid Reserved3 = new("4B1ACBE3-100B-11D0-A39B-00A0C90348F6");
    public static readonly Guid Reserved4 = new("4CFEDB20-75F6-11CF-9C0F-00A0C90349CB");

    public static readonly Guid AudioMedia = new("F8699E40-5B4D-11CF-A8FD-00805F5C442B");
    public static readonly Guid VideoMedia = new("BC19EFC0-5B4D-11CF-A8FD-00805F5C442B");
    public static readonly Guid CommandMedia = new("59DACFC0-59E6-11D0-A3AC-00A0C90348F6");
    public static readonly Guid JfifMedia = new("B61BE100-5B4E-11CF-A8FD-00805F5C442B");
    public static readonly Guid DegradableJpegMedia = new("35907DE0-E415-11CF-A917-00805F5C442B");
    public static readonly Guid FileTransferMedia = new("91BD222C-F21C-497A-8B6D-5AA86BFC0185");
    public static readonly Guid BinaryMedia = new("3AFB65E2-47EF-40F2-AC2C-70A90D71D343");

    public static readonly Guid NoErrorCorrection = new("20FB5700-5B55-11CF-A8FD-00805F5C442B");
    public static readonly Guid AudioSpread = new("BFC3CD50-618F-11CF-8BB2-00AA00B4E220");

    public static readonly Guid MutexLanguage = new("D6E22A00-35DA-11D1-9034-00A0C90349BE");
    public static readonly Guid MutexBitrate = new("D6E22A01-35DA-11D1-9034-00A0C90349BE");
    public static readonly Guid MutexUnknown = new("D6E22A02-35DA-11D1-9034-00A0C90349BE");

    public static readonly Guid BandwidthSharingExclusive = new("AF6060AA-5197-11D2-B6AF-00C04FD908E9");
    public static readonly Guid BandwidthSharingPartial = new("AF6060AB-5197-11D2-B6AF-00C04FD908E9");

    public static readonly Guid ExtensionTimecode = new("399595EC-8667-4E2D-8FDB-98814CE76C1E");
    public static readonly Guid ExtensionFileName = new("E165EC0E-19ED-45D7-B4A7-25CBD1E28E9B");
    public static readonly Guid ExtensionContentType = new("D590DC20-07BC-436C-9CF7-F3BBFBF1A4DC");
    public static readonly Guid ExtensionPixelAspectRatio = new("1B1EE554-F9EA-4BC8-821A-376B74E4C4B8");
    public static readonly Guid ExtensionSampleDuration = new("C6BD9450-867F-4907-83A3-C77921B733AD");
    public static readonly Guid ExtensionEncryptionSampleId = new("6698B84E-0AFA-4330-AEB2-1C0A98D7A44D");

    private static readonly Dictionary<Guid, string> ObjectNames = new()
    {
        [Header] = "Header Object",
        [Data] = "Data Object",
        [SimpleIndex] = "Simple Index Object",
        [Index] = "Index Object",
        [MediaObjectIndex] = "Media Object Index Object",
        [TimecodeIndex] = "Timecode Index Object",
        [FileProperties] = "File Properties Object",
        [StreamProperties] = "Stream Properties Object",
        [HeaderExtension] = "Header Extension Object",
        [CodecList] = "Codec List Object",
        [ScriptCommand] = "Script Command Object",
        [Marker] = "Marker Object",
        [BitrateMutualExclusion] = "Bitrate Mutual Exclusion Object",
        [ErrorCorrection] = "Error Correction Object",
        [ContentDescription] = "Content Description Object",
        [ExtendedContentDescription] = "Extended Content Description Object",
        [ContentBranding] = "Content Branding Object",
        [StreamBitrateProperties] = "Stream Bitrate Properties Object",
        [ContentEncryption] = "Content Encryption Object",
        [ExtendedContentEncryption] = "Extended Content Encryption Object",
        [DigitalSignature] = "Digital Signature Object",
        [Padding] = "Padding Object",
        [Compatibility] = "Compatibility Object",
        [IndexPlaceholder] = "Index Placeholder Object",
        [ExtendedStreamProperties] = "Extended Stream Properties Object",
        [AdvancedMutualExclusion] = "Advanced Mutual Exclusion Object",
        [GroupMutualExclusion] = "Group Mutual Exclusion Object",
        [StreamPrioritization] = "Stream Prioritization Object",
        [BandwidthSharing] = "Bandwidth Sharing Object",
        [LanguageList] = "Language List Object",
        [Metadata] = "Metadata Object",
        [MetadataLibrary] = "Metadata Library Object",
        [IndexParameters] = "Index Parameters Object",
        [MediaObjectIndexParameters] = "Media Object Index Parameters Object",
        [TimecodeIndexParameters] = "Timecode Index Parameters Object",
        [AdvancedContentEncryption] = "Advanced Content Encryption Object",
    };

    private static readonly Dictionary<Guid, string> StreamTypeNames = new()
    {
        [AudioMedia] = "Audio",
        [VideoMedia] = "Video",
        [CommandMedia] = "Command",
        [JfifMedia] = "JFIF image",
        [DegradableJpegMedia] = "Degradable JPEG image",
        [FileTransferMedia] = "File transfer",
        [BinaryMedia] = "Binary",
    };

    private static readonly Dictionary<Guid, string> ExclusionNames = new()
    {
        [MutexLanguage] = "language",
        [MutexBitrate] = "bitrate",
        [MutexUnknown] = "unknown",
    };

    private static readonly Dictionary<Guid, string> ExtensionNames = new()
    {
        [ExtensionTimecode] = "Timecode",
        [ExtensionFileName] = "File Name",
        [ExtensionContentType] = "Content Type",
        [ExtensionPixelAspectRatio] = "Pixel Aspect Ratio",
        [ExtensionSampleDuration] = "Sample Duration",
        [ExtensionEncryptionSampleId] = "Encryption Sample ID",
    };

    // The spec's text form: upper case hexadecimal in the 8-4-4-4-12 groups.
    public static string Text(Guid guid) => guid.ToString("D").ToUpperInvariant();

    public static string? ObjectName(Guid guid) => ObjectNames.GetValueOrDefault(guid);

    public static string? StreamTypeName(Guid guid) => StreamTypeNames.GetValueOrDefault(guid);

    public static string? ExclusionName(Guid guid) => ExclusionNames.GetValueOrDefault(guid);

    public static string? ExtensionName(Guid guid) => ExtensionNames.GetValueOrDefault(guid);

    // "Name (GUID)" for a known GUID, else the GUID alone.
    public static string Describe(Guid guid, string? name) => name is null ? Text(guid) : $"{name} ({Text(guid)})";
}
