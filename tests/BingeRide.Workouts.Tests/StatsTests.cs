using System.Diagnostics;
using static BingeRide.Workouts.Tests.TestUtil;

namespace BingeRide.Workouts.Tests;

public class StatsTests
{
    [Fact]
    public void One_hour_at_ftp_is_100_tss()
    {
        var s = WorkoutStats.Compute(Workout(Steady(3600, 100)), 250);
        Assert.Equal(3600, s.TotalSeconds);
        Assert.Equal(250, s.AverageWatts, 6);
        Assert.Equal(250, s.NormalizedPower, 6);
        Assert.Equal(1, s.IntensityFactor, 6);
        Assert.Equal(100, s.Tss, 6);
        Assert.Equal(250, s.MaxTargetWatts);
        Assert.Equal(0, s.EstimatedSeconds);
    }

    [Fact]
    public void Ramps_average_linearly_and_watts_targets_ignore_ftp()
    {
        var ramp = new WorkoutStep { Kind = StepKind.Erg, DurationSec = 100, PowerStart = PowerTarget.Watts(0), PowerEnd = PowerTarget.Watts(200) };
        var s = WorkoutStats.Compute(Workout(ramp), 300);
        Assert.Equal(100, s.AverageWatts, 6);
        Assert.Equal(200, s.MaxTargetWatts);
    }

    [Fact]
    public void Free_and_slope_steps_are_estimated_and_distance_steps_skipped()
    {
        var w = Workout(
            new WorkoutStep { Kind = StepKind.Free, DurationSec = 1800 },
            new WorkoutStep { Kind = StepKind.Slope, DurationSec = 1800, Grade = 6 },
            new WorkoutStep { Kind = StepKind.Slope, DistanceM = 2500, Grade = 3 });
        var s = WorkoutStats.Compute(w, 200);
        Assert.Equal(3600, s.TotalSeconds);
        Assert.Equal(2500, s.TotalMeters);
        Assert.Equal((100 + 130) / 2.0, s.AverageWatts, 6);
        Assert.Equal(3600, s.EstimatedSeconds);
        Assert.Equal(0, s.MaxTargetWatts);
    }

    [Fact]
    public void Intervals_have_np_above_average()
    {
        var w = WorkoutImporter.ImportFile(Sample("vo2max-60min.json")).Workout;
        var s = WorkoutStats.Compute(w, 250);
        Assert.Equal(3600, s.TotalSeconds);
        Assert.Equal(300, s.MaxTargetWatts, 6); // 120 % of 250
        Assert.True(s.NormalizedPower > s.AverageWatts * 1.05, $"NP {s.NormalizedPower:0} vs avg {s.AverageWatts:0}");
        Assert.InRange(s.IntensityFactor, 0.7, 0.95);
        Assert.InRange(s.Tss, 50, 90);
        Assert.Equal(300, s.EstimatedSeconds); // the final free spin
    }

    [Fact]
    public void Short_workouts_use_average_for_np()
    {
        var s = WorkoutStats.Compute(Workout(Steady(20, 100)), 200);
        Assert.Equal(200, s.NormalizedPower, 6);
    }

    [Fact]
    public void Route_only_workout_has_no_power()
    {
        var s = WorkoutStats.Compute(Workout(new WorkoutStep { Kind = StepKind.Slope, DistanceM = 1000, Grade = 2 }), 200);
        Assert.Equal((0.0, 1000.0, 0.0, 0.0, 0.0), (s.TotalSeconds, s.TotalMeters, s.AverageWatts, s.NormalizedPower, s.Tss));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(double.NaN)]
    public void Ftp_must_be_positive(double ftp) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkoutStats.Compute(Workout(Steady(60, 50)), ftp));

    [Fact]
    public void Six_hour_workout_is_fast_enough_for_a_preview()
    {
        var steps = Enumerable.Range(0, 720).Select(i => Steady(30, i % 2 == 0 ? 120 : 50)).ToArray();
        var w = Workout(steps);
        WorkoutStats.Compute(w, 250); // warm up the JIT
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) WorkoutStats.Compute(w, 250);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"20 computations took {sw.ElapsedMilliseconds} ms");
    }
}
