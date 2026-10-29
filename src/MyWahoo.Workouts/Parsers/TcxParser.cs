using MyWahoo.Core.Workouts;

namespace MyWahoo.Workouts.Parsers;

// Reads TCX courses (Trackpoint with Position and AltitudeMeters) into a distance-based Slope workout.
internal static class TcxParser
{
    public const string Format = "tcx";
    const string What = "TCX";

    public static Workout Parse(string text, string fallbackName, List<string> warnings)
    {
        var root = XmlUtil.Load(text, What).Root!;
        if (!XmlUtil.Is(root, "TrainingCenterDatabase"))
            throw new WorkoutFormatException($"Not a TCX file: the root element is <{root.Name.LocalName}>, expected <TrainingCenterDatabase>.");

        var points = new List<RoutePoint>();
        var withoutPosition = 0;
        foreach (var tp in XmlUtil.Descendants(root, "Trackpoint"))
        {
            if (XmlUtil.Child(tp, "Position") is not { } position)
            {
                withoutPosition++;
                continue;
            }
            var line = XmlUtil.Line(tp);
            var lat = XmlUtil.ChildNumber(position, "LatitudeDegrees", What)
                ?? throw new WorkoutFormatException($"TCX line {line}: the track point has no <LatitudeDegrees>.");
            var lon = XmlUtil.ChildNumber(position, "LongitudeDegrees", What)
                ?? throw new WorkoutFormatException($"TCX line {line}: the track point has no <LongitudeDegrees>.");
            points.Add(new RoutePoint(lat, lon, XmlUtil.ChildNumber(tp, "AltitudeMeters", What), line));
        }
        if (points.Count == 0) throw new WorkoutFormatException("The TCX file has no track points with a position.");
        if (withoutPosition > 0) warnings.Add($"{withoutPosition} TCX track point(s) without a position were skipped.");

        var route = RouteBuilder.Build(points, What);
        var name = XmlUtil.Descendants(root, "Course").Select(c => XmlUtil.ChildText(c, "Name")).FirstOrDefault(n => n is not null);
        return new Workout
        {
            Name = name ?? fallbackName,
            Description = route.Summary,
            Steps = route.Steps,
            SourceFormat = Format,
        };
    }
}
