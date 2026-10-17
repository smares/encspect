namespace Encspect.Tests;

// The checker of CrossFormatNamingTests finds what it should, whatever words stand around the count, and lets pass what is not a count.
public class SingularCheckTests
{
    [Theory]
    [InlineData("105 non-zero, 1 escapes, codebook 0")]
    [InlineData("need 58 frames in 1 audio packets; the stream holds 3")]
    [InlineData("a stream of 1 channels (section 4.2.4)")]
    [InlineData("255 streams and 1 coupled streams decode to more than 255 channels")]
    [InlineData("the streams change 1 times in the file")]
    [InlineData("the stream has 1 changes")]
    [InlineData("holds 1 sets")]
    [InlineData("gives durations to 1 samples, its stsz box sizes 20")]
    [InlineData("a frame of 1 bytes")]
    [InlineData("has 1 points")]
    [InlineData("1 subframes cannot hold 48 blocks")]
    [InlineData("a channel mask of 1 speakers")]
    [InlineData("1 byte after the last frame at 0x10 are not a frame.")]
    [InlineData("1 byte at 0x10 follow an ID3v2 tag")]
    [InlineData("1 of the seek table's 5 entries do not point")]
    [InlineData("1 of its 32 bytes are there")]
    [InlineData("Codebook 3 has 1 dimensions and 9000000 entries")]
    [InlineData("Timescale: 1 units per second")]
    public void ACountOfOneWrittenInThePluralIsFound(string text) => Assert.NotEmpty(SingularCheck.Problems(text));

    [Theory]
    [InlineData("2 of its 32 bytes are there")]
    [InlineData("1 byte after the last frame at 0x10 is not a frame.")]
    [InlineData("1 byte at 0x10 follows an ID3v2 tag")]
    [InlineData("105 non-zero, 1 escape, codebook 0")]
    [InlineData("Codebook 3 has 1 dimension and 9000000 entries")]
    [InlineData("7 groups of 1, 1, 1, 1, 1, 2, 1 windows")]
    [InlineData("a size of 1 to 1 bytes")]
    [InlineData("Track 1 Samples: 44 samples")]
    [InlineData("stream 1 codes 480 samples where stream 0 codes 960")]
    [InlineData("a ContentEncoding of order 1 whose settings are hidden")]
    [InlineData("one entry every 1 would need 15")]
    [InlineData("a Hz times 2^1 times 8")]
    [InlineData("1 of the seek table's 1 entry does not point")]
    [InlineData("22 codes is its")]
    public void WhatIsNotACountOfOneInThePluralPasses(string text) => Assert.Empty(SingularCheck.Problems(text));
}
