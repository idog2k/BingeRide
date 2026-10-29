using System.Text.Json;
using MyWahoo.Core.Session;
using MyWahoo.Workouts.Export;

namespace MyWahoo.App.Services;

public sealed record RideEntry(string FitPath, RideSummary Summary)
{
    public string Title => Summary.WorkoutName ?? "Ride";
    public string DateText => Summary.Start.ToLocalTime().ToString("ddd d MMM yyyy · HH:mm");

    public string StatsText
    {
        get
        {
            var s = Summary;
            var parts = new List<string> { Duration(s.MovingSec), $"{s.DistanceM / 1000:0.0} km", $"avg {s.AvgPowerW:0} W" };
            if (s.NormalizedPowerW > 0) parts.Add($"NP {s.NormalizedPowerW:0}");
            if (s.Tss > 0) parts.Add($"TSS {s.Tss:0}");
            if (s.AvgHeartRate is { } hr) parts.Add($"♥ {hr}");
            parts.Add($"{s.WorkKj:0} kJ");
            return string.Join(" · ", parts);
        }
    }

    internal static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

/// <summary>Finished rides: a FIT activity (to upload to Strava/Garmin Connect/intervals.icu) plus a JSON summary for the list.</summary>
public sealed class RideHistory(string folder)
{
    /// <summary>Rides shorter than this aren't worth a file (e.g. Start pressed by accident).</summary>
    public const int MinimumSeconds = 60;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Folder { get; } = folder;

    /// <summary>Saves the ride; returns null (with a reason) when it's too short to keep.</summary>
    public RideEntry? Save(RideRecording ride, out string? reason)
    {
        if (ride.Samples.Count < MinimumSeconds)
        {
            reason = $"Ride not saved: shorter than {MinimumSeconds / 60} minute of riding.";
            return null;
        }
        Directory.CreateDirectory(Folder);
        var summary = ride.Summarize();
        var stem = ride.StartTime.ToLocalTime().ToString("yyyy-MM-dd_HHmm") + (ride.WorkoutName is { } w ? "_" + WorkoutLibrary.Slug(w) : "");
        var fitPath = Path.Combine(Folder, stem + ".fit");
        for (var i = 2; File.Exists(fitPath); i++) fitPath = Path.Combine(Folder, $"{stem}-{i}.fit");

        var tmp = fitPath + ".tmp";
        using (var file = File.Create(tmp)) FitActivityWriter.Write(ride, file);
        File.Move(tmp, fitPath);
        File.WriteAllText(Path.ChangeExtension(fitPath, ".json"), JsonSerializer.Serialize(summary, Json));
        reason = null;
        return new RideEntry(fitPath, summary);
    }

    public IReadOnlyList<RideEntry> Load()
    {
        if (!Directory.Exists(Folder)) return [];
        var rides = new List<RideEntry>();
        foreach (var json in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<RideSummary>(File.ReadAllText(json), Json) is { } summary)
                    rides.Add(new RideEntry(Path.ChangeExtension(json, ".fit"), summary));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A damaged summary just drops out of the list; the .fit file is untouched.
            }
        }
        return rides.OrderByDescending(r => r.Summary.Start).ToList();
    }
}
