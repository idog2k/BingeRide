using MyWahoo.Core.Devices;
using MyWahoo.Core.Session;
using MyWahoo.Core.Settings;
using MyWahoo.Core.Workouts;
using Xunit;

namespace MyWahoo.Core.Tests;

public class LandmarkTests
{
    [Fact]
    public void Trips_are_sorted_and_span_a_bridge_to_the_moon()
    {
        Assert.Equal(Landmarks.All.OrderBy(l => l.Km).Select(l => l.Name), Landmarks.All.Select(l => l.Name));
        Assert.Contains(Landmarks.All, l => l.Name == "Tel Aviv → Haifa");
        Assert.Contains(Landmarks.All, l => l.Name == "New York → Los Angeles");
        // No flag emoji (regional indicators U+1F1E6..1F1FF): Windows can't draw them.
        Assert.DoesNotContain(Landmarks.All, l => l.Emoji.EnumerateRunes().Any(r => r.Value is >= 0x1F1E6 and <= 0x1F1FF));
    }

    [Fact]
    public void Progress_names_the_last_trip_done_and_the_next()
    {
        var start = Landmarks.Progress(0);
        Assert.Null(start.Passed);
        Assert.Equal("Golden Gate Bridge", start.Next!.Name);

        var p = Landmarks.Progress(14.2);
        Assert.Equal("Central Park loop", p.Passed!.Name);
        Assert.Equal("Tel Aviv → Herzliya", p.Next!.Name);
        Assert.Equal(14.2 / 15, p.FractionOfNext, 6);
        Assert.Equal("95% of 🏙️ Tel Aviv → Herzliya", Landmarks.NextText(p));

        var beyond = Landmarks.Progress(500_000);
        Assert.Null(beyond.Next);
        Assert.Equal("past 🌙 To the Moon!", Landmarks.NextText(beyond));
    }

    [Theory]
    [InlineData(95, "≈ ⚓ Tel Aviv → Haifa")]
    [InlineData(4400, "≈ 🛣️ New York → Los Angeles")]
    [InlineData(50, "1.2× 🏃 A marathon")]
    [InlineData(0.2, "")]
    public void Equivalents_read_naturally(double km, string expected) => Assert.Equal(expected, Landmarks.Equivalent(km));

    [Fact]
    public void Workout_distance_estimate_is_plausible()
    {
        var w = new Workout { Name = "w", Steps = [new WorkoutStep { Kind = StepKind.Erg, DurationSec = 3600, PowerStart = PowerTarget.Watts(200) }] };
        Assert.InRange(WorkoutEstimates.DistanceKm(w, 200, 84), 32, 38);

        var climb = new Workout { Name = "c", Steps = [new WorkoutStep { Kind = StepKind.Slope, DurationSec = 3600, Grade = 8 }] };
        Assert.InRange(WorkoutEstimates.DistanceKm(climb, 200, 84), 5, 9); // 130 W up 8%: ~7 km/h

        var route = new Workout { Name = "r", Steps = [new WorkoutStep { Kind = StepKind.Slope, DistanceM = 5000, Grade = 2 }] };
        Assert.Equal(5, WorkoutEstimates.DistanceKm(route, 200, 84), 6);
    }
}

public class LandmarkCueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 19, 0, 0, TimeSpan.Zero);

    private static List<string> RideFor(RideController c, double seconds, int watts = 300)
    {
        var cues = new List<string>();
        c.CueRaised += (_, text) => cues.Add(text);
        c.Tick(T0);
        c.StartRide();
        for (var i = 1; i <= seconds * 4; i++)
        {
            var now = T0.AddSeconds(i / 4.0);
            c.OnTrainerData(new TrainerData(watts, 90, null, now));
            c.Tick(now);
        }
        return cues;
    }

    [Fact]
    public void Passing_a_trip_shows_a_cue()
    {
        var c = new RideController(new AppSettings { ShowFood = false });
        var cues = RideFor(c, 8 * 60);
        Assert.Contains(cues, cue => cue.StartsWith("🌉 You just rode Golden Gate Bridge"));
        Assert.DoesNotContain(cues, cue => cue.StartsWith("🏆")); // first ride ever: no duplicate all-time news
    }

    [Fact]
    public void Earlier_rides_count_towards_all_time_milestones()
    {
        var c = new RideController(new AppSettings { ShowFood = false }) { LifetimeKmBefore = 4 };
        var cues = RideFor(c, 4 * 60); // ~2+ km: the lifetime total passes 5 km (Tel Aviv Port → Jaffa)
        Assert.Contains(cues, cue => cue.StartsWith("🏆 All your rides together: 🏖️ Tel Aviv Port → Jaffa"));
        Assert.True(c.LifetimeKm > 5);
    }

    [Fact]
    public void Cues_can_be_switched_off()
    {
        var c = new RideController(new AppSettings { ShowFood = false, ShowLandmarks = false }) { LifetimeKmBefore = 4 };
        Assert.Empty(RideFor(c, 8 * 60));
    }
}
