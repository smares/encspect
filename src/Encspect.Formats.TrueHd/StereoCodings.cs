using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// The "Stereo Coding" categories and the rule that classifies the matrices of the two-channel presentation (docs/design/coding-graphs.md, "Stereo
// Coding: the design"). The order is FLAC's with "Weighted mix" last, so the four FLAC categories keep their colors.
internal static class StereoCodings
{
    public const int LeftRight = 0;
    public const int LeftSide = 1;
    public const int SideRight = 2;
    public const int MidSide = 3;
    public const int WeightedMix = 4;

    public static readonly Category[] Categories =
    [
        new("Left/right stereo", Explain.StereoLeftRight) { Topic = Topics.Rematrixing },
        new("Left/side stereo", Explain.StereoLeftSide) { Topic = Topics.LeftSideStereo },
        new("Side/right stereo", Explain.StereoSideRight) { Topic = Topics.LeftSideStereo },
        new("Mid/side stereo", Explain.StereoMidSide) { Topic = Topics.MidSideStereo },
        new("Weighted mix", Explain.StereoWeightedMix) { Topic = Topics.Rematrixing },
    ];

    private enum Part
    {
        Left,
        Right,
        Sum,
        Difference,
        Weighted,
    }

    // The category of the matrices in force in a substream whose restart header gives it two matrix channels, or -1 for any other substream. The
    // matrices are composed in exact integer arithmetic into the map T from the two coded channels to left and right, without the rounding after each
    // matrix, the quantizer masks, the bypassed bits and the noise.
    public static int Classify(SubstreamState state)
    {
        if (state.LastMatrixChannel != 1)
        {
            return -1;
        }

        // Row m holds matrix channel m over the two coded channels, all rows over one common denominator.
        BigInteger[,] rows = { { BigInteger.One, BigInteger.Zero }, { BigInteger.Zero, BigInteger.One } };
        var scale = new BigInteger(1 << 14);
        for (int j = 0; j < state.MatrixCount; j++)
        {
            MatrixState matrix = state.Matrices[j];
            int target = matrix.OutChannel;
            var replaced = new BigInteger[2];
            for (int c = 0; c < 2; c++)
            {
                replaced[c] = (matrix.Coefficients[0] * rows[0, c]) + (matrix.Coefficients[1] * rows[1, c]);
            }

            for (int c = 0; c < 2; c++)
            {
                rows[1 - target, c] *= scale;
                rows[target, c] = replaced[c];
            }
        }

        // Rows in position order: position 0 is left and position 1 right where each position is taken once, else matrix channel 0 is left.
        bool isSwapped = state.ChannelAssignment[0] == 1 && state.ChannelAssignment[1] == 0;
        int left = isSwapped ? 1 : 0;
        BigInteger t00 = rows[left, 0];
        BigInteger t01 = rows[left, 1];
        BigInteger t10 = rows[1 - left, 0];
        BigInteger t11 = rows[1 - left, 1];
        if ((t00 * t11) - (t01 * t10) == BigInteger.Zero)
        {
            return WeightedMix;
        }

        // The rows of the adjugate say what each coded channel is in left and right, up to a common factor.
        Part first = Of(t11, -t01);
        Part second = Of(-t10, t00);
        bool hasLeft = first == Part.Left || second == Part.Left;
        bool hasRight = first == Part.Right || second == Part.Right;
        bool hasSum = first == Part.Sum || second == Part.Sum;
        bool hasDifference = first == Part.Difference || second == Part.Difference;
        if (hasLeft && hasRight)
        {
            return LeftRight;
        }

        if (hasLeft && hasDifference)
        {
            return LeftSide;
        }

        if (hasDifference && hasRight)
        {
            return SideRight;
        }

        return hasSum && hasDifference ? MidSide : WeightedMix;
    }

    private static Part Of(BigInteger leftTerm, BigInteger rightTerm)
    {
        if (rightTerm.IsZero)
        {
            return Part.Left;
        }

        if (leftTerm.IsZero)
        {
            return Part.Right;
        }

        if (leftTerm == rightTerm)
        {
            return Part.Sum;
        }

        return leftTerm == -rightTerm ? Part.Difference : Part.Weighted;
    }
}
