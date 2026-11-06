using System.Globalization;
using System.Text;

namespace BingeRide.Workouts.Parsers;

// Text clean-up and small parsing helpers shared by the text-based parsers.
internal static class TextUtil
{
    const string CurlyDoubleQuotes = "“”„‟″";

    // Strips BOM / zero-width characters, converts CRLF and CR to LF, replaces non-breaking spaces,
    // curly single quotes, primes, en dashes and minus signs with their ASCII forms. Curly double
    // quotes are left alone (see StraightenDoubleQuotes) because inside a JSON string
    // they are legitimate text.
    public static string Normalize(string text)
    {
        if (text.Length == 0) return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '﻿' or '​' or '‌' or '‍' or '⁠':
                    break;
                case '\r':
                    sb.Append('\n');
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    break;
                case ' ' or ' ' or ' ' or ' ' or ' ' or ' ' or ' ':
                    sb.Append(' ');
                    break;
                case '‘' or '’' or '‚' or '‛' or '′':
                    sb.Append('\'');
                    break;
                case '–' or '−':
                    sb.Append('-');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    public static bool HasCurlyDoubleQuotes(string text) => text.AsSpan().IndexOfAny(CurlyDoubleQuotes) >= 0;

    public static string StraightenDoubleQuotes(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (CurlyDoubleQuotes.Contains(chars[i])) chars[i] = '"';
        return new string(chars);
    }

    public static bool TryParseNumber(ReadOnlySpan<char> text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    // Null for null/blank input, otherwise the trimmed text.
    public static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // File name without directory or extension, or null.
    public static string? BaseName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = fileName.Trim();
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) name = name[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        return Blank(name);
    }

    public static string Shorten(string text, int max = 40) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
}
