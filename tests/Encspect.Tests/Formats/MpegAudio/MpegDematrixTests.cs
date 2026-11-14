using Encspect.Formats.MpegAudio.Decoding;

namespace Encspect.Tests;

// The dematrixing equations of the MPEG-2 multichannel extension (ISO/IEC 13818-3 2.5.3.2.1.1) as the decoder holds them: for every configuration, dematrix procedure and
// tc_allocation, signals matrixed forward (the base channels Lo and Ro from the presentation channels, the additional transmission channels as tc_allocation names
// them) must come back out of the program. The forward model is the standard's: Lo = L + C + LS and Ro = R + C + RS, with the surround channels of procedure 2 phase
// shifted into a monophonic surround signal S (Lo = L + C - S, Ro = R + C + S).
public class MpegDematrixTests
{
    // What T2, T3 and T4 carry for each tc_allocation (2.5.2.15, tables A to E).
    private static readonly Dictionary<string, string[]> Carries = new()
    {
        ["32"] = ["C LS RS", "L LS RS", "R LS RS", "C L RS", "C LS R", "C L R", "R L RS", "L LS R"],
        ["31"] = ["C LS", "L LS", "R LS", "C L", "C R", "L R"],
        ["30"] = ["C", "L", "R"],
        ["22"] = ["LS RS", "L RS", "LS R", "L R"],
        ["21"] = ["LS", "L", "R"],
    };

    public static TheoryData<string, int, int> Cases
    {
        get
        {
            var data = new TheoryData<string, int, int>();
            foreach ((string configuration, string[] allocations) in Carries)
            {
                for (int tc = 0; tc < allocations.Length; tc++)
                {
                    foreach (int procedure in new[] { 0, 1, 2 })
                    {
                        // Procedure 2 exists for 3/1 and 3/2 only, and tc_allocation 5 of 3/1 only for procedure 2.
                        if ((procedure == 2 && configuration is not ("32" or "31")) || (configuration == "31" && tc == 5 && procedure != 2))
                        {
                            continue;
                        }

                        data.Add(configuration, procedure, tc);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProgramsInvertTheMatrix(string configuration, int procedure, int allocation)
    {
        var random = new Random(configuration.GetHashCode(StringComparison.Ordinal) ^ (procedure * 31) ^ allocation);
        var channels = new Dictionary<string, float[]>();
        foreach (string name in new[] { "L", "R", "C", "LS", "RS" })
        {
            channels[name] = [.. Enumerable.Range(0, 12).Select(_ => (float)((random.NextDouble() * 2) - 1))];
        }

        bool hasMonoSurround = configuration is "31" or "21";
        bool isShifted = procedure == 2;
        if (configuration == "32" && isShifted && allocation == 5)
        {
            // Both surround channels are missing, so only their mean can be recovered, and the standard sets them equal.
            channels["RS"] = channels["LS"];
        }

        float[] surround = hasMonoSurround ? channels["LS"] : [.. Enumerable.Range(0, 12).Select(k => 0.5f * (channels["LS"][k] + channels["RS"][k]))];
        var variables = new float[Dematrix.Variables * 12];
        bool hasCenter = configuration[0] == '3';
        for (int k = 0; k < 12; k++)
        {
            float c = hasCenter ? channels["C"][k] : 0;
            float left = configuration[1] == '2' ? channels["LS"][k] : 0;
            float right = configuration[1] == '2' ? channels["RS"][k] : 0;
            float mono = configuration[1] == '1' ? surround[k] : 0;
            float s = surround[k];
            variables[(Dematrix.Lo * 12) + k] = isShifted ? channels["L"][k] + c - s : channels["L"][k] + c + left + mono;
            variables[(Dematrix.Ro * 12) + k] = isShifted ? channels["R"][k] + c + s : channels["R"][k] + c + right + mono;
        }

        string[] carried = Carries[configuration][allocation].Split(' ');
        for (int i = 0; i < carried.Length; i++)
        {
            channels[carried[i]].CopyTo(variables, (Dematrix.T2 + i) * 12);
        }

        Dematrix.Run(Dematrix.Get(configuration, procedure, allocation), variables);

        foreach ((string name, int variable) in new[] { ("L", Dematrix.L), ("R", Dematrix.R), ("C", Dematrix.C), ("LS", Dematrix.LS), ("RS", Dematrix.RS) })
        {
            bool isPresent = name switch
            {
                "C" => hasCenter,
                "LS" => configuration[1] != '0',
                "RS" => configuration[1] == '2',
                _ => true,
            };
            if (!isPresent)
            {
                continue;
            }

            for (int k = 0; k < 12; k++)
            {
                Assert.True(Math.Abs(variables[(variable * 12) + k] - channels[name][k]) < 1e-5, $"{configuration} procedure {procedure} tc_allocation {allocation}: {name}[{k}]");
            }
        }
    }

    [Fact]
    public void FactorsFollowTheTableOfDeNormalization()
    {
        // 2.5.3.2.5: L and R by 1 + sqrt 2 and C, LS and RS by sqrt 2 times that for procedures 0 and 2; for procedure 1 by 1.5 + 0.5 sqrt 2, with LS and RS by 2 times that and
        // C by sqrt 2 times; none for procedure 3.
        double sqrt2 = Math.Sqrt(2);
        Assert.Equal([(float)(1 + sqrt2), (float)(1 + sqrt2), (float)(sqrt2 * (1 + sqrt2)), (float)(sqrt2 * (1 + sqrt2)), (float)(sqrt2 * (1 + sqrt2))], Dematrix.Factors(0));
        Assert.Equal(Dematrix.Factors(0), Dematrix.Factors(2));
        Assert.Equal((float)(1.5 + (0.5 * sqrt2)), Dematrix.Factors(1)[0]);
        Assert.Equal((float)(2 * (1.5 + (0.5 * sqrt2))), Dematrix.Factors(1)[3]);
        Assert.Equal((float)(sqrt2 * (1.5 + (0.5 * sqrt2))), Dematrix.Factors(1)[2]);
        Assert.All(Dematrix.Factors(3), f => Assert.Equal(1f, f));
    }
}
