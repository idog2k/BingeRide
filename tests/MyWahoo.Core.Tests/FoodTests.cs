using MyWahoo.Core.Devices;
using MyWahoo.Core.Session;
using MyWahoo.Core.Settings;
using MyWahoo.Core.Workouts;
using Xunit;

namespace MyWahoo.Core.Tests;

public class FoodMenuTests
{
    [Fact]
    public void Kcal_is_almost_exactly_kj_for_cycling() => Assert.Equal(0.996, FoodMenu.KcalFromKj(1), 3);

    [Fact]
    public void Progress_walks_the_menu_in_order()
    {
        var start = FoodMenu.Progress(0);
        Assert.Empty(start.Burned);
        Assert.Equal("🍎", start.Next.Emoji);
        Assert.Equal(95, start.KcalToNext);

        var p = FoodMenu.Progress(400); // apple 95 + banana 105 + cookie 150 = 350
        Assert.Equal(["🍎", "🍌", "🍪"], p.Burned.Select(f => f.Emoji));
        Assert.Equal("🍕", p.Next.Emoji);
        Assert.Equal(235, p.KcalToNext, 6);
    }

    [Fact]
    public void Menu_repeats_for_very_long_rides()
    {
        var oneLap = FoodMenu.Sequence.Sum(f => f.Kcal);
        var p = FoodMenu.Progress(oneLap + 100);
        Assert.Equal(FoodMenu.Sequence.Count + 1, p.Burned.Count);
        Assert.Equal("🍎", p.Burned[^1].Emoji);
    }

    [Fact]
    public void Markers_land_where_the_energy_adds_up()
    {
        // 1 h at 200 W = 720 kJ ≈ 717 kcal: apple (95), banana (200), cookie (350), pizza (635); the donut needs 885.
        var w = new Workout { Name = "steady", Steps = [new WorkoutStep { Kind = StepKind.Erg, DurationSec = 3600, PowerStart = PowerTarget.Watts(200) }] };
        var markers = FoodMenu.Markers(w, ftp: 200);
        Assert.Equal(["🍎", "🍌", "🍪", "🍕"], markers.Select(m => m.Food.Emoji));
        var kcalPerSecond = FoodMenu.KcalFromKj(0.2);
        Assert.InRange(markers[0].TimeSec, 95 / kcalPerSecond, 95 / kcalPerSecond + 1); // 1 s resolution
        Assert.InRange(markers[3].TimeSec, 635 / kcalPerSecond, 635 / kcalPerSecond + 1);
    }

    [Fact]
    public void Hard_intervals_burn_food_faster_and_intensity_counts()
    {
        var w = new Workout
        {
            Name = "w",
            Steps =
            [
                new WorkoutStep { Kind = StepKind.Erg, DurationSec = 1800, PowerStart = PowerTarget.Percent(50) },
                new WorkoutStep { Kind = StepKind.Erg, DurationSec = 1800, PowerStart = PowerTarget.Percent(150) },
            ],
        };
        var markers = FoodMenu.Markers(w, ftp: 200);
        Assert.True(markers.Count(m => m.TimeSec > 1800) > markers.Count(m => m.TimeSec <= 1800));
        Assert.True(FoodMenu.Markers(w, 200, intensityPct: 110).Count >= markers.Count);
    }
}

public class FoodCueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Burning_an_apple_shows_a_cue_while_recording()
    {
        var c = new RideController(new AppSettings());
        c.Tick(T0);
        c.StartRide();
        var cues = new List<string>();
        c.CueRaised += (_, text) => cues.Add(text);
        // 300 W ≈ 0.3 kcal/s: the apple (95 kcal) after ~5 min.
        for (var i = 1; i <= 6 * 60 * 4; i++)
        {
            var now = T0.AddSeconds(i / 4.0);
            c.OnTrainerData(new TrainerData(300, 90, null, now));
            c.Tick(now);
        }
        Assert.Contains(cues, cue => cue.StartsWith("🍎 Apple burned!"));
        Assert.Equal(["🍎"], c.Food.Burned.Select(f => f.Emoji));
    }

    [Fact]
    public void Food_cue_waits_for_a_workout_cue_and_can_be_switched_off()
    {
        var c = new RideController(new AppSettings());
        c.Tick(T0);
        var workout = new Workout
        {
            Name = "w",
            Steps =
            [
                new WorkoutStep { Kind = StepKind.Erg, DurationSec = 312, PowerStart = PowerTarget.Watts(300) },
                new WorkoutStep { Kind = StepKind.Erg, DurationSec = 600, PowerStart = PowerTarget.Watts(300), Cues = [new(0, "Second block")] },
            ],
        };
        c.StartWorkout(workout);
        var cues = new List<(double At, string Text)>();
        c.CueRaised += (_, text) => cues.Add(((c.Recording!.Samples.Count), text));
        for (var i = 1; i <= 400 * 4; i++)
        {
            var now = T0.AddSeconds(i / 4.0);
            c.OnTrainerData(new TrainerData(300, 90, null, now));
            c.Tick(now);
        }
        // The apple is burned at ~318 s while "Second block" (from 312 s) is on screen for 10 s: it waits until ~322 s.
        var block = cues.FindIndex(x => x.Text == "Second block");
        var apple = cues.FindIndex(x => x.Text.StartsWith("🍎"));
        Assert.True(block >= 0 && apple > block, string.Join(" | ", cues.Select(x => $"{x.At}:{x.Text}")));
        Assert.InRange(cues[apple].At, 322, 324);

        var quiet = new RideController(new AppSettings { ShowFood = false, ShowLandmarks = false });
        quiet.Tick(T0);
        quiet.StartRide();
        var none = new List<string>();
        quiet.CueRaised += (_, text) => none.Add(text);
        for (var i = 1; i <= 400 * 4; i++)
        {
            var now = T0.AddSeconds(i / 4.0);
            quiet.OnTrainerData(new TrainerData(300, 90, null, now));
            quiet.Tick(now);
        }
        Assert.Empty(none);
    }
}
