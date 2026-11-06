using static BingeRide.Workouts.Tests.TestUtil;

namespace BingeRide.Workouts.Tests;

public class ValidatorTests
{
    static string OnlyError(params WorkoutStep[] steps) => Assert.Single(WorkoutValidator.Validate(Workout(steps)));

    [Fact]
    public void Valid_workout_has_no_errors()
    {
        var w = Workout(
            Steady(600, 60),
            new WorkoutStep { Kind = StepKind.Slope, DistanceM = 1000, Grade = 5, Gear = 10 },
            new WorkoutStep { Kind = StepKind.Free, DurationSec = 300, Cues = [new WorkoutCue(0, "Spin")] });
        Assert.Empty(WorkoutValidator.Validate(w));
    }

    [Fact]
    public void Empty_step_list() =>
        Assert.Equal(["The workout has no steps."], WorkoutValidator.Validate(Workout()));

    [Fact]
    public void Erg_without_power() =>
        Assert.Contains("ERG step has no power target", OnlyError(new WorkoutStep { Kind = StepKind.Erg, DurationSec = 60 }));

    [Fact]
    public void Distance_based_erg() =>
        Assert.Contains("must be time-based", OnlyError(Steady(0, 60) with { DistanceM = 500 }));

    [Fact]
    public void Too_short_step() =>
        Assert.Contains("at least 1 s", OnlyError(Steady(0.4, 60)));

    [Fact]
    public void Step_without_length() =>
        Assert.Contains("has no duration", OnlyError(Steady(0, 60)));

    [Fact]
    public void Duration_and_distance_together() =>
        Assert.Contains("both a duration and a distance", OnlyError(new WorkoutStep { Kind = StepKind.Free, DurationSec = 60, DistanceM = 100 }));

    [Fact]
    public void Longer_than_six_hours() =>
        Assert.Contains("the maximum is 6 h", OnlyError(Steady(4 * 3600, 60), Steady(2 * 3600 + 1, 60)));

    [Theory]
    [InlineData(-20.5)]
    [InlineData(25.5)]
    public void Grade_out_of_range(double grade) =>
        Assert.Contains("is outside -20 % to 25 %", OnlyError(new WorkoutStep { Kind = StepKind.Slope, DurationSec = 60, Grade = grade }));

    [Fact]
    public void Slope_without_grade() =>
        Assert.Contains("Slope step has no grade", OnlyError(new WorkoutStep { Kind = StepKind.Slope, DurationSec = 60 }));

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void Gear_out_of_range(int gear) =>
        Assert.Contains($"gear {gear} is outside 1-24", OnlyError(new WorkoutStep { Kind = StepKind.Free, DurationSec = 60, Gear = gear }));

    [Fact]
    public void Watts_and_percent_limits()
    {
        Assert.Contains("2001 W is above the 2000 W limit",
            OnlyError(new WorkoutStep { Kind = StepKind.Erg, DurationSec = 60, PowerStart = PowerTarget.Watts(2001), PowerEnd = PowerTarget.Watts(2001) }));
        Assert.Contains("301 % FTP is above the 300 % limit", OnlyError(Steady(60, 301)));
        Assert.Contains("is invalid", OnlyError(Steady(60, -5)));
    }

    [Fact]
    public void Ramp_end_is_checked_too() =>
        Assert.Contains("400 % FTP", OnlyError(Steady(60, 100) with { PowerEnd = PowerTarget.Percent(400) }));

    [Fact]
    public void Messages_name_the_step_repeat_and_headline()
    {
        var bad = Steady(60, 999) with { RepeatLabel = "2/5", Cues = [new WorkoutCue(0, "VO2 rep")] };
        Assert.StartsWith("Step 2 (repeat 2/5) \"VO2 rep\":", OnlyError(Steady(60, 50), bad));
    }

    [Fact]
    public void Many_errors_are_capped()
    {
        var errors = WorkoutValidator.Validate(Workout([.. Enumerable.Repeat(Steady(0.5, 50), 100)]));
        Assert.Equal(20, errors.Count);
        Assert.Contains("more problems", errors[^1]);
    }
}
