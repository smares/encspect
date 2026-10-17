using System.Text.Json;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Protection on streams: the typed field in the report, its JSON, CSV and summary, and what the MP4 and ASF parsers read of the protection
// objects. The fixtures' manifests (Mp4FixtureTests, AsfFixtureTests) hold the per-fixture figures; these tests hold the rules.
public class ProtectionTests
{
    private const string Cenc = "Common Encryption, AES-CTR (cenc)";

    [Fact]
    public void ProtectedStreamCarriesSchemeEncryptionAndSystems()
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg("mp4/cenc-aac.m4a").Streams);

        Assert.True(stream.IsProtected);
        Assert.Equal(Cenc, stream.Protection!.Scheme);
        Assert.True(stream.Protection.IsEncrypted);
        Assert.Equal(["Widevine Content Protection", "Microsoft PlayReady", "01234567-89ab-cdef-0123-456789abcdef"], stream.Protection.Systems);
    }

    [Fact]
    public void ClearFilesHaveNoProtection()
    {
        Assert.All(Fixtures.AnalyzeMpeg("mp4/lc-implicit.m4a").Streams, s => Assert.False(s.IsProtected));
        Assert.All(Fixtures.AnalyzeMpeg("mp4/itunes-purchase-clear.m4a").Streams, s => Assert.Null(s.Protection));
    }

    [Fact]
    public void JsonWritesProtectedStreamsOnly()
    {
        using JsonDocument protectedJson = JsonDocument.Parse(Fixtures.ToJson(Fixtures.AnalyzeMpeg("mp4/cenc-aac.m4a")));
        JsonElement stream = protectedJson.RootElement[0].GetProperty("streams")[0];
        Assert.True(stream.GetProperty("isProtected").GetBoolean());
        JsonElement protection = stream.GetProperty("protection");
        Assert.Equal(Cenc, protection.GetProperty("scheme").GetString());
        Assert.True(protection.GetProperty("isEncrypted").GetBoolean());
        Assert.Equal(3, protection.GetProperty("systems").GetArrayLength());

        using JsonDocument clearJson = JsonDocument.Parse(Fixtures.ToJson(Fixtures.AnalyzeMpeg("mp4/itunes-purchase-clear.m4a")));
        JsonElement clear = clearJson.RootElement[0].GetProperty("streams")[0];
        Assert.False(clear.TryGetProperty("isProtected", out _));
        Assert.False(clear.TryGetProperty("protection", out _));
    }

    [Fact]
    public void CsvAndSummaryTellWhetherAnyStreamIsProtected()
    {
        var writer = new StringWriter();
        ReportCsvWriter.Write([Fixtures.AnalyzeMpeg("mp4/cenc-aac.m4a"), Fixtures.AnalyzeMpeg("mp4/itunes-purchase-clear.m4a"), Fixtures.AnalyzeMpeg("mp4/moov-missing.m4a")], writer);

        string[] lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",Is Protected,Protection Scheme,DRM Systems", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(",false,,true,\"Common Encryption, AES-CTR (cenc)\",Widevine Content Protection; Microsoft PlayReady; 01234567-89ab-cdef-0123-456789abcdef", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",false,,false,,", lines[2], StringComparison.Ordinal);

        // A file without a stream says nothing about protection.
        Assert.EndsWith(",,,,,", lines[3], StringComparison.Ordinal);

        ReportSummary summary = ReportSummary.From(Fixtures.AnalyzeMpeg("mp4/cenc-aac.m4a"));
        Assert.True(summary.IsProtected);
        Assert.Equal([Cenc], summary.ProtectionSchemes);
        Assert.Equal(3, summary.DrmSystems.Count);
    }

    // Ciphertext is no valid coding: an encrypted track's samples are never read as frames, so the codec parsers raise nothing about them.
    [Theory]
    [InlineData("mp4/audible-aavd.m4a")]
    [InlineData("mp4/cbc1-iv16.m4a")]
    [InlineData("mp4/cbcs-constant-iv.m4a")]
    [InlineData("mp4/cenc-aac.m4a")]
    [InlineData("mp4/cenc-ac3.m4a")]
    [InlineData("mp4/cenc-decoy-group-in-edts.m4a")]
    [InlineData("mp4/cenc-decoy-group-in-entry.m4a")]
    [InlineData("mp4/cenc-dts.m4a")]
    [InlineData("mp4/cenc-eac3.m4a")]
    [InlineData("mp4/cenc-entry-out-of-range.m4a")]
    [InlineData("mp4/cenc-fragmented-fragment-default.mp4")]
    [InlineData("mp4/cenc-fragmented-fragment-group-v0.mp4")]
    [InlineData("mp4/cenc-fragmented-fragment-group-v2.mp4")]
    [InlineData("mp4/cenc-fragmented-fragment-group.mp4")]
    [InlineData("mp4/cenc-fragmented-misplaced-group.mp4")]
    [InlineData("mp4/cenc-fragmented-moof-in-trak.mp4")]
    [InlineData("mp4/cenc-fragmented-no-tfhd.mp4")]
    [InlineData("mp4/cenc-fragmented-piff-senc.mp4")]
    [InlineData("mp4/cenc-fragmented-senc.mp4")]
    [InlineData("mp4/cenc-fragmented-table-default.mp4")]
    [InlineData("mp4/cenc-fragmented-table-group.mp4")]
    [InlineData("mp4/cenc-fragmented-traf-in-trak.mp4")]
    [InlineData("mp4/cenc-fragmented-traf-missing-track.mp4")]
    [InlineData("mp4/cenc-fragmented.mp4")]
    [InlineData("mp4/cenc-groups-clear-tenc-protects.m4a")]
    [InlineData("mp4/cenc-groups-in-edts.m4a")]
    [InlineData("mp4/cenc-groups-index-zero.m4a")]
    [InlineData("mp4/cenc-groups-two-sbgp.m4a")]
    [InlineData("mp4/cenc-mpeg-audio.m4a")]
    [InlineData("mp4/cenc-no-tenc.m4a")]
    [InlineData("mp4/cenc-sample-group-default.m4a")]
    [InlineData("mp4/cenc-sample-group.m4a")]
    [InlineData("mp4/cenc-sbgp-in-trak.m4a")]
    [InlineData("mp4/cenc-sbgp-in-udta.m4a")]
    [InlineData("mp4/cenc-second-entry.m4a")]
    [InlineData("mp4/cenc-senc-after-mdat.m4a")]
    [InlineData("mp4/cenc-senc-tenc-clear.m4a")]
    [InlineData("mp4/cenc-sgpd-unknown-version.m4a")]
    [InlineData("mp4/cenc-sgpd-v2-default-length.m4a")]
    [InlineData("mp4/cenc-sgpd-v2-entry-lengths.m4a")]
    [InlineData("mp4/cenc-sinf-in-mp4a-entry.m4a")]
    [InlineData("mp4/cenc-sinf-in-trak.m4a")]
    [InlineData("mp4/cenc-stray-schi-in-entry.m4a")]
    [InlineData("mp4/cenc-stray-schi-in-trak.m4a")]
    [InlineData("mp4/cenc-stray-sinf-in-trak.m4a")]
    [InlineData("mp4/cenc-stray-tenc-in-entry.m4a")]
    [InlineData("mp4/cenc-stray-tenc-in-stbl.m4a")]
    [InlineData("mp4/cenc-two-enca-entries.m4a")]
    [InlineData("mp4/cenc-two-sinf.m4a")]
    [InlineData("mp4/cenc-two-tenc.m4a")]
    [InlineData("mp4/cens-pattern.m4a")]
    [InlineData("mp4/itunes-drms.m4a")]
    [InlineData("mp4/piff-scheme.m4a")]
    [InlineData("mp4/piff-senc-in-stbl.m4a")]
    [InlineData("mp4/piff-tenc-beside-cenc.m4a")]
    public void EncryptedSamplesAreNotReadAsFrames(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.True(stream.Protection!.IsEncrypted);
        Assert.All(report.Findings, f => Assert.StartsWith("mp4.", f.Code, StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Code == "mp4.samples-encrypted");
        foreach (Node node in stream.Nodes)
        {
            Assert.DoesNotContain(Descendants(node), n => n.Kind == NodeKind.Frame);
            Assert.Equal("frames not read: encrypted", node.Value?.ToString());
        }
    }

    [Fact]
    public void ProtectedButClearSamplesAreReadLikeAnyOther()
    {
        Report report = Fixtures.AnalyzeMpeg("mp4/cenc-clear-default.m4a");

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.True(stream.IsProtected);
        Assert.False(stream.Protection!.IsEncrypted);
        Assert.Equal(20 * 1024, stream.Samples);
        Assert.Contains(stream.Nodes.SelectMany(Descendants), n => n.Kind == NodeKind.Frame);
        Assert.Contains(report.Findings, f => f.Code == "mp4.protected-samples-clear");
    }

    // The encryption of a track whose coding has no parser is still a stream, named by its sample entry.
    [Fact]
    public void EncryptedTrackOfAnUnparsedCodingIsStillAStream()
    {
        Report report = Fixtures.AnalyzeMpeg("mp4/enca-no-sinf.m4a");

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.True(stream.Protection!.IsEncrypted);
        Assert.Equal("Encrypted audio", stream.Codec);
    }

    // The purchase's account, date and owner are tags of the file whatever protects the track.
    [Theory]
    [InlineData("mp4/itunes-purchase-clear.m4a", false)]
    [InlineData("mp4/itunes-drms.m4a", true)]
    public void PurchaserItemsAreMetadata(string name, bool isProtected)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        Node tag = Descendants(report.Root).Single(n => n.Kind == NodeKind.Tag && n.Name == "iTunes Metadata");
        foreach (string item in new[] { "Account", "Purchase Date", "Owner" })
        {
            Node node = Assert.Single(tag.Children, c => c.Name == item);
            Assert.Equal(NodeKind.Detail, node.Kind);
        }

        Assert.Equal(isProtected, Assert.Single(report.Streams).IsProtected);
        Assert.Contains("iTunes Metadata", ReportSummary.From(report).Tags);
        Assert.DoesNotContain(report.Findings, f => f.Message.Contains("buyer@example.invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRegistryNamesTheSystemsOfItsTable()
    {
        Assert.Equal("Widevine Content Protection", ProtectionSystems.Name(new Guid("edef8ba9-79d6-4ace-a3c8-27dcd51d21ed")));
        Assert.Equal("Microsoft PlayReady", ProtectionSystems.Name(new Guid("9a04f079-9840-4286-ab92-e65be0885f95")));
        Assert.Null(ProtectionSystems.Name(Guid.Empty));
        byte[] widevine = Convert.FromHexString("edef8ba979d64acea3c827dcd51d21ed");
        Assert.Equal("Widevine Content Protection", ProtectionSystems.Name(ProtectionSystems.FromBytes(widevine)));
        Assert.Equal("00000000-0000-0000-0000-000000000000", ProtectionSystems.Describe(Guid.Empty));
    }

    // A Digital Signature Object protects nothing: a Windows Media Audio stream in a file with one and no encryption object is read as usual.
    [Fact]
    public void SignatureAloneProtectsNothing()
    {
        byte[] stream = AsfBuilder.StreamProperties(1, 0x0161, 2, 44100, 16000, 743, 16, new byte[10]);
        byte[] file = AsfBuilder.Assemble(stream, 200, [AsfBuilder.Packet(200, 1, 1, 0, 100, 0)]);
        byte[] signature = AsfBuilder.Object("2211B3FC-BD23-11D2-B4B7-00A0C955FC6E", [2, 0, 0, 0, 4, 0, 0, 0, 1, 2, 3, 4]);

        Report plain = Analyzer.CreateDefault().Analyze("plain.asf", new MemoryStream(file));
        Report signed = Analyzer.CreateDefault().Analyze("signed.asf", new MemoryStream(AsfBuilder.InHeader((byte[])file.Clone(), signature)));

        StreamInfo read = Assert.Single(plain.Streams);
        Assert.Equal(read.Codec, Assert.Single(signed.Streams).Codec);
        Assert.False(Assert.Single(signed.Streams).IsProtected);
        Assert.DoesNotContain(signed.Findings, f => f.Code is "asf.content-encrypted" or "asf.signature-type");
        Assert.Contains(Descendants(signed.Root), n => n.Name == "Digital Signature Object");
    }

    // The same stream with an Encrypted Content Flag is described by its header: no media object goes to the codec.
    [Theory]
    [InlineData("asf/protected-drm1.asf", "Microsoft Digital Rights Manager version 1")]
    [InlineData("asf/protected-extended.asf", "Windows Media Rights Manager 7")]
    [InlineData("asf/protected-flag-only.asf", "Protection scheme not stated (Encrypted Content Flag)")]
    public void EncryptedAsfStreamsAreDescribedFromTheirHeader(string name, string scheme)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(scheme, stream.Protection!.Scheme);
        Assert.True(stream.Protection.IsEncrypted);
        Assert.Equal("Windows Media Audio 2", stream.Codec);
        Assert.Equal(44100, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
        Assert.Equal("frames not read: encrypted", stream.Nodes[0].Value?.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("wma.", StringComparison.Ordinal));
    }

    // Each Windows Media Audio parser describes an encrypted stream from its header and reads no packet, so none of its frame findings can arise.
    [Theory]
    [InlineData("asf/protected-wma-pro.asf", "Windows Media Audio Professional", 44100, 2)]
    [InlineData("asf/protected-wma-lossless.asf", "Windows Media Audio Lossless", 44100, 2)]
    [InlineData("asf/protected-wma-voice.asf", "Windows Media Audio Voice", 8000, 1)]
    public void EncryptedProfessionalLosslessAndVoiceStreamsReadNoPackets(string name, string codec, int rate, int channels)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal((codec, rate, channels), (stream.Codec, stream.SampleRate, stream.Channels));
        Assert.True(stream.Protection!.IsEncrypted);
        Assert.Equal("frames not read: encrypted", stream.Nodes[0].Value?.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("wma.", StringComparison.Ordinal));
        Assert.DoesNotContain(stream.Nodes.SelectMany(Descendants), n => n.Kind == NodeKind.Frame);
        Assert.True(stream.Duration > 0);
    }

    // A Matroska track with a ContentEncryption element of a ContentEncoding of type 1 (RFC 9559 section 5.1.4.1.31.8) is a protected stream whose scheme names the
    // algorithm and, for AES, the cipher mode; its samples are encrypted when the scope covers the frames, and its frames are not routed. The stream is made for a
    // track that no block uses and for one where a compression the analyzer does not undo lies over the encryption.
    [Theory]
    [InlineData("matroska/encrypted.mka", "Matroska ContentEncryption, AES-CTR", true, "matroska.track-encrypted")]
    [InlineData("matroska/encrypted-cbc.mka", "Matroska ContentEncryption, AES-CBC", true, "matroska.track-encrypted")]
    [InlineData("matroska/encrypted-3des.mka", "Matroska ContentEncryption, 3DES", true, "matroska.track-encrypted")]
    [InlineData("matroska/encrypted-3des-aes-settings.mka", "Matroska ContentEncryption, 3DES", true, "matroska.aes-settings-not-aes")]
    [InlineData("matroska/encrypted-under-bzlib.mka", "Matroska ContentEncryption, AES-CTR", true, "matroska.track-encrypted")]
    [InlineData("matroska/encrypted-empty.mka", "Matroska ContentEncryption, AES-CTR", true, "matroska.track-empty")]
    [InlineData("matroska/encryption-missing.mka", "Matroska ContentEncryption", true, "matroska.encryption-missing")]
    [InlineData("matroska/encrypted-private.mka", "Matroska ContentEncryption, AES-CTR", false, "matroska.codec-private-encrypted")]
    [InlineData("matroska/encrypted-next-encoding.mka", "Matroska ContentEncryption, AES-CTR", true, "matroska.track-encrypted")]
    [InlineData("matroska/encrypted-over-algorithm-none.mka", "Matroska ContentEncryption, AES-CTR", true, "matroska.track-encrypted")]
    [InlineData("matroska/encrypted-over-3des.mka", "Matroska ContentEncryption, AES-CTR", true, "matroska.track-encrypted")]
    [InlineData("matroska/encrypted-chain.mka", "Matroska ContentEncryption", true, "matroska.track-encrypted")]
    [InlineData("matroska/compression-hides-encryption.mka", "Matroska ContentEncryption", true, "matroska.encoding-hidden")]
    [InlineData("matroska/encrypted-in-chain-middle.mka", "Matroska ContentEncryption", true, "matroska.encoding-hidden")]
    [InlineData("matroska/encrypted-aac-delay.mka", "Matroska ContentEncryption, AES-CTR", true, "matroska.track-encrypted")]
    [InlineData("matroska/encrypted-aac.mka", "Matroska ContentEncryption, AES-CTR", true, "matroska.track-encrypted")]
    [InlineData("matroska/encrypted-vorbis.mka", "Matroska ContentEncryption, AES-CTR", true, "matroska.track-encrypted")]
    public void MatroskaContentEncryptionIsProtection(string name, string scheme, bool isEncrypted, string code)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(scheme, stream.Protection!.Scheme);
        Assert.Equal(isEncrypted, stream.Protection.IsEncrypted);
        Assert.Empty(stream.Protection.Systems);
        Assert.Equal(StreamKind.Audio, stream.Kind);
        Assert.Contains(report.Findings, f => f.Code == code);
        Assert.DoesNotContain(report.Findings, f => f.Code == "matroska.track-encoding-not-read");
        Assert.All(report.Findings, f => Assert.True(f.Code.StartsWith("matroska.", StringComparison.Ordinal) || (f.Code == "vorbis.writer-claim-differs" && name.EndsWith("encrypted-vorbis.mka", StringComparison.Ordinal)), f.Code));
        Assert.DoesNotContain(stream.Nodes.SelectMany(Descendants), n => n.Kind == NodeKind.Frame);
    }

    // The samples, the length and the bitrate of an encrypted track equal the clear track's. Opus drops its pre-skip from the samples (the container's timing
    // less the pre-skip) and counts its bitrate over the undelayed packets; AAC ignores the CodecDelay, so a delay does not change an encrypted AAC track.
    [Theory]
    [InlineData("matroska/encrypted.mka", "matroska/encryption-algorithm-default.mka")]
    [InlineData("matroska/encrypted-aac-delay.mka", "matroska/aac-delay.mka")]
    public void MatroskaEncryptedTracksCountWhatTheClearTrackDoes(string encryptedName, string clearName)
    {
        StreamInfo encrypted = Assert.Single(Fixtures.AnalyzeMpeg(encryptedName).Streams);
        StreamInfo clear = Assert.Single(Fixtures.AnalyzeMpeg(clearName).Streams);

        Assert.True(clear.Samples > 0);
        Assert.Equal(clear.Samples, encrypted.Samples);
        Assert.Equal(clear.Duration, encrypted.Duration, 6);
        Assert.Equal(clear.AverageBitrate ?? 0, encrypted.AverageBitrate ?? 0, 0.05);
    }

    // Encodings that share a ContentEncodingOrder cannot be ordered: nothing is read, and the track is encrypted by an unknown algorithm if any of them is an
    // encryption.
    [Theory]
    [InlineData("matroska/same-order-encryption-and-stripping.mka")]
    [InlineData("matroska/same-order-aes-modes.mka")]
    [InlineData("matroska/same-order-over-encryption.mka")]
    public void MatroskaTiedEncryptionsAreEncryptedByAnUnknownAlgorithm(string name)
    {
        StreamInfo stream = Assert.Single(Fixtures.AnalyzeMpeg(name).Streams);

        Assert.Equal("Matroska ContentEncryption", stream.Protection!.Scheme);
        Assert.True(stream.Protection.IsEncrypted);
        Assert.DoesNotContain(stream.Nodes.SelectMany(Descendants), n => n.Kind == NodeKind.Frame);
    }

    // A CodecPrivate that is zlib-compressed or header-stripped is undone and handed to the AAC parser as its configuration, as the Opus and Vorbis parsers get theirs:
    // the stream is the clear track's, with the clear track's samples.
    [Theory]
    [InlineData("matroska/aac-private-zlib.mka")]
    [InlineData("matroska/aac-private-stripped.mka")]
    public void MatroskaUndoneCodecPrivateIsTheAacConfiguration(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("AAC LC", stream.Codec);
        Assert.Equal(6144, stream.Samples);
        Assert.Empty(report.Findings);
    }

    // A CodecPrivate that is encrypted or not undone is not handed over, and the clear frames are analyzed all the same, without a configuration.
    [Theory]
    [InlineData("matroska/aac-private-bzlib.mka", "matroska.codec-private-not-read", false)]
    [InlineData("matroska/aac-private-encrypted.mka", "matroska.codec-private-encrypted", true)]
    public void MatroskaUnreadCodecPrivateDoesNotBlockClearAacFrames(string name, string code, bool isProtected)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(isProtected, stream.IsProtected);
        Assert.True(stream.Duration > 0);
        Assert.Contains(report.Findings, f => f.Code == code);
        Assert.Contains(report.Findings, f => f.Code == "aac.no-configuration");
        Assert.DoesNotContain(report.Findings, f => f.Code == "matroska.track-encoding-not-read");
    }

    // A finding in the fields of an inflated CodecPrivate, which all cover the same file range, points at the field it is about: the identification header's Framing Bit,
    // not the comment header's, which has the same name and range.
    [Fact]
    public void MatroskaFindingInInflatedBytesPointsAtItsOwnField()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/vorbis-private-zlib-framing.mka");

        Finding finding = Assert.Single(report.Findings, f => f.Code == "vorbis.framing-missing");
        Node node = report.Root;
        Node parent = node;
        foreach (int index in finding.Path!)
        {
            parent = node;
            node = node.Children[index];
        }

        Assert.Equal("Framing Bit", node.Name);
        Assert.Equal("Identification Header", parent.Name);
    }

    // An encryption of the CodecPrivate on a track that has none covers nothing: no protection (and, for Opus, the missing CodecPrivate is the finding).
    [Fact]
    public void MatroskaEncryptionOfAMissingCodecPrivateIsNotProtection()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/encrypted-private-missing.mka");

        Assert.Null(Assert.Single(report.Streams).Protection);
        Assert.Contains(report.Findings, f => f.Code == "matroska.encryption-covers-nothing");
        Assert.Contains(report.Findings, f => f.Code == "matroska.codec-private-missing");
    }

    // Tied encodings give a clear and an encrypted track the same container timing less the CodecDelay (Opus, whose CodecPrivate is not trusted when the
    // encodings cannot be ordered, takes the CodecDelay and not the OpusHead's pre-skip): 80 ms of blocks, 6.5 ms of delay, the rest at 48 kHz.
    [Fact]
    public void MatroskaTiedTracksHaveTheContainersTiming()
    {
        StreamInfo clear = Assert.Single(Fixtures.AnalyzeMpeg("matroska/same-order-two-compressions.mka").Streams);
        StreamInfo encrypted = Assert.Single(Fixtures.AnalyzeMpeg("matroska/same-order-encryption-and-stripping.mka").Streams);

        Assert.Equal(0.0735, clear.Duration, 6);
        Assert.Equal(3528, clear.Samples);
        Assert.Equal(clear.Duration, encrypted.Duration, 6);
        Assert.Equal(clear.Samples, encrypted.Samples);
    }

    // A ContentEncryption under a compression is ignored even when the orders tie: only an encoding of type 1 makes the track encrypted.
    [Fact]
    public void MatroskaTiedStrayEncryptionIsNotProtection()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/same-order-stray-encryption.mka");

        Assert.Null(Assert.Single(report.Streams).Protection);
        Assert.DoesNotContain(report.Findings, f => f.Code is "matroska.encryption-ignored" or "matroska.encryption-none");
    }

    // Encodings that share a ContentEncodingOrder cannot be ordered: nothing is read, and with no encryption among them nothing is protected. (That the
    // result does not depend on the order of the elements is checked over every permutation by check_matroska_encodings.py.)
    [Fact]
    public void MatroskaEncodingsOfOneOrderAreNotTrusted()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/same-order-two-compressions.mka");

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Null(stream.Protection);
        Assert.Contains(report.Findings, f => f.Code == "matroska.track-encoding-not-read" && f.Message.Contains("cannot be ordered", StringComparison.Ordinal));
        Assert.DoesNotContain(stream.Nodes.SelectMany(Descendants), n => n.Kind == NodeKind.Frame);
    }

    // A compression whose scope hides the next encoding makes that encoding's settings unreadable: a hidden compression is not read, and the frames are not
    // handed to the codec parser.
    [Fact]
    public void MatroskaHiddenCompressionIsNotRead()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/compression-hides-compression.mka");

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Null(stream.Protection);
        Assert.Contains(report.Findings, f => f.Code == "matroska.track-encoding-not-read");
        Assert.DoesNotContain(stream.Nodes.SelectMany(Descendants), n => n.Kind == NodeKind.Frame);
    }

    // The finding and the stream's protection come from one reading of the ContentEncodings: both name the encryption that lies under a compression.
    [Fact]
    public void MatroskaFindingNamesWhatTheProtectionNames()
    {
        Report report = Fixtures.AnalyzeMpeg("matroska/encrypted-under-bzlib.mka");

        Finding finding = Assert.Single(report.Findings, f => f.Code.StartsWith("matroska.track-", StringComparison.Ordinal) && f.Code != "matroska.track-empty");
        Assert.Equal("matroska.track-encrypted", finding.Code);
        Assert.Contains("encrypted (AES-CTR) and compressed with bzlib", finding.Message, StringComparison.Ordinal);
    }

    // ContentEncAlgo 0 states that the data are not encrypted, and a ContentEncryption under a compression is ignored (RFC 9559 sections 5.1.4.1.31.8 and 5.1.4.1.31.9):
    // the frames are read as they are and the track is not protected.
    [Theory]
    [InlineData("matroska/encryption-algorithm-none.mka", "matroska.encryption-none")]
    [InlineData("matroska/encryption-algorithm-default.mka", "matroska.encryption-none")]
    [InlineData("matroska/encryption-covers-nothing.mka", "matroska.encryption-covers-nothing")]
    [InlineData("matroska/hidden-encryption-covers-nothing.mka", "matroska.encoding-hidden")]
    [InlineData("matroska/encryption-ignored.mka", "matroska.encryption-ignored")]
    public void MatroskaEncryptionThatEncryptsNothingIsNotProtection(string name, string code)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Null(stream.Protection);
        Assert.False(stream.IsProtected);
        Assert.Contains(report.Findings, f => f.Code == code);
        Assert.DoesNotContain(report.Findings, f => f.Code is "matroska.track-encrypted" or "matroska.track-encoding-not-read");
        Assert.Contains(stream.Nodes.SelectMany(Descendants), n => n.Kind == NodeKind.Frame);
    }

    // A protected stream no codec parser describes is still a stream, so that its protection is reported: MPEG audio, video, a clear track of an unparsed coding.
    [Theory]
    [InlineData("asf/protected-mpeg-audio.asf", "MPEG audio Layer III", true)]
    [InlineData("asf/protected-video-only.asf", "Video", true)]
    [InlineData("mp4/cenc-clear-unparsed.m4a", "Apple Lossless", false)]
    public void ProtectedStreamsOfUnparsedCodingsAreReported(string name, string codec, bool isEncrypted)
    {
        Report report = Fixtures.AnalyzeMpeg(name);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(codec, stream.Codec);
        Assert.Equal(isEncrypted, stream.Protection!.IsEncrypted);
        Assert.DoesNotContain(stream.Nodes.SelectMany(Descendants), n => n.Kind == NodeKind.Frame);
    }

    // A protected video stream is a stream of the report, but the summary describes the file by its first audio stream.
    [Fact]
    public void SummaryTakesCodecAndRateFromTheFirstAudioStream()
    {
        Report report = Fixtures.AnalyzeMpeg("asf/protected-audio-and-video.asf");
        Assert.Equal([StreamKind.Audio, StreamKind.Video], report.Streams.Select(s => s.Kind).Order());

        ReportSummary summary = ReportSummary.From(report);
        Assert.Equal("Windows Media Audio 2", summary.Codec);
        Assert.Equal(44100, summary.SampleRate);
        Assert.Equal(["Microsoft Digital Rights Manager version 1"], summary.ProtectionSchemes);
        Assert.True(summary.IsProtected);
        Assert.Equal(0.1, summary.Duration!.Value, 3);

        Report videoOnlyReport = Fixtures.AnalyzeMpeg("asf/protected-video-only.asf");
        ReportSummary videoOnly = ReportSummary.From(videoOnlyReport);
        Assert.True(videoOnly.IsProtected);
        Assert.Null(videoOnly.Codec);
        Assert.Null(videoOnly.SampleRate);
        Assert.Null(videoOnly.Duration);

        var json = new MemoryStream();
        ReportJsonWriter.Write([videoOnlyReport], json);
        string text = System.Text.Encoding.UTF8.GetString(json.ToArray());
        Assert.Contains("\"kind\": \"Video\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sampleRate\"", text, StringComparison.Ordinal);

        var csv = new StringWriter();
        ReportCsvWriter.Write([videoOnlyReport], csv);
        Assert.Contains(",Microsoft Digital Rights Manager version 1,", csv.ToString(), StringComparison.Ordinal);
    }

    // An Advanced Content Encryption Object protects the streams its records name: none when it holds no record, and every stream where the records were not
    // read in full.
    [Fact]
    public void AdvancedContentEncryptionProtectsTheStreamsItsRecordsName()
    {
        Report empty = Fixtures.AnalyzeMpeg("asf/protected-advanced-no-records.asf");
        Assert.All(empty.Streams, s => Assert.False(s.IsProtected));
        Assert.Contains(empty.Findings, f => f.Code == "asf.no-content-encryption-record" && f.Message.Contains("no content encryption record", StringComparison.Ordinal));

        Report many = Fixtures.AnalyzeMpeg("asf/protected-advanced-many-records.asf");
        Assert.Contains(many.Findings, f => f.Code == "asf.too-many-entries");
        Assert.All(many.Streams, st => Assert.True(st.IsProtected));
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        yield return node;
        foreach (Node child in node.Children.SelectMany(Descendants))
        {
            yield return child;
        }
    }
}
