using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Aac;

// The Statistics nodes of a stream's coding, from its CodingTally, as the MPEG parser shows the
// coding statistics mp3guessenc and EncSpot print: window sequences and shapes, stereo coding, the
// tools channel streams use, codebooks, max_sfb and the bandwidth it gives, global gain, SBR, fill
// and data stream elements. Shares are rounded half to even, to tenths of a percent.
internal static class CodingStatistics
{
    private static readonly string[] WindowSequences = ["Only Long", "Long Start", "Eight Short", "Long Stop"];
    private static readonly string[] MidSideMasks = ["None (0)", "Per Band (1)", "All Bands (2)", "Reserved (3)"];

    // cover makes a node over the stream's bytes with a value, description and topic.
    public static void Add(Node statistics, CodingTally tally, StreamSetup setup, Func<string, NodeValue?, string, string?, Node> cover)
    {
        Node blocks = statistics.Add(cover("Raw Data Blocks", NodeValue.Integer(tally.Blocks, "blocks", "block"), Explain.StatBlocks, Topics.RawDataBlocks));
        if (tally.DamagedBlocks > 0)
        {
            blocks.Add(cover("Unreadable Blocks", Share(tally.DamagedBlocks, tally.Blocks, "block"), Explain.StatDamagedBlocks, null));
        }

        Node elements = statistics.Add(cover("Elements", NodeValue.Integer(tally.Elements.Sum(), "elements", "element"), Explain.StatElements, Topics.RawDataBlocks));
        for (int id = 0; id < 7; id++)
        {
            if (tally.Elements[id] > 0)
            {
                elements.Add(cover(AacTables.ElementName(id), NodeValue.Integer(tally.Elements[id], "elements", "element"), Explain.StatElementCount, null));
            }
        }

        long streams = tally.ChannelStreams;
        if (streams > 0)
        {
            Node windows = statistics.Add(cover("Window Sequences", NodeValue.Integer(streams, "channel streams", "channel stream"), Explain.StatWindowSequences, Topics.Windows));
            for (int w = 0; w < 4; w++)
            {
                if (tally.WindowSequences[w] > 0)
                {
                    windows.Add(cover(WindowSequences[w], Share(tally.WindowSequences[w], streams, "channel stream"), Explain.StatWindowSequence, null));
                }
            }

            Node shapes = statistics.Add(cover("Window Shapes", NodeValue.Integer(streams, "channel streams", "channel stream"), Explain.StatWindowShapes, Topics.Windows));
            for (int s = 0; s < 2; s++)
            {
                if (tally.WindowShapes[s] > 0)
                {
                    shapes.Add(cover(s == 0 ? "Sine" : "KBD", Share(tally.WindowShapes[s], streams, "channel stream"), Explain.StatWindowShape, null));
                }
            }
        }

        if (tally.Pairs > 0)
        {
            Node pairs = statistics.Add(cover("Channel Pairs", NodeValue.Integer(tally.Pairs, "elements", "element"), Explain.StatPairs, Topics.MidSide));
            pairs.Add(cover("Common Window", Share(tally.CommonWindow, tally.Pairs, "element"), Explain.StatCommonWindow, Topics.Windows));
            if (tally.CommonWindow > 0)
            {
                Node masks = pairs.Add(cover("Mid/Side Masks", NodeValue.Integer(tally.CommonWindow, "elements", "element"), Explain.StatMidSideMasks, Topics.MidSide));
                for (int m = 0; m < 4; m++)
                {
                    if (tally.MidSideMasks[m] > 0)
                    {
                        masks.Add(cover(MidSideMasks[m], Share(tally.MidSideMasks[m], tally.CommonWindow, "element"), Explain.StatMidSideMask, null));
                    }
                }

                pairs.Add(cover("Mid/Side Bands", Percent(tally.MidSideBands, tally.PairBands), Explain.StatMidSideBands, Topics.MidSide));
                pairs.Add(cover("Intensity Stereo", Share(tally.IntensityPairs, tally.CommonWindow, "element"), Explain.StatIntensityPairs, Topics.Intensity));
                if (tally.IntensityBands > 0)
                {
                    pairs.Add(cover("Intensity Stereo Bands", Percent(tally.IntensityBands, tally.PairBands), Explain.StatIntensityBands, Topics.Intensity));
                    pairs.Add(cover("Out of Phase", Share(tally.IntensityOutOfPhase, tally.IntensityBands, "band"), Explain.StatOutOfPhase, Topics.Intensity));
                }
            }
        }

        if (streams > 0)
        {
            Node noise = statistics.Add(cover("Noise Substitution", Share(tally.NoiseStreams, streams, "channel stream"), Explain.StatNoise, Topics.Pns));
            noise.Add(cover("Noise Bands", Percent(tally.NoiseBands, tally.Bands), Explain.StatNoiseBands, null));
            long tns = tally.Tns.Sum();
            Node tnsNode = statistics.Add(cover("TNS", Share(tns, streams, "channel stream"), Explain.StatTns, Topics.Tns));
            for (int w = 0; w < 4; w++)
            {
                if (tally.WindowSequences[w] > 0)
                {
                    tnsNode.Add(cover(WindowSequences[w], $"{Number(tally.Tns[w])} of {Number(tally.WindowSequences[w])} ({Tenths(tally.Tns[w], tally.WindowSequences[w])}%)",
                        Explain.StatTnsWindow, null));
                }
            }

            if (tns > 0)
            {
                tnsNode.Add(cover("Filters", NodeValue.Integer(tally.TnsFilters, "filters", "filter"), Explain.StatTnsFilters, null));
            }

            statistics.Add(cover("Pulse Data", Share(tally.Pulses, streams, "channel stream"), Explain.StatPulses, Topics.PulseData));
            if (tally.GainControl > 0 || setup.ObjectType == 3)
            {
                statistics.Add(cover("Gain Control", Share(tally.GainControl, streams, "channel stream"), Explain.StatGainControl, null));
            }

            if (tally.Prediction > 0 || setup.ObjectType == 1)
            {
                Node prediction = statistics.Add(cover("Prediction", Share(tally.Prediction, streams, "channel stream"), Explain.StatPrediction, null));
                prediction.Add(cover("Predictor Resets", NodeValue.Integer(tally.PredictorResets, "channel streams", "channel stream"), Explain.StatPredictorResets, null));
            }

            if (tally.LongTermPrediction > 0 || setup.ObjectType == 4)
            {
                statistics.Add(cover("Long-Term Prediction", Share(tally.LongTermPrediction, streams, "channel stream"), Explain.StatLtp, null));
            }

            Node codebooks = statistics.Add(cover("Codebooks", NodeValue.Integer(tally.Bands, "bands", "band"), Explain.StatCodebooks, Topics.Codebooks));
            for (int book = 0; book < 16; book++)
            {
                if (tally.Codebooks[book] > 0)
                {
                    codebooks.Add(cover($"Codebook {book}", Share(tally.Codebooks[book], tally.Bands, "band"), Explain.StatCodebook, null));
                }
            }

            AddMaxSfb(statistics, "Max SFB (Long Windows)", tally.MaxSfbLong, cover, Explain.StatMaxSfbLong);
            AddMaxSfb(statistics, "Max SFB (Short Windows)", tally.MaxSfbShort, cover, Explain.StatMaxSfbShort);
            int common = MostCommon(tally.MaxSfbLong);
            if (common > 0 && common <= setup.LongBands)
            {
                int hertz = (int)Math.Round(setup.LineFrequency(setup.LongOffsets[common]), MidpointRounding.ToEven);
                statistics.Add(cover("Bandwidth", $"highest coded band ends at about {Number(hertz)} Hz (max_sfb {common})", Explain.StatBandwidth, Topics.Windows));
            }

            Node gain = statistics.Add(cover("Global Gain", null, Explain.StatGlobalGain, null));
            for (int ch = 0; ch < tally.MinimumGain.Count; ch++)
            {
                if (tally.MaximumGain[ch] >= 0)
                {
                    Node channel = gain.Add(cover($"Channel {ch}", null, Explain.StatGainChannel, null));
                    channel.Add(cover("Minimum", NodeValue.Integer(tally.MinimumGain[ch]), Explain.StatGainMinimum, null));
                    channel.Add(cover("Maximum", NodeValue.Integer(tally.MaximumGain[ch]), Explain.StatGainMaximum, null));
                }
            }
        }

        if (tally.SbrBlocks > 0)
        {
            Node sbr = statistics.Add(cover("SBR", Share(tally.SbrBlocks, tally.Blocks, "access unit"), Explain.StatSbr, Topics.Sbr));
            sbr.Add(cover("Parametric Stereo", Share(tally.PsBlocks, tally.Blocks, "access unit"), Explain.StatPs, Topics.ParametricStereo));
            sbr.Add(cover("SBR Headers", NodeValue.Integer(tally.SbrHeaders, "headers", "header"), Explain.StatSbrHeaders, null));
            if (tally.SbrUnread > 0)
            {
                sbr.Add(cover("Unread SBR Data", NodeValue.Integer(tally.SbrUnread, "payloads", "payload"), Explain.StatSbrUnread, null));
            }

            if (tally.FirstHeader is { } header)
            {
                int rate = setup.SbrSampleRate;
                string Edge(int band) => $"QMF band {band}, about {Number(Sbr.BandFrequency(band, rate))} Hz";
                SbrBands? bands = header.Bands;
                sbr.Add(cover("Start Frequency", $"{header.StartFrequency}" + (bands is { } b1 ? $" ({Edge(b1.Kx)})" : string.Empty), Explain.StatSbrStart, null));
                sbr.Add(cover("Stop Frequency", $"{header.StopFrequency}" + (bands is { } b2 ? $" ({Edge(b2.K2)})" : string.Empty), Explain.StatSbrStop, null));
                sbr.Add(cover("Crossover Band", NodeValue.Integer(header.XoverBand), Explain.StatSbrXover, null));
                sbr.Add(cover("Amplitude Resolution", header.AmpRes == 0 ? "1.5 dB" : "3 dB", Explain.StatSbrAmpRes, null));
                sbr.Add(cover("HasHeaderChanges", NodeValue.Boolean(tally.SbrHeaderChanged), Explain.StatSbrChanges, null));
            }
        }

        if (tally.Elements[6] > 0)
        {
            Node fill = statistics.Add(cover("Fill Elements", $"{Number(tally.Elements[6])} elements, {Number(tally.FillBytes)} bytes", Explain.StatFill, Topics.RawDataBlocks));
            for (int type = 0; type < 16; type++)
            {
                if (tally.Extensions[type] > 0)
                {
                    fill.Add(cover(AacTables.ExtensionTypeName(type), $"{Number(tally.Extensions[type])} payloads, {Number(tally.ExtensionBytes[type])} bytes", Explain.StatExtension, null));
                }
            }

            if (tally.InvalidFillData > 0)
            {
                fill.Add(cover("Invalid Fill Data", NodeValue.Integer(tally.InvalidFillData, "payloads", "payload"), Explain.StatInvalidFill, null));
            }
        }

        if (tally.Elements[4] > 0)
        {
            statistics.Add(cover("Data Stream Elements", $"{Number(tally.Elements[4])} elements, {Number(tally.DataStreamBytes)} bytes", Explain.StatDse, Topics.RawDataBlocks));
        }
    }

    private static void AddMaxSfb(Node statistics, string name, long[] counts, Func<string, NodeValue?, string, string?, Node> cover, string description)
    {
        long total = counts.Sum();
        if (total == 0)
        {
            return;
        }

        Node node = statistics.Add(cover(name, NodeValue.Integer(counts.Count(c => c > 0), "values", "value"), description, Topics.Windows));
        for (int value = counts.Length - 1; value >= 0; value--)
        {
            if (counts[value] > 0)
            {
                node.Add(cover(Number(value), Share(counts[value], total, "channel stream"), Explain.StatMaxSfbValue, null));
            }
        }
    }

    private static int MostCommon(long[] counts)
    {
        int best = -1;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0 && (best < 0 || counts[i] > counts[best]))
            {
                best = i;
            }
        }

        return best;
    }

    private static string Share(long part, long whole, string unit) =>
        $"{(part == 1 ? $"1 {unit}" : $"{Number(part)} {unit}s")} ({Tenths(part, whole)}%)";

    private static NodeValue Percent(long part, long whole) => NodeValue.Decimal(RoundHalfEven(part * 1000L, whole) / 10.0, 1, "%");

    private static string Tenths(long part, long whole)
    {
        long tenths = RoundHalfEven(part * 1000L, whole);
        return $"{Number(tenths / 10)}.{Number(tenths % 10)}";
    }

    private static long RoundHalfEven(long numerator, long denominator)
    {
        if (denominator == 0)
        {
            return 0;
        }

        long quotient = Math.DivRem(numerator, denominator, out long rest);
        if (rest * 2 > denominator || (rest * 2 == denominator && quotient % 2 != 0))
        {
            quotient++;
        }

        return quotient;
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
