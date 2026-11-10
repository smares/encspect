using Encspect.Core;
using Encspect.Tags.VorbisComment;

namespace Encspect.Tests;

// The Vorbis comment tag parser: read from bytes held whole, and the counting of its findings.
public class VorbisCommentTests
{
    [Fact]
    public void AVorbisCommentHeldWholeIsReadFromItsBytes()
    {
        byte[] comment = OggBuilder.VorbisComment("vendor", "TITLE=Chunk");
        var container = new ChunkedContainer(tags: new Dictionary<string, string> { ["VCMT"] = VorbisCommentTagParser.TagName });
        Report report = TestFiles.Analyze(ChunkedContainer.Build("RIFF", "TEST", ("VCMT", comment)), container);

        Node tag = report.Root.Children.Single().Children.Single().Children.Single(c => c.Kind == NodeKind.Tag);
        Assert.Equal("Chunk", tag.Children.Single(c => c.Name == "TITLE").Value?.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);
    }

    // The sentence that says how many comments fit the data, and the findings that count them once.
    [Fact]
    public void VorbisCommentFindingsAreCountedAndEvidenceSaysOneComment()
    {
        TagProbe one = ParseComment(OggBuilder.VorbisComment("vendor", "TITLE=One"));
        Assert.Equal("The vendor string and 1 comment fit the data", Assert.Single(one.Evidence).Description);

        // Past 1,024 comments the nodes are built when read: the same nodes, with the findings counted once.
        string[] comments = [.. Enumerable.Range(0, 3000).Select(i => i % 3 == 0 ? $"TITLE=Song {i}" : i % 3 == 1 ? "NOSEPARATOR" : "bad name\u0001=x")];
        TagProbe many = ParseComment(OggBuilder.VorbisComment("vendor", comments));
        Assert.Equal(1000, many.Findings.Single(f => f.Code == "vorbis-comment.no-separator").Count);
        Assert.Equal(1000, many.Findings.Single(f => f.Code == "vorbis-comment.invalid-field-name").Count);
        Assert.All(many.Findings, f => Assert.NotNull(f.Node));
        Node tag = many.Tag!;
        Assert.Equal(3 + 3000, tag.Children.Count);
        Assert.Equal("Song 0", tag.Children[3].Value?.ToString());
        Assert.Equal(["Length", "Text"], tag.Children[4].Children.Select(c => c.Name));
        Assert.Equal(["Length", "Field Name", "Value"], tag.Children[3 + 2999].Children.Select(c => c.Name));
        Assert.Equal("Comment", tag.Children[3 + 2999].Name);

        // And the same nodes as a small tag has: the vendor and the first nine comments of each.
        TagProbe small = ParseComment(OggBuilder.VorbisComment("vendor", comments[..9]));
        foreach (int i in new[] { 0, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11 })
        {
            Assert.Equal((small.Tag!.Children[i].Name, small.Tag.Children[i].Value?.ToString(), small.Tag.Children[i].Offset, small.Tag.Children[i].Length),
                (tag.Children[i].Name, tag.Children[i].Value?.ToString(), tag.Children[i].Offset, tag.Children[i].Length));
        }
    }

    private static TagProbe ParseComment(byte[] data) => new VorbisCommentTagParser().ParseWhole(new MappedBytes(data, 0), CancellationToken.None)!;
}
