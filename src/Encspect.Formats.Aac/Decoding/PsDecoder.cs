namespace Encspect.Formats.Aac.Decoding;

internal sealed class PsDecoder
{
    public void Reset()
    {
    }

    public void Process(SbrElement element, ReadOnlySpan<int> slot, Span<float> left, Span<float> right)
    {
        element.Synthesize(0, element.Real(0), element.Imaginary(0), left);
        left.CopyTo(right);
    }
}
