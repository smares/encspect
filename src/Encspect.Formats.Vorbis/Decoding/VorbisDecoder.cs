using Encspect.Core;

namespace Encspect.Formats.Vorbis.Decoding;

// The decoder of Vorbis I (Vorbis I specification; floors 0 and 1, residues 0 to 2, the square polar channel coupling). The parser's entropy pass (PacketWalker) reads the
// floors and residues of every audio packet into the buffers the session lends; this decoder adds the reconstruction: the floor curves, the vectors the residue entries stand for,
// the inverse coupling, the inverse MDCT and the windowed overlap of the blocks. The code follows libvorbis 1.3.7 (BSD-3-Clause, notice in THIRD-PARTY-NOTICES.md) where the
// specification leaves details open. The stream stores no checksum over its audio, so the decoder runs only where a front end asks for the samples.
public sealed class VorbisDecoder : IDecoder
{
    public string Id => VorbisParser.CodecId;

    public bool DeclaresChecksums => false;

    // Coded audio at about 32 kbps takes about 1,300 units a byte; silence needs far more per byte (24 units a sample for one byte of a long block), which the floor of the budget
    // covers for hours (docs/design/lossy-decoders.md, "Per-byte allowance").
    public long WorkPerInputByte => 2048;

    public IFrameDecoder Open(DecodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Setup.Configuration is not { } configuration || configuration.Length <= Identification.Length)
        {
            throw new InvalidDataException("The stream gives no identification and setup header to configure the decoder with");
        }

        ReadOnlySpan<byte> bytes = configuration.Span;
        Identification? identification = Identification.Read(bytes[..Identification.Length]);
        if (identification is not { IsUsable: true, HasFraming: true, Channels: > 0, Rate: > 0 })
        {
            throw new InvalidDataException("The identification header is not one a Vorbis decoder accepts");
        }

        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(configuration.Data, out ArraySegment<byte> segment);
        SetupModel? setup = (segment.Array is { } array ? SetupModel.SharedFor(array) : null) ?? SetupModel.Read(bytes[Identification.Length..], identification.Channels, context.CancellationToken)
            ?? throw new InvalidDataException("The setup header is not one a Vorbis decoder accepts");
        return new FrameDecoder(context, identification, setup);
    }
}
