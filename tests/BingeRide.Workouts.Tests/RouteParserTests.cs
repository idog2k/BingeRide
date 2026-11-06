using System.Globalization;
using System.Text;
using static BingeRide.Workouts.Tests.TestUtil;

namespace BingeRide.Workouts.Tests;

public class RouteParserTests
{
    const double MetresPerDegree = 6_371_008.8 * Math.PI / 180;

    // Points due north every spacingM metres; elevation is a function of distance.
    static IEnumerable<(double Lat, double Lon, double? Ele)> Line(int count, Func<double, double?> elevation, double spacingM = 50) =>
        Enumerable.Range(0, count).Select(i => (45.0 + i * spacingM / MetresPerDegree, 7.0, elevation(i * spacingM)));

    static string Gpx(IEnumerable<(double Lat, double Lon, double? Ele)> points, string tag = "trkpt", string name = "<trk><name>Track name</name><trkseg>")
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?>\n<gpx version=\"1.1\" xmlns=\"http://www.topografix.com/GPX/1/1\">\n");
        var route = tag == "rtept";
        sb.Append(route ? "<rte><name>Route name</name>\n" : name + "\n");
        foreach (var (lat, lon, ele) in points)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<{tag} lat=\"{lat}\" lon=\"{lon}\">");
            if (ele is { } e) sb.Append(CultureInfo.InvariantCulture, $"<ele>{e}</ele>");
            sb.Append($"</{tag}>\n");
        }
        sb.Append(route ? "</rte>\n" : "</trkseg></trk>\n").Append("</gpx>\n");
        return sb.ToString();
    }

    [Fact]
    public void Sample_rolling_hills_becomes_distance_based_slope_steps()
    {
        var result = WorkoutImporter.ImportFile(Sample("rolling-hills.gpx"));
        Assert.Equal("gpx", result.Format);
        var w = result.Workout;
        Assert.Equal("Rolling Hills 5k", w.Name);
        Assert.Contains("5.0 km route", w.Description);
        Assert.Equal(5000, w.TotalDistanceM, 0.01 * 5000);
        Assert.Equal(0, w.TotalDurationSec);
        Assert.True(w.Steps.Count is >= 8 and <= 50, $"{w.Steps.Count} steps");
        Assert.All(w.Steps, s =>
        {
            Assert.Equal(StepKind.Slope, s.Kind);
            Assert.True(s.IsDistanceBased);
            Assert.InRange(s.Grade!.Value, -15, 20);
            Assert.True(s.DistanceM >= 50, $"segment of {s.DistanceM} m");
        });
        Assert.Contains(w.Steps, s => s.Grade > 3);
        Assert.Contains(w.Steps, s => s.Grade < -3);
        for (var i = 1; i < w.Steps.Count; i++)
            Assert.True(Math.Abs(w.Steps[i].Grade!.Value - w.Steps[i - 1].Grade!.Value) >= 0.4, $"steps {i - 1} and {i} should have merged");
    }

    [Fact]
    public void Constant_grade_merges_into_one_step()
    {
        var w = Import(Gpx(Line(41, d => 100 + d * 0.05)), "climb.gpx").Workout;
        var step = Assert.Single(w.Steps);
        Assert.Equal(5.0, step.Grade!.Value, 0.15);
        Assert.Equal(2000, step.DistanceM, 1);
        Assert.Equal("Track name", w.Name);
    }

    [Fact]
    public void Grades_are_clamped_and_route_points_are_supported()
    {
        // 1 km at +40 % then 1 km at -30 %.
        var w = Import(Gpx(Line(41, d => d <= 1000 ? d * 0.4 : 400 - (d - 1000) * 0.3), tag: "rtept"), "x.gpx").Workout;
        Assert.Equal("Route name", w.Name);
        Assert.Equal(20, w.Steps[0].Grade);
        Assert.Equal(-15, w.Steps[^1].Grade);
    }

    [Fact]
    public void Smoothing_damps_single_point_spikes()
    {
        // Flat 2 km with points every 10 m and one 10 m GPS spike: unsmoothed that would be ±9 %.
        var w = Import(Gpx(Line(201, d => Math.Abs(d - 1000) < 1 ? 110 : 100, spacingM: 10)), "x.gpx").Workout;
        Assert.All(w.Steps, s => Assert.InRange(s.Grade!.Value, -3, 3));
        Assert.Equal(2000, w.TotalDistanceM, 1);
    }

    [Fact]
    public void Sparse_points_are_resampled_so_segments_stay_near_100_m()
    {
        // Points 400 m apart on a rolling profile still give ~100 m segments (before merging).
        var w = Import(Gpx(Line(11, d => 100 + 30 * Math.Sin(d / 600), spacingM: 400)), "x.gpx").Workout;
        Assert.True(w.Steps.Count > 4, $"{w.Steps.Count} steps");
        Assert.Equal(4000, w.TotalDistanceM, 1);
    }

    [Fact]
    public void Missing_elevation_is_an_error()
    {
        Assert.Contains("no elevation data", Fails(Gpx(Line(10, _ => null)), "x.gpx").Message);
        var partial = Fails(Gpx(Line(10, d => d > 200 ? null : 100)), "x.gpx");
        Assert.Contains("have no elevation (first at line", partial.Message);
    }

    [Fact]
    public void Gpx_without_points_is_an_error()
    {
        Assert.Contains("no track points", Fails("<gpx><trk><name>x</name></trk></gpx>", "x.gpx").Message);
    }

    [Fact]
    public void Tcx_course_is_read()
    {
        var sb = new StringBuilder("""
            <?xml version="1.0" encoding="UTF-8"?>
            <TrainingCenterDatabase xmlns="http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2">
              <Courses><Course><Name>TCX climb</Name><Track>

            """);
        foreach (var (lat, lon, ele) in Line(21, d => 200 + d * 0.03))
            sb.Append(CultureInfo.InvariantCulture,
                $"<Trackpoint><Position><LatitudeDegrees>{lat}</LatitudeDegrees><LongitudeDegrees>{lon}</LongitudeDegrees></Position><AltitudeMeters>{ele}</AltitudeMeters></Trackpoint>\n");
        sb.Append("<Trackpoint><Time>2024-01-01T00:00:00Z</Time></Trackpoint>\n");
        sb.Append("</Track></Course></Courses></TrainingCenterDatabase>\n");

        var result = Import(sb.ToString(), "climb.tcx");
        Assert.Equal("tcx", result.Format);
        Assert.Equal("TCX climb", result.Workout.Name);
        Assert.Equal(3.0, Assert.Single(result.Workout.Steps).Grade!.Value, 0.15);
        Assert.Contains(result.Warnings, x => x.Contains("without a position"));
    }
}
