namespace Encspect.Formats.Mpeg;

// Bytes of one frame's main data, at a file offset inside some frame's data.
internal readonly record struct MainDataRun(long Offset, int Length, long Owner)
{
    public long End => Offset + Length;
}

internal enum MainDataProblem
{
    None,
    BeforeStream,
    Overlap,
    AcrossGap,
}

internal readonly record struct MainDataPlacement(long Reservoir, int Earlier, MainDataProblem Problem, bool Overflows);

// The Layer III bit reservoir (ISO/IEC 11172-3 2.4.2.7, main_data_begin): main data runs
// through the data of consecutive frames of one stream, the bytes after each frame's side
// information, as one byte stream. A frame's main data starts main_data_begin bytes before its own
// data. The stream continues across junk and tags between frames, which decoders disagree on, so
// main data that reaches back across them is reported; a new stream starts it empty.
//
// Every main-data byte is recorded as a run owned by its frame. Where main data overlaps the
// previous frame's or does not fit its frame, the earlier owner keeps the bytes.
internal sealed class BitReservoir(List<MainDataRun> runs)
{
    // main_data_begin is at most 511 bytes, so older frames never matter again.
    private const int Reach = 512;

    private readonly List<Area> _recent = [];
    private (int Version, int SampleRate)? _stream;
    private long _position;
    private long _previousEnd;
    private int _frames;

    public void Reset()
    {
        _stream = null;
        _recent.Clear();
        _position = 0;
        _previousEnd = 0;
        _frames = 0;
    }

    public MainDataPlacement Add(MpegHeader header, long frameOffset, long frameEnd, long dataOffset, int dataLength,
        int mainDataBegin, int mainDataLength, bool truncated)
    {
        if (_stream != (header.VersionBits, header.SampleRateIndex))
        {
            Reset();
            _stream = (header.VersionBits, header.SampleRateIndex);
        }

        long start = _position;
        long end = start + dataLength;
        long reservoir = start - _previousEnd;
        long begin = start - mainDataBegin;
        long mainEnd = begin + mainDataLength;
        _recent.Add(new Area(start, dataLength, dataOffset, frameOffset, frameEnd));

        MainDataProblem problem = MainDataProblem.None;
        if (mainDataBegin > reservoir)
        {
            problem = _frames == 0 ? MainDataProblem.BeforeStream : MainDataProblem.Overlap;
        }
        else if (begin < start && CrossesGap(begin))
        {
            problem = MainDataProblem.AcrossGap;
        }

        long ownStart = Math.Max(Math.Max(begin, _previousEnd), 0);
        long ownEnd = Math.Min(mainEnd, end);
        int earlier = 0;
        foreach (Area area in _recent)
        {
            long from = Math.Max(ownStart, area.Start);
            long to = Math.Min(ownEnd, area.Start + area.Length);
            if (from < to)
            {
                runs.Add(new MainDataRun(area.FileOffset + (from - area.Start), (int)(to - from), frameOffset));
                earlier += area.Start < start ? (int)(to - from) : 0;
            }
        }

        _previousEnd = Math.Max(_previousEnd, ownEnd);
        _position = end;
        _frames++;
        _recent.RemoveAll(a => a.Start + a.Length <= _position - Reach);

        return new MainDataPlacement(reservoir, earlier, problem, mainEnd > end && !truncated);
    }

    // Whether main data starting at stream position begin lies in a frame before a gap.
    private bool CrossesGap(long begin)
    {
        for (int j = _recent.Count - 1; j > 0; j--)
        {
            if (_recent[j - 1].FrameEnd != _recent[j].FrameOffset)
            {
                return true;
            }

            if (_recent[j - 1].Start <= begin)
            {
                return false;
            }
        }

        return false;
    }

    // A frame's data in the reservoir's byte stream and in the file.
    private readonly record struct Area(long Start, int Length, long FileOffset, long FrameOffset, long FrameEnd);
}
