namespace MyWahoo.Workouts.Parsers;

// Content sniffing: whole-document detection and workout-region extraction from chat text.
internal static class FormatDetector
{
    const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    public static string? FromExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".json" => JsonWorkoutParser.Format,
        ".zwo" => ZwoParser.Format,
        ".erg" => ErgMrcParser.ErgFormat,
        ".mrc" => ErgMrcParser.MrcFormat,
        ".txt" => IcuTextParser.Format,
        ".gpx" => GpxParser.Format,
        ".tcx" => TcxParser.Format,
        ".fit" => FitWorkoutParser.Format,
        _ => null,
    };

    // Format of a whole text document, or null if it isn't recognisable.
    public static string? Detect(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.StartsWith("{")) return JsonWorkoutParser.Format;
        if (text.Contains("<workout_file", Ci)) return ZwoParser.Format;
        if (text.Contains("<gpx", Ci)) return GpxParser.Format;
        if (text.Contains("<TrainingCenterDatabase", Ci)) return TcxParser.Format;
        if (text.Contains("[COURSE HEADER]", Ci)) return ErgMrcParser.ErgFormat;
        if (CountIcuStepLines(text) >= 1) return IcuTextParser.Format;
        if (text.Contains('{') && text.Contains("steps", Ci)) return JsonWorkoutParser.Format;
        return null;
    }

    // Finds a workout inside arbitrary text (a chat answer or one code block) and returns the
    // exact region to parse: a JSON object containing "steps", a <workout_file>, an ERG/MRC
    // course, or a run of ≥ 2 intervals.icu step lines.
    public static (string Format, string Region)? Identify(string text)
    {
        if (JsonRegion(text) is { } json) return (JsonWorkoutParser.Format, json);
        if (ZwoRegion(text) is { } zwo) return (ZwoParser.Format, zwo);
        if (ErgRegion(text) is { } erg) return (ErgMrcParser.ErgFormat, erg);
        if (IcuRegion(text) is { } icu) return (IcuTextParser.Format, icu);
        return null;
    }

    public static string DisplayName(string format) => format switch
    {
        JsonWorkoutParser.Format => "MyWahoo JSON",
        ZwoParser.Format => "Zwift ZWO",
        ErgMrcParser.ErgFormat => "ERG",
        ErgMrcParser.MrcFormat => "MRC",
        IcuTextParser.Format => "intervals.icu text",
        GpxParser.Format => "GPX",
        TcxParser.Format => "TCX",
        FitWorkoutParser.Format => "FIT",
        _ => format,
    };

    static int CountIcuStepLines(string text)
    {
        var count = 0;
        foreach (var line in text.Split('\n'))
            if (IcuTextParser.IsStepLine(line)) count++;
        return count;
    }

    static string? JsonRegion(string text)
    {
        var from = 0;
        while (from < text.Length)
        {
            var start = text.IndexOf('{', from);
            if (start < 0) return null;
            var end = MatchingBrace(text, start);
            var region = end < 0 ? text[start..] : text[start..(end + 1)];
            if (region.Contains("steps", Ci)) return region;
            if (end < 0) return null;
            from = end + 1;
        }
        return null;
    }

    // Index of the '}' closing the '{' at start, skipping JSON strings; -1 if unbalanced.
    static int MatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return i;
            }
        }
        return -1;
    }

    static string? ZwoRegion(string text)
    {
        var start = text.IndexOf("<workout_file", Ci);
        if (start < 0) return null;
        var declaration = text.LastIndexOf("<?xml", start, Ci);
        if (declaration >= 0)
        {
            var close = text.IndexOf("?>", declaration, StringComparison.Ordinal);
            if (close > 0 && close < start && text.AsSpan(close + 2, start - close - 2).IsWhiteSpace()) start = declaration;
        }
        const string endTag = "</workout_file>";
        var end = text.IndexOf(endTag, start, Ci);
        return end < 0 ? text[start..] : text[start..(end + endTag.Length)];
    }

    static string? ErgRegion(string text)
    {
        var start = text.IndexOf("[COURSE HEADER]", Ci);
        if (start < 0) return null;
        var endMarker = text.IndexOf("[COURSE TEXT]", start, Ci) >= 0 ? "[END COURSE TEXT]" : "[END COURSE DATA]";
        var end = text.IndexOf(endMarker, start, Ci);
        return end < 0 ? text[start..] : text[start..(end + endMarker.Length)];
    }

    static string? IcuRegion(string text)
    {
        var lines = text.Split('\n');
        int first = -1, last = -1, count = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IcuTextParser.IsStepLine(lines[i])) continue;
            count++;
            if (first < 0) first = i;
            last = i;
        }
        if (count < 2) return null;
        while (first > 0 && IcuTextParser.IsRepeatHeader(lines[first - 1])) first--;
        return string.Join('\n', lines[first..(last + 1)]);
    }
}
