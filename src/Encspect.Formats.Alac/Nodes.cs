// Follows Apple's ALAC reference implementation (macosforge/alac, ALACMagicCookieDescription.txt and ALACDecoder.cpp), Apache License 2.0:
// Copyright (c) 2011 Apple Inc. See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Alac;

// The nodes of the report built from the records when something reads them: the magic cookie, and a frame with its elements. Functions only of the
// records, so the findings can build the node they point to.
internal static class Nodes
{
    public static Node Cookie(FieldMap map, AlacCookie c)
    {
        Node cookie = map.Bytes("Magic Cookie", 0, c.Length, null, Explain.Cookie, Topics.Alac);
        if (c.FormatAtomAt >= 0)
        {
            Node atom = cookie.Add(map.Bytes("Format Atom", c.FormatAtomAt, AlacCookie.AtomLength, null, Explain.FormatAtom, Topics.Alac));
            atom.Add(map.Bytes("Size", c.FormatAtomAt, 4, NodeValue.Integer(c.FormatAtomSize, "bytes"), Explain.AtomSize, Topics.Alac));
            atom.Add(map.Bytes("Type", c.FormatAtomAt + 4, 4, "frma", Explain.FormatAtomType, Topics.Alac));
            atom.Add(map.Bytes("Format", c.FormatAtomAt + 8, 4, FourCc(c.FormatAtomFormat), Explain.FormatAtomFormat, Topics.Alac));
        }

        if (c.ConfigAtomAt >= 0)
        {
            Node atom = cookie.Add(map.Bytes("Config Atom Header", c.ConfigAtomAt, AlacCookie.AtomLength, null, Explain.ConfigAtom, Topics.Alac));
            atom.Add(map.Bytes("Size", c.ConfigAtomAt, 4, NodeValue.Integer(c.ConfigAtomSize, "bytes"), Explain.AtomSize, Topics.Alac));
            atom.Add(map.Bytes("Type", c.ConfigAtomAt + 4, 4, "alac", Explain.ConfigAtomType, Topics.Alac));
            atom.Add(map.Bytes("Version and Flags", c.ConfigAtomAt + 8, 4, NodeValue.Hex(c.ConfigAtomFlags, 8), Explain.ConfigAtomFlags, Topics.Alac));
        }

        int length = Math.Min(AlacCookie.ConfigLength, Math.Max(0, c.Length - c.ConfigAt));
        if (length == 0)
        {
            return cookie;
        }

        Node config = cookie.Add(map.Bytes("Specific Config", c.ConfigAt, length, null, c.IsComplete ? Explain.SpecificConfig : Explain.ShortConfig, Topics.Alac));
        if (!c.IsComplete)
        {
            return cookie;
        }

        int at = c.ConfigAt;
        config.Add(map.Bytes("Frame Length", at, 4, NodeValue.Integer(c.FrameLength, "samples", "sample"), Explain.FrameLength, Topics.Alac));
        config.Add(map.Bytes("Compatible Version", at + 4, 1, NodeValue.Integer(c.CompatibleVersion), Explain.CompatibleVersion, Topics.Alac));
        config.Add(map.Bytes("Bit Depth", at + 5, 1, NodeValue.Integer(c.BitDepth, "bits", "bit"), Explain.BitDepth, Topics.Alac));
        config.Add(map.Bytes("History Weight (pb)", at + 6, 1, NodeValue.Integer(c.Pb), Explain.HistoryWeight, Topics.AdaptiveGolomb));
        config.Add(map.Bytes("Initial History (mb)", at + 7, 1, NodeValue.Integer(c.Mb), Explain.InitialHistory, Topics.AdaptiveGolomb));
        config.Add(map.Bytes("Maximum Golomb Parameter (kb)", at + 8, 1, NodeValue.Integer(c.Kb), Explain.MaximumParameter, Topics.AdaptiveGolomb));
        config.Add(map.Bytes("Channels", at + 9, 1, NodeValue.Integer(c.Channels, "channels", "channel"), Explain.Channels, Topics.ChannelLayouts));
        config.Add(map.Bytes("Maximum Run", at + 10, 2, NodeValue.Integer(c.MaxRun), Explain.MaximumRun, Topics.AdaptiveGolomb));
        config.Add(map.Bytes("Maximum Frame Size", at + 12, 4, c.MaxFrameBytes == 0 ? "unknown" : NodeValue.Integer(c.MaxFrameBytes, "bytes"), c.MaxFrameBytes == 0 ? Explain.MaximumFrameSizeUnknown : Explain.MaximumFrameSize, Topics.Alac));
        config.Add(map.Bytes("Average Bitrate", at + 16, 4, c.AverageBitRate == 0 ? "unknown" : NodeValue.Integer(c.AverageBitRate, "bit/s"), c.AverageBitRate == 0 ? Explain.AverageBitRateUnknown : Explain.AverageBitRate, Topics.Alac));
        config.Add(map.Bytes("Sample Rate", at + 20, 4, NodeValue.Integer(c.SampleRate, "Hz"), Explain.SampleRate, Topics.Alac));

        if (c.LayoutAt >= 0)
        {
            int layoutAt = c.LayoutAt;
            Node layout = cookie.Add(map.Bytes("Channel Layout Info", layoutAt, AlacCookie.LayoutLength, null, Explain.LayoutInfo, Topics.ChannelLayouts));
            layout.Add(map.Bytes("Size", layoutAt, 4, NodeValue.Integer(c.LayoutSize, "bytes"), Explain.LayoutSize, Topics.ChannelLayouts));
            layout.Add(map.Bytes("Type", layoutAt + 4, 4, "chan", Explain.LayoutType, Topics.ChannelLayouts));
            layout.Add(map.Bytes("Version and Flags", layoutAt + 8, 4, NodeValue.Hex(c.LayoutFlags, 8), Explain.LayoutFlags, Topics.ChannelLayouts));
            layout.Add(map.Bytes("Channel Layout Tag", layoutAt + 12, 4, CoreAudioLayouts.TagText(c.LayoutTag), Explain.LayoutTag, Topics.ChannelLayouts));
            layout.Add(map.Bytes("Channel Bitmap", layoutAt + 16, 4, NodeValue.Hex(c.LayoutBitmap, 8), Explain.LayoutBitmap, Topics.ChannelLayouts));
            layout.Add(map.Bytes("Channel Description Count", layoutAt + 20, 4, NodeValue.Integer(c.LayoutDescriptions), Explain.LayoutDescriptions, Topics.ChannelLayouts));
        }

        if (c.TerminatorAt >= 0)
        {
            int terminator = c.TerminatorAt;
            Node atom = cookie.Add(map.Bytes("Terminator Atom", terminator, AlacCookie.TerminatorLength, null, Explain.TerminatorAtom, Topics.Alac));
            atom.Add(map.Bytes("Size", terminator, 4, NodeValue.Integer(AlacCookie.TerminatorLength, "bytes"), Explain.AtomSize, Topics.Alac));
            atom.Add(map.Bytes("Type", terminator + 4, 4, NodeValue.Hex(0, 8), Explain.TerminatorType, Topics.Alac));
        }

        if (c.ExtraAt >= 0)
        {
            cookie.Add(map.Bytes("Extra Data", c.ExtraAt, c.ExtraLength, NodeValue.Integer(c.ExtraLength, "bytes"), Explain.CookieExtra, Topics.Alac));
        }

        return cookie;
    }

    private static string FourCc(uint value)
    {
        Span<char> text = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            int b = (int)((value >> (24 - (8 * i))) & 0xFF);
            text[i] = b is >= 0x20 and < 0x7F ? (char)b : '.';
        }

        return new string(text);
    }

    // The node of the frame at an index in the records.
    public static Node Frame(Records r, int index)
    {
        FrameRecord f = r.Frames[index];
        IReadOnlyList<ByteRange>? ranges = r.Ranges.GetValueOrDefault(index);
        long length = ranges is not null ? ranges[^1].End - f.Offset : f.Length;
        return new Node("Frame", f.Offset, length, f.Samples > 0 ? NodeValue.Integer(f.Samples, "samples", "sample") : NodeValue.Integer(f.Length, "bytes", "byte"), () => FrameChildren(r, index))
        {
            Kind = NodeKind.Frame,
            Description = Explain.Frame,
            Topic = Topics.Alac,
            Ranges = ranges,
        };
    }

    // The node of an element of a detailed frame; an element past the ones the records keep lies in the Further Elements node, and the frame's node
    // stands for one the records do not place.
    public static Node Element(Records r, int frame, int element)
    {
        Node node = Frame(r, frame);
        int detail = r.Frames[frame].Detail;
        if (detail >= 0 && element >= r.Details[detail].Elements.Length)
        {
            return node.Children.FirstOrDefault(c => c.Name == "Further Elements") ?? node;
        }

        IReadOnlyList<Node> children = node.Children;
        return element >= 0 && element < children.Count ? children[element] : node;
    }

    public static Node FrameChild(Records r, int frame, string name) => Frame(r, frame).Children.FirstOrDefault(c => c.Name == name) ?? Frame(r, frame);

    private static IReadOnlyList<Node> FrameChildren(Records r, int index)
    {
        FrameRecord f = r.Frames[index];
        bool hasRanges = (f.Flags & FrameFlags.HasRanges) != 0;
        int size = hasRanges ? (int)r.Ranges[index].Sum(x => x.Length) : f.Length;
        FieldMap map = hasRanges ? FieldMap.Of(r.Ranges[index], size) : FieldMap.At(f.Offset);
        if (size == 0)
        {
            return [];
        }

        if (!f.IsWalked)
        {
            return [map.Bytes("Frame Data", 0, size, NodeValue.Integer(size, "bytes", "byte"), Explain.UnwalkedFrameData, Topics.Alac)];
        }

        if (f.Detail < 0)
        {
            return [map.Bytes("Frame Data", 0, size, NodeValue.Integer(size, "bytes", "byte"), Explain.UndetailedFrameData, Topics.Alac)];
        }

        FrameDetail detail = r.Details[f.Detail];
        var children = new List<Node>(detail.Elements.Length + 2);
        long totalBits = size * 8L;
        long end = 0;
        for (int i = 0; i < detail.Elements.Length; i++)
        {
            children.Add(ElementNode(r, map, in detail.Elements[i]));
            end = detail.Elements[i].End;
        }

        int further = detail.Total - detail.Elements.Length;
        if (further > 0 && detail.RestEnd > detail.RestStart)
        {
            children.Add(map.Bits("Further Elements", detail.RestStart, detail.RestEnd, NodeValue.Integer(further, "elements", "element"), Explain.FurtherElements, Topics.Alac));
            end = detail.RestEnd;
        }

        bool hasEnd = (f.Flags & FrameFlags.HasEnd) != 0;
        if (hasEnd && f.Walked * 8L > end)
        {
            children.Add(map.Bits("Padding", end, f.Walked * 8L, NodeValue.Integer(f.Walked * 8L - end, "bits", "bit"), Explain.Padding, Topics.Alac));
        }

        if (hasEnd && f.Walked < size)
        {
            children.Add(map.Bytes("Trailing Data", f.Walked, size - f.Walked, NodeValue.Integer(size - f.Walked, "bytes", "byte"), Explain.TrailingData, Topics.Alac));
        }
        else if (!hasEnd && end < totalBits)
        {
            // The frame ends without an end tag, or the walk stopped at a fault: the bits from the last element read are shown as they are.
            children.Add(map.Bits("Unread Data", end, totalBits, NodeValue.Integer(totalBits - end, "bits", "bit"), Explain.UnreadData, Topics.Alac));
        }

        return children;
    }

    private static string TagValue(int tag) => tag switch
    {
        ElementTags.SingleChannel => "single channel",
        ElementTags.ChannelPair => "channel pair",
        ElementTags.Coupling => "coupling channel",
        ElementTags.Lfe => "LFE",
        ElementTags.DataStream => "data stream",
        ElementTags.ProgramConfig => "program config",
        ElementTags.Fill => "fill",
        _ => "end",
    };

    private static string TagDescription(int tag) => tag switch
    {
        ElementTags.SingleChannel => Explain.SingleChannelElement,
        ElementTags.ChannelPair => Explain.ChannelPairElement,
        ElementTags.Coupling => Explain.CouplingElement,
        ElementTags.Lfe => Explain.LfeElement,
        ElementTags.DataStream => Explain.DataStreamElement,
        ElementTags.ProgramConfig => Explain.ProgramConfigElement,
        ElementTags.Fill => Explain.FillElement,
        _ => Explain.EndElement,
    };

    private static Node ElementNode(Records r, FieldMap map, in ElementRecord e)
    {
        int tag = e.Tag;
        long start = e.Start;
        NodeValue? value = ElementTags.IsAudio(tag) && e.IsHeaderRead ? NodeValue.Integer(e.Samples, "samples", "sample")
            : tag is ElementTags.DataStream or ElementTags.Fill ? NodeValue.Integer(e.Samples, "bytes", "byte") : null;
        Node element = map.Bits(ElementTags.Name(tag), start, e.End, value, TagDescription(tag), Topics.Alac);
        element.Add(map.Bits("Element Tag", start, start + 3, TagValue(tag), Explain.ElementTag, Topics.Alac));
        if (ElementTags.IsAudio(tag))
        {
            Audio(r, map, in e, element);
        }
        else if (tag == ElementTags.DataStream)
        {
            DataStream(map, in e, element);
        }
        else if (tag == ElementTags.Fill)
        {
            Fill(map, in e, element);
        }

        return element;
    }

    private static void Audio(Records r, FieldMap map, in ElementRecord e, Node element)
    {
        long start = e.Start;
        if (e.End < start + 19)
        {
            // The element ends inside its header.
            return;
        }

        element.Add(map.Bits("Instance Tag", start + 3, start + 7, NodeValue.Integer(e.Instance), Explain.InstanceTag, Topics.Alac));
        element.Add(map.Bits("Reserved Bits", start + 7, start + 19, e.AreReservedBitsZero ? NodeValue.Integer(0) : "not zero", e.AreReservedBitsZero ? Explain.ReservedBits : Explain.ReservedBitsSet, Topics.Alac));
        if (e.End < start + 23)
        {
            return;
        }

        element.Add(map.Bits("IsPartialFrame", start + 19, start + 20, NodeValue.Boolean(e.IsPartial), e.IsPartial ? Explain.PartialFrame : Explain.WholeFrame, Topics.Alac));
        element.Add(map.Bits("Bytes Shifted", start + 20, start + 22, NodeValue.Integer(e.BytesShifted, "bytes", "byte"), e.BytesShifted switch
        {
            0 => Explain.NoBytesShifted,
            3 => Explain.BytesShiftedInvalid,
            _ => Explain.BytesShifted,
        }, Topics.WastedBits));
        element.Add(map.Bits("IsUncompressed", start + 22, start + 23, NodeValue.Boolean(e.IsUncompressed), e.IsUncompressed ? Explain.Uncompressed : Explain.Compressed, Topics.Alac));
        long at = start + 23;
        if (e.IsPartial && e.End >= at + 32)
        {
            element.Add(map.Bits("Sample Count", at, at + 32, NodeValue.Integer(e.Samples, "samples", "sample"), Explain.SampleCount, Topics.Alac));
            at += 32;
        }

        int channels = ElementTags.Channels(e.Tag);
        if (e.IsUncompressed)
        {
            if (e.End > at)
            {
                element.Add(map.Bits("Uncompressed Samples", at, e.End, NodeValue.Integer(e.Samples, "samples", "sample"), Explain.UncompressedSamples, Topics.Alac));
            }

            return;
        }

        if (e.End < at + 16)
        {
            return;
        }

        element.Add(map.Bits("Mix Bits", at, at + 8, NodeValue.Integer(e.MixBits, "bits", "bit"), channels == 2 ? Explain.MixBits : Explain.MixBitsMono, Topics.Prediction));
        element.Add(map.Bits("Mix Resolution", at + 8, at + 16, NodeValue.Integer(e.MixResolution), channels == 2 ? (e.MixResolution != 0 ? Explain.MixResolutionMixed : Explain.MixResolutionPlain) : Explain.MixResolutionMono, Topics.Prediction));
        at += 16;
        ReadOnlySpan<string> names = ["First Channel", "Second Channel"];
        for (int channel = 0; channel < channels; channel++)
        {
            ChannelRecord c = channel == 0 ? e.First : e.Second;
            long parametersEnd = at + 16 + (c.Order * 16L);
            if (e.End < at + 16)
            {
                return;
            }

            string name = names[channel];
            bool isComplete = e.End >= parametersEnd;
            Node predictor = map.Bits($"{name} Predictor", at, Math.Min(parametersEnd, e.End), NodeValue.Integer(c.Order, "coefficients", "coefficient"), Explain.Predictor, Topics.Prediction);
            predictor.Add(map.Bits("Prediction Type", at, at + 4, NodeValue.Integer(c.PredictionType), c.PredictionType == 0 ? Explain.PredictionTypePlain : Explain.PredictionTypeFirstOrder, Topics.Prediction));
            predictor.Add(map.Bits("Predictor Shift", at + 4, at + 8, NodeValue.Integer(c.PredictorShift, "bits", "bit"), Explain.PredictorShift, Topics.Prediction));
            predictor.Add(map.Bits("History Weight Factor", at + 8, at + 11, NodeValue.Integer(c.WeightFactor), Explain.WeightFactor, Topics.AdaptiveGolomb));
            predictor.Add(map.Bits("Predictor Order", at + 11, at + 16, NodeValue.Integer(c.Order), Explain.PredictorOrder, Topics.Prediction));
            if (c.Order > 0 && isComplete)
            {
                ReadOnlySpan<byte> stored = r.Store.Span(c.CoefficientsAt, c.Order * 2);
                var list = new List<string>(c.Order);
                for (int i = 0; i < c.Order; i++)
                {
                    list.Add(BinaryPrimitives.ReadInt16BigEndian(stored[(i * 2)..]).ToString(CultureInfo.InvariantCulture));
                }

                predictor.Add(map.Bits("Predictor Coefficients", at + 16, parametersEnd, string.Join(", ", list), Explain.PredictorCoefficients, Topics.Prediction));
            }

            element.Add(predictor);
            at = parametersEnd;
            if (!isComplete)
            {
                return;
            }
        }

        long samples = e.Samples;
        long shiftBits = (long)e.BytesShifted * 8 * samples * channels;
        if (shiftBits > 0 && e.BytesShifted != 3)
        {
            long shiftEnd = Math.Min(at + shiftBits, e.End);
            element.Add(map.Bits("Shifted Bits", at, shiftEnd, NodeValue.Integer(shiftBits, "bits", "bit"), Explain.ShiftedBits, Topics.WastedBits));
            at += shiftBits;
        }

        for (int channel = 0; channel < channels; channel++)
        {
            ChannelRecord c = channel == 0 ? e.First : e.Second;
            if (c.ResidualEnd <= at && !(c.ResidualEnd == at && samples == 0))
            {
                break;
            }

            Node residuals = map.Bits($"{names[channel]} Residuals", at, c.ResidualEnd, NodeValue.Integer(samples, "samples", "sample"), Explain.Residuals, Topics.AdaptiveGolomb);
            residuals.Add(map.Bits("Zero Runs", at, c.ResidualEnd, NodeValue.Integer(c.ZeroRuns, "runs", "run"), Explain.ZeroRuns, Topics.AdaptiveGolomb));
            residuals.Add(map.Bits("Long Codes", at, c.ResidualEnd, NodeValue.Integer(c.LongCodes, "codes", "code"), Explain.LongCodes, Topics.AdaptiveGolomb));
            element.Add(residuals);
            at = c.ResidualEnd;
        }
    }

    private static void DataStream(FieldMap map, in ElementRecord e, Node element)
    {
        long start = e.Start;
        if (e.End < start + 16)
        {
            return;
        }

        bool isAligned = (e.Flags & ElementFlags.ByteAligned) != 0;
        element.Add(map.Bits("Instance Tag", start + 3, start + 7, NodeValue.Integer(e.Instance), Explain.DataStreamInstanceTag, Topics.Alac));
        element.Add(map.Bits("IsByteAligned", start + 7, start + 8, NodeValue.Boolean(isAligned), Explain.DataStreamAligned, Topics.Alac));
        long countEnd = e.Samples >= 255 ? start + 24 : start + 16;
        element.Add(map.Bits("Data Length", start + 8, Math.Min(countEnd, e.End), NodeValue.Integer(e.Samples, "bytes", "byte"), Explain.DataStreamCount, Topics.Alac));
        long dataStart = e.End - (e.Samples * 8L);
        if (dataStart > countEnd)
        {
            element.Add(map.Bits("Padding", countEnd, dataStart, NodeValue.Integer(dataStart - countEnd, "bits", "bit"), Explain.Padding, Topics.Alac));
        }

        if (e.Samples > 0 && dataStart >= countEnd)
        {
            element.Add(map.Bits("Data", dataStart, e.End, NodeValue.Integer(e.Samples, "bytes", "byte"), Explain.DataStreamData, Topics.Alac));
        }
    }

    private static void Fill(FieldMap map, in ElementRecord e, Node element)
    {
        long start = e.Start;
        if (e.End < start + 7)
        {
            return;
        }

        // The count field is 4 bits, or 4 and 8 more when the first is 15: the element is 8 bits longer than the tag, the 4-bit count and the data.
        long countEnd = e.End - start - 7 - (e.Samples * 8L) == 8 ? start + 15 : start + 7;
        element.Add(map.Bits("Fill Count", start + 3, Math.Min(countEnd, e.End), NodeValue.Integer(e.Samples, "bytes", "byte"), Explain.FillCount, Topics.Alac));
        long dataStart = e.End - (e.Samples * 8L);
        if (e.Samples > 0 && dataStart >= countEnd)
        {
            element.Add(map.Bits("Fill Data", dataStart, e.End, NodeValue.Integer(e.Samples, "bytes", "byte"), Explain.FillData, Topics.Alac));
        }
    }
}
