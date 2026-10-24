using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// The outcome of reading one substream of one unit to its end.
internal sealed class WalkResult
{
    public SubstreamFault Fault { get; set; }

    // The bit the fault was found at.
    public int FaultBit { get; set; }

    public int Blocks { get; set; }

    // The samples the blocks hold.
    public int Samples { get; set; }

    public SubstreamTail Tail { get; set; }

    // The restart headers read, in order.
    public List<RestartHeader> Restarts { get; } = [];

    public bool HasBlockSizeCheckMismatch { get; set; }

    public bool ChangesMatricesAfterFirstBlock { get; set; }

    public bool HasBlockSizeCheck { get; set; }

    // The restart headers that came after the first block.
    public int LateRestarts { get; set; }

    public bool IsRead => Fault == SubstreamFault.None;

    // The samples the substream outputs: its blocks' without those the end of stream marker drops.
    public int OutputSamples => Math.Max(0, Samples - Tail.DroppedSamples);

    public bool IsDropMoreThanHeld => Tail.DroppedSamples > Samples;
}

internal static class SubstreamWalk
{
    // Reads the blocks and the tail of a substream. content is the substream's bytes without its parity byte and CRC-8. planes may be empty.
    public static WalkResult Run(SubstreamState state, ReadOnlySpan<byte> content, bool hasCheckData, bool isMlp, int unitSamples, int channelListSize, Span<int> planes,
        int stride, ParameterLog? log = null, bool isReplay = false)
    {
        var result = new WalkResult();
        var reader = new SubstreamReader(content, state, isMlp, unitSamples, channelListSize, planes, stride, isReplay, log);
        while (true)
        {
            int startBit = reader.Position;
            log?.Open($"Block {result.Blocks}", startBit, Explain.Block, Topics.DecodingParameters);
            bool isRead = reader.ReadBlock(out BlockInfo block);
            if (!isRead)
            {
                result.Fault = reader.Fault;
                result.FaultBit = reader.Position;
                log?.Unwind(Math.Min(reader.Position, content.Length * 8));
                return result;
            }

            result.Blocks++;
            result.Samples = reader.Samples;
            result.HasBlockSizeCheck |= state.HasBlockSizeCheck;
            result.HasBlockSizeCheckMismatch |= block.HasBlockSizeCheckMismatch;
            result.ChangesMatricesAfterFirstBlock |= block.ChangesMatricesAfterFirstBlock;
            if (block.HasRestartHeader)
            {
                result.Restarts.Add(block.Restart);
                if (result.Blocks > 1)
                {
                    result.LateRestarts++;
                }
            }

            log?.Close(reader.Position, NodeValue.Integer(block.Samples, "samples"));
            if (block.IsLast)
            {
                break;
            }
        }

        if (!reader.ReadTail(hasCheckData, out SubstreamTail tail))
        {
            result.Fault = reader.Fault;
            result.FaultBit = reader.Position;
            return result;
        }

        result.Tail = tail;
        return result;
    }
}
