using System.Xml.Linq;
using BingeRide.Core.Workouts;

namespace BingeRide.Workouts.Parsers;

// Reads Zwift .zwo workouts (plus the Auuki Slope/OnSlope/OffSlope extension). Element and
// attribute names are matched case-insensitively; power values are FTP fractions.
internal static class ZwoParser
{
    public const string Format = "zwo";
    const string What = "ZWO";

    public static Workout Parse(string text, string fallbackName, List<string> warnings)
    {
        var root = XmlUtil.Load(text, What).Root!;
        if (!XmlUtil.Is(root, "workout_file"))
            throw new WorkoutFormatException($"Not a Zwift workout: the root element is <{root.Name.LocalName}>, expected <workout_file>.");

        var sport = XmlUtil.ChildText(root, "sportType");
        if (sport is not null && !sport.Equals("bike", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"The workout's sportType is \"{sport}\"; importing it as a bike workout.");

        double? ftp = null;
        if (XmlUtil.ChildText(root, "ftpOverride") is { } ftpText)
        {
            if (TextUtil.TryParseNumber(ftpText, out var value) && value > 0) ftp = value;
            else warnings.Add($"Ignored <ftpOverride>{ftpText}</ftpOverride> because it isn't a positive number.");
        }

        var tags = XmlUtil.Child(root, "tags") is { } tagsElement
            ? XmlUtil.Children(tagsElement, "tag")
                .Select(t => TextUtil.Blank(XmlUtil.Attr(t, "name")?.Value ?? t.Value))
                .OfType<string>()
                .ToList()
            : [];

        var workoutElement = XmlUtil.Child(root, "workout")
            ?? throw new WorkoutFormatException("The ZWO file has no <workout> element.");

        var reader = new ElementReader(warnings);
        var steps = new List<WorkoutStep>();
        var absoluteCues = new List<(double Offset, string Text)>();
        foreach (var element in workoutElement.Elements())
        {
            var start = steps.Count;
            switch (element.Name.LocalName.ToLowerInvariant())
            {
                case "steadystate" or "solidstate":
                    steps.Add(reader.Steady(element));
                    break;
                case "warmup":
                    steps.Add(reader.WarmupCooldown(element, warmup: true));
                    break;
                case "cooldown":
                    steps.Add(reader.WarmupCooldown(element, warmup: false));
                    break;
                case "ramp":
                    steps.Add(reader.Ramp(element));
                    break;
                case "intervalst":
                    reader.Intervals(element, steps);
                    break;
                case "freeride":
                    steps.Add(reader.FreeRide(element));
                    break;
                case "maxeffort":
                    steps.Add(new WorkoutStep
                    {
                        Kind = StepKind.Free,
                        DurationSec = reader.Duration(element, "Duration"),
                        Cues = StepBuilder.Cues("Max effort!"),
                    });
                    break;
                case "textevent":
                    absoluteCues.AddRange(TextEvents([element]));
                    continue;
                default:
                    throw Error(element, $"<{element.Name.LocalName}> isn't a supported workout element");
            }
            foreach (var (offset, message) in TextEvents(XmlUtil.Children(element, "textevent")))
                StepBuilder.PlaceCue(steps, start, offset, message);
        }

        if (steps.Count == 0) throw new WorkoutFormatException("The ZWO <workout> element has no steps.");
        foreach (var (offset, message) in absoluteCues) StepBuilder.PlaceCue(steps, 0, offset, message);

        return new Workout
        {
            Name = XmlUtil.ChildText(root, "name") ?? fallbackName,
            Description = XmlUtil.ChildText(root, "description"),
            Author = XmlUtil.ChildText(root, "author"),
            AuthorFtp = ftp,
            Tags = tags,
            Steps = steps,
            SourceFormat = Format,
        };
    }

    static IEnumerable<(double Offset, string Text)> TextEvents(IEnumerable<XElement> events)
    {
        foreach (var e in events)
        {
            if (TextUtil.Blank(XmlUtil.Attr(e, "message")?.Value) is not { } message) continue;
            yield return (XmlUtil.Number(e, "timeoffset", What) ?? 0, message);
        }
    }

    static WorkoutFormatException Error(XElement element, string problem) =>
        new($"ZWO line {XmlUtil.Line(element)}: {problem}.");

    sealed class ElementReader(List<string> warnings)
    {
        bool _percentWarned;

        public double Duration(XElement e, string attribute)
        {
            var value = XmlUtil.Number(e, attribute, What)
                ?? throw Error(e, $"<{e.Name.LocalName}> needs a {attribute} (seconds)");
            return value > 0 ? value : throw Error(e, $"<{e.Name.LocalName}> {attribute} must be positive");
        }

        // FTP fraction → %FTP. Values above 5 are assumed to already be percentages.
        PowerTarget ToTarget(double fraction)
        {
            if (fraction <= 5) return PowerTarget.Percent(Math.Round(fraction * 100, 2));
            if (!_percentWarned)
            {
                warnings.Add("Some power values are above 5 (500 % FTP); they were read as percentages instead of FTP fractions.");
                _percentWarned = true;
            }
            return PowerTarget.Percent(fraction);
        }

        // Power, or the average of PowerLow/PowerHigh-style attributes.
        PowerTarget? Power(XElement e, string single, string low, string high)
        {
            if (XmlUtil.Number(e, single, What) is { } value) return ToTarget(value);
            var lo = XmlUtil.Number(e, low, What);
            var hi = XmlUtil.Number(e, high, What);
            return (lo ?? hi, hi ?? lo) is ({ } a, { } b) ? ToTarget((a + b) / 2) : null;
        }

        static int? Cadence(XElement e, string attribute) =>
            XmlUtil.Number(e, attribute, What) is { } rpm && rpm > 0 ? (int)Math.Round(Math.Min(rpm, 1e6)) : null;

        static WorkoutStep SlopeStep(double sec, double grade, int? cadence) => new()
        {
            Kind = StepKind.Slope,
            DurationSec = sec,
            Grade = grade,
            Cadence = cadence,
        };

        public WorkoutStep Steady(XElement e)
        {
            var sec = Duration(e, "Duration");
            var cadence = Cadence(e, "Cadence");
            if (XmlUtil.Number(e, "Slope", What) is { } grade) return SlopeStep(sec, grade, cadence);
            var power = Power(e, "Power", "PowerLow", "PowerHigh")
                ?? throw Error(e, $"<{e.Name.LocalName}> needs a Power attribute (FTP fraction, e.g. Power=\"0.75\")");
            return StepBuilder.Erg(sec, power, power, cadence, []);
        }

        public WorkoutStep WarmupCooldown(XElement e, bool warmup)
        {
            var sec = Duration(e, "Duration");
            var cadence = Cadence(e, "Cadence");
            var lo = XmlUtil.Number(e, "PowerLow", What);
            var hi = XmlUtil.Number(e, "PowerHigh", What);
            if (lo is null && hi is null)
            {
                var steady = XmlUtil.Number(e, "Power", What) is { } p
                    ? ToTarget(p)
                    : throw Error(e, $"<{e.Name.LocalName}> needs PowerLow and PowerHigh");
                return StepBuilder.Erg(sec, steady, steady, cadence, []);
            }
            var (a, b) = (lo ?? hi!.Value, hi ?? lo!.Value);
            // Real-world files disagree on which attribute holds the start; a warm-up always
            // climbs and a cool-down always descends.
            var (from, to) = warmup ? (Math.Min(a, b), Math.Max(a, b)) : (Math.Max(a, b), Math.Min(a, b));
            return StepBuilder.Erg(sec, ToTarget(from), ToTarget(to), cadence, []);
        }

        public WorkoutStep Ramp(XElement e)
        {
            var sec = Duration(e, "Duration");
            var lo = XmlUtil.Number(e, "PowerLow", What) ?? throw Error(e, "<Ramp> needs PowerLow");
            var hi = XmlUtil.Number(e, "PowerHigh", What) ?? throw Error(e, "<Ramp> needs PowerHigh");
            return StepBuilder.Erg(sec, ToTarget(lo), ToTarget(hi), Cadence(e, "Cadence"), []);
        }

        public WorkoutStep FreeRide(XElement e)
        {
            var sec = Duration(e, "Duration");
            var cadence = Cadence(e, "Cadence");
            if (XmlUtil.Number(e, "Slope", What) is { } grade) return SlopeStep(sec, grade, cadence);
            var flat = XmlUtil.Attr(e, "FlatRoad")?.Value.Trim();
            return new WorkoutStep
            {
                Kind = StepKind.Free,
                DurationSec = sec,
                Grade = flat is "1" or "true" or "True" ? 0 : null,
                Cadence = cadence,
            };
        }

        public void Intervals(XElement e, List<WorkoutStep> output)
        {
            var repeat = XmlUtil.Number(e, "Repeat", What) ?? 1;
            if (repeat != Math.Round(repeat) || repeat is < 1 or > StepBuilder.MaxRepeat)
                throw Error(e, $"<IntervalsT> Repeat must be a whole number between 1 and {StepBuilder.MaxRepeat}");

            var body = new List<WorkoutStep> { Phase(e, "On", Duration(e, "OnDuration"), Cadence(e, "Cadence")) };
            var off = XmlUtil.Number(e, "OffDuration", What) ?? 0;
            if (off < 0) throw Error(e, "<IntervalsT> OffDuration can't be negative");
            if (off > 0) body.Add(Phase(e, "Off", off, Cadence(e, "CadenceResting")));
            StepBuilder.AddRepeat(output, body, (int)repeat, null);
        }

        WorkoutStep Phase(XElement e, string phase, double sec, int? cadence)
        {
            if (XmlUtil.Number(e, phase + "Slope", What) is { } grade) return SlopeStep(sec, grade, cadence);
            var power = Power(e, phase + "Power", $"Power{phase}Low", $"Power{phase}High")
                ?? throw Error(e, $"<IntervalsT> needs {phase}Power (or Power{phase}Low/Power{phase}High)");
            return StepBuilder.Erg(sec, power, power, cadence, []);
        }
    }
}
