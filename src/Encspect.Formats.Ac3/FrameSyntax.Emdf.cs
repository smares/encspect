using Encspect.Core;

namespace Encspect.Formats.Ac3;

// Object audio in a frame: the EMDF containers (ETSI TS 102 366 Annex H) that the auxiliary data
// and the skip fields may hold, and the JOC and OAMD payloads of ETSI TS 103 420 inside them. The
// containers are found by their sync word and read field by field; the JOC header and the OAMD
// object count are the only payload fields read, so nothing is decoded.
internal sealed partial class FrameSyntax
{
    internal const int EmdfSync = 0x5838;

    // The smallest container: sync word and length, version, key ID, the end marker and the
    // shortest protection data.
    private const int MinEmdfBits = 54;
    private const int MaxEmdfContainers = 4;
    private const int MaxEmdfPayloads = 32;

    // For each byte, the alignments at which it lies inside the sync word.
    private static readonly byte[] SyncCandidates = BuildCandidates(EmdfSync);

    // Thrown where a finding has been reported already and the container cannot be read on.
    private sealed class EmdfStop : Exception;

    // The analyzer reads at most this many variable_bits groups and payloads; H.2 sets no limit.
    private sealed class EmdfLimit(string message) : Exception(message);

    private int _lastGroups;
    private int _emdfBound;

    private static byte[] BuildCandidates(int pattern)
    {
        var table = new byte[256];
        for (int k = 0; k < 8; k++)
        {
            table[(pattern >> k) & 0xFF] |= (byte)(1 << k);
        }

        return table;
    }

    // Looks for containers in the bits [start, end) of the frame, which a skip field, the auxiliary
    // data or the bits no audio block read hold, and reads each as it is found. A candidate must pass
    // EmdfTest, a dry run of the structure as MediaInfo's Ac3_EMDF_Test makes it, since the sync
    // word's 16 bits occur by chance in data. A frame whose additional bit stream information sets
    // the extension flag announces a container, so one that starts the region, or follows another, is
    // read on its sync word alone and its damage reported, but only where a field starts (a skip field or
    // the bits after the last block read); the bits after where reading stopped, mid-block, start nowhere. What the analysis found is kept in the
    // record (where each starts), so building nodes reads the same containers.
    private void EmdfRegion(int start, int end, bool announced = true)
    {
        if (end - start < MinEmdfBits)
        {
            return;
        }

        int next = FindEmdf(start, end, announced);
        int more = _b.Derived(1, next >= 0 ? 1 : 0);
        for (int count = 0; more != 0 && count < MaxEmdfContainers; count++)
        {
            int at = start + _b.Derived(16, Math.Max(0, next - start));
            int stop = EmdfContainer(at, end);
            next = FindEmdf(stop, end, announced);
            more = _b.Derived(1, count + 1 < MaxEmdfContainers && next >= 0 ? 1 : 0);
        }
    }

    private int FindEmdf(int from, int end, bool announced)
    {
        if (_b.IsBuildingNodes || end - from < MinEmdfBits)
        {
            return -1;
        }

        if (announced && _r.ExtensionFlag == 1 && _b.Peek(from, 16) == EmdfSync)
        {
            return from;
        }

        for (int at = from; at >= 0 && at < end;)
        {
            at = _b.FindPattern16(at, end, EmdfSync, SyncCandidates);
            if (at < 0 || end - at < MinEmdfBits)
            {
                return -1;
            }

            if (EmdfTest(at, end))
            {
                return at;
            }

            at++;
        }

        return -1;
    }

    // Whether a container that starts at a sync word is well formed: its length fits, its version
    // is 0, the payloads reach the end marker inside the length, the protection length is defined,
    // and the fields end exactly at emdf_container_length, after zero padding to the byte boundary (ETSI TS 102 366 H.2).
    private bool EmdfTest(int at, int end)
    {
        int pos = at + 16;
        int limit = end;

        int Take(int width)
        {
            if (pos + width > limit)
            {
                throw new EndOfStreamException();
            }

            int value = _b.Peek(pos, width);
            pos += width;
            return value;
        }

        void Skip(long width)
        {
            if (width < 0 || pos + width > limit)
            {
                throw new EndOfStreamException();
            }

            pos += (int)width;
        }

        int Variable(int n)
        {
            long total = 0;
            for (int group = 1; ; group++)
            {
                total += Take(n);
                if (Take(1) == 0)
                {
                    return (int)Math.Min(total, int.MaxValue);
                }

                if (group == 4)
                {
                    throw new EndOfStreamException();
                }

                total = (total << n) + (1L << n);
            }
        }

        try
        {
            int length = Take(16);
            int body = pos;
            if (body + (length * 8L) > end)
            {
                return false;
            }

            limit = body + (length * 8);
            if (Take(2) != 0)
            {
                return false;
            }

            if (Take(3) == 7)
            {
                Variable(3);
            }

            for (int payload = 0; ; payload++)
            {
                if (payload >= MaxEmdfPayloads)
                {
                    return false;
                }

                int id = Take(5);
                if (id == 0)
                {
                    break;
                }

                if (id == 0x1F)
                {
                    Variable(5);
                }

                bool hasOffset = Take(1) != 0;
                if (hasOffset)
                {
                    Skip(12);
                }

                if (Take(1) != 0)
                {
                    Variable(11);
                }

                if (Take(1) != 0)
                {
                    Variable(2);
                }

                if (Take(1) != 0)
                {
                    Skip(8);
                }

                if (Take(1) == 0)
                {
                    bool aligned = false;
                    if (!hasOffset)
                    {
                        aligned = Take(1) != 0;
                        if (aligned)
                        {
                            Skip(2);
                        }
                    }

                    if (hasOffset || aligned)
                    {
                        Skip(7);
                    }
                }

                Skip(Variable(8) * 8L);
            }

            int primary = Take(2);
            int secondary = Take(2);
            if (primary == 0)
            {
                return false;
            }

            Skip(ProtectionBits(primary));
            Skip(secondary == 0 ? 0 : ProtectionBits(secondary));
            int padding = (8 - ((pos - body) & 7)) & 7;
            return (padding == 0 || Take(padding) == 0) && pos == limit;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private void Need(int width)
    {
        if (_b.Position + width > _emdfBound)
        {
            throw new EndOfStreamException();
        }
    }

    private int E(string name, int width, string description, Func<int, NodeValue>? value = null)
    {
        Need(width);
        return F(name, width, description, value, Topics.ObjectAudio);
    }

    private bool EFlag(string name, string description) => E(name, 1, description, BooleanValue) != 0;

    // variable_bits(n) (ETSI TS 102 366 H.2.1.2.1): groups of n bits, each followed by a bit that
    // asks for another.
    private int VariableBits(string name, int n, string description, Func<int, NodeValue>? value = null)
    {
        int start = _b.Position;
        long total = 0;
        for (int group = 1; ; group++)
        {
            _lastGroups = group;
            Need(n + 1);
            total += _b.Read(n);
            if (_b.Read(1) == 0)
            {
                break;
            }

            if (group == 4)
            {
                throw new EmdfLimit("a variable_bits field asks for a fifth group, where the analyzer reads four");
            }

            total = (total << n) + (1L << n);
        }

        int result = (int)Math.Min(total, int.MaxValue);
        _lastName = name;
        _lastStart = start;
        _lastEnd = _b.Position;
        _t?.Field(name, start, _b.Position, value is null ? NodeValue.Integer(result) : value(result), description, Topics.ObjectAudio);
        return result;
    }

    // One emdf_container (H.2.1.2) at a sync word; returns the bit where it ends, which the next
    // search starts from.
    private int EmdfContainer(int at, int end)
    {
        _b.MoveTo(at);
        int depth = _t?.OpenCount ?? 0;
        _t?.Open("EMDF Container", at, Explain.EmdfContainer, Topics.ObjectAudio);
        _r.EmdfContainers++;
        int stop = end;
        int lengthBytes = 0;
        bool hasJoc = false;
        bool hasOamd = false;
        bool complete = false;
        int declaredEnd = end;
        try
        {
            _emdfBound = end;
            E("Sync Word", 16, Explain.EmdfSync, Hex4);
            lengthBytes = E("Container Length", 16, Explain.EmdfContainerLength, v => NodeValue.Integer(v, "bytes"));
            int body = _b.Position;
            declaredEnd = body + (lengthBytes * 8);
            if (declaredEnd > end)
            {
                Report(Issue.EmdfTruncated, "Container Length", _lastStart, _lastEnd,
                    $"emdf_container_length gives {lengthBytes} bytes, but the {(end - body) / 8} bytes that remain in the field end first");
            }

            _emdfBound = Math.Min(declaredEnd, end);
            int version = E("Version", 2, Explain.EmdfVersion);
            string versionField = "Version";
            if (version == 3)
            {
                version += VariableBits("Version Extension", 2, Explain.EmdfVersion);
                versionField = "Version Extension";
            }

            if (version != 0)
            {
                Report(Issue.EmdfVersion, versionField, _lastStart, _lastEnd, $"emdf_version is {version}; ETSI TS 102 366 H.2.2.2.2 defines 0 only");
                stop = Math.Max(_b.Position, Math.Min(declaredEnd, end));
                _t?.CloseTo(depth, stop);
                _b.MoveTo(stop);
                return stop;
            }

            int keyId = E("Key ID", 3, Explain.EmdfKeyId);
            if (keyId == 7)
            {
                VariableBits("Key ID Extension", 3, Explain.EmdfKeyId);
            }

            bool ended = false;
            for (int payload = 0; payload < MaxEmdfPayloads && !ended; payload++)
            {
                ended = EmdfPayload(declaredEnd, ref hasJoc, ref hasOamd);
                if (_emdfBound < 0)
                {
                    stop = Math.Min(declaredEnd, end);
                    _t?.CloseTo(depth, stop);
                    _b.MoveTo(stop);
                    return stop;
                }
            }

            if (!ended)
            {
                throw new EmdfLimit($"the container holds more than the {MaxEmdfPayloads} payloads the analyzer reads");
            }

            EmdfProtection();
            int fields = _b.Position;
            int padding = (8 - ((fields - body) & 7)) & 7;
            if (padding > 0 && fields + padding <= _emdfBound)
            {
                if (E("Padding", padding, Explain.EmdfPadding) != 0)
                {
                    Report(Issue.EmdfReserved, "Padding", _lastStart, _lastEnd, "the bits after the last field that fill the last byte are not 0 (H.2.2.1.2)");
                }
            }

            complete = true;
            if (_b.Position != declaredEnd && declaredEnd <= end)
            {
                Report(Issue.EmdfLength, "EMDF Container", at, Math.Min(end, Math.Max(declaredEnd, _b.Position)),
                    $"the fields end after {(_b.Position - body + 7) / 8} bytes, but emdf_container_length gives {lengthBytes}");
            }

            stop = Math.Min(end, Math.Max(declaredEnd, _b.Position));
        }
        catch (EndOfStreamException)
        {
            if (declaredEnd <= end)
            {
                Report(Issue.EmdfLength, "EMDF Container", at, declaredEnd,
                    $"the container's fields run past the {lengthBytes} bytes emdf_container_length gives");
            }
            else
            {
                Report(Issue.EmdfTruncated, "EMDF Container", at, end, "the container's fields run past the end of the field that holds it");
            }

            stop = Math.Min(declaredEnd, end);
        }
        catch (EmdfStop)
        {
            stop = Math.Min(declaredEnd, end);
        }
        catch (EmdfLimit limit)
        {
            Report(Issue.EmdfLimit, "EMDF Container", at, Math.Min(declaredEnd, end), limit.Message);
            stop = Math.Min(declaredEnd, end);
        }

        if (complete && hasJoc != hasOamd)
        {
            Report(Issue.ObjectAudioIncomplete, "EMDF Container", at, stop,
                hasJoc ? "it holds a JOC payload but no OAMD payload" : "it holds an OAMD payload but no JOC payload");
        }

        _t?.CloseTo(depth, stop);
        _b.MoveTo(stop);
        return stop;
    }

    // emdf_payload_id, emdf_payload_config and emdf_payload_size with the payload's bytes (H.2.1.2,
    // H.2.1.3); true for the end marker. A payload that runs past the container ends the container
    // (_emdfBound is set to -1).
    private bool EmdfPayload(int declaredEnd, ref bool hasJoc, ref bool hasOamd)
    {
        int start = _b.Position;
        Need(5);
        int id = _b.Read(5);
        if (id == 0)
        {
            _t?.Field("Payload ID", start, _b.Position, "0 (end of payloads)", Explain.EmdfPayloadId, Topics.ObjectAudio);
            return true;
        }

        int bound = _emdfBound;
        _t?.Open("Payload", start, Explain.EmdfPayload, Topics.ObjectAudio);
        int idEnd = _b.Position;
        if (id == 0x1F)
        {
            _t?.Field("Payload ID", start, idEnd, "31 (extended)", Explain.EmdfPayloadId, Topics.ObjectAudio);
            id += VariableBits("Payload ID Extension", 5, Explain.EmdfPayloadId);
        }
        else
        {
            _t?.Field("Payload ID", start, idEnd, $"{id} ({Ac3Names.EmdfPayload(id)})", Explain.EmdfPayloadId, Topics.ObjectAudio);
        }

        EmdfPayloadConfig();
        int size = VariableBits("Payload Size", 8, Explain.EmdfPayloadSize, v => NodeValue.Integer(v, "bytes"));
        int sizeStart = _lastStart;
        int sizeEnd = _lastEnd;
        int payloadStart = _b.Position;
        long payloadEnd = payloadStart + (size * 8L);
        if (payloadEnd > bound)
        {
            Report(Issue.EmdfPayloadSize, "Payload Size", sizeStart, sizeEnd,
                $"the payload with ID {id} is {size} bytes, but only {(bound - payloadStart) / 8} bytes remain in the container");
            _t?.CloseTo(_t.OpenCount - 1, bound);
            _b.MoveTo(Math.Min(bound, _b.Limit));
            _emdfBound = -1;
            return true;
        }

        int endBit = (int)payloadEnd;
        if (id == Ac3Names.JocId)
        {
            hasJoc = true;
            _r.HasJoc = true;
            _r.JocStart = start;
            _r.JocEnd = endBit;
            Joc(start, payloadStart, endBit);
        }
        else if (id == Ac3Names.OamdId)
        {
            hasOamd = true;
            _r.HasOamd = true;
            Oamd(start, payloadStart, endBit);
        }
        else
        {
            _t?.Field("Payload Data", payloadStart, endBit, NodeValue.Integer(size, "bytes"), Explain.EmdfPayloadData, Topics.ObjectAudio);
        }

        _emdfBound = bound;
        _b.MoveTo(endBit);
        _t?.Close(endBit, $"{id} ({Ac3Names.EmdfPayload(id)})");
        return false;
    }

    private void EmdfPayloadConfig()
    {
        _t?.Open("Payload Configuration", _b.Position, Explain.EmdfPayloadConfig, Topics.ObjectAudio);
        bool hasOffset = EFlag("HasSampleOffset", Explain.EmdfSmploffste);
        if (hasOffset)
        {
            E("Sample Offset", 11, Explain.EmdfSmploffst, v => NodeValue.Integer(v, "samples", "sample"));
            E("Reserved", 1, Explain.EmdfConfigReserved);
        }

        if (EFlag("HasDuration", Explain.EmdfDuratione))
        {
            VariableBits("Duration", 11, Explain.EmdfDuration, v => NodeValue.Integer(v, "samples", "sample"));
            if (_lastGroups > 2)
            {
                Report(Issue.EmdfReserved, "Duration", _lastStart, _lastEnd, "duration has more than the two groups of 11 bits that H.2.2.3.4 allows");
            }
        }

        if (EFlag("HasGroupId", Explain.EmdfGroupide))
        {
            VariableBits("Group ID", 2, Explain.EmdfGroupId);
        }

        if (EFlag("HasCodecData", Explain.EmdfCodecdatae))
        {
            E("Codec Data", 8, Explain.EmdfCodecData, v => NodeValue.Hex(v, 2));
        }

        if (!EFlag("DiscardsUnknownPayload", Explain.EmdfDiscard))
        {
            bool aligned = false;
            if (!hasOffset)
            {
                aligned = EFlag("IsFrameAligned", Explain.EmdfFrameAligned);
                if (aligned)
                {
                    EFlag("CreatesDuplicate", Explain.EmdfCreateDuplicate);
                    EFlag("RemovesDuplicate", Explain.EmdfRemoveDuplicate);
                }
            }

            if (hasOffset || aligned)
            {
                E("Priority", 5, Explain.EmdfPriority);
                E("Processing Allowed", 2, Explain.EmdfProcAllowed, v => $"{v} ({Ac3Names.EmdfProcessing(v)})");
            }
        }

        _t?.Close(_b.Position);
    }

    // emdf_protection (H.2.1.4): the lengths, and the bits themselves, which the specification
    // leaves to the implementation, so they are stepped over.
    private void EmdfProtection()
    {
        _t?.Open("Protection", _b.Position, Explain.EmdfProtection, Topics.ObjectAudio);
        int primary = E("Primary Protection Length", 2, Explain.EmdfProtectionPrimary, v => v == 0 ? "0 (reserved)" : $"{v} ({ProtectionBits(v)} bits)");
        if (primary == 0)
        {
            Report(Issue.EmdfReserved, "Primary Protection Length", _lastStart, _lastEnd, "protection_length_primary is 0, which ETSI TS 102 366 H.2.2.4.1 reserves");
            throw new EmdfStop();
        }

        int secondary = E("Secondary Protection Length", 2, Explain.EmdfProtectionSecondary, v => v == 0 ? "0 (none)" : $"{v} ({ProtectionBits(v)} bits)");
        foreach ((string name, int bits) in new[] { ("Primary Protection Bits", ProtectionBits(primary)), ("Secondary Protection Bits", secondary == 0 ? 0 : ProtectionBits(secondary)) })
        {
            if (bits == 0)
            {
                continue;
            }

            Need(bits);
            int start = _b.Position;
            _b.Skip(bits);
            _t?.Field(name, start, _b.Position, NodeValue.Integer(bits, "bits"), Explain.EmdfProtectionBits, Topics.ObjectAudio);
        }

        _t?.Close(_b.Position);
    }

    private static int ProtectionBits(int code) => code switch { 1 => 8, 2 => 32, _ => 128 };

    // joc_header and the first fields of joc_info (ETSI TS 103 420 6.2.2, 6.2.3).
    private void Joc(int group, int start, int end)
    {
        _emdfBound = end;
        if (end - start < 12)
        {
            Report(Issue.PayloadShort, "Payload", group, end, "the JOC payload is shorter than the 12 bits joc_header takes (TS 103 420 6.2.2)");
            return;
        }

        _t?.Open("JOC Header", start, Explain.JocHeader, Topics.ObjectAudio);
        int dmx = E("Downmix Configuration", 3, Explain.JocDmxConfig, v => $"{v} ({Ac3Names.JocDownmix(v)})");
        int objects = E("JOC Object Count", 6, Explain.JocObjectCount, v => NodeValue.Integer(v + 1, "objects", "object")) + 1;
        int extension = E("Extension Configuration", 3, Explain.JocExtConfig, v => v == 0 ? "0 (none)" : $"{v} (reserved)");
        _t?.Close(_b.Position);
        _r.JocObjects = objects;
        if (dmx >= 5 || objects > 16 || extension != 0)
        {
            Report(Issue.JocReserved, "JOC Header", start, start + 12,
                dmx >= 5 ? $"joc_dmx_config_idx is {dmx}, which TS 103 420 Table 47 reserves"
                : objects > 16 ? $"joc_num_objects_bits gives {objects} objects, above the 16 that 6.3.2.4 allows"
                : $"joc_ext_config_idx is {extension}, which TS 103 420 Table 49 reserves");
        }

        if (end - start >= 30)
        {
            _t?.Open("JOC Info", _b.Position, Explain.JocInfo, Topics.ObjectAudio);
            E("Clip Gain X Bits", 3, Explain.JocClipGain);
            E("Clip Gain Y Bits", 5, Explain.JocClipGain);
            E("Sequence Count", 10, Explain.JocSeqCount);
            _t?.Close(_b.Position);
        }
    }

    // The start of object_audio_metadata_payload (ETSI TS 103 420 5.5.2).
    private void Oamd(int group, int start, int end)
    {
        _emdfBound = end;
        try
        {
            if (E("OAMD Version", 2, Explain.OamdVersion) == 3)
            {
                E("OAMD Version Extension", 3, Explain.OamdVersion);
            }

            int count = E("Object Count", 5, Explain.OamdObjectCount, v => v == 0x1F ? "31 (extended)" : NodeValue.Integer(v + 1, "objects", "object"));
            if (count == 0x1F)
            {
                count += E("Object Count Extension", 7, Explain.OamdObjectCount);
            }

            _r.OamdObjects = count + 1;
        }
        catch (EndOfStreamException)
        {
            Report(Issue.PayloadShort, "Payload", group, end, "the OAMD payload ends inside its version or object count (TS 103 420 5.5.2)");
        }
    }
}
