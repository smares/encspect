using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// The channel list a substream's matrix channels are positioned in (docs/design/truehd-format.md, "Presentations and substreams"). Which list a
// substream uses is inferred from the major sync, and the list is chosen as the first candidate with as many channels as the substream's restart
// header says (M + 1), so that the result does not depend on what FFmpeg knew about the channel count.
internal static class Presentation
{
    // The lists substream k of count may use, in the order of preference.
    public static List<Speaker[]> Candidates(in MajorSync sync, int substream, int count)
    {
        var lists = new List<Speaker[]>();
        if (!sync.IsTrueHd)
        {
            Speaker[]? arrangement = Tables.ArrangementSpeakers(sync.Arrangement);
            if (count >= 2 && substream == 0)
            {
                lists.Add(Tables.Stereo);
            }
            else if (arrangement is not null)
            {
                lists.Add(arrangement);
            }

            return lists;
        }

        Speaker[]? six = sync.SixChannelAssignment != 0 ? Tables.SpeakersOf(sync.SixChannelAssignment) : null;
        Speaker[]? eight = sync.EightChannelAssignment != 0 ? Tables.SpeakersOf(sync.EightChannelAssignment) : null;
        void Add(Speaker[]? list)
        {
            if (list is not null)
            {
                lists.Add(list);
            }
        }

        if (count == 1)
        {
            Add(eight);
            Add(six);
        }
        else if (substream == 0)
        {
            lists.Add(Tables.Stereo);
        }
        else if (count == 2)
        {
            Add(six);
            Add(eight);
        }
        else if (substream == 1)
        {
            Add(six);
        }
        else
        {
            Add(eight);
            Add(six);
        }

        return lists;
    }

    // The most channels any candidate has, or 8 where there is none: what a channel assignment may reach before the substream's own channel count is known.
    public static int MaxListSize(in MajorSync sync, int substream, int count)
    {
        List<Speaker[]> lists = Candidates(sync, substream, count);
        return lists.Count == 0 ? 8 : lists.Max(l => l.Length);
    }

    // The list with as many channels as the substream outputs, or null where none has.
    public static Speaker[]? Resolve(in MajorSync sync, int substream, int count, int channels) =>
        Candidates(sync, substream, count).FirstOrDefault(l => l.Length == channels);
}
