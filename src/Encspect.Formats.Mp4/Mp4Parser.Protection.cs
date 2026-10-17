using Encspect.Core;

namespace Encspect.Formats.Mp4;

// Protected tracks: the sample entries enca and encv (ISO/IEC 14496-12 8.12.1) whose sinf box names the
// original format (frma, 8.12.2), the scheme (schm, 8.12.5) and the scheme's data (schi, 8.12.6), which
// for Common Encryption (ISO/IEC 23001-7) is the track encryption box tenc; the pssh boxes that name the
// DRM systems; senc, saiz and saio, which carry the samples' initialization vectors; and the entries of
// iTunes protection (drms, drmi) and Audible (aavd, with an adrm box), whose boxes are read as far as a
// source describes them. Nothing is decrypted. A track whose samples are encrypted is described by its
// sample entry and its sample tables alone; ciphertext is never handed to a codec parser.
public sealed partial class Mp4Parser
{
    // The bytes FFmpeg's mov_read_adrm takes from an adrm box: 8, a 56-byte blob, 4 and a 20-byte checksum.
    private const int AudibleDrmLength = 88;

    // The extension boxes of Microsoft's PIFF, which GPAC names (gf_isom_solve_uuid_box): the sample encryption and the track encryption box.
    private static readonly Guid PiffSampleEncryption = new("A2394F52-5A9B-4F14-A244-6C427C648DF4");

    private static readonly Guid PiffTrackEncryption = new("8974DBCE-7BE7-4C51-84F9-7148F9882554");


    // The default ISO/IEC 23001-7 gives for a track whose samples are protected: an initialization
    // vector per sample of 0 (a constant one in tenc), 8 or 16 bytes.
    private static bool IsInitializationVectorSize(int size) => size is 0 or 8 or 16;

    // What a scheme the registry list does not name is called; it reads as a sentence's own words when it stands inside one (InSentence).
    private const string UnnamedScheme = "Protection scheme";

    private static string InSentence(string scheme) => scheme.StartsWith(UnnamedScheme, StringComparison.Ordinal) ? char.ToLowerInvariant(scheme[0]) + scheme[1..] : scheme;

    private static bool ProtectsByCommonEncryption(string? scheme) => scheme is "cenc" or "cens" or "cbc1" or "cbcs";

    // The registry's description of a protection scheme (the MP4 registration authority's schemes list, type
    // "protection scheme"), in the project's wording for Common Encryption.
    private static string SchemeName(string scheme) => scheme switch
    {
        "cenc" => "Common Encryption, AES-CTR (cenc)",
        "cens" => "Common Encryption, AES-CTR with pattern (cens)",
        "cbc1" => "Common Encryption, AES-CBC (cbc1)",
        "cbcs" => "Common Encryption, AES-CBC with pattern (cbcs)",
        "cvar" => "AES-CTR sample variant (cvar)",
        "cva2" => "AES-CTR sample variant (cva2)",
        "cva1" => "AES-CBC-128 sample variant (cva1)",
        "sve1" => "AES-CTR content sensitive encryption (sve1)",
        "odkm" => "OMA DRM KMS (odkm)",
        "cpcm" => "CPCM protected content (cpcm)",
        _ => $"{UnnamedScheme} {scheme}",
    };

    private static bool IsProtectedEntry(string format) => format is "enca" or "encv" or "encs" or "enct" or "encm" or "encf" or "encu" or "encp" or "enc3" or "drms" or "drmi" or "aavd";

    // ISO/IEC 14496-12 8.12.2.
    private static void OriginalFormat(State state, Node node, BoxFields f)
    {
        node.Value = f.FourCc(node, "Format", Explain.OriginalFormat);
        if (state.InSinf && state.CurrentEntry is { Protection: { } protection })
        {
            protection.OriginalFormat ??= node.Value.ToString();
            protection.OriginalFormats.Add(node.Value.ToString()!);
        }
    }

    // 8.12.5: scheme_type, scheme_version and, with flag 1, a URI.
    private static void SchemeType(State state, Node node, BoxFields f)
    {
        uint flags = FullBox(node, f).Flags;
        string type = f.FourCc(node, "Scheme Type", Explain.SchemeType);
        f.U32(node, "Scheme Version", Explain.SchemeVersion);
        if ((flags & 1) != 0 && f.Remaining > 0)
        {
            f.CString(node, "Scheme URI", Explain.SchemeUri);
        }

        string named = ProtectsByCommonEncryption(type) ? "Common Encryption" : SchemeName(type);
        node.Value = named.StartsWith(UnnamedScheme, StringComparison.Ordinal) ? type : $"{type} ({named})";
        if (!state.InSinf || state.CurrentEntry is not { Protection: not null })
        {
            MarkProtectionScheme(state);
        }

        if (state.InSinf && state.CurrentEntry is { Protection: { } protection })
        {
            protection.SchemeType ??= type;
            protection.SchemeTypes.Add(type);
            protection.SchemeNode ??= node;
        }
    }

    // ISO/IEC 23001-7: the track's default encryption parameters. Version 1 adds the pattern of
    // encrypted and skipped blocks (cens and cbcs).
    private static void TrackEncryption(State state, Node node, BoxFields f)
    {
        int version = FullBox(node, f).Version;
        f.U8(node, "Reserved", Explain.Reserved);
        if (version >= 1)
        {
            long at = f.Position;
            int pattern = (int)f.Take(1);
            node.Add(Node.FromBits("Default Crypt Byte Block", at, 0, 4, NodeValue.Integer(pattern >> 4, "blocks", "block"), description: Explain.CryptByteBlock));
            node.Add(Node.FromBits("Default Skip Byte Block", at, 4, 8, NodeValue.Integer(pattern & 0xF, "blocks", "block"), description: Explain.SkipByteBlock));
        }
        else
        {
            f.U8(node, "Reserved", Explain.Reserved);
        }

        int isProtected = f.U8(node, "Default IsProtected", Explain.DefaultIsProtected);
        node.Children[^1].Value = isProtected <= 1 ? NodeValue.Boolean(isProtected == 1) : NodeValue.Integer(isProtected);
        int ivSize = f.U8(node, "Default Per Sample IV Size", Explain.DefaultIvSize, unit: "bytes");
        long kidAt = f.Position;
        byte[] kid = f.TakeBytes(16);
        node.Add(new Node("Default KID", kidAt, 16, ProtectionSystems.Text(ProtectionSystems.FromBytes(kid))) { Description = Explain.DefaultKid });
        int? constantSize = null;
        byte[]? constant = null;
        if (isProtected != 0 && ivSize == 0 && f.Remaining > 0)
        {
            constantSize = f.U8(node, "Default Constant IV Size", Explain.ConstantIvSize, unit: "bytes");
            long ivAt = f.Position;
            constant = f.TakeBytes(Math.Min(constantSize.Value, f.Remaining));
            node.Add(new Node("Default Constant IV", ivAt, constant.Length, Convert.ToHexString(constant).ToLowerInvariant()) { Description = Explain.ConstantIv });
        }

        node.Value = isProtected != 0 ? "protected by default" : "not protected by default";
        if (isProtected != 0)
        {
            MarkProtecting(state);
        }

        if (constant is not null && constantSize is { } declared && constant.Length < declared)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.tenc-constant-iv", $"The tenc box at 0x{node.Offset:X} declares a constant initialization vector of {declared} bytes, but holds {constant.Length}.", node);
        }

        if (!IsInitializationVectorSize(ivSize))
        {
            state.Findings.AddNow(Severity.Warning, "mp4.tenc-iv-size", $"The tenc box at 0x{node.Offset:X} gives a per-sample initialization vector size of {ivSize} bytes; 0, 8 and 16 are valid (ISO/IEC 23001-7).", node);
        }
        else if (isProtected != 0 && ivSize == 0 && constantSize is not (8 or 16))
        {
            state.Findings.AddNow(Severity.Warning, "mp4.tenc-constant-iv",
                constantSize is null
                    ? $"The tenc box at 0x{node.Offset:X} gives no initialization vector per sample and holds no constant one, which a protected track needs (ISO/IEC 23001-7)."
                    : $"The tenc box at 0x{node.Offset:X} gives a constant initialization vector of {constantSize} bytes; 8 and 16 are valid (ISO/IEC 23001-7).", node);
        }

        if (state.InSinf && state.CurrentEntry is { Protection: { } protection })
        {
            protection.Encryption ??= new TrackEncryptionInfo(isProtected, node);
            protection.TrackEncryptions.Add((isProtected, ivSize));
        }
    }

    // ISO/IEC 23001-7: a DRM system's own data for getting the keys, named by its system ID.
    private static void ProtectionSystemSpecificHeader(State state, Node node, BoxFields f)
    {
        int version = FullBox(node, f).Version;
        long idAt = f.Position;
        Guid id = ProtectionSystems.FromBytes(f.TakeBytes(16));
        string name = ProtectionSystems.Describe(id);
        node.Add(new Node("System ID", idAt, 16, ProtectionSystems.Name(id) is { } known ? $"{ProtectionSystems.Text(id)} ({known})" : ProtectionSystems.Text(id))
        {
            Description = Explain.SystemId,
            Topic = Topics.CommonEncryption,
        });
        node.Value = name;
        if (version > 1)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.pssh-version", $"The pssh box at 0x{node.Offset:X} has version {version}; ISO/IEC 23001-7 defines 0 and 1, so its fields are not read further.", node);
        }
        else
        {
            if (version == 1)
            {
                uint count = f.U32(node, "KID Count", Explain.PsshKidCount);
                long room = f.Remaining / 16;
                if (count > room)
                {
                    state.Findings.AddNow(Severity.Warning, "mp4.pssh-kids-exceed-box", $"The pssh box at 0x{node.Offset:X} declares {count} key IDs, but holds room for {room}.", node);
                }

                long kidsStart = f.Position;
                long present = Math.Min(count, room);
                for (long i = 0; i < Math.Min(present, MaxListedEntries); i++)
                {
                    long at = f.Position;
                    node.Add(new Node("KID", at, 16, ProtectionSystems.Text(ProtectionSystems.FromBytes(f.TakeBytes(16)))) { Description = Explain.PsshKid });
                }

                MoreEntries(node, (uint)present, kidsStart + (MaxListedEntries * 16L), kidsStart + (present * 16));
                f.Skip(Math.Max(0, present - MaxListedEntries) * 16);
            }

            uint size = f.U32(node, "Data Size", Explain.PsshDataSize, unit: "bytes");
            if (size > f.Remaining)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.pssh-data-exceeds-box", $"The pssh box at 0x{node.Offset:X} declares {size} bytes of data, but holds {f.Remaining}.", node);
            }

            long data = Math.Min(size, f.Remaining);
            if (data > 0)
            {
                f.Bytes(node, "Data", data, Explain.PsshData);
            }

            if (f.Remaining > 0)
            {
                f.Bytes(node, "Extra Data", f.Remaining, Explain.UnreadData);
            }
        }

        if (state.SystemSet.Add(name))
        {
            state.Systems.Add(name);
        }
    }

    // ISO/IEC 23001-7: the samples' initialization vectors and subsample ranges. Their layout depends on
    // the track's per-sample IV size, which a track fragment's sample entry gives, so only the count is read.
    private static void SampleEncryption(State state, Node node, BoxFields f)
    {
        // A sample encryption box that is too short for its fields still says that samples are encrypted.
        MarkProtecting(state);
        uint flags = FullBox(node, f).Flags;
        node.Add(Node.FromBits("UsesSubsampleEncryption", node.Children[^1].Offset + 2, 6, 7, NodeValue.Boolean((flags & 2) != 0), description: Explain.UsesSubsampleEncryption));
        uint count = f.U32(node, "Sample Count", Explain.SencSampleCount);
        node.Value = NodeValue.Integer(count, "samples", "sample");
        if (f.Remaining > 0)
        {
            f.Bytes(node, "Sample Entries", f.Remaining, Explain.SencEntries);
        }
    }

    // The type of auxiliary information of a saiz or saio box (8.7.8, 8.7.9), which for a Common Encryption scheme is its scheme type; without one the
    // information is the scheme's. Auxiliary information of an encryption type says that samples are protected.
    private static void MarkIfEncryptionInformation(State state, Node node, BoxFields f, uint flags)
    {
        string? type = null;
        if ((flags & 1) != 0)
        {
            try
            {
                type = f.FourCc(node, "Auxiliary Information Type", Explain.AuxInfoType);
            }
            catch (EndOfStreamException)
            {
                // A type that cannot be read may be an encryption type.
                MarkProtecting(state);
                throw;
            }
        }

        if (type is null || ProtectsByCommonEncryption(type))
        {
            MarkProtecting(state);
        }

        if ((flags & 1) != 0)
        {
            f.U32(node, "Auxiliary Information Type Parameter", Explain.AuxInfoTypeParameter);
        }
    }

    // A saiz or saio box whose version and flags cannot be read has no type to say its information is not encryption data.
    private static (int Version, uint Flags) FullBoxOrMarkProtecting(State state, Node node, BoxFields f)
    {
        try
        {
            return FullBox(node, f);
        }
        catch (EndOfStreamException)
        {
            MarkProtecting(state);
            throw;
        }
    }

    // ISO/IEC 14496-12 8.7.8: the size of each sample's auxiliary information, here the encryption data of senc's layout.
    private static void SampleAuxiliaryInformationSizes(State state, Node node, BoxFields f)
    {
        uint flags = FullBoxOrMarkProtecting(state, node, f).Flags;
        MarkIfEncryptionInformation(state, node, f, flags);

        int fixedSize = f.U8(node, "Default Sample Info Size", Explain.DefaultSampleInfoSize, unit: "bytes");
        uint count = f.U32(node, "Sample Count", Explain.SaizSampleCount);
        node.Value = NodeValue.Integer(count, "samples", "sample");
        if (fixedSize == 0)
        {
            if (count > f.Remaining)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.entries-exceed-box", $"The saiz box at 0x{node.Offset:X} declares {count} samples, but holds room for {f.Remaining} sizes.", node);
            }

            if (f.Remaining > 0)
            {
                f.Bytes(node, "Sample Info Sizes", Math.Min(f.Remaining, count), Explain.SaizSizes);
            }
        }
    }

    // 8.7.9: where each chunk's or run's auxiliary information lies in the file.
    private static void SampleAuxiliaryInformationOffsets(State state, Node node, BoxFields f)
    {
        (int version, uint flags) = FullBoxOrMarkProtecting(state, node, f);
        MarkIfEncryptionInformation(state, node, f, flags);

        uint count = f.U32(node, "Entries", Explain.SaioEntries);
        node.Value = NodeValue.Integer(count, "entries", "entry");
        int width = version == 1 ? 8 : 4;
        long room = f.Remaining / width;
        if (count > room)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.entries-exceed-box", $"The saio box at 0x{node.Offset:X} declares {count} offsets, but holds room for {room}.", node);
        }

        long start = f.Position;
        for (long i = 0; i < Math.Min(count, Math.Min(room, MaxListedEntries)); i++)
        {
            f.Field(node, "Offset", width, Explain.SaioOffset, v => NodeValue.Hex(v, width * 2));
        }

        MoreEntries(node, (uint)Math.Min(count, room), start + (MaxListedEntries * (long)width), start + (Math.Min(count, room) * width));
        f.Skip(Math.Max(0, Math.Min(count, room) - MaxListedEntries) * width);
    }

    // ISO/IEC 23001-7: a sample group entry that overrides the track's encryption defaults for some samples.
    private static void SampleEncryptionGroupEntry(State state, Node entry, BoxFields f, long length)
    {
        if (length < 20)
        {
            // An entry that cannot be read is taken to protect.
            MarkProtecting(state);
            f.Bytes(entry, "Entry", Math.Min(length, f.Remaining), Explain.GroupDescription);
            return;
        }

        long end = f.Position + length;
        f.U8(entry, "Reserved", Explain.Reserved);
        long at = f.Position;
        int pattern = (int)f.Take(1);
        entry.Add(Node.FromBits("Crypt Byte Block", at, 0, 4, NodeValue.Integer(pattern >> 4, "blocks", "block"), description: Explain.CryptByteBlock));
        entry.Add(Node.FromBits("Skip Byte Block", at, 4, 8, NodeValue.Integer(pattern & 0xF, "blocks", "block"), description: Explain.SkipByteBlock));
        int isProtected = f.U8(entry, "IsProtected", Explain.GroupIsProtected);
        entry.Children[^1].Value = isProtected <= 1 ? NodeValue.Boolean(isProtected == 1) : NodeValue.Integer(isProtected);
        entry.Value = isProtected != 0 ? "protected" : "not protected";
        int ivSize = f.U8(entry, "Per Sample IV Size", Explain.DefaultIvSize, unit: "bytes");
        long kidAt = f.Position;
        entry.Add(new Node("KID", kidAt, 16, ProtectionSystems.Text(ProtectionSystems.FromBytes(f.TakeBytes(16)))) { Description = Explain.GroupKid });
        if (isProtected != 0 && ivSize == 0 && f.Position < end)
        {
            int size = f.U8(entry, "Constant IV Size", Explain.ConstantIvSize, unit: "bytes");
            long ivAt = f.Position;
            byte[] iv = f.TakeBytes(Math.Min(size, Math.Min(f.Remaining, end - f.Position)));
            entry.Add(new Node("Constant IV", ivAt, iv.Length, Convert.ToHexString(iv).ToLowerInvariant()) { Description = Explain.ConstantIv });
        }

        if (isProtected != 0)
        {
            MarkProtecting(state);
        }

        if (f.Position < end)
        {
            f.Skip(Math.Min(end - f.Position, f.Remaining));
        }
    }

    // The size of a seig entry that states none: 20 bytes, and a constant IV with its size byte where the entry protects and has no IV per sample.
    private static long SeigEntryLength(BoxFields f)
    {
        var peek = new BoxFields(f.Reader, f.Position, f.End);
        if (peek.Remaining < 20)
        {
            return peek.Remaining;
        }

        peek.Skip(2);
        bool isProtected = peek.Take(1) != 0;
        bool hasNoVector = peek.Take(1) == 0;
        if (!isProtected || !hasNoVector)
        {
            return 20;
        }

        peek.Skip(16);
        return peek.Remaining > 0 ? Math.Min(21L + (long)peek.Take(1), f.Remaining) : 20;
    }

    // The boxes inside the schi of an iTunes protected entry (user, key, iVIV and priv), whose content a source names but does not describe:
    // VLC's libmp4.c lists them as the DRM atoms of a drms entry and skips them, and MediaInfo's File_Mpeg4 calls the entry's encryption "iTunes".
    private static bool ItunesProtectionBox(State state, Node node, BoxFields f)
    {
        if (f.Remaining > 0)
        {
            f.Bytes(node, "Data", f.Remaining, Explain.ItunesProtectionData);
        }

        return true;
    }

    // Audible's adrm box (FFmpeg's mov_read_adrm): after eight bytes a 56-byte blob that FFmpeg decrypts with a key
    // derived from the account's activation bytes, four more bytes, and 20 bytes that FFmpeg compares with a SHA-1 it computes.
    private static void AudibleDrm(State state, Node node, BoxFields f)
    {
        if (f.Remaining < AudibleDrmLength)
        {
            state.Findings.AddNow(Severity.Warning, "mp4.adrm-short", $"The adrm box at 0x{node.Offset:X} holds {f.Remaining} bytes, fewer than the {AudibleDrmLength} FFmpeg's reader takes from it; its fields are not read.", node);
            f.Bytes(node, "Data", f.Remaining, Explain.UnreadData);
            return;
        }

        f.Bytes(node, "Leading Bytes", 8, Explain.AdrmLeading);
        f.Bytes(node, "DRM Blob", 56, Explain.AdrmBlob);
        f.Bytes(node, "Skipped Bytes", 4, Explain.AdrmSkipped);
        f.Bytes(node, "File Checksum", 20, Explain.AdrmChecksum);
        if (f.Remaining > 0)
        {
            f.Bytes(node, "Data", f.Remaining, Explain.UnreadData);
        }
    }

    // What the sample entries say once every box is read, for the tracks that are protected.
    private static void CheckProtectedEntry(State state, SampleEntry entry)
    {
        if (!IsProtectedEntry(entry.Format))
        {
            return;
        }

        EntryProtection? protection = entry.Protection;
        if (entry.Format is "enca" or "encv" or "encs" or "enct" or "encm" or "encf" or "encu" or "encp" or "enc3")
        {
            if (protection is null)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.protected-entry-no-sinf", $"The {entry.Format} sample entry at 0x{entry.Node.Offset:X} has no sinf box, so the scheme and the original format are not stated.", entry.Node);
                return;
            }

            if (protection.OriginalFormat is null)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.protected-entry-no-frma", $"The sinf box of the {entry.Format} sample entry at 0x{entry.Node.Offset:X} has no frma box, so the original format is not stated (ISO/IEC 14496-12 8.12.1).", protection.SinfNode ?? entry.Node);
            }

            if (protection.SchemeType is null)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.protected-entry-no-schm", $"The sinf box of the {entry.Format} sample entry at 0x{entry.Node.Offset:X} has no schm box, so the scheme is not stated (ISO/IEC 14496-12 8.12.1).", protection.SinfNode ?? entry.Node);
            }
            else if (ProtectsByCommonEncryption(protection.SchemeType) && protection.Encryption is null)
            {
                state.Findings.AddNow(Severity.Warning, "mp4.protected-entry-no-tenc",
                    $"The {protection.SchemeType} scheme of the sample entry at 0x{entry.Node.Offset:X} has no tenc box, so its samples' default encryption is not stated (ISO/IEC 23001-7).", protection.SchemeNode ?? entry.Node);
            }
        }
    }

    // Whether the entry's key information says that no sample is protected: it is a Common Encryption entry whose sinf boxes agree on a known scheme,
    // name the original format and give track encryption defaults that all set the default to not protected. The entries of iTunes protection and Audible
    // never are, nor one without sinf, with a scheme the project does not know or without tenc: only the scheme can say a sample is not encrypted.
    private static bool IsClearByKeyInformation(SampleEntry entry) =>
        entry.Format is not ("drms" or "drmi" or "aavd") && entry.Protection is { } protection
        && protection.OriginalFormats.Count > 0 && protection.OriginalFormats.Distinct().Count() == 1
        && protection.SchemeTypes.Count > 0 && protection.SchemeTypes.Distinct().Count() == 1 && ProtectsByCommonEncryption(protection.SchemeTypes[0])
        && protection.TrackEncryptions.Count > 0 && protection.TrackEncryptions.Distinct().Count() == 1 && protection.TrackEncryptions[0].IsProtected == 0;

    // What a track's protection is, from the sample descriptions its samples use: the entry it is read through, whether any of them is protected,
    // and whether the samples are encrypted. A track counts as encrypted unless its key information says no sample is protected and nothing in the
    // file says otherwise: every protected entry in use is clear by its key information, no seig sample group description anywhere in the track or its
    // track fragments protects (or cannot be read), and there is no sample encryption box. The analyzer does not prove sample by sample that an
    // encrypted track is partly clear.
    private static (SampleEntry Entry, bool IsProtected, bool IsEncrypted) Decide(State state, Track track)
    {
        var indexes = new SortedSet<uint>();
        if (track.SampleToChunk is { } chunks)
        {
            // A run of chunks that the chunk offset box does not have is used by no sample.
            long chunkCount = track.ChunkOffsets?.Length ?? long.MaxValue;
            foreach ((uint firstChunk, uint samples, uint description) in chunks)
            {
                if (samples > 0 && firstChunk <= chunkCount)
                {
                    indexes.Add(description);
                }
            }
        }

        if (track.Id is { } id && state.FragmentDescriptions.TryGetValue(id, out HashSet<uint>? fragments))
        {
            indexes.UnionWith(fragments);
        }

        if (indexes.Count == 0)
        {
            indexes.Add(1);
        }

        List<SampleEntry> used = [.. indexes.Where(d => d >= 1 && d <= track.Entries.Count).Select(d => track.Entries[(int)d - 1])];
        // A description that points to no entry, or a track fragment that does not say whose it is, may be any entry.
        bool isUnresolved = indexes.Any(d => d < 1 || d > track.Entries.Count) || state.IsFragmentHeaderMissing;
        List<SampleEntry> protectedEntries = [.. used.Where(IsProtectedEntry)];
        if (isUnresolved)
        {
            protectedEntries = [.. protectedEntries.Concat(track.Entries.Where(e => IsProtectedEntry(e) && !protectedEntries.Contains(e)))];
        }

        if (protectedEntries.Count == 0)
        {
            // A scheme stated in the track outside its sample entries protects the track whatever its entries say.
            bool hasScheme = track.HasProtectionScheme || state.DoesEveryTrackHaveProtectionScheme;
            return (used.FirstOrDefault() ?? track.Entries[0], hasScheme, hasScheme);
        }

        SampleEntry chosen = protectedEntries.FirstOrDefault(e => !IsClearByKeyInformation(e)) ?? protectedEntries[0];
        bool isEncrypted = isUnresolved || protectedEntries.Any(e => !IsClearByKeyInformation(e)) || track.HasProtectingEvidence || state.DoesEveryTrackHaveProtectingEvidence
            || state.ProtectedTracks.Contains(track.Id ?? 0);
        return (chosen, true, isEncrypted);
    }

    // Something that says samples are protected, wherever it lies. In a track fragment it is the fragment's, which goes to its track once
    // the fragment is read (the innermost box counts, though a fragment may lie in a trak box); in a track, the track's; anywhere else
    // (the top level, moov, a skipped part of the file) it applies to every track.
    private static void MarkProtecting(State state)
    {
        if (state.Fragment is { } fragment)
        {
            fragment.HasProtectingEvidence = true;
        }
        else if (state.Track is { } track)
        {
            track.HasProtectingEvidence = true;
        }
        else
        {
            state.DoesEveryTrackHaveProtectingEvidence = true;
        }
    }

    // A sinf or schm box outside a sample entry's protection scheme information: the track it lies in, or every track, is protected by a scheme.
    private static void MarkProtectionScheme(State state)
    {
        MarkProtecting(state);
        if (state.Fragment is null)
        {
            if (state.Track is { } track)
            {
                track.HasProtectionScheme = true;
            }
            else
            {
                state.DoesEveryTrackHaveProtectionScheme = true;
            }
        }
    }

    // Whether the entry is protected: its format says so, or it carries a sinf box whatever its format.
    private static bool IsProtectedEntry(SampleEntry entry) => IsProtectedEntry(entry.Format) || entry.Protection is not null;

    // Once a track fragment is read: what it says about protection goes to its track, or to every track where the fragment does not name one.
    private static void ResolveFragment(State state, FragmentTrack fragment)
    {
        if (!fragment.HasHeader)
        {
            state.IsFragmentHeaderMissing = true;
        }

        if (!fragment.HasProtectingEvidence)
        {
            return;
        }

        // Evidence belongs to a track only where the fragment names exactly one existing track.
        if (fragment.HeaderCount == 1 && !fragment.IsInTrack && !fragment.IsMisplaced && state.TrackIdCounts.GetValueOrDefault(fragment.TrackId) == 1)
        {
            state.ProtectedTracks.Add(fragment.TrackId);
        }
        else
        {
            state.DoesEveryTrackHaveProtectingEvidence = true;
        }
    }

    // The scheme as the stream reports it.
    private static string SchemeOf(SampleEntry entry) => entry.Format switch
    {
        "drms" or "drmi" => "iTunes protection (" + entry.Format + ")",
        "aavd" => "Audible (aavd)",
        _ => entry.Protection?.SchemeType is { } type ? SchemeName(type) : $"{UnnamedScheme} not stated",
    };
}
