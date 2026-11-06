using System.Xml;
using System.Xml.Linq;

namespace BingeRide.Workouts.Parsers;

// Safe XML loading plus case-insensitive, namespace-agnostic element/attribute lookup.
internal static class XmlUtil
{
    static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static XDocument Load(string text, string what)
    {
        // An XML declaration preceded by blank lines (common in pasted text) is a hard error for
        // XmlReader; blank it out instead of trimming so line numbers stay correct.
        var first = 0;
        while (first < text.Length && char.IsWhiteSpace(text[first])) first++;
        if (first > 0 && string.CompareOrdinal(text, first, "<?xml", 0, 5) == 0)
        {
            var end = text.IndexOf("?>", first, StringComparison.Ordinal);
            if (end > 0) text = string.Concat(text.AsSpan(0, first), new string(' ', end + 2 - first), text.AsSpan(end + 2));
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), Settings);
            var doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
            return doc.Root is null ? throw new WorkoutFormatException($"The {what} file has no root element.") : doc;
        }
        catch (XmlException ex)
        {
            throw new WorkoutFormatException(
                $"The {what} file isn't valid XML (line {ex.LineNumber}, position {ex.LinePosition}): {StripPosition(ex.Message)}", ex);
        }
    }

    static string StripPosition(string message)
    {
        var i = message.LastIndexOf(" Line ", StringComparison.Ordinal);
        return i > 0 ? message[..i] : message;
    }

    public static int Line(XObject node) => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    public static bool Is(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    public static XElement? Child(XElement element, string localName)
    {
        foreach (var child in element.Elements())
            if (Is(child, localName)) return child;
        return null;
    }

    public static IEnumerable<XElement> Children(XElement element, string localName) =>
        element.Elements().Where(e => Is(e, localName));

    public static IEnumerable<XElement> Descendants(XElement element, string localName) =>
        element.Descendants().Where(e => Is(e, localName));

    public static string? ChildText(XElement element, string localName) =>
        Child(element, localName) is { } child ? TextUtil.Blank(child.Value) : null;

    public static XAttribute? Attr(XElement element, string name)
    {
        foreach (var attribute in element.Attributes())
            if (string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)) return attribute;
        return null;
    }

    public static bool HasAttr(XElement element, string name) => Attr(element, name) is not null;

    // Numeric attribute value, null if absent; throws a line-referenced error if not a number.
    public static double? Number(XElement element, string name, string what)
    {
        if (Attr(element, name) is not { } attribute || string.IsNullOrWhiteSpace(attribute.Value)) return null;
        if (TextUtil.TryParseNumber(attribute.Value, out var value)) return value;
        throw new WorkoutFormatException(
            $"{what} line {Line(element)}: <{element.Name.LocalName}> {attribute.Name.LocalName}=\"{attribute.Value}\" is not a number.");
    }

    // Numeric child-element value (e.g. <ele>), null if absent or blank.
    public static double? ChildNumber(XElement element, string localName, string what)
    {
        if (ChildText(element, localName) is not { } text) return null;
        if (TextUtil.TryParseNumber(text, out var value)) return value;
        throw new WorkoutFormatException($"{what} line {Line(element)}: <{localName}> value \"{text}\" is not a number.");
    }
}
