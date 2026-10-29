using System.Text.RegularExpressions;
using MyWahoo.Core.Workouts;

namespace MyWahoo.Workouts.Parsers;

// Reads TrainerRoad / GoldenCheetah style .erg (watts) and .mrc (%FTP) files: a [COURSE HEADER],
// a [COURSE DATA] polyline of (minutes, value) points and an optional [COURSE TEXT] cue list.
internal static partial class ErgMrcParser
{
    public const string ErgFormat = "erg";
    public const string MrcFormat = "mrc";

    // Parses the document; formatHint ("erg"/"mrc") is used only when the MINUTES line is missing.
    public static Workout Parse(string text, string fallbackName, string formatHint, List<string> warnings)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var points = new List<(double Minutes, double Value, int Line)>();
        var cues = new List<(double Sec, string Text)>();
        string? format = null;
        var section = "";

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var lineNo = i + 1;
            if (line.Length == 0 || line[0] == ';') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                section = string.Join(' ', line[1..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
                continue;
            }

            if (MinutesLine().Match(line) is { Success: true } minutes)
            {
                format = minutes.Groups[1].Value.Equals("WATTS", StringComparison.OrdinalIgnoreCase) ? ErgFormat : MrcFormat;
                continue;
            }

            switch (section)
            {
                case "COURSE HEADER":
                    var eq = line.IndexOf('=');
                    if (eq > 0) header[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                    else warnings.Add($"Line {lineNo}: ignored header line \"{TextUtil.Shorten(line)}\".");
                    break;
                case "COURSE DATA":
                    var fields = line.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 2 || !TextUtil.TryParseNumber(fields[0], out var min) || !TextUtil.TryParseNumber(fields[1], out var value))
                        throw new WorkoutFormatException($"Line {lineNo}: expected \"minutes value\" in [COURSE DATA], got \"{TextUtil.Shorten(line)}\".");
                    points.Add((min, value, lineNo));
                    break;
                case "COURSE TEXT":
                    cues.Add(ParseCue(line, lineNo));
                    break;
            }
        }

        if (format is null)
        {
            format = formatHint == MrcFormat ? MrcFormat : ErgFormat;
            warnings.Add($"No \"MINUTES WATTS\" / \"MINUTES PERCENT\" line; values were read as {(format == ErgFormat ? "watts" : "% of FTP")}.");
        }
        if (points.Count < 2)
            throw new WorkoutFormatException("The [COURSE DATA] section needs at least two \"minutes value\" points.");

        var steps = BuildSteps(points, format, warnings);
        foreach (var (sec, cueText) in cues.OrderBy(c => c.Sec))
            StepBuilder.PlaceCue(steps, 0, sec, cueText); // course text times are absolute

        double? ftp = header.TryGetValue("FTP", out var ftpText) && TextUtil.TryParseNumber(ftpText, out var f) && f > 0 ? f : null;
        return new Workout
        {
            Name = TextUtil.BaseName(header.GetValueOrDefault("FILE NAME")) ?? fallbackName,
            Description = TextUtil.Blank(header.GetValueOrDefault("DESCRIPTION")),
            AuthorFtp = ftp,
            Steps = steps,
            SourceFormat = format,
        };
    }

    static (double Sec, string Text) ParseCue(string line, int lineNo)
    {
        // Canonical: seconds<TAB>text[<TAB>duration]; also accept "seconds text" with spaces.
        var tab = line.Split('\t', StringSplitOptions.TrimEntries);
        if (tab.Length >= 2 && TextUtil.TryParseNumber(tab[0], out var sec)) return (sec, tab[1]);
        if (CueLine().Match(line) is { Success: true } m && TextUtil.TryParseNumber(m.Groups[1].Value, out sec))
            return (sec, m.Groups[2].Value.Trim());
        throw new WorkoutFormatException($"Line {lineNo}: expected \"seconds<TAB>text\" in [COURSE TEXT], got \"{TextUtil.Shorten(line)}\".");
    }

    static List<WorkoutStep> BuildSteps(List<(double Minutes, double Value, int Line)> points, string format, List<string> warnings)
    {
        var segments = new List<(double Sec, double From, double To)>();
        double carry = 0;
        var tiny = 0;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var sec = (b.Minutes - a.Minutes) * 60;
            if (sec < -1e-6)
                throw new WorkoutFormatException($"Line {b.Line}: time goes backwards ({b.Minutes:0.##} min after {a.Minutes:0.##} min).");
            if (sec < 1e-6) continue; // vertical jump: no step

            var segment = (Sec: sec, From: Math.Round(a.Value, 2), To: Math.Round(b.Value, 2));
            if (sec < 1)
            {
                // Sub-second transitions (e.g. 0.01 min) can't be ridden; fold them into a neighbour.
                tiny++;
                if (segments.Count > 0) segments[^1] = segments[^1] with { Sec = segments[^1].Sec + sec };
                else carry += sec;
                continue;
            }
            segment.Sec += carry;
            carry = 0;

            if (segments.Count > 0 && segment.From == segment.To && segments[^1] is var last && last.From == last.To && last.From == segment.From)
                segments[^1] = last with { Sec = last.Sec + segment.Sec };
            else
                segments.Add(segment);
        }
        if (tiny > 0) warnings.Add($"{tiny} transition(s) shorter than 1 s were merged into neighbouring steps.");
        if (segments.Count == 0) throw new WorkoutFormatException("The [COURSE DATA] points don't describe any steps.");

        Func<double, PowerTarget> target = format == ErgFormat ? PowerTarget.Watts : PowerTarget.Percent;
        return [.. segments.Select(s => StepBuilder.Erg(s.Sec, target(s.From), target(s.To), null, []))];
    }

    [GeneratedRegex(@"^MINUTES\s+(WATTS|PERCENT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MinutesLine();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s+(.+)$")]
    private static partial Regex CueLine();
}
