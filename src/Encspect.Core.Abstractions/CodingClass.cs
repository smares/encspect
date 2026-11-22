namespace Encspect.Core;

// How a coding reconstructs audio from its stream, from the coding's definition: a property of the coding, never of where the audio came from.
public enum CodingClass
{
    // The coding reconstructs the samples it was given exactly (linear and floating-point PCM, FLAC, ALAC, ...).
    Lossless,

    // The coding discards information by design: companding (A-law, mu-law), ADPCM and the perceptual codecs.
    Lossy,

    // A lossy stream plus an optional correction file that makes it lossless (a WavPack hybrid file, an OptimFROG DualStream file). Which of the two a
    // stream is right now is the pairing's status (StreamInfo.Correction).
    Hybrid,
}

// The class of each coding by the name its parser reports, for the streams a parser leaves without one. The parsers whose coding depends on the stream (WavPack, OptimFROG) state it themselves. A coding whose class the stream alone does not
// settle (MPEG-4 SLS, whose enhancement layer may be cut short of lossless), an unlisted coding and a stream that is not audio have none.
public static class CodingClasses
{
    // The class of the stream: the one its parser stated, else the one its coding's name has; null where there is none.
    public static CodingClass? Of(StreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CodingClass is { } stated)
        {
            return stated;
        }

        if (stream.Kind != StreamKind.Audio || stream.IsCorrectionFile)
        {
            return null;
        }

        return OfName(stream.Codec);
    }

    public static CodingClass? OfName(string codec)
    {
        ArgumentNullException.ThrowIfNull(codec);

        if (codec is "PCM" or "PCM (floating-point)" or "integer PCM" or "floating-point PCM" or "linear PCM" or "FLAC" or "Apple Lossless" or "Monkey's Audio" or "TTA" or "TAK"
            or "Dolby TrueHD" or "MLP" or "Windows Media Audio Lossless" or "DST"
            || codec.StartsWith("DTS-HD Master Audio", StringComparison.Ordinal) || codec.StartsWith("ALS", StringComparison.Ordinal))
        {
            return CodingClass.Lossless;
        }

        if (codec.StartsWith("SLS", StringComparison.Ordinal))
        {
            return null;
        }

        if (codec is "Other" or "Video" or "Encrypted audio" or "Encrypted video" or "RealAudio" or "WavPack" or "MPEG-4 audio" or "SLS" or "SLS non-core"
            or "DTS-HD" or "MAT" or "Sonarc" or "MSN Audio" or "Control Resources VQLPC" or "Control Resources CR10" or "Echo Speech EchoSC1" or "Audiofile AF36" or "Audiofile AF10"
            or "DSP Solutions DIGISTD" or "DSP Solutions DIGIFIX" or "DSP Solutions DIGIREAL" or "DRM" or "Raw SPORT" or "unknown" or "extensible"
            or "null" or "SBR" or "PS" or "TTSI" or "Main synthetic" or "Wavetable synthesis" or "Microsoft ACM audio" or "QuickTime audio" or "Reserved" or "reserved" or "General MIDI" or "Algorithmic synthesis and audio effects" or "Audio Sync" or "SMR Simple" or "SMR Main" or "other")
        {
            return null;
        }

        string[] lossyPrefixes =
        [
            "MPEG", "AAC", "HE-AAC", "xHE-AAC", "ER ", "Layer-", "SSC", "USAC", "SAOC", "A-law", "Mu-law", "IBM mu-law", "IBM A-law", "IMA ADPCM", "Microsoft ADPCM",
            "GSM", "MACE", "ACE ", "ATRAC", "Sony ATRAC", "Vorbis", "Opus", "Speex", "Musepack", "AC-3", "E-AC-3", "Dolby AC-3", "Dolby AC-2", "DTS", "Windows Media Audio",
            "VSELP", "IBM CVSD", "G.7", "Intel G.7", "Microsoft G.7", "DSP Group TrueSpeech", "aptX", "WMA", "ESS Technology AC-3", "AC-4", "AMR", "13K Voice", "ima4", "sevc", "sqcp", "QDM2", "TwinVQ", "CELP", "HVXC", "LD MPEG Surround", "ER TwinVQ", "ER CELP", "ER HVXC", "HILN", "ER HILN", "Parametric", "ER Parametric",
        ];
        foreach (string prefix in lossyPrefixes)
        {
            if (codec.StartsWith(prefix, StringComparison.Ordinal))
            {
                return CodingClass.Lossy;
            }
        }

        return codec.Contains("ADPCM", StringComparison.Ordinal) ? CodingClass.Lossy : null;
    }
}
