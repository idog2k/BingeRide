using static BingeRide.Workouts.Tests.TestUtil;

namespace BingeRide.Workouts.Tests;

public class IcuTextParserTests
{
    static WorkoutStep One(string line) => Assert.Single(Import(line + "\n", "x.txt").Workout.Steps);

    [Fact]
    public void Sample_over_unders_with_sections_repeats_zones_and_freeride()
    {
        var result = WorkoutImporter.ImportFile(Sample("over-unders.txt"));
        Assert.Equal("icu", result.Format);
        Assert.Empty(result.Warnings);
        var w = result.Workout;
        Assert.Equal("over-unders", w.Name);
        Assert.Equal(3540, w.TotalDurationSec);

        var s = w.Steps;
        Assert.Equal(24, s.Count);
        Assert.Equal((PowerTarget.Percent(45), PowerTarget.Percent(75)), (s[0].PowerStart!.Value, s[0].PowerEnd!.Value));
        Assert.Equal(90, s[0].Cadence);
        Assert.Equal("Warmup", s[0].Headline);
        Assert.Equal(["Under", "Main set"], s[2].Cues.Select(c => c.Text));
        Assert.Equal("1/4", s[2].RepeatLabel);
        Assert.Equal(PowerTarget.Percent(105), s[3].PowerStart);
        Assert.Equal(PowerTarget.Percent(50), s[6].PowerStart); // Z1
        Assert.Equal("Recover", s[6].Headline);
        Assert.Equal("4/4", s[21].RepeatLabel);
        Assert.Null(s[22].RepeatLabel);
        Assert.Equal("Cooldown", s[22].Headline);
        Assert.Equal((StepKind.Free, 300.0), (s[23].Kind, s[23].DurationSec));
    }

    [Theory]
    [InlineData("1h", 3600)]
    [InlineData("10m", 600)]
    [InlineData("30s", 30)]
    [InlineData("1m30", 90)]
    [InlineData("1m30s", 90)]
    [InlineData("90s", 90)]
    [InlineData("5'", 300)]
    [InlineData("30\"", 30)]
    [InlineData("5'30\"", 330)]
    [InlineData("1h30", 5400)]
    [InlineData("1h5m", 3900)]
    [InlineData("2.5m", 150)]
    [InlineData("10min", 600)]
    [InlineData("10 min", 600)]
    [InlineData("45sec", 45)]
    public void Durations(string token, double seconds)
    {
        Assert.Equal(seconds, One($"- {token} 60%").DurationSec);
    }

    [Theory]
    [InlineData("- 10m 55%", 55, PowerUnit.PercentFtp)]
    [InlineData("- 30s 120%", 120, PowerUnit.PercentFtp)]
    [InlineData("- 3m 200w", 200, PowerUnit.Watts)]
    [InlineData("- 3m 200W", 200, PowerUnit.Watts)]
    [InlineData("- 3m 180-220w", 200, PowerUnit.Watts)]
    [InlineData("- 2m 95-105%", 100, PowerUnit.PercentFtp)]
    [InlineData("- 2m 95%-105%", 100, PowerUnit.PercentFtp)]
    [InlineData("- 10m Z2", 65, PowerUnit.PercentFtp)]
    [InlineData("- 10m z4", 98, PowerUnit.PercentFtp)]
    [InlineData("- 5m Z7", 160, PowerUnit.PercentFtp)]
    [InlineData("- 10m Z2-Z3", 74, PowerUnit.PercentFtp)]
    [InlineData("- 10m 75 % FTP", 75, PowerUnit.PercentFtp)]
    [InlineData("- 10m @ 75% of FTP", 75, PowerUnit.PercentFtp)]
    [InlineData("* 10m 75%", 75, PowerUnit.PercentFtp)]
    public void Steady_targets(string line, double value, PowerUnit unit)
    {
        var step = One(line);
        Assert.Equal(StepKind.Erg, step.Kind);
        Assert.False(step.IsRamp);
        Assert.Equal(new PowerTarget(value, unit), step.PowerStart);
    }

    [Theory]
    [InlineData("- 5m ramp 50-75%", 50, 75)]
    [InlineData("- Warmup 10m ramp 40%-70%", 40, 70)]
    [InlineData("- 5m ramp 75-50%", 75, 50)]
    [InlineData("- 5m ramp 50% 75%", 50, 75)]
    public void Ramps(string line, double from, double to)
    {
        var step = One(line);
        Assert.True(step.IsRamp);
        Assert.Equal((PowerTarget.Percent(from), PowerTarget.Percent(to)), (step.PowerStart!.Value, step.PowerEnd!.Value));
    }

    [Fact]
    public void Text_before_the_duration_is_the_headline_and_cadence_is_read()
    {
        var step = One("- Warmup 10m ramp 40%-70%");
        Assert.Equal("Warmup", step.Headline);
        Assert.Equal(95, One("- 4m 110% 95rpm").Cadence);
        Assert.Equal(95, One("- 4m 110% 90-100rpm").Cadence);
    }

    [Theory]
    [InlineData("- 5m slope 6%", 6)]
    [InlineData("- 5m 6% grade", 6)]
    [InlineData("- 5m slope -3%", -3)]
    public void Slope_extension(string line, double grade)
    {
        var step = One(line);
        Assert.Equal((StepKind.Slope, grade), (step.Kind, step.Grade!.Value));
    }

    [Fact]
    public void Slope_and_freeride_accept_gears()
    {
        Assert.Equal(8, One("- 5m slope 6% gear 8").Gear);
        var free = One("- 20m freeride gear 12");
        Assert.Equal((StepKind.Free, 1200.0, 12), (free.Kind, free.DurationSec, free.Gear!.Value));
        Assert.Equal(StepKind.Free, One("- 20m free ride").Kind);
        Assert.Equal((StepKind.Free, null), (One("- 5m free").Kind, One("- 5m free").Headline));
    }

    [Fact]
    public void Keywords_inside_cue_text_stay_text()
    {
        var spin = One("- 5m 50% free spin");
        Assert.Equal((StepKind.Erg, "free spin"), (spin.Kind, spin.Headline));
        var gentle = One("- 10m 70% on a gentle slope");
        Assert.Equal((StepKind.Erg, "on a gentle slope"), (gentle.Kind, gentle.Headline));
        Assert.Equal(PowerTarget.Percent(70), gentle.PowerStart);
    }

    [Fact]
    public void Repeat_applies_until_a_blank_line_or_non_step_line()
    {
        var s = Import("""
            3x
            - 1m 120%
            - 1m 50%

            - 5m 60%
            Main set 2x
            - 30s 150%
            Cooldown
            - 5m 50%
            """, "x.txt").Workout.Steps;
        Assert.Equal(["1/3", "1/3", "2/3", "2/3", "3/3", "3/3", null, "1/2", "2/2", null], s.Select(x => x.RepeatLabel));
        Assert.Equal("Main set", s[7].Headline);
        Assert.Equal("Cooldown", s[9].Headline);
    }

    [Theory]
    [InlineData("- 2km 80%", "Line 1: distance steps like \"2km\" aren't supported")]
    [InlineData("- 500mtr 80%", "distance steps")]
    [InlineData("- 10m", "Line 1: no power target")]
    [InlineData("- 80%", "no duration")]
    [InlineData("- 10m Z2 HR", "heart-rate and pace targets")]
    [InlineData("- 10m 75%\nThis takes 45 minutes\n- 5m 50%", "Line 2: can't understand \"This takes 45 minutes\"")]
    [InlineData("- 10m 75% 3x", "Line 1: can't understand \"3x\"")]
    [InlineData("- 10m ramp 75%", "a ramp needs a start and an end")]
    [InlineData("- 10m freeride 50%", "freeride step can't also have a power target")]
    [InlineData("- 10m 75% gear 4", "\"gear\" only applies")]
    [InlineData("- 10m 10m 75%", "two durations")]
    [InlineData("4x\n\n- 10m 75%", "Line 1: the repeat \"4x\" has no step lines")]
    [InlineData("Warmup\nCooldown", "No workout steps found")]
    public void Errors_carry_line_numbers(string text, string expected)
    {
        Assert.Contains(expected, Fails(text, "x.txt").Message);
    }
}
