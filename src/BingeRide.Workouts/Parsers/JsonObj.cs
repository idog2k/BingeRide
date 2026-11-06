using System.Text.Json;

namespace BingeRide.Workouts.Parsers;

// Case-insensitive view over a JSON object that remembers which properties were read, so the
// leftovers can be reported as "unknown property" warnings. JSON nulls count as absent.
internal sealed class JsonObj(JsonElement element, string path)
{
    readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    // Location used in messages, e.g. "steps[2] (steady)".
    public string Path { get; set; } = path;

    public bool Has(string name) => Find(name, out _);

    public void MarkUsed(string name) => _used.Add(name);

    public bool TryGet(string name, out JsonElement value)
    {
        _used.Add(name);
        return Find(name, out value);
    }

    bool Find(string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Null
                && string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    public string? GetString(string name)
    {
        if (!TryGet(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => throw Error(name, "must be a text string"),
        };
    }

    public double? GetNumber(string name)
    {
        if (!TryGet(name, out var value)) return null;
        return TryReadNumber(value, out var number) ? number : throw Error(name, "must be a number");
    }

    public int? GetInteger(string name)
    {
        if (GetNumber(name) is not { } number) return null;
        if (Math.Abs(number - Math.Round(number)) > 1e-9 || Math.Abs(number) > int.MaxValue)
            throw Error(name, "must be a whole number");
        return (int)Math.Round(number);
    }

    // Accepts JSON numbers and numeric strings such as "75" or "75%".
    public static bool TryReadNumber(JsonElement value, out double number)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                number = value.GetDouble();
                return double.IsFinite(number);
            case JsonValueKind.String:
                var text = (value.GetString() ?? "").Trim();
                if (text.EndsWith('%')) text = text[..^1];
                return TextUtil.TryParseNumber(text, out number);
            default:
                number = 0;
                return false;
        }
    }

    public WorkoutFormatException Error(string name, string problem) => new($"{Path}: \"{name}\" {problem}.");

    public void WarnUnknown(List<string> warnings)
    {
        foreach (var property in element.EnumerateObject())
            if (!_used.Contains(property.Name))
                warnings.Add($"{Path}: unknown property \"{property.Name}\" ignored.");
    }
}
