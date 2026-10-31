using Encspect.Core;

namespace Encspect.Formats.Alac;

// ALAC frames a container stores one to a packet (MP4's alac sample entry, Matroska's A_ALAC), with the magic cookie from the container's configuration
// (the alac box, the CodecPrivate). Each packet is walked as Apple's reference decoder reads a frame, far enough to know where every element ends: the
// element headers, the prediction parameters and the adaptive Golomb code of the residuals. Nothing is decoded; ALAC stores no checksum, and its decoder
// waits under "Later" (docs/design/decoders.md, decision 5).
public sealed class AlacCodecParser : ICodecParser
{
    public const string CodecId = "alac";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "alac.payload-not-analyzed", "ALAC handed over as one payload is not analyzed; only packets, one for each frame, are.", context.Parent);
            return;
        }

        var state = new State(context) { Stream = context.Parent };
        if (context.Configuration is { } configuration && configuration.Data.Length > 0)
        {
            Configure(state, configuration);
        }
        else
        {
            state.Findings.AddNow(Severity.Warning, "alac.cookie-missing",
                "The container stores no magic cookie for the stream, which holds the frame length, the bit depth, the channels and the tuning parameters that reading a frame needs, so the frames are not read.", context.Parent);
        }

        int index = 0;
        foreach (MappedBytes packet in packets)
        {
            if (index++ % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            state.AddFrame(packet);
        }

        Summary.Finish(state, context.Configuration);
    }

    private static void Configure(State s, CodecConfiguration configuration)
    {
        MappedBytes data = configuration.Data;
        AlacCookie cookie = AlacCookie.Read(data.Span);
        s.Records.Cookie = cookie;
        s.CookieBytes = data;
        Node node = configuration.Parent.Add(Nodes.Cookie(FieldMap.Of(data), cookie));
        s.CookieNode = node;
        Summary.CookieFindings(s, cookie, node);
        s.Figures = new StreamFigures(cookie.IsComplete ? cookie.SampleRate : 0);
    }
}
