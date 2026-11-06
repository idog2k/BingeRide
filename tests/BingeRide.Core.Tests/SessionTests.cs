using BingeRide.Core.Devices;
using BingeRide.Core.Protocol;
using BingeRide.Core.Session;
using BingeRide.Core.Settings;
using BingeRide.Core.Workouts;
using Xunit;

namespace BingeRide.Core.Tests;

public class RiderPhysicsTests
{
    [Fact]
    public void Speed_and_power_are_inverse()
    {
        foreach (var grade in new[] { -3.0, 0, 2, 8 })
        {
            var v = RiderPhysics.SteadySpeed(200, grade, 84);
            Assert.Equal(200, RiderPhysics.PowerForSpeed(v, grade, 84), 1);
        }
    }

    [Fact]
    public void Plausible_values()
    {
        Assert.InRange(RiderPhysics.SteadySpeed(200, 0, 84) * 3.6, 32, 38);
        Assert.InRange(RiderPhysics.SteadySpeed(200, 8, 84) * 3.6, 9, 13);
        Assert.Equal(0, RiderPhysics.SteadySpeed(0, 0, 84));
        Assert.True(RiderPhysics.SteadySpeed(0, -6, 84) > 10); // coasting downhill
    }
}

public class WorkoutPlayerTests
{
    private static Workout Sample() => new()
    {
        Name = "t",
        Steps =
        [
            new WorkoutStep { Kind = StepKind.Erg, DurationSec = 60, PowerStart = PowerTarget.Percent(50), PowerEnd = PowerTarget.Percent(100), Cues = [new(0, "warm"), new(30, "halfway")] },
            new WorkoutStep { Kind = StepKind.Slope, DurationSec = 30, Grade = 5, Gear = 8 },
            new WorkoutStep { Kind = StepKind.Slope, DistanceM = 100, Grade = 2 },
            new WorkoutStep { Kind = StepKind.Free, DurationSec = 10 },
        ],
    };

    [Fact]
    public void Ramps_and_cues()
    {
        var p = new WorkoutPlayer(Sample());
        var start = p.Start();
        Assert.Contains(start, e => e is CueReached { Text: "warm" });
        Assert.Equal(100, p.TargetWatts(200));
        Assert.Contains(p.Advance(30, 0), e => e is CueReached { Text: "halfway" });
        Assert.Equal(150, p.TargetWatts(200)!.Value, 6);
        Assert.DoesNotContain(p.Advance(0.5, 0), e => e is CueReached);
    }

    [Fact]
    public void Carries_time_over_steps_and_walks_distance_steps()
    {
        var p = new WorkoutPlayer(Sample());
        p.Start();
        var events = p.Advance(65, 0);
        Assert.Equal(1, p.StepIndex);
        Assert.Equal(5, p.StepElapsedSec, 6);
        Assert.Contains(events, e => e is StepStarted { Index: 1 });

        p.Advance(25, 0);
        Assert.Equal(2, p.StepIndex);
        p.Advance(60, 60); // time alone doesn't finish a distance step
        Assert.Equal(2, p.StepIndex);
        p.Advance(5, 50);
        Assert.Equal(3, p.StepIndex);
        Assert.Contains(p.Advance(10, 0), e => e is WorkoutFinished);
        Assert.True(p.IsFinished);
        Assert.Equal(1, p.Progress);
    }

    [Fact]
    public void Skip_and_previous()
    {
        var p = new WorkoutPlayer(Sample());
        p.Start();
        p.SkipStep();
        Assert.Equal(1, p.StepIndex);
        p.Advance(10, 0);
        p.PreviousStep(); // >3 s in: restart this step
        Assert.Equal(1, p.StepIndex);
        Assert.Equal(0, p.StepElapsedSec);
        p.PreviousStep(); // at the start: go back one
        Assert.Equal(0, p.StepIndex);
    }
}

public class RideControllerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);

    private sealed class RecordingTrainer : ITrainer
    {
        public List<TrainerTarget> Targets { get; } = [];
        public string Name => "rec";
        public ulong Address => 1;
        public ControlPlane ControlPlane => ControlPlane.Zwift;
        public bool IsConnected => true;
        public double? ReportedGearRatio => null;
        public event EventHandler<TrainerData>? DataReceived { add { } remove { } }
        public event EventHandler<string>? Log { add { } remove { } }
        public event EventHandler? Disconnected { add { } remove { } }
        public void SetTarget(TrainerTarget target) => Targets.Add(target);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (RideController, RecordingTrainer) Create(AppSettings? s = null)
    {
        var c = new RideController(s ?? new AppSettings { Ftp = 140 });
        var t = new RecordingTrainer();
        c.Trainer = t;
        return (c, t);
    }

    private static void Ride(RideController c, DateTimeOffset from, int seconds, int watts = 150, double cadence = 85)
    {
        for (var i = 0; i <= seconds * 4; i++)
        {
            var now = from.AddSeconds(i / 4.0);
            c.OnTrainerData(new TrainerData(watts, cadence, null, now));
            c.Tick(now);
        }
    }

    [Fact]
    public void Free_mode_click_shifts_gears()
    {
        var (c, t) = Create();
        c.OnClick(ClickButtons.Plus);
        c.OnClick(ClickButtons.Plus);
        c.OnClick(ClickButtons.B); // B is gear-down by default (Click v2 "+" puck)
        Assert.Equal(13, c.Gear);
        Assert.Equal(new SimTarget(0, 13), t.Targets.Last());
    }

    [Fact]
    public void Erg_mode_click_changes_watts()
    {
        var (c, t) = Create();
        c.SetMode(RideMode.Erg);
        c.SetErgWatts(150);
        c.OnClick(ClickButtons.Plus);
        Assert.Equal(new ErgTarget(155), t.Targets.Last());
        c.OnClick(ClickButtons.Minus | ClickButtons.B); // both pressed at once: two steps down
        Assert.Equal(new ErgTarget(145), t.Targets.Last());
    }

    [Fact]
    public void Slope_mode_uses_manual_grade()
    {
        var (c, t) = Create();
        c.SetMode(RideMode.Slope);
        c.AdjustGrade(+1);
        c.AdjustGrade(+1);
        Assert.Equal(new SimTarget(1.0, 12), t.Targets.Last());
    }

    [Fact]
    public void Workout_drives_erg_then_slope_with_suggested_gear_and_intensity_bias()
    {
        var (c, t) = Create();
        var w = new Workout
        {
            Name = "w",
            Steps =
            [
                new WorkoutStep { Kind = StepKind.Erg, DurationSec = 10, PowerStart = PowerTarget.Percent(100), Cues = [new(0, "Go")] },
                new WorkoutStep { Kind = StepKind.Slope, DurationSec = 10, Grade = 6, Gear = 7 },
            ],
        };
        c.StartWorkout(w);
        Assert.Equal(new ErgTarget(140), t.Targets.Last());
        Assert.Equal("Go", c.Cue);

        c.Up(); // in an ERG step "+" raises intensity
        Assert.Equal(102, c.WorkoutIntensityPct);
        Assert.Equal(new ErgTarget(143), t.Targets.Last());

        Ride(c, T0, 11);
        Assert.Equal(1, c.Player!.StepIndex);
        Assert.Equal(new SimTarget(6, 7), t.Targets.Last());
        c.Up(); // in a slope step "+" shifts
        Assert.Equal(new SimTarget(6, 8), t.Targets.Last());
    }

    [Fact]
    public void Workout_auto_pauses_when_rider_stops_pedalling()
    {
        var (c, _) = Create();
        c.StartWorkout(new Workout { Name = "w", Steps = [new WorkoutStep { Kind = StepKind.Erg, DurationSec = 600, PowerStart = PowerTarget.Watts(100) }] });
        Ride(c, T0, 10);
        var at = c.Player!.ElapsedSec;
        Ride(c, T0.AddSeconds(10.25), 20, watts: 0, cadence: 0);
        Assert.True(c.IsAutoPaused);
        Assert.InRange(c.Player.ElapsedSec - at, 0, 3.5);
    }

    [Fact]
    public void Live_stats_accumulate()
    {
        var (c, _) = Create();
        c.StartRide(T0);
        Ride(c, T0, 60, watts: 200);
        Assert.Equal(200, c.PowerW, 1);
        Assert.Equal(85, c.CadenceRpm);
        Assert.InRange(c.RideSeconds, 59, 61);
        Assert.InRange(c.WorkKj, 11.5, 12.5);
        Assert.InRange(c.DistanceM, 350, 650);
    }

    [Fact]
    public void Finished_workout_falls_back_to_free_ride()
    {
        var (c, t) = Create();
        c.StartWorkout(new Workout { Name = "w", Steps = [new WorkoutStep { Kind = StepKind.Erg, DurationSec = 2, PowerStart = PowerTarget.Watts(100) }] });
        Ride(c, T0, 4);
        Assert.True(c.Player!.IsFinished);
        Assert.Equal(new SimTarget(0, 12), t.Targets.Last());
    }
}

public class SettingsTests
{
    [Fact]
    public void Round_trips_including_click_mapping()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bingeride-{Guid.NewGuid():N}.json");
        try
        {
            var s = new AppSettings { Ftp = 155, TrainerAddress = 0xAABBCCDDEEFF };
            s.ClickMapping[ClickButtons.Y] = ClickAction.PauseResume;
            SettingsStore.Save(s, path);
            var back = SettingsStore.Load(path);
            Assert.Equal(155, back.Ftp);
            Assert.Equal(0xAABBCCDDEEFFUL, back.TrainerAddress);
            Assert.Equal(ClickAction.PauseResume, back.ClickMapping[ClickButtons.Y]);
            Assert.Contains("\"PauseResume\"", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bingeride-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Equal(140, SettingsStore.Load(path).Ftp);
            Assert.True(File.Exists(path + ".bad"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bad");
        }
    }

    [Fact]
    public void Old_MyWahoo_folder_is_moved_to_the_new_name_once()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"bingeride-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(parent, "MyWahoo", "Rides"));
        File.WriteAllText(Path.Combine(parent, "MyWahoo", "Rides", "ride.fit"), "x");
        try
        {
            var folder = AppFolders.Resolve(parent);
            Assert.Equal(Path.Combine(parent, "BingeRide"), folder);
            Assert.True(File.Exists(Path.Combine(folder, "Rides", "ride.fit")));
            Assert.False(Directory.Exists(Path.Combine(parent, "MyWahoo")));

            // The old version recreating its folder later doesn't move anything over the new one.
            Directory.CreateDirectory(Path.Combine(parent, "MyWahoo"));
            Assert.Equal(folder, AppFolders.Resolve(parent));
            Assert.True(Directory.Exists(Path.Combine(parent, "MyWahoo")));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
