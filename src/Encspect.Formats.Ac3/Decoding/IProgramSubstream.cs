namespace Encspect.Formats.Ac3.Decoding;

// One substream of a program as the merger sees it: it decodes the frame of a unit and delivers blocks of 256 samples as it finishes them.
internal interface IProgramSubstream
{
    int Acmod { get; }

    bool HasLfe { get; }

    List<(int Channel, int Location, int Length)> Transients { get; }

    void Reset();

    void FlushPending();

    void DecodeUnit(ref IntReader r, long firstSample, int blocks);
}
