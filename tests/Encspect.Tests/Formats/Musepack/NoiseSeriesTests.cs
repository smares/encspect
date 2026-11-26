using System.Runtime.InteropServices;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests.Formats.Musepack;

// The numeric series "Noise-Substituted Channels" of Musepack SV7 and SV8 (docs/design/coding-graphs.md, "Musepack SV4 to SV8"): one value for each frame (SV8: each
// audio packet) that has a coded bandwidth, with the same x values, which add up to the tally's figure where no frame is cut or damaged.
public class NoiseSeriesTests
{
    [Theory]
    [InlineData("musepack/external-mppenc-1.1.mpc")]
    [InlineData("musepack/external-mpcenc-pns.mpc")]
    [InlineData("musepack/external-mpcenc-packets.mpc")]
    public void The_series_shares_the_coded_bandwidths_x_values_and_adds_up_to_the_tally(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        StreamInfo stream = Assert.Single(report.Streams);

        Series noise = stream.Series.Single(s => s.Name == "Noise-Substituted Channels");
        Series bandwidth = stream.Series.Single(s => s.Name == "Coded Bandwidth");

        Assert.Equal(bandwidth.Values.Length, noise.Values.Length);
        Assert.True(MemoryMarshal.TryGetArray(noise.X, out ArraySegment<double> own) && MemoryMarshal.TryGetArray(bandwidth.X, out ArraySegment<double> shared) && ReferenceEquals(own.Array, shared.Array));

        // 8 bytes of values for each unit (docs/design/coding-graphs.md, "Memory budget"), in an array without slack, and no x array of its own.
        Assert.True(MemoryMarshal.TryGetArray(noise.Values, out ArraySegment<double> values) && values.Array!.Length == noise.Values.Length);
        Assert.Equal("channels", noise.Unit);
        Assert.Equal(GraphConcept.None, noise.Concept);
        Node tally = Fixtures.Resolve(stream.Nodes[0], "Statistics/Noise-Substituted Channels").Single();
        Assert.Equal(tally.Value!.AsInteger(), (long)noise.Values.ToArray().Sum());
    }
}
