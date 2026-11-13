using Encspect.Core;

namespace Encspect.Tests;

// A container's stream whose first frame is AC-3 (so its decoding session starts there) but that holds E-AC-3 dependent frames is named "E-AC-3", as it was before the node was made at
// the first frame; the measurements of such a stream are found through the same node.
public class Ac3StreamNameTests
{
    [Fact]
    public void A_core_with_dependent_frames_in_a_wave_file_is_an_E_AC_3_stream()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/built-extensible-ac3-core-dependent.wav");

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("E-AC-3", stream.Nodes[0].Name);
        Assert.Equal("E-AC-3", stream.Codec);
    }
}
