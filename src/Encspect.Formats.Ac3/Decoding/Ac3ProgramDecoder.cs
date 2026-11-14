using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// An AC-3 program is decoded by the AC-3 decoder alone, or, where E-AC-3 dependent substreams follow its core, by the program merger that places their channels (the feed marks such
// records with a leading -1): the program's frames all take one of the two ways, since its dependent substreams are part of what makes it one program.
internal sealed class Ac3ProgramDecoder(DecodeContext context, SyncFrame header) : IFrameDecoder
{
    private FrameDecoder? _plain;
    private Eac3FrameDecoder? _merged;

    public int Decode(in CodedFrame frame)
    {
        SampleBuffers lent = context.LentBuffers;
        if (_merged is not null || (frame.IsWalked && !lent.IsEmpty && lent.Narrow[0] == -1))
        {
            _merged ??= new Eac3FrameDecoder(context, header);
            return _merged.Decode(frame);
        }

        _plain ??= new FrameDecoder(context, header);
        return _plain.Decode(frame);
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end)
    {
        _plain?.Finish(end);
        _merged?.Finish(end);
        return [];
    }
}
