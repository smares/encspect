namespace Encspect.Core;

public sealed class TagProbeContext
{
    public TagProbeContext(ByteReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        Reader = reader;
    }

    public ByteReader Reader { get; }

    public CancellationToken CancellationToken => Reader.CancellationToken;
}
