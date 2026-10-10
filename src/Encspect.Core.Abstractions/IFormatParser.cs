namespace Encspect.Core;

public interface IFormatParser
{
    string Name { get; }

    // Returns a confidence from 0 (not this format) to 100 (certain). The host resets the
    // reader position afterward, so implementations may move it freely.
    int Sniff(ByteReader reader);

    void Parse(ParseContext context);
}
