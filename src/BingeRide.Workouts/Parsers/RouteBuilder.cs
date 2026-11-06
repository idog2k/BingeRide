using BingeRide.Core.Workouts;

namespace BingeRide.Workouts.Parsers;

// A route point; Line is the source line for error messages.
internal readonly record struct RoutePoint(double Lat, double Lon, double? Elevation, int Line);

// Turns a GPS route into distance-based Slope steps: haversine distances, elevation resampled to a
// 10 m grid and smoothed with a ~50 m moving average, ~100 m segments, grades clamped to
// [-15, 20] %, and consecutive segments whose grades differ by less than 0.5 % merged.
// Resampling first keeps the smoothing meaningful whether points are 1 m or 100 m apart.
internal static class RouteBuilder
{
    const double EarthRadiusM = 6_371_008.8;
    const double GridM = 10;
    const int SmoothHalfWindow = 2;    // ±2 grid samples → 5 × 10 m ≈ 50 m window
    const int SegmentSamples = 10;     // 100 m segments
    const double MergeGradeDiff = 0.5;
    const double MinGrade = -15, MaxGrade = 20;

    public sealed record Route(List<WorkoutStep> Steps, double DistanceM, double ClimbM)
    {
        public string Summary => $"{DistanceM / 1000:0.0} km route with {ClimbM:0} m of climbing.";
    }

    public static Route Build(IReadOnlyList<RoutePoint> points, string what)
    {
        if (points.Count < 2) throw new WorkoutFormatException($"The {what} route needs at least two points with coordinates.");

        var missing = 0;
        var firstMissingLine = 0;
        foreach (var p in points)
        {
            if (!double.IsFinite(p.Lat) || !double.IsFinite(p.Lon) || Math.Abs(p.Lat) > 90 || Math.Abs(p.Lon) > 180)
                throw new WorkoutFormatException($"{what} line {p.Line}: invalid coordinates ({p.Lat}, {p.Lon}).");
            if (p.Elevation is null && missing++ == 0) firstMissingLine = p.Line;
        }
        if (missing == points.Count)
            throw new WorkoutFormatException(
                $"The {what} route has no elevation data, so grades can't be computed. Export it again with elevation included.");
        if (missing > 0)
            throw new WorkoutFormatException(
                $"{missing} {what} point(s) have no elevation (first at line {firstMissingLine}); every point needs one.");

        var n = points.Count;
        var dist = new double[n];
        var ele = new double[n];
        ele[0] = points[0].Elevation!.Value;
        for (var i = 1; i < n; i++)
        {
            dist[i] = dist[i - 1] + Haversine(points[i - 1], points[i]);
            ele[i] = points[i].Elevation!.Value;
        }
        var total = dist[^1];
        if (total < GridM) throw new WorkoutFormatException($"The {what} route is only {total:0.#} m long.");

        var (x, gridEle) = Resample(dist, ele, total);
        var smooth = Smooth(gridEle);

        // Boundaries every 100 m on the grid; a tail shorter than 50 m joins the last segment.
        var last = x.Length - 1;
        var bounds = new List<int>();
        for (var k = 0; k < last; k += SegmentSamples) bounds.Add(k);
        if (bounds.Count > 1 && x[last] - x[bounds[^1]] < SegmentSamples * GridM / 2) bounds.RemoveAt(bounds.Count - 1);
        bounds.Add(last);

        var merged = new List<(double Length, double Rise)>();
        for (var k = 0; k + 1 < bounds.Count; k++)
        {
            var (a, b) = (bounds[k], bounds[k + 1]);
            var segment = (Length: x[b] - x[a], Rise: smooth[b] - smooth[a]);
            if (merged.Count > 0 && Math.Abs(Grade(merged[^1]) - Grade(segment)) < MergeGradeDiff)
                merged[^1] = (merged[^1].Length + segment.Length, merged[^1].Rise + segment.Rise);
            else
                merged.Add(segment);
        }

        double climb = 0;
        for (var k = 1; k < smooth.Length; k++) climb += Math.Max(0, smooth[k] - smooth[k - 1]);

        var steps = merged
            .Select(s => new WorkoutStep { Kind = StepKind.Slope, DistanceM = s.Length, Grade = Math.Round(Grade(s), 1) })
            .ToList();
        if (steps.Count > StepBuilder.MaxSteps) throw new WorkoutFormatException($"The {what} route is too long to ride as one workout.");
        return new Route(steps, total, climb);
    }

    static double Grade((double Length, double Rise) s) => Math.Clamp(s.Rise / s.Length * 100, MinGrade, MaxGrade);

    // Linear interpolation of elevation at 0, 10, 20 … m and at the route end.
    static (double[] X, double[] Ele) Resample(double[] dist, double[] ele, double total)
    {
        var count = (int)Math.Ceiling(total / GridM) + 1;
        var xs = new double[count];
        var es = new double[count];
        var j = 0;
        for (var k = 0; k < count; k++)
        {
            var x = Math.Min(k * GridM, total);
            while (j < dist.Length - 2 && dist[j + 1] < x) j++;
            var span = dist[j + 1] - dist[j];
            var t = span > 0 ? Math.Clamp((x - dist[j]) / span, 0, 1) : 1;
            xs[k] = x;
            es[k] = ele[j] + (ele[j + 1] - ele[j]) * t;
        }
        return (xs, es);
    }

    // Centred moving average that shrinks symmetrically at the ends, so a constant grade stays exact.
    static double[] Smooth(double[] values)
    {
        var n = values.Length;
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++) prefix[i + 1] = prefix[i] + values[i];
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            var h = Math.Min(SmoothHalfWindow, Math.Min(i, n - 1 - i));
            result[i] = (prefix[i + h + 1] - prefix[i - h]) / (2 * h + 1);
        }
        return result;
    }

    static double Haversine(RoutePoint a, RoutePoint b)
    {
        const double toRad = Math.PI / 180;
        var dLat = (b.Lat - a.Lat) * toRad;
        var dLon = (b.Lon - a.Lon) * toRad;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(a.Lat * toRad) * Math.Cos(b.Lat * toRad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusM * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
    }
}
