namespace Encspect.Formats.Matroska;

// The EBML element types (RFC 8794 section 7). Matroska's UTF-8 and string types differ in the characters they allow
// (section 7.4, 7.5); both are read as text.
internal enum ElementType : byte
{
    Master,
    Unsigned,
    Signed,
    Float,
    String,
    Utf8,
    Date,
    Binary,
}

// One element of the schemas of RFC 8794 and RFC 9559, as the generated table (Elements.cs) lists it. Path is the schema's
// EBML path: "\Segment\Tracks\TrackEntry\TrackNumber", where a "+" in front of a name marks an element that may contain
// itself (RFC 8794 section 11.1.6.2).
internal sealed class ElementDefinition
{
    public ElementDefinition(uint id, string name, ElementType type, string path, string description, bool isUnknownSizeAllowed = false, bool isDeprecated = false, string? defaultValue = null)
    {
        DefaultValue = defaultValue;
        Id = id;
        Name = name;
        Type = type;
        Path = path.Replace("+", string.Empty, StringComparison.Ordinal);
        IsRecursive = path.EndsWith("\\+" + name, StringComparison.Ordinal);
        Description = description;
        IsUnknownSizeAllowed = isUnknownSizeAllowed;
        IsDeprecated = isDeprecated;
        int slash = Path.LastIndexOf('\\');
        ParentPath = slash <= 0 ? string.Empty : Path[..slash];
        Depth = Path.Count(c => c == '\\');
    }

    public uint Id { get; }

    public string Name { get; }

    public ElementType Type { get; }

    public string Path { get; }

    public string ParentPath { get; }

    // The number of names in the path: the Segment is 1, the Top-Level Elements 2.
    public int Depth { get; }

    public bool IsRecursive { get; }

    public string Description { get; }

    public bool IsUnknownSizeAllowed { get; }

    public bool IsDeprecated { get; }

    // The value of the element when it is empty or absent (RFC 8794 section 11.1.6.8 and 6.1), as the registry writes it, or null.
    public string? DefaultValue { get; }

    // The CRC-32 and Void elements may be a child of any Master Element (RFC 8794 section 11.3).
    public bool IsGlobal => Id is Schema.Crc32 or Schema.Void;

    // A direct child of the Segment: the Top-Level Elements (RFC 9559 section 6.1).
    public bool IsTopLevel => Depth == 2 && Path.StartsWith("\\Segment\\", StringComparison.Ordinal);
}

internal static partial class Schema
{
    public const uint Ebml = 0x1A45DFA3;
    public const uint Segment = 0x18538067;
    public const uint SeekHead = 0x114D9B74;
    public const uint Info = 0x1549A966;
    public const uint Cluster = 0x1F43B675;
    public const uint Tracks = 0x1654AE6B;
    public const uint Cues = 0x1C53BB6B;
    public const uint Attachments = 0x1941A469;
    public const uint Chapters = 0x1043A770;
    public const uint Tags = 0x1254C367;
    public const uint Crc32 = 0xBF;
    public const uint Void = 0xEC;

    public const uint Timestamp = 0xE7;
    public const uint Position = 0xA7;
    public const uint PrevSize = 0xAB;
    public const uint SimpleBlock = 0xA3;
    public const uint BlockGroup = 0xA0;
    public const uint Block = 0xA1;

    private static readonly Dictionary<uint, ElementDefinition> ById = Definitions.ToDictionary(d => d.Id);

    private static readonly Dictionary<string, ElementDefinition> ByPath = Definitions.ToDictionary(d => d.Path, StringComparer.Ordinal);

    public static IReadOnlyList<ElementDefinition> All => Definitions;

    public static ElementDefinition? Find(ulong id) => id <= uint.MaxValue && ById.TryGetValue((uint)id, out ElementDefinition? definition) ? definition : null;

    // The definition of the element the path names, or null.
    public static ElementDefinition? FindPath(string path) => ByPath.GetValueOrDefault(path);

    // Whether the schema places child in parent (a Master Element or the root of the document): by its path, except that the
    // two global elements fit in any master and a recursive element in itself.
    public static bool IsChildOf(ElementDefinition child, ElementDefinition? parent)
    {
        if (child.IsGlobal)
        {
            return true;
        }

        if (parent is null)
        {
            return child.ParentPath.Length == 0;
        }

        return child.ParentPath == parent.Path || (parent.IsRecursive && parent.Id == child.Id);
    }
}
