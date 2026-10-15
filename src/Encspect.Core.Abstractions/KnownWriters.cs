namespace Encspect.Core;

// The software whose output formats the project has seen, to tell a writer claim that cannot be true of the format it stands in
// (an APEv2 Encoder item that names LAME on a WavPack stream). A row is added only where the project's own fixtures, which real
// tools wrote, show what the tool writes; software not listed raises nothing, and neither does software that writes many
// formats (FFmpeg and its libavformat, GPAC and MP4Box), which is left out on purpose.
public static class KnownWriters
{
    // The kinds of stream the rows name; a parser passes the one its stream is.
    public const string MpegAudio = "mpeg-audio";
    public const string Aac = "aac";
    public const string Usac = "usac";
    public const string Opus = "opus";
    public const string Vorbis = "vorbis";
    public const string Speex = "speex";
    public const string WavPack = "wavpack";

    // Kinds no listed software writes yet; a listed writer on one of them is the case the table exists for.
    public const string Musepack = "musepack";
    public const string Ac3 = "ac3";
    public const string Dts = "dts";
    public const string WindowsMedia = "wma";

    private static readonly Dictionary<string, (string Kind, string Label)> Rows = new(StringComparer.OrdinalIgnoreCase)
    {
        // LAME 3.100: the external-lame-* fixtures (make_external_mpeg_fixtures.py), Layer III only.
        ["LAME"] = (MpegAudio, "MPEG audio"),
        // FAAC 1.30 to 2.2 (faac_tool.py, make_external_aac_fixtures.py): AAC in ADTS and MP4.
        ["faac"] = (Aac, "AAC"),
        // fdkaac 1.0.0 with libfdk-aac 2.0.2 (fdk_tool.py, make_external_aac_fixtures.py): AAC in ADTS and MP4.
        ["fdkaac"] = (Aac, "AAC"),
        // exhale 1.2.2 (exhale_tool.py, make_external_aac_fixtures.py): xHE-AAC (USAC) in MP4.
        ["exhale"] = (Usac, "xHE-AAC (USAC)"),
        // opusenc 0.2 (make_external_vorbis_opus_fixtures.py): Ogg Opus.
        ["opusenc"] = (Opus, "Opus"),
        // oggenc 1.4.2 (make_external_vorbis_opus_fixtures.py): Ogg Vorbis.
        ["oggenc"] = (Vorbis, "Vorbis"),
        // speexenc 1.2.1 (make_external_ogg_fixtures.py): Ogg Speex.
        ["speexenc"] = (Speex, "Speex"),
        // wavpack 5.6.0 (make_external_wavpack_fixtures.py): WavPack.
        ["wavpack"] = (WavPack, "WavPack"),
    };

    // What the software named by a writer's text writes, as a label such as "MPEG audio", when it is listed and writes another kind
    // of stream than kind; null when the text names software that is not listed or writes that kind.
    public static string? WritesInstead(string writerText, string kind)
    {
        ArgumentNullException.ThrowIfNull(writerText);

        return Rows.TryGetValue(ClaimRoles.FamilyOf(writerText), out (string Kind, string Label) row) && row.Kind != kind ? row.Label : null;
    }
}
