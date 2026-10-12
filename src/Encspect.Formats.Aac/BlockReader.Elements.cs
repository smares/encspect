using System.Text;
using Encspect.Core;

namespace Encspect.Formats.Aac;

internal sealed partial class BlockReader
{
    // Printable text in a payload is shown when it runs at least this long.
    private const int MinimumText = 4;

    // A payload without text longer than this is cut from a skeleton; shorter ones stay, so their
    // nodes show their bytes.
    private const int KeptPayload = 16;

    // coupling_channel_element (ISO/IEC 14496-3 4.4.2): the elements it is coupled to, its
    // domain and gain scale, an individual_channel_stream and the gain element lists, Huffman-coded
    // with the scalefactor codebook.
    private void Coupling(ref AacBits bits, Node? parent, int start)
    {
        Node? n = Scratch();
        try
        {
            Head(n, 2, start);
            int tag = (int)bits.Read(4);
            Field(n, "Element Instance Tag", start + 3, start + 7, NodeValue.Integer(tag), Explain.ElementInstanceTag);
            int at = bits.Original;
            bool independent = bits.ReadBit();
            Field(n, "IsIndependentlySwitched", at, at + 1, NodeValue.Boolean(independent), Explain.CceIndependentlySwitched);
            int coupled = (int)bits.Read(3) + 1;
            Field(n, "Coupled Elements", at + 1, at + 4, NodeValue.Integer(coupled), Explain.CceCoupledElements);
            int lists = 0;
            for (int c = 0; c < coupled; c++)
            {
                int targetStart = bits.Original;
                lists++;
                bool pair = bits.ReadBit();
                int targetTag = (int)bits.Read(4);
                string channels = string.Empty;
                if (pair)
                {
                    bool left = bits.ReadBit();
                    bool right = bits.ReadBit();
                    lists += left && right ? 1 : 0;
                    channels = left && right ? ", both channels" : left ? ", left channel" : right ? ", right channel" : ", no channel";
                }

                Field(n, "Target", targetStart, bits.Original, n is null ? null : $"{(pair ? "CPE" : "SCE")} {targetTag}{channels}", Explain.CceTarget);
            }

            at = bits.Original;
            int domain = (int)bits.Read(1);
            Field(n, "Coupling Domain", at, at + 1, domain == 0 ? "0 (before TNS)" : "1 (after TNS)", Explain.CceDomain);
            int sign = (int)bits.Read(1);
            Field(n, "Gain Element Sign", at + 1, at + 2, NodeValue.Integer(sign), Explain.CceGainSign);
            int scale = (int)bits.Read(2);
            Field(n, "Gain Element Scale", at + 2, at + 4, NodeValue.Integer(scale), Explain.CceGainScale);
            _lastChannelElement = 2;
            _lastChannelTag = tag;
            Ics ics = _ics[0];
            ChannelStream(ref bits, n, ics, false, 0, "Channel", bits.Original);
            int gainsStart = bits.Original;
            int length;
            if (bits.IsSkeleton)
            {
                length = lists > 1 ? Cut(ref bits, CutKind.Gains) : 0;
            }
            else
            {
                for (int c = 1; c < lists; c++)
                {
                    bool common = independent || bits.ReadBit();
                    if (common)
                    {
                        Codebooks.Scalefactor.Decode(ref bits);
                        continue;
                    }

                    for (int g = 0; g < ics.Groups; g++)
                    {
                        for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
                        {
                            if (ics.Codebooks[(g * MaxBands) + sfb] != 0)
                            {
                                Codebooks.Scalefactor.Decode(ref bits);
                            }
                        }
                    }
                }

                length = bits.Original - gainsStart;
                if (lists > 1)
                {
                    _cuts.Add(new BlockCut(CutKind.Gains, gainsStart, length, -1));
                }
            }

            if (lists > 1)
            {
                Field(n, "Gain Element Lists", gainsStart, gainsStart + length, NodeValue.Integer(lists - 1), Explain.CceGainLists);
            }

            _elements.Add(new ElementSpan(2, start, bits.Original, -1));
            if (_setup.ChannelConfiguration > 0)
            {
                Soft("aac.coupling-without-program",
                    $"a coupling channel element, which channel configuration {_setup.ChannelConfiguration} does not provide for: fdk-aac's decoder drops such access units",
                    start, start + 3, "Element ID");
            }

            if (parent is not null)
            {
                Close(parent, n!, "CCE", start, bits.Original, $"coupling channel element, tag {tag}", Explain.CceElement, Topics.RawDataBlocks);
            }
        }
        catch (Exception e) when (n is not null && e is BlockError or BlockOverrun)
        {
            throw Fail(e, parent, n, "CCE", start, bits, "coupling channel element", Explain.CceElement);
        }
    }

    // data_stream_element (ISO/IEC 14496-3 4.4.2): bytes for the application, which
    // decoders skip, byte-aligned to the raw data block where its flag says so.
    private void DataStream(ref AacBits bits, Node? parent, int start)
    {
        Node? n = Scratch();
        try
        {
            Head(n, 4, start);
            int tag = (int)bits.Read(4);
            Field(n, "Element Instance Tag", start + 3, start + 7, NodeValue.Integer(tag), Explain.ElementInstanceTag);
            bool aligned = bits.ReadBit();
            Field(n, "IsByteAligned", start + 7, start + 8, NodeValue.Boolean(aligned), Explain.DseByteAligned);
            int count = (int)bits.Read(8);
            Field(n, "Count", start + 8, start + 16, NodeValue.Integer(count, "bytes"), Explain.DseCount);
            if (count == 255)
            {
                int extra = (int)bits.Read(8);
                Field(n, "Escape Count", start + 16, start + 24, NodeValue.Integer(extra), Explain.DseEscapeCount);
                count += extra;
            }

            if (aligned)
            {
                bits.Align(_anchor);
            }

            Payload(ref bits, n, count * 8, "Data", Explain.DseData);
            _elements.Add(new ElementSpan(4, start, bits.Original, -1));
            if (_tally is not null)
            {
                _tally.DataStreamBytes += count;
            }

            if (parent is not null)
            {
                Close(parent, n!, "DSE", start, bits.Original, $"data stream element, tag {tag}, {count} bytes", Explain.DataStreamElement, Topics.RawDataBlocks);
            }
        }
        catch (Exception e) when (n is not null && e is BlockError or BlockOverrun)
        {
            throw Fail(e, parent, n, "DSE", start, bits, "data stream element", Explain.DataStreamElement);
        }
    }

    // A payload of bytes: the bytes and any text they hold. A long one without text is cut; every
    // payload notes a cut, empty where it is kept, so a skeleton knows which it was.
    private void Payload(ref AacBits bits, Node? n, int length, string name, string description, bool inBytes = true)
    {
        int start = bits.Original;
        bool kept = true;
        if (bits.IsSkeleton)
        {
            (_, int cut, _) = bits.TakeCut();
            kept = cut == 0;
        }

        if (kept)
        {
            bits.Skip(length);
            (int textStart, int textLength) = FindText(ref bits, start, start + length);
            if (textLength > 0)
            {
                string text = TextAt(ref bits, textStart, textLength);
                n?.Add(_where.Bits(name, start, start + length, inBytes ? NodeValue.Integer(length / 8, "bytes") : NodeValue.Integer(length, "bits"), description));
                n?.Add(_where.Bits("Text", textStart * 8, (textStart + textLength) * 8, text, Explain.ElementText));
                if (_tally is not null)
                {
                    _tally.Texts++;
                    if (_tally.FirstText is null)
                    {
                        _tally.FirstText = text;
                        _tally.FirstTextBlock = _block;
                    }
                }

                if (!bits.IsSkeleton)
                {
                    _cuts.Add(new BlockCut(CutKind.Payload, start, 0, -1));
                }

                return;
            }

            if (!bits.IsSkeleton)
            {
                bool cut = length > KeptPayload * 8;
                _cuts.Add(new BlockCut(CutKind.Payload, start, cut ? length : 0, -1));
            }
        }

        if (n is not null && length > 0)
        {
            n.Add(_where.Bits(name, start, start + length, inBytes ? NodeValue.Integer(length / 8, "bytes") : NodeValue.Integer(length, "bits"), description));
        }
    }

    // The longest run of printable ASCII in the whole bytes of [start, end), counted from the start
    // of the access unit, which encoders that sign their streams write byte-aligned: its first byte
    // and length, or a length of 0.
    private static (int Start, int Length) FindText(ref AacBits bits, int start, int end)
    {
        int first = (start + 7) / 8;
        int last = end / 8;
        int best = -1;
        int bestLength = 0;
        int run = -1;
        for (int i = first; i <= last; i++)
        {
            bool printable = i < last && bits.ByteAt(i * 8) is >= 0x20 and < 0x7F;
            if (printable && run < 0)
            {
                run = i;
            }
            else if (!printable && run >= 0)
            {
                if (i - run > bestLength)
                {
                    (best, bestLength) = (run, i - run);
                }

                run = -1;
            }
        }

        return bestLength >= MinimumText ? (best, bestLength) : (0, 0);
    }

    private static string TextAt(ref AacBits bits, int start, int length)
    {
        var builder = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            builder.Append((char)bits.ByteAt((start + i) * 8));
        }

        return builder.ToString();
    }

    // program_config_element (4.4.1.1) inside a raw data block, where channel configuration 0
    // leaves the layout to it; its comment is byte-aligned to the block.
    private void ProgramConfig(ref AacBits bits, Node? parent, int start)
    {
        Node? n = Scratch();
        try
        {
            Head(n, 5, start);
            int at = bits.Original;
            Field(n, "Element Instance Tag", at, at + 4, NodeValue.Integer(bits.Read(4)), Explain.PceTag);
            long objectType = bits.Read(2);
            Field(n, "Object Type", at + 4, at + 6, n is null ? null : $"{objectType} ({AacTables.ObjectTypeName((int)objectType + 1)})", Explain.PceObjectType);
            long rateIndex = bits.Read(4);
            Field(n, "Sample Rate", at + 6, at + 10, n is null ? null : AacTables.SampleRate((int)rateIndex) is { } rate ? NodeValue.Integer(rate, "Hz") : $"{rateIndex} (reserved)",
                Explain.PceSampleRate);
            int front = (int)bits.Read(4);
            Field(n, "Front Channel Elements", at + 10, at + 14, NodeValue.Integer(front), Explain.FrontElements);
            int side = (int)bits.Read(4);
            Field(n, "Side Channel Elements", at + 14, at + 18, NodeValue.Integer(side), Explain.SideElements);
            int back = (int)bits.Read(4);
            Field(n, "Back Channel Elements", at + 18, at + 22, NodeValue.Integer(back), Explain.BackElements);
            int lfe = (int)bits.Read(2);
            Field(n, "LFE Channel Elements", at + 22, at + 24, NodeValue.Integer(lfe), Explain.LfeElements);
            int data = (int)bits.Read(3);
            Field(n, "Associated Data Elements", at + 24, at + 27, NodeValue.Integer(data), Explain.DataElements);
            int coupling = (int)bits.Read(4);
            Field(n, "Valid Coupling Channel Elements", at + 27, at + 31, NodeValue.Integer(coupling), Explain.CouplingElements);
            Mixdown(ref bits, n, "HasMonoMixdown", Explain.HasMonoMixdown, "Mono Mixdown Element", Explain.MonoMixdown, 4);
            Mixdown(ref bits, n, "HasStereoMixdown", Explain.HasStereoMixdown, "Stereo Mixdown Element", Explain.StereoMixdown, 4);
            int matrix = bits.Original;
            bool hasMatrix = bits.ReadBit();
            Field(n, "HasMatrixMixdown", matrix, matrix + 1, NodeValue.Boolean(hasMatrix), Explain.HasMatrixMixdown);
            if (hasMatrix)
            {
                Field(n, "Matrix Mixdown Index", matrix + 1, matrix + 3, NodeValue.Integer(bits.Read(2)), Explain.MatrixMixdown);
                Field(n, "HasPseudoSurround", matrix + 3, matrix + 4, NodeValue.Boolean(bits.ReadBit()), Explain.PseudoSurround);
            }

            int channels = lfe;
            foreach ((string position, int count) in new[] { ("Front", front), ("Side", side), ("Back", back) })
            {
                for (int i = 0; i < count; i++)
                {
                    int elementStart = bits.Original;
                    bool isPair = bits.ReadBit();
                    int tag = (int)bits.Read(4);
                    channels += isPair ? 2 : 1;
                    Field(n, $"{position} Element", elementStart, elementStart + 5, n is null ? null : $"{(isPair ? "CPE" : "SCE")} {tag}", Explain.PceChannelElement);
                }
            }

            for (int i = 0; i < lfe; i++)
            {
                int tagStart = bits.Original;
                Field(n, "LFE Element Tag", tagStart, tagStart + 4, NodeValue.Integer(bits.Read(4)), Explain.LfeTag);
            }

            for (int i = 0; i < data; i++)
            {
                int tagStart = bits.Original;
                Field(n, "Associated Data Element Tag", tagStart, tagStart + 4, NodeValue.Integer(bits.Read(4)), Explain.DataTag);
            }

            for (int i = 0; i < coupling; i++)
            {
                int elementStart = bits.Original;
                bool independent = bits.ReadBit();
                int tag = (int)bits.Read(4);
                Field(n, "Coupling Element", elementStart, elementStart + 5, n is null ? null : $"CCE {tag}{(independent ? ", independently switched" : string.Empty)}",
                    Explain.CouplingElement);
            }

            bits.Align(_anchor);
            int commentStart = bits.Original;
            int comment = (int)bits.Read(8);
            Field(n, "Comment Length", commentStart, commentStart + 8, NodeValue.Integer(comment, "bytes"), Explain.CommentLength);
            if (comment > 0)
            {
                int textStart = bits.Original;
                bits.Skip(comment * 8);
                if (n is not null)
                {
                    var text = new StringBuilder(comment);
                    for (int i = 0; i < comment; i++)
                    {
                        text.Append((char)bits.ByteAt(textStart + (8 * i)));
                    }

                    n.Add(_where.Bits("Comment", textStart, bits.Original, text.ToString(), Explain.Comment));
                }
            }

            _elements.Add(new ElementSpan(5, start, bits.Original, -1));
            if (parent is not null)
            {
                Close(parent, n!, "PCE", start, bits.Original, $"program config element, {channels} channels", Explain.ProgramConfigElement, Topics.RawDataBlocks);
            }
        }
        catch (Exception e) when (n is not null && e is BlockError or BlockOverrun)
        {
            throw Fail(e, parent, n, "PCE", start, bits, "program config element", Explain.ProgramConfigElement);
        }
    }

    private void Mixdown(ref AacBits bits, Node? n, string flag, string flagDescription, string name, string description, int width)
    {
        int at = bits.Original;
        bool present = bits.ReadBit();
        Field(n, flag, at, at + 1, NodeValue.Boolean(present), flagDescription);
        if (present)
        {
            Field(n, name, at + 1, at + 1 + width, NodeValue.Integer(bits.Read(width)), description);
        }
    }

    // fill_element (ISO/IEC 14496-3 4.4.2.7): a count of bytes, with an escape count where it is 15, then
    // extension payloads until the bytes are used up.
    private void Fill(ref AacBits bits, Node? parent, int start)
    {
        Node? n = Scratch();
        string summary = "fill element";
        try
        {
            Head(n, 6, start);
            int count = (int)bits.Read(4);
            Field(n, "Count", start + 3, start + 7, NodeValue.Integer(count), Explain.FillCount);
            if (count == 15)
            {
                int extra = (int)bits.Read(8);
                Field(n, "Escape Count", start + 7, start + 15, NodeValue.Integer(extra), Explain.FillEscapeCount);
                count += extra - 1;
            }

            int end = bits.Original + (count * 8);
            // A skeleton knows its limit only up to its next cut; the pass that wrote it checked this.
            if (!bits.IsSkeleton && end > bits.OriginalLimit)
            {
                throw new BlockError("aac.element-past-end", $"declares {count} bytes, more than its access unit holds after it", null);
            }

            var types = new List<int>(1);
            int left = count;
            while (left > 0)
            {
                int payloadStart = bits.Original;
                int used = ExtensionPayload(ref bits, n, left, out int type);
                types.Add(type);
                if (used > left)
                {
                    Soft("aac.fill-payload-overrun", $"an extension payload of {used} bytes in a fill element that has {left} bytes left", payloadStart, payloadStart + 4, "Extension Type");
                    bits.MoveTo(end);
                    break;
                }

                left -= used;
                if (bits.Original != end - (left * 8))
                {
                    bits.MoveTo(end - (left * 8));
                }
            }

            if (_tally is not null)
            {
                _tally.FillBytes += count;
            }

            summary = count == 0 ? "fill element, no payload" : $"fill element, {string.Join(", ", types.Select(t => AacTables.ExtensionTypeName(t)))}, {count} bytes";
            _elements.Add(new ElementSpan(6, start, bits.Original, -1));
            if (parent is not null)
            {
                Close(parent, n!, "FIL", start, bits.Original, summary, Explain.FillElement, Topics.RawDataBlocks);
            }
        }
        catch (Exception e) when (n is not null && e is BlockError or BlockOverrun)
        {
            throw Fail(e, parent, n, "FIL", start, bits, summary, Explain.FillElement);
        }
    }

    // extension_payload (ISO/IEC 14496-3 4.4.2.7, 4.5.2.7): its type, then SBR data, dynamic range control, fill
    // data, a data element or other bits. Returns the bytes it takes.
    private int ExtensionPayload(ref AacBits bits, Node? parent, int count, out int type)
    {
        Node? n = Scratch();
        int start = bits.Original;
        type = (int)bits.Read(4);
        Field(n, "Extension Type", start, start + 4, $"{type} ({AacTables.ExtensionTypeName(type)})", Explain.FillExtensionType);
        int end = start + (count * 8);
        int used = count;
        string? topic = null;
        switch (type)
        {
            case 13 or 14:
                SbrPayload(ref bits, n, type == 14, end);
                topic = Topics.Sbr;
                break;
            case 11:
                used = DynamicRange(ref bits, n);
                topic = Topics.DynamicRange;
                break;
            case 1:
                FillData(ref bits, n, count, start);
                break;
            case 2:
                used = DataElement(ref bits, n, count, end);
                break;
            default:
                Payload(ref bits, n, (count * 8) - 4, "Other Bits", Explain.OtherBits, inBytes: false);
                break;
        }

        if (_tally is not null && type < 16)
        {
            _tally.Extensions[type]++;
            _tally.ExtensionBytes[type] += Math.Min(used, count);
        }

        if (parent is not null)
        {
            Close(parent, n!, "Extension Payload", start, bits.Original, $"{AacTables.ExtensionTypeName(type)}, {used} bytes", Explain.ExtensionPayload, topic);
        }

        return used;
    }

    // EXT_FILL_DATA: fill_nibble '0000' and fill bytes '10100101' (4.5.2.7).
    private void FillData(ref AacBits bits, Node? n, int count, int start)
    {
        int nibble = (int)bits.Read(4);
        Field(n, "Fill Nibble", start + 4, start + 8, NodeValue.Integer(nibble), Explain.FillNibble);
        int bytesStart = bits.Original;
        int length = (count - 1) * 8;
        if (bits.IsSkeleton)
        {
            if (length > 0)
            {
                Cut(ref bits, CutKind.Fill);
            }
        }
        else
        {
            bool valid = nibble == 0;
            for (int i = 0; i < count - 1; i++)
            {
                valid &= bits.Read(8) == 0xA5;
            }

            if (length > 0)
            {
                _cuts.Add(new BlockCut(CutKind.Fill, bytesStart, length, -1));
            }

            if (!valid)
            {
                if (_tally is not null)
                {
                    _tally.InvalidFillData++;
                }

                Soft("aac.fill-data-invalid", "fill data other than the fill nibble 0000 and fill bytes 10100101 the specification requires", start, bytesStart + length,
                    "Extension Payload");
            }
        }

        if (length > 0)
        {
            Field(n, "Fill Bytes", bytesStart, bytesStart + length, NodeValue.Integer(count - 1, "bytes"), Explain.FillBytes);
        }
    }

    // EXT_DATA_ELEMENT (4.4.2.7): its version and, for ANC_DATA, the length in bytes of 255 or less
    // each and the data bytes. Returns the bytes it takes.
    private int DataElement(ref AacBits bits, Node? n, int count, int end)
    {
        int at = bits.Original;
        int version = (int)bits.Read(4);
        Field(n, "Data Element Version", at, at + 4, version == 0 ? "0 (ANC_DATA)" : $"{version} (reserved)", Explain.DataElementVersion);
        if (version != 0)
        {
            Payload(ref bits, n, end - bits.Original, "Other Bits", Explain.OtherBits, inBytes: false);
            return count;
        }

        int lengthStart = bits.Original;
        int length = 0;
        int loops = 0;
        int part;
        do
        {
            part = (int)bits.Read(8);
            length += part;
            loops++;
        }
        while (part == 255);

        Field(n, "Data Element Length", lengthStart, bits.Original, NodeValue.Integer(length, "bytes"), Explain.DataElementLength);
        if (_tally is not null)
        {
            _tally.AncillaryElements++;
            _tally.AncillaryBytes += length;
        }

        Payload(ref bits, n, length * 8, "Data", Explain.AncillaryData);
        return length + loops + 1;
    }

    // dynamic_range_info (ISO/IEC 14496-3 4.4.2.7, with excluded_channels): the program it applies to, the
    // channels it leaves out, the bands it divides the spectrum into, the program's reference level
    // and per band a gain change in 0.25 dB steps (4.5.2.7). Returns the bytes it takes, its type
    // nibble's byte included.
    private int DynamicRange(ref AacBits bits, Node? n)
    {
        int bytes = 1;
        int bands = 1;
        int at = bits.Original;
        bool pce = bits.ReadBit();
        Field(n, "HasPceTag", at, at + 1, NodeValue.Boolean(pce), Explain.DrcHasPceTag);
        if (pce)
        {
            Field(n, "PCE Instance Tag", bits.Original, bits.Original + 4, NodeValue.Integer(bits.Read(4)), Explain.DrcPceTag);
            Field(n, "DRC Tag Reserved Bits", bits.Original, bits.Original + 4, NodeValue.Integer(bits.Read(4)), Explain.DrcReserved);
            bytes++;
        }

        at = bits.Original;
        bool excluded = bits.ReadBit();
        Field(n, "HasExcludedChannels", at, at + 1, NodeValue.Boolean(excluded), Explain.DrcHasExcluded);
        if (excluded)
        {
            int excludedStart = bits.Original;
            int masks = 0;
            bool more;
            do
            {
                bits.Skip(7);
                more = bits.ReadBit();
                masks++;
                bytes++;
            }
            while (more);

            Field(n, "Excluded Channels", excludedStart, bits.Original, NodeValue.Integer(masks * 7, "channel flags"), Explain.DrcExcluded);
        }

        at = bits.Original;
        bool hasBands = bits.ReadBit();
        Field(n, "HasDrcBands", at, at + 1, NodeValue.Boolean(hasBands), Explain.DrcHasBands);
        if (hasBands)
        {
            int increment = (int)bits.Read(4);
            Field(n, "DRC Band Increment", at + 1, at + 5, NodeValue.Integer(increment), Explain.DrcBandIncrement);
            Field(n, "DRC Interpolation Scheme", at + 5, at + 9, NodeValue.Integer(bits.Read(4)), Explain.DrcInterpolation);
            bytes++;
            bands += increment;
            for (int i = 0; i < bands; i++)
            {
                int top = bits.Original;
                Field(n, "DRC Band Top", top, top + 8, NodeValue.Integer(bits.Read(8)), Explain.DrcBandTop);
                bytes++;
            }
        }

        at = bits.Original;
        bool reference = bits.ReadBit();
        Field(n, "HasProgramReferenceLevel", at, at + 1, NodeValue.Boolean(reference), Explain.DrcHasReference);
        if (reference)
        {
            int level = (int)bits.Read(7);
            Field(n, "Program Reference Level", at + 1, at + 8, n is null ? null : $"{level} ({-level * 0.25:0.00} dB)", Explain.DrcReferenceLevel);
            Field(n, "Program Reference Level Reserved Bit", at + 8, at + 9, NodeValue.Integer(bits.Read(1)), Explain.DrcReserved);
            bytes++;
        }

        for (int i = 0; i < bands; i++)
        {
            int gainStart = bits.Original;
            bool negative = bits.ReadBit();
            int control = (int)bits.Read(7);
            Field(n, "Dynamic Range", gainStart, gainStart + 8, n is null ? null : $"{(negative ? "-" : "+")}{control * 0.25:0.00} dB", Explain.DrcGain);
            bytes++;
        }

        if (_tally is not null)
        {
            _tally.DynamicRangeBands += bands;
        }

        return bytes;
    }

    // An SBR extension payload (ISO/IEC 14496-3 4.4.2, sbr_extension_data): a CRC where its type says, whether an
    // SBR header follows and the header, then sbr_data for the channel element before the fill
    // element, read where a header with usable frequency tables is known; a skeleton cuts the data.
    private void SbrPayload(ref AacBits bits, Node? n, bool crc, int end)
    {
        _hasSbr = true;
        if (crc)
        {
            int at = bits.Original;
            Field(n, "SBR CRC", at, at + 10, NodeValue.Hex(bits.Read(10), 3), Explain.SbrCrc);
        }

        int flag = bits.Original;
        bool hasHeader = bits.ReadBit();
        Field(n, "HasSbrHeader", flag, flag + 1, NodeValue.Boolean(hasHeader), Explain.HasSbrHeader, Topics.Sbr);
        int key = (_lastChannelElement << 4) | _lastChannelTag;
        SbrHeader? header = null;
        if (hasHeader)
        {
            header = ReadSbrHeader(ref bits, n);
            if (bits.Original > end)
            {
                Soft("aac.sbr-past-fill", "SBR data that runs past its fill element", flag, end, "HasSbrHeader");
                bits.MoveTo(end);
                return;
            }
        }

        int dataStart = bits.Original;
        int length = end - dataStart;
        int psAt = -1;
        if (bits.IsSkeleton)
        {
            if (length > 0)
            {
                (_, int cutLength, int extra) = bits.TakeCut();
                length = cutLength;
                psAt = extra < 0 ? -1 : dataStart + extra;
            }
        }
        else
        {
            if (_tally is not null)
            {
                psAt = TallySbr(ref bits, key, header, dataStart, end, n);
            }

            if (length > 0)
            {
                _cuts.Add(new BlockCut(CutKind.Sbr, dataStart, length, psAt < 0 ? -1 : psAt - dataStart));
            }
        }

        if (psAt >= 0)
        {
            _hasPs = true;
        }

        if (n is not null && length > 0)
        {
            Node data = _where.Bits("SBR Data", dataStart, dataStart + length, NodeValue.Integer(length, "bits"), Explain.SbrData, Topics.Sbr);
            if (psAt >= 0)
            {
                data.Add(_where.Bits("Parametric Stereo Extension", psAt, psAt + 2, "2 (EXT_ID_PS)", Explain.PsExtension, Topics.ParametricStereo));
            }

            n.Add(data);
        }

        bits.MoveTo(dataStart + length);
    }

    // Reads the payload's sbr_data in the pass that counts the coding, with the element's header:
    // returns where its parametric stereo extension's ID lies, or -1.
    private int TallySbr(ref AacBits bits, int key, SbrHeader? header, int dataStart, int end, Node? n)
    {
        CodingTally tally = _tally!;
        if (header is not null)
        {
            tally.SbrHeaders++;
            if (!_sbr.TryGetValue(key, out SbrHeader? previous) || !previous.SameFrequencies(header))
            {
                (SbrBands? bands, string? error) = Sbr.Derive(header, _setup.SbrSampleRate);
                header = header with { Bands = bands, BandError = error };
            }
            else
            {
                header = header with { Bands = previous.Bands, BandError = previous.BandError };
            }

            if (tally.FirstHeader is null)
            {
                tally.FirstHeader = header;
            }
            else if (!tally.FirstHeader.SameFrequencies(header) || tally.FirstHeader.AmpRes != header.AmpRes)
            {
                tally.SbrHeaderChanged = true;
            }

            _sbr[key] = header;
        }

        if (_lastChannelElement is not (0 or 1 or 2))
        {
            Soft("aac.sbr-without-channel-element", "SBR data that follows no single channel or channel pair element", dataStart, end, "SBR Data");
            tally.SbrUnread++;
            return -1;
        }

        if (!_sbr.TryGetValue(key, out SbrHeader? current))
        {
            Soft("aac.sbr-before-header", "SBR data before the first SBR header of its element, which decoders cannot read", dataStart, end, "SBR Data");
            tally.SbrUnread++;
            return -1;
        }

        if (current.Bands is null)
        {
            Soft("aac.sbr-header-invalid", $"an SBR header whose frequency tables cannot be built: {current.BandError}", dataStart, end, "SBR Data");
            tally.SbrUnread++;
            return -1;
        }

        var sub = bits.Window(dataStart, end);
        try
        {
            (int ps, string? error) = Sbr.ReadData(ref sub, current, _lastChannelElement == 1);
            if (error is not null)
            {
                Soft("aac.sbr-data-invalid", $"SBR data with {error}", dataStart, end, "SBR Data");
                tally.SbrUnread++;
                return -1;
            }

            return ps;
        }
        catch (BlockOverrun)
        {
            Soft("aac.sbr-past-fill", "SBR data that runs past its fill element", dataStart, end, "SBR Data");
            tally.SbrUnread++;
            return -1;
        }
    }

    // sbr_header (ISO/IEC 14496-3 4.4.2), with the defaults of 4.5.2 for the fields it leaves out.
    private SbrHeader ReadSbrHeader(ref AacBits bits, Node? parent)
    {
        Node? n = Scratch();
        int start = bits.Original;
        int ampRes = (int)bits.Read(1);
        Field(n, "bs_amp_res", start, start + 1, ampRes == 0 ? "0 (1.5 dB)" : "1 (3 dB)", Explain.SbrAmpRes);
        int startFrequency = (int)bits.Read(4);
        Field(n, "bs_start_freq", start + 1, start + 5, NodeValue.Integer(startFrequency), Explain.SbrStartFreq);
        int stopFrequency = (int)bits.Read(4);
        Field(n, "bs_stop_freq", start + 5, start + 9, NodeValue.Integer(stopFrequency), Explain.SbrStopFreq);
        int xover = (int)bits.Read(3);
        Field(n, "bs_xover_band", start + 9, start + 12, NodeValue.Integer(xover), Explain.SbrXoverBand);
        Field(n, "bs_reserved", start + 12, start + 14, NodeValue.Integer(bits.Read(2)), Explain.SbrReserved);
        bool extra1 = bits.ReadBit();
        Field(n, "HasHeaderExtra1", start + 14, start + 15, NodeValue.Boolean(extra1), Explain.SbrHeaderExtra1);
        bool extra2 = bits.ReadBit();
        Field(n, "HasHeaderExtra2", start + 15, start + 16, NodeValue.Boolean(extra2), Explain.SbrHeaderExtra2);
        var header = new SbrHeader { AmpRes = ampRes, StartFrequency = startFrequency, StopFrequency = stopFrequency, XoverBand = xover };
        if (extra1)
        {
            int at = bits.Original;
            header = header with { FrequencyScale = (int)bits.Read(2), AlterScale = (int)bits.Read(1), NoiseBands = (int)bits.Read(2) };
            Field(n, "bs_freq_scale", at, at + 2, NodeValue.Integer(header.FrequencyScale), Explain.SbrFreqScale);
            Field(n, "bs_alter_scale", at + 2, at + 3, NodeValue.Integer(header.AlterScale), Explain.SbrAlterScale);
            Field(n, "bs_noise_bands", at + 3, at + 5, NodeValue.Integer(header.NoiseBands), Explain.SbrNoiseBands);
        }

        if (extra2)
        {
            int at = bits.Original;
            header = header with { LimiterBands = (int)bits.Read(2), LimiterGains = (int)bits.Read(2), InterpolatedFrequency = (int)bits.Read(1), SmoothingMode = (int)bits.Read(1) };
            Field(n, "bs_limiter_bands", at, at + 2, NodeValue.Integer(header.LimiterBands), Explain.SbrLimiterBands);
            Field(n, "bs_limiter_gains", at + 2, at + 4, NodeValue.Integer(header.LimiterGains), Explain.SbrLimiterGains);
            Field(n, "bs_interpol_freq", at + 4, at + 5, NodeValue.Integer(header.InterpolatedFrequency), Explain.SbrInterpolFreq);
            Field(n, "bs_smoothing_mode", at + 5, at + 6, NodeValue.Integer(header.SmoothingMode), Explain.SbrSmoothingMode);
        }

        if (parent is not null)
        {
            Close(parent, n!, "SBR Header", start, bits.Original, $"start {startFrequency}, stop {stopFrequency}, crossover {xover}", Explain.SbrHeader, Topics.Sbr);
        }

        return header;
    }

    // --- Nodes -----------------------------------------------------------------------------------

    private Node? Scratch() => _nodes ? new Node("Scratch", 0, 0) : null;

    private Node Close(Node parent, Node scratch, string name, int start, int end, NodeValue? value, string description, string? topic = null, NodeKind kind = NodeKind.Detail)
    {
        Node node = _where.Bits(name, start, end, value, description, topic, kind);
        foreach (Node child in scratch.Children)
        {
            node.Add(child);
        }

        parent.Add(node);
        return node;
    }

    private void Field(Node? parent, string name, int start, int end, NodeValue? value, string description, string? topic = null)
    {
        parent?.Add(_where.Bits(name, start, end, value, description, topic));
    }

    // A field of an integer value, whose value is only made where nodes are built.
    private void Int(Node? parent, string name, int start, int end, long value, string description, string? unit = null)
    {
        parent?.Add(_where.Bits(name, start, end, NodeValue.Integer(value, unit), description));
    }

    private void Head(Node? n, int id, int start)
    {
        if (n is not null)
        {
            n.Add(_where.Bits("Element ID", start, start + 3, $"{id} ({AacTables.ElementName(id)})", Explain.ElementId));
        }
    }

    // Closes a container that reading broke off in: its node runs to where reading stopped, or to the
    // end of the data where it ran past it, and becomes the error's node unless a deeper one is.
    private Exception Fail(Exception e, Node? parent, Node scratch, string name, int start, AacBits bits, NodeValue? value, string description, NodeKind kind = NodeKind.Detail)
    {
        BlockError error = e as BlockError ?? new BlockError("aac.element-past-end", "runs past the end of its access unit", null, toEnd: true);
        int end = error.ToEnd ? bits.OriginalLimit : Math.Max(bits.Original, start);
        if (parent is not null)
        {
            Node node = Close(parent, scratch, name, start, end, value, description, kind: kind);
            error.Node ??= node;
        }

        error.Element ??= name;
        return error;
    }

    private void Soft(string code, string what, int start, int end, string? nodeName)
    {
        if (_tally is null)
        {
            return;
        }

        _soft.Add(new SoftFinding(code, what, nodeName is null ? null : _where.Bits(nodeName, start, end, null, string.Empty)));
    }

    // The bands of codebook book (13 noise substitution, 14 and 15 intensity stereo) of a channel, at
    // its section data.
    private void BandsNode(Node n, Ics ics, int book, string name, string description, string topic)
    {
        if (!HasBand(ics, b => b == book))
        {
            return;
        }

        n.Add(_where.Bits(name, ics.SectionStart, ics.SectionEnd, BandList(ics, (g, sfb) => ics.Codebooks[(g * MaxBands) + sfb] == book), description, topic));
    }

    // The intensity stereo bands of a pair's second channel, in phase (INTENSITY_HCB, 15) or out of
    // phase (INTENSITY_HCB2, 14), where ms_used inverts the sign (4.6.8.2.3).
    private void IntensityNode(Node n, Ics ics, bool masked)
    {
        if (!HasBand(ics, b => b is 14 or 15))
        {
            return;
        }

        bool OutOfPhase(int g, int sfb) => (ics.Codebooks[(g * MaxBands) + sfb] == 14) != (masked && _midSide[(g * MaxBands) + sfb]);
        string inPhase = BandList(ics, (g, sfb) => ics.Codebooks[(g * MaxBands) + sfb] is 14 or 15 && !OutOfPhase(g, sfb));
        string outOfPhase = BandList(ics, (g, sfb) => ics.Codebooks[(g * MaxBands) + sfb] is 14 or 15 && OutOfPhase(g, sfb));
        string value = inPhase == "none" ? $"{outOfPhase} out of phase" : outOfPhase == "none" ? $"{inPhase} in phase" : $"{inPhase} in phase; {outOfPhase} out of phase";
        Node channel = n.Children[^1];
        channel.Add(_where.Bits("Intensity Stereo Bands", ics.SectionStart, ics.SectionEnd, value, Explain.IntensityBands, Topics.Intensity));
    }

    private static bool HasBand(Ics ics, Func<int, bool> test)
    {
        for (int g = 0; g < ics.Groups; g++)
        {
            for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
            {
                if (test(ics.Codebooks[(g * MaxBands) + sfb]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The bands a test picks, as ranges ("0-3, 7"), per window group where there are several.
    private static string BandList(Ics ics, Func<int, int, bool> test)
    {
        var parts = new List<string>();
        for (int g = 0; g < ics.Groups; g++)
        {
            var ranges = new List<string>();
            int run = -1;
            for (int sfb = 0; sfb <= ics.MaxSfb; sfb++)
            {
                bool on = sfb < ics.MaxSfb && test(g, sfb);
                if (on && run < 0)
                {
                    run = sfb;
                }
                else if (!on && run >= 0)
                {
                    ranges.Add(run == sfb - 1 ? $"{run}" : $"{run}-{sfb - 1}");
                    run = -1;
                }
            }

            if (ranges.Count > 0)
            {
                parts.Add((ics.Groups > 1 ? $"group {g}: " : string.Empty) + string.Join(", ", ranges));
            }
        }

        return parts.Count == 0 ? "none" : string.Join("; ", parts);
    }

    private static string FlagList(uint flags, int count)
    {
        var ranges = new List<string>();
        int run = -1;
        for (int i = 0; i <= Math.Min(count, 32); i++)
        {
            bool on = i < Math.Min(count, 32) && ((flags >> i) & 1) != 0;
            if (on && run < 0)
            {
                run = i;
            }
            else if (!on && run >= 0)
            {
                ranges.Add(run == i - 1 ? $"{run}" : $"{run}-{i - 1}");
                run = -1;
            }
        }

        return ranges.Count == 0 ? "none" : string.Join(", ", ranges);
    }

    private static string GroupText(Ics ics) =>
        ics.Groups == 1 ? "one group of 8 windows" : $"{ics.Groups} groups of {string.Join(", ", ics.GroupLengths.Take(ics.Groups))} windows";

    public static string WindowSequenceName(int sequence) => sequence switch
    {
        0 => "only long",
        1 => "long start",
        2 => "eight short",
        _ => "long stop",
    };

    private static string WindowSequenceText(int sequence) => $"{sequence} ({WindowSequenceName(sequence)})";

    private static string MaskText(int mask) => mask switch
    {
        0 => "0 (no mid/side)",
        1 => "1 (per band)",
        2 => "2 (all bands)",
        _ => "3 (reserved)",
    };

    // What each codebook number codes (4.6.3): the dimension, signedness and largest absolute value of
    // codebooks 1 to 11, and the band types 13 to 15.
    public static string CodebookName(int book) => book switch
    {
        0 => "zero",
        1 or 2 => "signed quadruples up to 1",
        3 or 4 => "unsigned quadruples up to 2",
        5 or 6 => "signed pairs up to 4",
        7 or 8 => "unsigned pairs up to 7",
        9 or 10 => "unsigned pairs up to 12",
        11 => "unsigned pairs up to 16, which escapes to larger values",
        12 => "reserved",
        13 => "noise substitution",
        14 => "intensity stereo, out of phase",
        _ => "intensity stereo, in phase",
    };
}
