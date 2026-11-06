using System.Text.Json;
using BingeRide.Core.Workouts;

namespace BingeRide.Workouts.Parsers;

// Reads the native "bingeride-workout" v1 JSON format (see docs/workout-format.md). Claude writes it,
// so it is lenient: comments, trailing commas, case-insensitive keys, "min"/"duration" aliases,
// FTP fractions and a missing power_unit are all accepted with warnings.
internal static class JsonWorkoutParser
{
    public const string Format = "json";
    public const string FormatName = "bingeride-workout";
    // Files written before the app was renamed from MyWahoo.
    public const string LegacyFormatName = "mywahoo-workout";

    static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    const string StepTypes = "steady, ramp, slope, free or repeat";

    public static Workout Parse(string text, string fallbackName, List<string> warnings)
    {
        using var doc = LoadDocument(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new WorkoutFormatException(
                "The workout must be a JSON object like {\"format\": \"bingeride-workout\", \"version\": 1, \"name\": \"…\", \"steps\": [...]}.");

        var top = new JsonObj(root, "workout");
        top.MarkUsed("$schema");

        var format = top.GetString("format");
        if (format is null) warnings.Add($"Missing \"format\": \"{FormatName}\"; reading it anyway.");
        else if (!format.Trim().Equals(FormatName, StringComparison.OrdinalIgnoreCase)
                 && !format.Trim().Equals(LegacyFormatName, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Unexpected format \"{format}\"; reading it as {FormatName}.");

        var version = top.GetNumber("version");
        if (version is null) warnings.Add("Missing \"version\": 1; assuming version 1.");
        else if (version != 1) warnings.Add($"Version {version} isn't known; reading it as version 1.");

        var name = TextUtil.Blank(top.GetString("name"));
        if (name is null)
        {
            warnings.Add($"No \"name\"; using \"{fallbackName}\".");
            name = fallbackName;
        }

        var ftp = top.GetNumber("ftp");
        if (ftp is <= 0)
        {
            warnings.Add("Ignored \"ftp\" because it isn't a positive number of watts.");
            ftp = null;
        }

        var tags = ReadTags(top, warnings);

        if (!top.TryGet("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array)
            throw new WorkoutFormatException("The workout needs a \"steps\" array.");
        if (stepsElement.GetArrayLength() == 0)
            throw new WorkoutFormatException("The \"steps\" array is empty.");

        var reader = new StepReader(ResolveUnit(top, stepsElement, warnings), warnings);
        var steps = new List<WorkoutStep>();
        var index = 0;
        foreach (var item in stepsElement.EnumerateArray())
            reader.Read(item, $"steps[{index++}]", steps, insideRepeat: false);

        var workout = new Workout
        {
            Name = name,
            Description = TextUtil.Blank(top.GetString("description")),
            Author = TextUtil.Blank(top.GetString("author")),
            AuthorFtp = ftp,
            Tags = tags,
            Steps = steps,
            SourceFormat = Format,
        };
        top.WarnUnknown(warnings);
        return workout;
    }

    static JsonDocument LoadDocument(string text)
    {
        try
        {
            return JsonDocument.Parse(text, Options);
        }
        catch (JsonException ex)
        {
            var message = ex.Message;
            var cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
            if (cut > 0) message = message[..cut];
            var where = ex.LineNumber is { } line ? $" at line {line + 1}, column {(ex.BytePositionInLine ?? 0) + 1}" : "";
            throw new WorkoutFormatException($"Invalid JSON{where}: {message}", ex);
        }
    }

    static IReadOnlyList<string> ReadTags(JsonObj top, List<string> warnings)
    {
        if (!top.TryGet("tags", out var element)) return [];
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                return [.. element.EnumerateArray()
                    .Select(t => TextUtil.Blank(t.ValueKind == JsonValueKind.String ? t.GetString() : t.GetRawText()))
                    .OfType<string>()];
            case JsonValueKind.String:
                return [.. (element.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            default:
                warnings.Add("Ignored \"tags\" because it isn't an array of strings.");
                return [];
        }
    }

    static PowerUnit ResolveUnit(JsonObj top, JsonElement steps, List<string> warnings)
    {
        var text = top.GetString("power_unit") ?? top.GetString("powerUnit");
        if (text is not null)
        {
            return text.Trim().ToLowerInvariant() switch
            {
                "pct_ftp" or "pct" or "percent" or "%ftp" or "% ftp" or "%" or "ftp" or "percent_ftp" => PowerUnit.PercentFtp,
                "watts" or "watt" or "w" => PowerUnit.Watts,
                _ => throw new WorkoutFormatException($"Unknown \"power_unit\" \"{text}\"; use \"pct_ftp\" or \"watts\"."),
            };
        }

        var values = new List<double>();
        CollectPowerValues(steps, values);
        if (values.Count == 0) return PowerUnit.PercentFtp;
        if (values.All(v => v <= 300))
        {
            warnings.Add("No \"power_unit\"; the values look like % of FTP, so \"pct_ftp\" was assumed.");
            return PowerUnit.PercentFtp;
        }
        warnings.Add("No \"power_unit\"; some values are above 300, so \"watts\" was assumed.");
        return PowerUnit.Watts;
    }

    static void CollectPowerValues(JsonElement steps, List<double> values)
    {
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object) continue;
            foreach (var property in step.EnumerateObject())
            {
                if (property.Name.Equals("steps", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.Array) CollectPowerValues(property.Value, values);
                }
                else if (property.Name.Equals("power", StringComparison.OrdinalIgnoreCase)
                         || property.Name.Equals("from", StringComparison.OrdinalIgnoreCase)
                         || property.Name.Equals("to", StringComparison.OrdinalIgnoreCase))
                {
                    if (JsonObj.TryReadNumber(property.Value, out var value)) values.Add(value);
                }
            }
        }
    }

    // Reads steps with a fixed power unit, flattening repeats into the output list.
    sealed class StepReader(PowerUnit unit, List<string> warnings)
    {
        bool _fractionWarned;

        public void Read(JsonElement item, string path, List<WorkoutStep> output, bool insideRepeat)
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new WorkoutFormatException(
                    $"{path}: each step must be an object like {{\"type\": \"steady\", \"sec\": 300, \"power\": 75}}.");

            var o = new JsonObj(item, path);
            var type = ResolveType(o);
            o.Path = $"{path} ({type})";

            switch (type)
            {
                case "repeat":
                    ReadRepeat(o, path, output, insideRepeat);
                    break;
                case "steady":
                {
                    var sec = Length(o, allowDistance: false).Sec;
                    var power = Power(o, "power") ?? throw o.Error("power", "is required");
                    output.Add(StepBuilder.Erg(sec, power, power, Rpm(o), Cues(o, sec)));
                    break;
                }
                case "ramp":
                {
                    var sec = Length(o, allowDistance: false).Sec;
                    var from = Power(o, "from") ?? throw o.Error("from", "is required (start power)");
                    var to = Power(o, "to") ?? throw o.Error("to", "is required (end power)");
                    output.Add(StepBuilder.Erg(sec, from, to, Rpm(o), Cues(o, sec)));
                    break;
                }
                case "slope":
                {
                    var (sec, metres) = Length(o, allowDistance: true);
                    var grade = o.GetNumber("grade") ?? throw o.Error("grade", "is required (road grade in %, e.g. 4.5)");
                    output.Add(new WorkoutStep
                    {
                        Kind = StepKind.Slope,
                        DurationSec = sec,
                        DistanceM = metres,
                        Grade = grade,
                        Gear = Gear(o),
                        Cadence = Rpm(o),
                        Cues = Cues(o, sec),
                    });
                    break;
                }
                case "free":
                {
                    var (sec, metres) = Length(o, allowDistance: true);
                    output.Add(new WorkoutStep
                    {
                        Kind = StepKind.Free,
                        DurationSec = sec,
                        DistanceM = metres,
                        Grade = o.GetNumber("grade"),
                        Gear = Gear(o),
                        Cadence = Rpm(o),
                        Cues = Cues(o, sec),
                    });
                    break;
                }
            }
            o.WarnUnknown(warnings);
        }

        void ReadRepeat(JsonObj o, string path, List<WorkoutStep> output, bool insideRepeat)
        {
            if (insideRepeat)
                throw new WorkoutFormatException(
                    $"{o.Path}: repeats can only be nested one level deep; put the inner steps directly in the outer repeat.");

            var times = o.GetInteger("times") ?? o.GetInteger("count") ?? throw o.Error("times", "is required (how many times to repeat)");
            if (times is < 1 or > StepBuilder.MaxRepeat)
                throw o.Error("times", $"must be between 1 and {StepBuilder.MaxRepeat}");
            if (!o.TryGet("steps", out var children) || children.ValueKind != JsonValueKind.Array || children.GetArrayLength() == 0)
                throw o.Error("steps", "must be a non-empty array of steps");

            var body = new List<WorkoutStep>();
            var index = 0;
            foreach (var child in children.EnumerateArray())
                Read(child, $"{path}.steps[{index++}]", body, insideRepeat: true);

            var start = output.Count;
            StepBuilder.AddRepeat(output, body, times, o.GetString("text"));
            foreach (var cue in CueList(o, durationSec: 0))
                StepBuilder.PlaceCue(output, start, cue.OffsetSec, cue.Text);
        }

        string ResolveType(JsonObj o)
        {
            var text = o.GetString("type");
            if (text is not null)
            {
                return text.Trim().ToLowerInvariant() switch
                {
                    "steady" or "steadystate" or "steady_state" => "steady",
                    "ramp" => "ramp",
                    "slope" or "sim" => "slope",
                    "free" or "freeride" or "free_ride" => "free",
                    "repeat" or "repeats" => "repeat",
                    _ => throw new WorkoutFormatException($"{o.Path}: unknown step type \"{text}\" (use {StepTypes})."),
                };
            }

            var inferred = o.Has("steps") ? "repeat"
                : o.Has("from") || o.Has("to") ? "ramp"
                : o.Has("power") ? "steady"
                : o.Has("grade") ? "slope"
                : throw new WorkoutFormatException($"{o.Path}: missing \"type\" ({StepTypes}).");
            warnings.Add($"{o.Path}: no \"type\"; treated as \"{inferred}\".");
            return inferred;
        }

        static (double Sec, double Metres) Length(JsonObj o, bool allowDistance)
        {
            if (!allowDistance && o.Has("m"))
                throw new WorkoutFormatException(
                    $"{o.Path}: \"m\" is a distance in metres, but ERG steps must be time-based; use \"sec\" or \"min\".");

            double?[] given =
            [
                o.GetNumber("sec"),
                o.GetNumber("seconds"),
                o.GetNumber("duration"),
                o.GetNumber("min") * 60,
                o.GetNumber("minutes") * 60,
                allowDistance ? o.GetNumber("m") : null,
            ];
            var count = given.Count(v => v is not null);
            var choices = allowDistance ? "\"sec\" (or \"min\") or a distance \"m\"" : "\"sec\" (or \"min\")";
            if (count == 0) throw new WorkoutFormatException($"{o.Path}: needs a duration {choices}.");
            if (count > 1) throw new WorkoutFormatException($"{o.Path}: give only one of {choices}.");

            var metres = given[5];
            var value = metres ?? given.First(v => v is not null)!.Value;
            if (value <= 0) throw new WorkoutFormatException($"{o.Path}: the {(metres is null ? "duration" : "distance")} must be positive.");
            return metres is null ? (value, 0) : (0, value);
        }

        PowerTarget? Power(JsonObj o, string name)
        {
            if (o.GetNumber(name) is not { } value) return null;
            if (unit == PowerUnit.Watts) return PowerTarget.Watts(value);
            if (value is > 0 and <= 3)
            {
                value = Math.Round(value * 100, 2);
                if (!_fractionWarned)
                {
                    warnings.Add("Power values of 3 or less were read as fractions of FTP (e.g. 0.75 → 75 %).");
                    _fractionWarned = true;
                }
            }
            return PowerTarget.Percent(value);
        }

        static int? Rpm(JsonObj o) => (o.GetNumber("rpm") ?? o.GetNumber("cadence")) is { } rpm
            ? (int)Math.Round(Math.Clamp(rpm, -1e6, 1e6))
            : null;

        int? Gear(JsonObj o)
        {
            if (o.GetNumber("gear") is not { } gear) return null;
            var rounded = (int)Math.Round(Math.Clamp(gear, -1e6, 1e6));
            if (rounded != gear) warnings.Add($"{o.Path}: gear {gear} rounded to {rounded}.");
            return rounded;
        }

        IReadOnlyList<WorkoutCue> Cues(JsonObj o, double durationSec) =>
            StepBuilder.Cues(o.GetString("text"), CueList(o, durationSec));

        List<WorkoutCue> CueList(JsonObj o, double durationSec)
        {
            var cues = new List<WorkoutCue>();
            if (!o.TryGet("cues", out var array)) return cues;
            if (array.ValueKind != JsonValueKind.Array)
                throw o.Error("cues", "must be an array like [{\"at\": 60, \"text\": \"…\"}]");

            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                var path = $"{o.Path}.cues[{index++}]";
                if (item.ValueKind == JsonValueKind.String)
                {
                    if (TextUtil.Blank(item.GetString()) is { } plain) cues.Add(new WorkoutCue(0, plain));
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object)
                    throw new WorkoutFormatException($"{path}: each cue must be an object like {{\"at\": 60, \"text\": \"…\"}}.");

                var c = new JsonObj(item, path);
                var at = c.GetNumber("at") ?? c.GetNumber("offset") ?? c.GetNumber("sec") ?? 0;
                var text = TextUtil.Blank(c.GetString("text") ?? c.GetString("message")) ?? throw c.Error("text", "is required");
                if (at < 0) throw c.Error("at", "can't be negative");
                if (durationSec > 0 && at >= durationSec)
                    warnings.Add($"{path}: the cue at {at:0.#} s is past the end of its {durationSec:0.#} s step.");
                c.WarnUnknown(warnings);
                cues.Add(new WorkoutCue(at, text));
            }
            return cues;
        }
    }
}
