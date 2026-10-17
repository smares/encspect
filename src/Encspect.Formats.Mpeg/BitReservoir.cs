using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// Bytes of one frame's main data, at a file offset inside some frame's data; Owner is that frame's
// index among the file's frames and gaps.
internal readonly record struct MainDataRun(long Offset, int Length, int Owner)
{
    public long End => Offset + Length;
}

internal enum MainDataProblem
{
    None,
    BeforeStream,
    Overlap,
    AcrossGap,
    InHeaderFrame,
}

internal readonly record struct MainDataPlacement(long Reservoir, int Earlier, MainDataProblem Problem, bool Overflows);

// The Layer III bit reservoir (ISO/IEC 11172-3 2.4.2.7, main_data_begin): main data runs
// through the data of consecutive frames of one stream, the bytes after each frame's side
// information, as one byte stream. A frame's main data starts main_data_begin bytes before its own
// data. The stream continues across junk and tags between frames, which decoders disagree on, so
// main data that reaches back across them is reported; a new stream starts it empty. A Xing, Info
// or VBRI header frame is a valid frame without main data: minimp3 and mp3guessenc keep its bytes
// in the reservoir, as here, while mpg123 and ffmpeg skip the frame, so main data that reaches
// back into it is reported too.
//
// Every main-data byte is recorded as a run owned by its frame. Where main data overlaps the
// previous frame's or does not fit its frame, the earlier owner keeps the bytes.
internal sealed class BitReservoir(ChunkedList<MainDataRun> runs)
{
    // main_data_begin is at most 511 bytes, so older frames never matter again.
    private const int Reach = 512;

    // Frames without data after their side information hold no reservoir bytes and are not kept,
    // so the list stays bounded whatever the frame lengths; a gap before one is carried over to
    // the next frame that has data.
    private readonly List<Area> _recent = [];
    private MpegHeader? _stream;
    private long _position;
    private long _previousEnd;
    private long? _frameEnd;
    private bool _gapPending;

    public void Reset()
    {
        _stream = null;
        _recent.Clear();
        _position = 0;
        _previousEnd = 0;
        _frameEnd = null;
        _gapPending = false;
    }

    public MainDataPlacement Add(MpegHeader header, int owner, long frameOffset, long frameEnd, long dataOffset, int dataLength,
        int mainDataBegin, int mainDataLength, bool truncated, bool isHeaderFrame = false)
    {
        if (_stream is not { } stream || !stream.IsSameStream(header))
        {
            Reset();
            _stream = header;
        }

        long start = _position;
        long end = start + dataLength;
        long reservoir = start - _previousEnd;
        long begin = start - mainDataBegin;
        long mainEnd = begin + mainDataLength;
        bool gapBefore = _gapPending || (_frameEnd is { } previousFrameEnd && previousFrameEnd != frameOffset);
        int earlierAreas = _recent.Count;
        if (dataLength > 0)
        {
            _recent.Add(new Area(start, dataLength, dataOffset, isHeaderFrame, gapBefore));
        }

        _gapPending = dataLength == 0 && gapBefore;
        _frameEnd = frameEnd;

        MainDataProblem problem = MainDataProblem.None;
        if (begin < 0)
        {
            problem = MainDataProblem.BeforeStream;
        }
        else if (mainDataBegin > reservoir)
        {
            problem = MainDataProblem.Overlap;
        }
        else if (begin < start && CrossesGap(begin, gapBefore, earlierAreas))
        {
            problem = MainDataProblem.AcrossGap;
        }
        else if (begin < start && _recent.Any(a => a.IsHeaderFrame && begin < a.Start + a.Length))
        {
            problem = MainDataProblem.InHeaderFrame;
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
                runs.Add(new MainDataRun(area.FileOffset + (from - area.Start), (int)(to - from), owner));
                earlier += area.Start < start ? (int)(to - from) : 0;
            }
        }

        _previousEnd = Math.Max(_previousEnd, ownEnd);
        _position = end;
        _recent.RemoveAll(a => a.Start + a.Length <= _position - Reach);

        return new MainDataPlacement(reservoir, earlier, problem, mainEnd > end && !truncated);
    }

    // Whether main data starting at stream position begin lies in a frame before a gap: the gap
    // before the current frame, or one before an earlier frame that begin lies before.
    private bool CrossesGap(long begin, bool gapBefore, int earlierAreas)
    {
        if (gapBefore)
        {
            return true;
        }

        for (int j = earlierAreas - 1; j >= 0; j--)
        {
            if (_recent[j].Start <= begin)
            {
                return false;
            }

            if (j > 0 && _recent[j].HasGapBefore)
            {
                return true;
            }
        }

        return false;
    }

    // A frame's data in the reservoir's byte stream and in the file, and whether a gap or a tag
    // lies between it and the frame before it.
    private readonly record struct Area(long Start, int Length, long FileOffset, bool IsHeaderFrame, bool HasGapBefore);
}
