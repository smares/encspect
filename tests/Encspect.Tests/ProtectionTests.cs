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
    [InlineData("mp4/cenc-aac.m4a")]
    [InlineData("mp4/cbcs-constant-iv.m4a")]
    [InlineData("mp4/cens-pattern.m4a")]
    [InlineData("mp4/cbc1-iv16.m4a")]
    [InlineData("mp4/cenc-ac3.m4a")]
    [InlineData("mp4/cenc-eac3.m4a")]
    [InlineData("mp4/cenc-dts.m4a")]
    [InlineData("mp4/cenc-mpeg-audio.m4a")]
    [InlineData("mp4/cenc-sample-group.m4a")]
    [InlineData("mp4/cenc-fragmented.mp4")]
    [InlineData("mp4/fairplay-drms.m4a")]
    [InlineData("mp4/audible-aavd.m4a")]
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
        Assert.Equal("encrypted audio", stream.Codec);
    }

    // The purchase's account, date and owner are tags of the file whatever protects the track.
    [Theory]
    [InlineData("mp4/itunes-purchase-clear.m4a", false)]
    [InlineData("mp4/fairplay-drms.m4a", true)]
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
    [InlineData("asf/protected-drm1.asf", "Microsoft Digital Rights Management version 1")]
    [InlineData("asf/protected-extended.asf", "Windows Media Rights Manager 7")]
    [InlineData("asf/protected-flag-only.asf", "encrypted content flag")]
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

    private static IEnumerable<Node> Descendants(Node node)
    {
        yield return node;
        foreach (Node child in node.Children.SelectMany(Descendants))
        {
            yield return child;
        }
    }
}
