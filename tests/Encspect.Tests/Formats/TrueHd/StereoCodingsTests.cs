using Encspect.Formats.TrueHd;

namespace Encspect.Tests.Formats.TrueHd;

// The rule that classifies the matrices of a two-channel presentation, on states filled by hand: the branches no committed file reaches.
public class StereoCodingsTests
{
    private const int One = 1 << 14;

    private static SubstreamState State(int first, int second, params (int Out, int A, int B)[] matrices)
    {
        var state = new SubstreamState { LastMatrixChannel = 1, MatrixCount = matrices.Length };
        state.ChannelAssignment[0] = first;
        state.ChannelAssignment[1] = second;
        for (int j = 0; j < matrices.Length; j++)
        {
            state.Matrices[j].OutChannel = matrices[j].Out;
            state.Matrices[j].Coefficients[0] = matrices[j].A;
            state.Matrices[j].Coefficients[1] = matrices[j].B;
        }

        return state;
    }

    [Fact]
    public void A_matrix_that_only_scales_its_channel_is_left_right_stereo()
    {
        Assert.Equal(StereoCodings.LeftRight, StereoCodings.Classify(State(0, 1, (0, One * 3 / 2, 0))));
        Assert.Equal(StereoCodings.LeftRight, StereoCodings.Classify(State(0, 1, (1, 0, -One * 9 / 16))));
        Assert.Equal(StereoCodings.LeftRight, StereoCodings.Classify(State(0, 1)));
    }

    [Fact]
    public void Where_a_position_is_taken_twice_matrix_channel_0_is_left()
    {
        // Left/side by a matrix on channel 1: channel 0 is left whichever positions the assignment names.
        Assert.Equal(StereoCodings.LeftSide, StereoCodings.Classify(State(0, 0, (1, One, -One))));
        Assert.Equal(StereoCodings.LeftSide, StereoCodings.Classify(State(1, 1, (1, One, -One))));
        // A swap names each position once: matrix channel 1 is left, so the same matrix is side/right.
        Assert.Equal(StereoCodings.SideRight, StereoCodings.Classify(State(1, 0, (1, One, -One))));
    }

    [Fact]
    public void The_other_codings_and_a_singular_map()
    {
        Assert.Equal(StereoCodings.SideRight, StereoCodings.Classify(State(0, 1, (0, -One, One))));
        Assert.Equal(StereoCodings.MidSide, StereoCodings.Classify(State(0, 1, (0, One / 2, One / 2), (1, One, -One))));
        Assert.Equal(StereoCodings.WeightedMix, StereoCodings.Classify(State(0, 1, (0, One, One / 2))));
        Assert.Equal(StereoCodings.WeightedMix, StereoCodings.Classify(State(0, 1, (1, One, 0))));
    }

    [Fact]
    public void A_substream_of_another_channel_count_has_no_coding()
    {
        Assert.Equal(-1, StereoCodings.Classify(new SubstreamState { LastMatrixChannel = 5 }));
    }
}
