using System.Globalization;
using System.Text.RegularExpressions;
using BingeRide.Core.Workouts;

namespace BingeRide.Workouts.Parsers;

// Reads intervals.icu plain-text workouts: "- 10m 55%" step lines, "Main set 5x" repeat headers,
// zones (Z1..Z7), ramps, ranges, freeride, cadence, and our slope extension ("- 5m slope 6%").
// Word-only lines (e.g. "Warmup") are section titles and become the next step's cue.
internal static partial class IcuTextParser
{
    public const string Format = "icu";

    readonly record struct PowerSpec(double Low, double High, PowerUnit Unit, bool IsRange)
    {
        public double Mid => (Low + High) / 2;
    }

    public static Workout Parse(string text, string fallbackName, List<string> warnings)
    {
        var output = new List<WorkoutStep>();
        List<WorkoutStep>? repeatBody = null;
        var repeatTimes = 0;
        var repeatLine = 0;
        string? repeatText = null;
        string? section = null;

        void CloseRepeat()
        {
            if (repeatBody is null) return;
            if (repeatBody.Count == 0)
                throw Error(repeatLine, $"the repeat \"{repeatTimes}x\" has no step lines under it.");
            StepBuilder.AddRepeat(output, repeatBody, repeatTimes, repeatText);
            repeatBody = null;
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                CloseRepeat();
                continue;
            }

            if (TryGetBulletBody(line, out var body))
            {
                var step = ParseStep(body, lineNo);
                if (section is not null)
                {
                    step = StepBuilder.AddCue(step, new WorkoutCue(0, section));
                    section = null;
                }
                (repeatBody ?? output).Add(step);
                if (output.Count > StepBuilder.MaxSteps) throw new WorkoutFormatException("The workout has too many steps.");
                continue;
            }

            CloseRepeat();
            var heading = line.TrimStart('#').Trim();
            if (RepeatHeader().Match(heading) is { Success: true } repeat)
            {
                if (!int.TryParse(repeat.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out repeatTimes)
                    || repeatTimes is < 1 or > StepBuilder.MaxRepeat)
                    throw Error(lineNo, $"the repeat count must be between 1 and {StepBuilder.MaxRepeat}.");
                repeatText = TextUtil.Blank(repeat.Groups["text"].Value.Trim().TrimEnd(':', '-', '–').Trim()) ?? section;
                section = null;
                repeatBody = [];
                repeatLine = lineNo;
                continue;
            }

            if (!heading.Any(char.IsDigit))
            {
                section = TextUtil.Blank(heading.TrimEnd(':').Trim());
                continue;
            }

            throw Error(lineNo, $"can't understand \"{TextUtil.Shorten(line)}\". Step lines start with \"-\", e.g. \"- 10m 65%\".");
        }
        CloseRepeat();

        if (output.Count == 0)
            throw new WorkoutFormatException("No workout steps found. Step lines look like \"- 10m 65%\".");
        return new Workout { Name = fallbackName, Steps = output, SourceFormat = Format };
    }

    // True for "- 10m 55%"-style lines that contain a duration token (used for format detection).
    public static bool IsStepLine(string line)
    {
        if (!TryGetBulletBody(line.Trim(), out var body)) return false;
        foreach (var token in Tokens(Prepare(body)))
            if (TryParseDuration(token, out _)) return true;
        return false;
    }

    public static bool IsRepeatHeader(string line) => RepeatHeader().IsMatch(line.Trim().TrimStart('#').Trim());

    static bool TryGetBulletBody(string line, out string body)
    {
        body = "";
        if (line.Length < 2 || line[0] is not ('-' or '*' or '•') || line[1] is '-' or '*') return false;
        body = line[1..].Trim();
        return body.Length > 0;
    }

    static string Prepare(string body)
    {
        var s = JoinPercent().Replace(body, "$1%");
        s = JoinWatts().Replace(s, "$1w");
        s = JoinRpm().Replace(s, "$1rpm");
        s = JoinTimeUnit().Replace(s, "$1$2");
        return JoinRange().Replace(s, "$1-$2");
    }

    static string[] Tokens(string s) => s.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);

    static WorkoutStep ParseStep(string body, int lineNo)
    {
        var tokens = Tokens(Prepare(body));
        double? duration = null;
        bool ramp = false, free = false, bareFree = false, slope = false;
        PowerSpec? power = null, power2 = null;
        int? cadence = null, gear = null;
        var words = new List<string>();

        for (var k = 0; k < tokens.Length; k++)
        {
            var token = tokens[k].Trim('(', ')', ';', '@');
            if (!token.Any(char.IsLetterOrDigit)) continue; // stray punctuation such as "@" or "|"
            var lower = token.ToLowerInvariant();

            if (TryParseDuration(token, out var sec))
            {
                if (duration is not null) throw Error(lineNo, $"two durations (\"{token}\"); one step per line.");
                duration = sec;
                continue;
            }
            if (Distance().IsMatch(lower))
                throw Error(lineNo, $"distance steps like \"{token}\" aren't supported; use a duration such as 10m or 30s.");

            switch (lower)
            {
                case "ramp":
                    ramp = true;
                    continue;
                case "freeride":
                    free = true;
                    continue;
                case "free" when k + 1 < tokens.Length && tokens[k + 1].Equals("ride", StringComparison.OrdinalIgnoreCase):
                    free = true;
                    k++;
                    continue;
                case "free":
                    bareFree = true; // "- 5m free" is a freeride, "- 5m 50% free spin" is just text
                    break;
                case "slope" or "grade" when IsPercent(tokens, k + 1) || IsPercent(tokens, k - 1):
                    slope = true; // only next to a % value, so "on a gentle slope" stays text
                    continue;
                case "ftp" or "%ftp":
                    continue; // "75% FTP"
                case "of" when k + 1 < tokens.Length && tokens[k + 1].Equals("ftp", StringComparison.OrdinalIgnoreCase):
                    continue; // "75% of FTP"
                case "gear":
                    if (k + 1 < tokens.Length && int.TryParse(tokens[k + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g))
                    {
                        gear = g;
                        k++;
                        continue;
                    }
                    throw Error(lineNo, "\"gear\" must be followed by a gear number, e.g. \"gear 12\".");
            }

            if (TryParsePower(lower, out var p))
            {
                if (power is null) power = p;
                else if (power2 is null) power2 = p;
                else throw Error(lineNo, $"too many power targets (\"{token}\").");
                continue;
            }
            if (Cadence().Match(lower) is { Success: true } rpm)
            {
                var lo = double.Parse(rpm.Groups["a"].Value, CultureInfo.InvariantCulture);
                var hi = rpm.Groups["b"].Success ? double.Parse(rpm.Groups["b"].Value, CultureInfo.InvariantCulture) : lo;
                cadence = (int)Math.Round((lo + hi) / 2);
                continue;
            }
            if (lower is "hr" or "lthr" or "pace" || lower.EndsWith("hr", StringComparison.Ordinal) || lower.EndsWith("pace", StringComparison.Ordinal))
                throw Error(lineNo, "heart-rate and pace targets aren't supported; use power (%, w or zones), slope or freeride.");
            if (token.Any(char.IsDigit))
                throw Error(lineNo, $"can't understand \"{token}\".");
            words.Add(token);
        }

        if (bareFree && power is null && !slope)
        {
            free = true;
            words.RemoveAt(words.FindIndex(w => w.Equals("free", StringComparison.OrdinalIgnoreCase)));
        }
        if (duration is not { } length) throw Error(lineNo, "no duration found (e.g. 10m, 30s, 1m30).");
        if (length <= 0) throw Error(lineNo, "the duration must be positive.");
        var cues = StepBuilder.Cues(words.Count > 0 ? string.Join(' ', words) : null);

        if (free)
        {
            if (power is not null || slope || ramp) throw Error(lineNo, "a freeride step can't also have a power target, ramp or slope.");
            return new WorkoutStep { Kind = StepKind.Free, DurationSec = length, Gear = gear, Cadence = cadence, Cues = cues };
        }

        if (slope)
        {
            if (power is not { Unit: PowerUnit.PercentFtp } grade || power2 is not null || ramp)
                throw Error(lineNo, "a slope step needs exactly one grade in %, e.g. \"- 5m slope 6%\".");
            return new WorkoutStep
            {
                Kind = StepKind.Slope,
                DurationSec = length,
                Grade = grade.IsRange ? grade.Mid : grade.Low,
                Gear = gear,
                Cadence = cadence,
                Cues = cues,
            };
        }

        if (power is not { } target)
            throw Error(lineNo, "no power target found (e.g. 75%, 200w, Z2, ramp 50-75% or freeride).");
        if (gear is not null) throw Error(lineNo, "\"gear\" only applies to slope and freeride steps.");

        if (ramp)
        {
            PowerTarget from, to;
            if (power2 is { } end)
            {
                if (target.IsRange || end.IsRange || target.Unit != end.Unit)
                    throw Error(lineNo, "write a ramp as \"ramp 50-75%\" or \"ramp 50% 75%\".");
                (from, to) = (Target(target.Low, target.Unit), Target(end.Low, end.Unit));
            }
            else if (target.IsRange)
            {
                (from, to) = (Target(target.Low, target.Unit), Target(target.High, target.Unit));
            }
            else
            {
                throw Error(lineNo, "a ramp needs a start and an end, e.g. \"ramp 50-75%\".");
            }
            return StepBuilder.Erg(length, from, to, cadence, cues);
        }

        if (power2 is not null) throw Error(lineNo, "two power targets; for a ramp write \"ramp 50-75%\".");
        var steady = Target(target.IsRange ? target.Mid : target.Low, target.Unit);
        return StepBuilder.Erg(length, steady, steady, cadence, cues);
    }

    static PowerTarget Target(double value, PowerUnit unit) => new(Math.Round(value, 2), unit);

    static bool IsPercent(string[] tokens, int index) =>
        index >= 0 && index < tokens.Length && TryParsePower(tokens[index].ToLowerInvariant(), out var p) && p.Unit == PowerUnit.PercentFtp;

    static bool TryParsePower(string token, out PowerSpec spec)
    {
        spec = default;
        if (PercentRange().Match(token) is { Success: true } pr)
        {
            spec = new PowerSpec(Num(pr, "a"), Num(pr, "b"), PowerUnit.PercentFtp, true);
            return true;
        }
        if (Percent().Match(token) is { Success: true } p)
        {
            spec = new PowerSpec(Num(p, "a"), Num(p, "a"), PowerUnit.PercentFtp, false);
            return true;
        }
        if (WattsRange().Match(token) is { Success: true } wr)
        {
            spec = new PowerSpec(Num(wr, "a"), Num(wr, "b"), PowerUnit.Watts, true);
            return true;
        }
        if (Watts().Match(token) is { Success: true } w)
        {
            spec = new PowerSpec(Num(w, "a"), Num(w, "a"), PowerUnit.Watts, false);
            return true;
        }
        if (Zone().Match(token) is { Success: true } z
            && PowerZones.TryGetMidpoint(z.Groups["a"].Value[0] - '0', out var low)
            && PowerZones.TryGetMidpoint(z.Groups["b"].Success ? z.Groups["b"].Value[0] - '0' : z.Groups["a"].Value[0] - '0', out var high))
        {
            spec = new PowerSpec(low, high, PowerUnit.PercentFtp, z.Groups["b"].Success);
            return true;
        }
        return false;

        static double Num(Match m, string group) => double.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
    }

    // Parses 1h, 10m, 30s, 1m30, 1m30s, 1h30, 90s, 5', 30", 5'30", 2.5m, 10min, 30sec, 1hr.
    internal static bool TryParseDuration(string token, out double seconds)
    {
        seconds = 0;
        var m = DurationToken().Match(token);
        if (!m.Success) return false;
        var (h, min, s) = (m.Groups["h"], m.Groups["m"], m.Groups["s"]);
        if (!h.Success && !min.Success && !s.Success) return false;

        var bareTail = s.Success && !m.Groups["su"].Success;
        if (bareTail && !h.Success && !min.Success) return false; // a bare number is not a duration

        double Value(Group g) => g.Success ? double.Parse(g.Value, CultureInfo.InvariantCulture) : 0;
        seconds = Value(h) * 3600 + Value(min) * 60;
        // "1h30" means 1 h 30 min; "1m30" and "5'30" mean 30 s.
        seconds += bareTail && !min.Success ? Value(s) * 60 : Value(s);
        return true;
    }

    static WorkoutFormatException Error(int line, string message) => new($"Line {line}: {message}");

    [GeneratedRegex(@"^(?:(?<h>\d+(?:\.\d+)?)(?:hrs|hr|h))?(?:(?<m>\d+(?:\.\d+)?)(?:mins|min|m|'))?(?:(?<s>\d+(?:\.\d+)?)(?<su>secs|sec|s|"")?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationToken();

    [GeneratedRegex(@"^\d+(?:\.\d+)?(?:km|mtr|mi|miles?|yd|yds)$")]
    private static partial Regex Distance();

    [GeneratedRegex(@"^(?<a>\d+(?:\.\d+)?)%?(?:ftp)?-(?<b>\d+(?:\.\d+)?)%(?:ftp)?$")]
    private static partial Regex PercentRange();

    [GeneratedRegex(@"^(?<a>-?\d+(?:\.\d+)?)%(?:ftp)?$")]
    private static partial Regex Percent();

    [GeneratedRegex(@"^(?<a>\d+(?:\.\d+)?)w?-(?<b>\d+(?:\.\d+)?)w$")]
    private static partial Regex WattsRange();

    [GeneratedRegex(@"^(?<a>\d+(?:\.\d+)?)w$")]
    private static partial Regex Watts();

    [GeneratedRegex(@"^z(?<a>[1-7])(?:-z?(?<b>[1-7]))?$")]
    private static partial Regex Zone();

    [GeneratedRegex(@"^(?<a>\d+)(?:-(?<b>\d+))?rpm$")]
    private static partial Regex Cadence();

    [GeneratedRegex(@"^(?<text>.*?)\s*(?<n>\d+)\s*[x×]\s*:?$", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatHeader();

    [GeneratedRegex(@"(\d)\s+%")]
    private static partial Regex JoinPercent();

    [GeneratedRegex(@"(\d)\s*(?:watts?|w)\b", RegexOptions.IgnoreCase)]
    private static partial Regex JoinWatts();

    [GeneratedRegex(@"(\d)\s*rpm\b", RegexOptions.IgnoreCase)]
    private static partial Regex JoinRpm();

    [GeneratedRegex(@"(\d)\s+(hrs|hr|mins|min|secs|sec)\b", RegexOptions.IgnoreCase)]
    private static partial Regex JoinTimeUnit();

    [GeneratedRegex(@"(\d%?w?)\s+-\s*(\d)")]
    private static partial Regex JoinRange();
}
