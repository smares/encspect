using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// An AC-3 core (bit stream ID up to 8) as the independent substream of a program that has E-AC-3 dependent substreams (Annex E 2.3.1.2 allows it): the AC-3 decoder's blocks go to the
// program's merger, which places the dependent substreams' channels by their channel maps.
internal sealed class Ac3Substream : IProgramSubstream
{
    private const int BlockSamples = 256;

    private readonly FrameDecoder _core;
    private readonly Action<long, float[], int, int> _deliver;

    public Ac3Substream(DecodeContext context, int acmod, bool hasLfe, int rate, Action<long, float[], int, int> deliver)
    {
        _core = new FrameDecoder(context, acmod, hasLfe, rate);
        _deliver = deliver;
        Acmod = acmod;
        HasLfe = hasLfe;
    }

    public int Acmod { get; }

    public bool HasLfe { get; }

    public List<(int Channel, int Location, int Length)> Transients { get; } = [];

    public void Reset() => _core.ResetState();

    public void FlushPending()
    {
    }

    public void DecodeUnit(ref IntReader r, long firstSample, int blocks)
    {
        _core.DecodeBlocks(ref r, blocks);
        for (int block = 0; block < blocks; block++)
        {
            _deliver(firstSample + ((long)block * BlockSamples), _core.Planar, _core.Stride, block * BlockSamples);
        }
    }
}
