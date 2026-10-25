using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Encspect.Core;

public enum NodeValueKind
{
    Text,
    Integer,
    Decimal,
    Boolean,
    Hex,
}

// A node's value with its type, so front ends get numbers and booleans instead of display text.
// ToString gives the text the report shows ("128 kbps", "0.261 s", "0x1A2B", "true"). Values that
// are names, descriptions or mixed text ("255 (none)") are Text; a string converts to one
// implicitly, so text values stay terse where nodes are built.
public sealed class NodeValue : IEquatable<NodeValue>, ISpanFormattable
{
    private static readonly NodeValue True = new(NodeValueKind.Boolean, null, 1, 0, 0, null, null, false);
    private static readonly NodeValue False = new(NodeValueKind.Boolean, null, 0, 0, 0, null, null, false);

    private readonly string? _text;
    private readonly long _integer;
    private readonly double _decimal;
    private readonly string? _singularUnit;

    private NodeValue(NodeValueKind kind, string? text, long integer, double number, int digits, string? unit, string? singularUnit, bool showsSign)
    {
        Kind = kind;
        _text = text;
        _integer = integer;
        _decimal = number;
        Digits = digits;
        Unit = unit;
        _singularUnit = singularUnit;
        ShowsSign = showsSign;
    }

    public NodeValueKind Kind { get; }

    // What an Integer or Decimal counts or measures ("kbps", "Hz", "s", "bytes", "samples", "dB"),
    // or null for a plain number.
    public string? Unit { get; }

    // The decimals a Decimal is shown with, or the digits a Hex value is padded to.
    public int Digits { get; }

    // Whether a Decimal shows a plus sign when it is not negative ("+1.5 dB").
    public bool ShowsSign { get; }

    public static NodeValue Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new NodeValue(NodeValueKind.Text, text, 0, 0, 0, null, null, false);
    }

    // A unit that counts things in the plural reads in the singular for one ("1 byte", "2 bytes"): the plural nouns the reports count in, so that a
    // call needs the three-argument form only for a unit not listed here. The unit itself, which JSON writes, stays as given.
    public static NodeValue Integer(long value, string? unit = null) =>
        new(NodeValueKind.Integer, null, value, 0, 0, unit, unit is not null && Singulars.TryGetValue(unit, out string? singular) ? singular : null, false);

    // The plural units that read in the singular for one, and their singulars.
    public static IReadOnlyDictionary<string, string> SingularUnits => Singulars;

    private static readonly FrozenDictionary<string, string> Singulars = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bytes"] = "byte",
        ["bits"] = "bit",
        ["samples"] = "sample",
        ["frames"] = "frame",
        ["blocks"] = "block",
        ["channels"] = "channel",
        ["packets"] = "packet",
        ["items"] = "item",
        ["entries"] = "entry",
        ["levels"] = "level",
        ["degrees"] = "degree",
        ["ticks"] = "tick",
        ["bands"] = "band",
        ["subbands"] = "subband",
        ["times"] = "time",
        ["extensions"] = "extension",
        ["offsets"] = "offset",
        ["escapes"] = "escape",
        ["units"] = "unit",
        ["units per second"] = "unit per second",
        ["audio packets"] = "audio packet",
        ["pages"] = "page",
        ["streams"] = "stream",
        ["records"] = "record",
        ["elements"] = "element",
        ["segments"] = "segment",
        ["objects"] = "object",
        ["values"] = "value",
        ["sets"] = "set",
        ["words"] = "word",
        ["sections"] = "section",
        ["tracks"] = "track",
        ["chunks"] = "chunk",
        ["payloads"] = "payload",
        ["fields"] = "field",
        ["steps"] = "step",
        ["coefficients"] = "coefficient",
        ["granules"] = "granule",
        ["groups"] = "group",
        ["ranges"] = "range",
        ["pieces"] = "piece",
        ["parts"] = "part",
        ["headers"] = "header",
        ["sizes"] = "size",
        ["markers"] = "marker",
        ["cue points"] = "cue point",
        ["comments"] = "comment",
        ["loops"] = "loop",
        ["sample frames"] = "sample frame",
    }.ToFrozenDictionary();

    // A count whose unit reads in the singular for one ("1 frame", "3 frames").
    public static NodeValue Integer(long value, string unit, string singularUnit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(singularUnit);
        return new NodeValue(NodeValueKind.Integer, null, value, 0, 0, unit, singularUnit, false);
    }

    // The value as computed; the report shows it rounded to decimals.
    public static NodeValue Decimal(double value, int decimals, string? unit = null, bool showsSign = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimals, 15);
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A decimal value must be finite.");
        }

        return new NodeValue(NodeValueKind.Decimal, null, 0, value, decimals, unit, null, showsSign);
    }

    public static NodeValue Boolean(bool value) => value ? True : False;

    // An integer shown in hexadecimal with at least digits digits, such as a CRC or a flags field.
    public static NodeValue Hex(long value, int digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return Hex((ulong)value, digits);
    }

    // A hex value that may use all 64 bits, such as a channel mask.
    public static NodeValue Hex(ulong value, int digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(digits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 16);
        return new NodeValue(NodeValueKind.Hex, null, unchecked((long)value), 0, digits, null, null, false);
    }

    [return: NotNullIfNotNull(nameof(text))]
    public static implicit operator NodeValue?(string? text) => text is null ? null : Text(text);

    public static bool operator ==(NodeValue? left, NodeValue? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(NodeValue? left, NodeValue? right) => !(left == right);

    // The text of a Text value, or null.
    public string? AsText() => Kind == NodeValueKind.Text ? _text : null;

    // The number of an Integer or Hex value, or null; also null for a Hex value above long.MaxValue,
    // which AsUnsigned gives.
    public long? AsInteger() => Kind == NodeValueKind.Integer || (Kind == NodeValueKind.Hex && _integer >= 0) ? _integer : null;

    // The number of a Hex value, or null.
    public ulong? AsUnsigned() => Kind == NodeValueKind.Hex ? Unsigned : null;

    // The number of an Integer, Hex or Decimal value, or null.
    public double? AsDouble() => Kind switch
    {
        NodeValueKind.Integer => _integer,
        NodeValueKind.Hex => Unsigned,
        NodeValueKind.Decimal => _decimal,
        _ => null,
    };

    public bool? AsBoolean() => Kind == NodeValueKind.Boolean ? _integer != 0 : null;

    public override string ToString() => Kind switch
    {
        NodeValueKind.Text => _text!,
        NodeValueKind.Integer => WithUnit(_integer.ToString(CultureInfo.InvariantCulture), _integer == 1 && _singularUnit is not null ? _singularUnit : Unit),
        NodeValueKind.Decimal => WithUnit(FormatDecimal(), Unit),
        NodeValueKind.Boolean => _integer != 0 ? "true" : "false",
        _ => "0x" + Unsigned.ToString("X" + Digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
    };

    // Writes ToString's text without allocating; writers call it for every node.
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        charsWritten = 0;
        switch (Kind)
        {
            case NodeValueKind.Text:
                return Append(destination, ref charsWritten, _text);
            case NodeValueKind.Boolean:
                return Append(destination, ref charsWritten, _integer != 0 ? "true" : "false");
            case NodeValueKind.Hex:
                return Append(destination, ref charsWritten, "0x")
                    && Unsigned.TryFormat(destination[charsWritten..], out int hexWritten, Digits switch { 2 => "X2", 4 => "X4", 8 => "X8", _ => "X" + Digits.ToString(CultureInfo.InvariantCulture) }, CultureInfo.InvariantCulture)
                    && Advance(ref charsWritten, hexWritten);
            case NodeValueKind.Integer:
                return _integer.TryFormat(destination, out charsWritten, default, CultureInfo.InvariantCulture)
                    && AppendUnit(destination, ref charsWritten, _integer == 1 && _singularUnit is not null ? _singularUnit : Unit);
            default:
                string text = FormatDecimal();
                return Append(destination, ref charsWritten, text) && AppendUnit(destination, ref charsWritten, Unit);
        }
    }

    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public bool Equals(NodeValue? other) =>
        other is not null && Kind == other.Kind && _text == other._text && _integer == other._integer && _decimal.Equals(other._decimal)
        && Digits == other.Digits && Unit == other.Unit && _singularUnit == other._singularUnit && ShowsSign == other.ShowsSign;

    public override bool Equals(object? obj) => obj is NodeValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, _text, _integer, _decimal, Digits, Unit);

    // A Hex value holds its 64 bits in _integer.
    private ulong Unsigned => unchecked((ulong)_integer);

    private string FormatDecimal()
    {
        // Zero is never shown as "-0.0".
        double value = _decimal == 0 ? 0 : _decimal;
        string text = value.ToString("F" + Digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return ShowsSign && value >= 0 ? "+" + text : text;
    }

    private static string WithUnit(string number, string? unit) => unit is null ? number : number + " " + unit;

    private static bool Append(Span<char> destination, ref int written, string? text)
    {
        if (text is null || !text.AsSpan().TryCopyTo(destination[written..]))
        {
            return text is null;
        }

        written += text.Length;
        return true;
    }

    private static bool AppendUnit(Span<char> destination, ref int written, string? unit) =>
        unit is null || (Append(destination, ref written, " ") && Append(destination, ref written, unit));

    private static bool Advance(ref int written, int count)
    {
        written += count;
        return true;
    }
}
