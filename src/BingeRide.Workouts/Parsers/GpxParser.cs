using BingeRide.Core.Workouts;

namespace BingeRide.Workouts.Parsers;

// Reads GPX tracks (trkpt) or routes (rtept) with <ele> into a distance-based Slope workout.
internal static class GpxParser
{
    public const string Format = "gpx";
    const string What = "GPX";

    public static Workout Parse(string text, string fallbackName, List<string> warnings)
    {
        var root = XmlUtil.Load(text, What).Root!;
        if (!XmlUtil.Is(root, "gpx"))
            throw new WorkoutFormatException($"Not a GPX file: the root element is <{root.Name.LocalName}>, expected <gpx>.");

        var elements = XmlUtil.Descendants(root, "trkpt").ToList();
        if (elements.Count == 0) elements = [.. XmlUtil.Descendants(root, "rtept")];
        if (elements.Count == 0)
            throw new WorkoutFormatException("The GPX file has no track points (<trkpt>) or route points (<rtept>).");

        var points = new List<RoutePoint>(elements.Count);
        foreach (var e in elements)
        {
            var line = XmlUtil.Line(e);
            var lat = XmlUtil.Number(e, "lat", What) ?? throw new WorkoutFormatException($"GPX line {line}: the point has no lat attribute.");
            var lon = XmlUtil.Number(e, "lon", What) ?? throw new WorkoutFormatException($"GPX line {line}: the point has no lon attribute.");
            points.Add(new RoutePoint(lat, lon, XmlUtil.ChildNumber(e, "ele", What), line));
        }

        var route = RouteBuilder.Build(points, What);
        var metadata = XmlUtil.Child(root, "metadata");
        var name = (metadata is null ? null : XmlUtil.ChildText(metadata, "name"))
                   ?? FirstName(root, "trk")
                   ?? FirstName(root, "rte")
                   ?? fallbackName;
        var description = (metadata is null ? null : XmlUtil.ChildText(metadata, "desc")) ?? FirstDesc(root);

        return new Workout
        {
            Name = name,
            Description = description is null ? route.Summary : $"{description}\n{route.Summary}",
            Steps = route.Steps,
            SourceFormat = Format,
        };
    }

    static string? FirstName(System.Xml.Linq.XElement root, string container) =>
        XmlUtil.Children(root, container).Select(c => XmlUtil.ChildText(c, "name")).FirstOrDefault(n => n is not null);

    static string? FirstDesc(System.Xml.Linq.XElement root) =>
        XmlUtil.Children(root, "trk").Select(c => XmlUtil.ChildText(c, "desc")).FirstOrDefault(n => n is not null);
}
