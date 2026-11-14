using System.Globalization;
using System.Runtime.CompilerServices;

namespace Encspect.Formats.MpegAudio.Decoding;

// The dematrixing of the MPEG-2 multichannel extension in the subband domain (ISO/IEC 13818-3 2.5.3.2.1.1) and its de-normalization (2.5.3.2.5). The equations are held as
// the standard states them, one statement after the other in its order of evaluation, so that a statement may use what an earlier one computed: Lo and Ro are the base
// channels T0 and T1, T2 to T4 the additional ones, L, R, C, LS and RS the presentation channels (the mono surround S takes the place of LS), and jS the monophonic
// surround signal of dematrix procedure 2. A test checks that every program inverts the encoder's matrix.
internal static class Dematrix
{
    // The variables a statement reads and writes.
    public const int Lo = 0;
    public const int Ro = 1;
    public const int T2 = 2;
    public const int T3 = 3;
    public const int T4 = 4;
    public const int L = 5;
    public const int R = 6;
    public const int C = 7;
    public const int LS = 8;
    public const int RS = 9;
    public const int JS = 10;
    public const int Variables = 11;

    private static readonly string[] Names = ["Lo", "Ro", "T2", "T3", "T4", "L", "R", "C", "LS", "RS", "jS"];

    // One program per configuration (by the additional channels' roles), procedure class and tc_allocation: the statements of the standard.
    private static readonly Dictionary<(string Config, int Procedure, int Tc), Program> Programs = Build();

    public sealed class Program(Statement[] statements)
    {
        public Statement[] Statements { get; } = statements;
    }

    public sealed class Statement(int target, (float Coefficient, int Source)[] terms)
    {
        public int Target { get; } = target;

        public (float Coefficient, int Source)[] Terms { get; } = terms;
    }

    // The program of a configuration ("32", "31", "30", "22" or "21": the front channels and the surround channels, a second stereo program counting as none), the
    // dematrix_procedure (0 to 3) and the tc_allocation of the subband group. A procedure of 1 uses the equations of 0, and 2 those of 0 where it has none of its own.
    public static Program Get(string configuration, int procedure, int allocation)
    {
        int key = procedure == 3 ? 3 : procedure == 2 && configuration is "32" or "31" ? 2 : 0;
        if (key == 3 || !Programs.TryGetValue((configuration, key, allocation), out Program? program))
        {
            program = Programs[(configuration, 3, 0)];
        }

        return program;
    }

    // The factor each presentation channel is multiplied with after dematrixing: the inverse weighting times the de-normalization (2.5.3.2.5), L, R, C, LS and RS.
    public static float[] Factors(int procedure)
    {
        double sqrt2 = Math.Sqrt(2.0);
        return procedure switch
        {
            1 => [(float)(1.5 + (0.5 * sqrt2)), (float)(1.5 + (0.5 * sqrt2)), (float)(sqrt2 * (1.5 + (0.5 * sqrt2))), (float)(2.0 * (1.5 + (0.5 * sqrt2))), (float)(2.0 * (1.5 + (0.5 * sqrt2)))],
            3 => [1f, 1f, 1f, 1f, 1f],
            _ => [(float)(1.0 + sqrt2), (float)(1.0 + sqrt2), (float)(sqrt2 * (1.0 + sqrt2)), (float)(sqrt2 * (1.0 + sqrt2)), (float)(sqrt2 * (1.0 + sqrt2))],
        };
    }

    // Runs a program over twelve subband samples per variable (vars holds Variables rows of 12).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Run(Program program, float[] vars)
    {
        foreach (Statement statement in program.Statements)
        {
            int target = statement.Target * 12;
            (float coefficient, int source) = statement.Terms[0];
            int from = source * 12;
            for (int k = 0; k < 12; k++)
            {
                vars[target + k] = coefficient * vars[from + k];
            }

            for (int t = 1; t < statement.Terms.Length; t++)
            {
                (coefficient, source) = statement.Terms[t];
                from = source * 12;
                for (int k = 0; k < 12; k++)
                {
                    vars[target + k] += coefficient * vars[from + k];
                }
            }
        }
    }

    private static Dictionary<(string, int, int), Program> Build()
    {
        var programs = new Dictionary<(string, int, int), Program>();

        void Add(string configuration, int procedure, params string[] allocations)
        {
            for (int tc = 0; tc < allocations.Length; tc++)
            {
                programs[(configuration, procedure, tc)] = Parse(allocations[tc]);
            }
        }

        // 3/2 with dematrix procedure 0 and 1.
        Add("32", 0,
            "L=Lo-T2-T3;R=Ro-T2-T4;C=T2;LS=T3;RS=T4",
            "C=Lo-T2-T3;R=Ro-C-T4;L=T2;LS=T3;RS=T4",
            "C=Ro-T2-T4;L=Lo-C-T3;R=T2;LS=T3;RS=T4",
            "LS=Lo-T3-T2;R=Ro-T2-T4;C=T2;L=T3;RS=T4",
            "L=Lo-T2-T3;RS=Ro-T4-T2;C=T2;LS=T3;R=T4",
            "LS=Lo-T3-T2;RS=Ro-T4-T2;C=T2;L=T3;R=T4",
            "C=Ro-T2-T4;LS=Lo-T3-C;R=T2;L=T3;RS=T4",
            "C=Lo-T2-T3;RS=Ro-T4-C;L=T2;LS=T3;R=T4");

        // 3/2 with dematrix procedure 2: the surround channels are phase shifted (the shift itself is left to the reproduction), and the monophonic surround signal jS
        // enters the equations.
        Add("32", 2,
            "jS=0.5*T3+0.5*T4;L=Lo-T2+jS;R=Ro-T2-jS;C=T2;LS=T3;RS=T4",
            "jS=0.5*T3+0.5*T4;C=Lo-T2+jS;R=Ro-C-jS;L=T2;LS=T3;RS=T4",
            "jS=0.5*T3+0.5*T4;C=Ro-T2-jS;L=Lo-C+jS;R=T2;LS=T3;RS=T4",
            "R=Lo+Ro-2*T2-T3;LS=-2*Lo+2*T2+2*T3-T4;C=T2;L=T3;RS=T4",
            "L=Lo+Ro-2*T2-T4;RS=2*Ro-2*T2-2*T4-T3;C=T2;LS=T3;R=T4",
            "LS=0.5*Ro-0.5*Lo+0.5*T3-0.5*T4;RS=LS;C=T2;L=T3;R=T4",
            "C=0.5*Ro+0.5*Lo-0.5*T2-0.5*T3;LS=Ro-Lo-T2+T3-T4;R=T2;L=T3;RS=T4",
            "C=0.5*Lo+0.5*Ro-0.5*T2-0.5*T4;RS=Ro-Lo+T2-T3-T4;L=T2;LS=T3;R=T4");

        // 3/1 with procedure 0 and 1, then 2; the mono surround signal S takes the place of LS.
        Add("31", 0,
            "L=Lo-T2-T3;R=Ro-T2-T3;C=T2;LS=T3",
            "C=Lo-T2-T3;R=Ro-C-T3;L=T2;LS=T3",
            "C=Ro-T2-T3;L=Lo-C-T3;R=T2;LS=T3",
            "LS=Lo-T2-T3;R=Ro-T2-LS;C=T2;L=T3",
            "LS=Ro-T2-T3;L=Lo-T2-LS;C=T2;R=T3");
        Add("31", 2,
            "L=Lo-T2+T3;R=Ro-T2-T3;C=T2;LS=T3",
            "C=Lo-T2+T3;R=Ro-C-T3;L=T2;LS=T3",
            "C=Ro-T2-T3;L=Lo-C+T3;R=T2;LS=T3",
            "LS=-Lo+T2+T3;R=Ro-T2-LS;C=T2;L=T3",
            "LS=Ro-T2-T3;L=Lo-T2+LS;C=T2;R=T3",
            "C=0.5*Ro+0.5*Lo-0.5*T2-0.5*T3;LS=0.5*Ro-0.5*Lo+0.5*T2-0.5*T3;L=T2;R=T3");

        Add("30", 0,
            "L=Lo-T2;R=Ro-T2;C=T2",
            "C=Lo-T2;R=Ro-C;L=T2",
            "C=Ro-T2;L=Lo-C;R=T2");
        Add("22", 0,
            "L=Lo-T2;R=Ro-T3;LS=T2;RS=T3",
            "R=Ro-T3;LS=Lo-T2;L=T2;RS=T3",
            "L=Lo-T2;RS=Ro-T3;LS=T2;R=T3",
            "LS=Lo-T2;RS=Ro-T3;L=T2;R=T3");
        Add("21", 0,
            "L=Lo-T2;R=Ro-T2;LS=T2",
            "LS=Lo-T2;R=Ro-LS;L=T2",
            "LS=Ro-T2;L=Lo-LS;R=T2");

        // Without dematrixing (procedure 3, or no additional channel to dematrix with): the base channels are L and R and the additional ones follow in the order
        // of the configuration, which tc_allocation 0 names.
        Add("32", 3, "L=Lo;R=Ro;C=T2;LS=T3;RS=T4");
        Add("31", 3, "L=Lo;R=Ro;C=T2;LS=T3");
        Add("30", 3, "L=Lo;R=Ro;C=T2");
        Add("22", 3, "L=Lo;R=Ro;LS=T2;RS=T3");
        Add("21", 3, "L=Lo;R=Ro;LS=T2");
        Add("20", 3, "L=Lo;R=Ro");
        return programs;
    }

    private static Program Parse(string text)
    {
        var statements = new List<Statement>();
        foreach (string part in text.Split(';'))
        {
            string[] sides = part.Split('=');
            int target = Array.IndexOf(Names, sides[0]);
            var terms = new List<(float, int)>();
            string expression = sides[1];
            int index = 0;
            while (index < expression.Length)
            {
                float sign = 1f;
                if (expression[index] is '+' or '-')
                {
                    sign = expression[index] == '-' ? -1f : 1f;
                    index++;
                }

                int end = index;
                while (end < expression.Length && expression[end] is not ('+' or '-'))
                {
                    end++;
                }

                string term = expression[index..end];
                float coefficient = 1f;
                int star = term.IndexOf('*', StringComparison.Ordinal);
                if (star >= 0)
                {
                    coefficient = float.Parse(term[..star], CultureInfo.InvariantCulture);
                    term = term[(star + 1)..];
                }

                terms.Add((sign * coefficient, Array.IndexOf(Names, term)));
                index = end;
            }

            statements.Add(new Statement(target, [.. terms]));
        }

        return new Program([.. statements]);
    }
}
