using static BingeRide.Workouts.Tests.TestUtil;

namespace BingeRide.Workouts.Tests;

public class ErgMrcParserTests
{
    static string Course(string data, string unitsLine = "MINUTES WATTS", string text = "") => $"""
        [COURSE HEADER]
        VERSION = 2
        UNITS = ENGLISH
        DESCRIPTION = Test
        {unitsLine}
        [END COURSE HEADER]
        [COURSE DATA]
        {data}
        [END COURSE DATA]
        {text}
        """;

    [Fact]
    public void Sample_erg_builds_steady_and_ramp_steps_with_cues()
    {
        var result = WorkoutImporter.ImportFile(Sample("threshold-2x20.erg"));
        Assert.Equal("erg", result.Format);
        Assert.Empty(result.Warnings);
        var w = result.Workout;
        Assert.Equal("Threshold 2x20", w.Name);
        Assert.Equal(250, w.AuthorFtp);
        Assert.StartsWith("Two 20-minute", w.Description);
        Assert.Equal(3900, w.TotalDurationSec);

        var s = w.Steps;
        Assert.Equal([600.0, 180, 1200, 300, 1200, 420], s.Select(x => x.DurationSec));
        Assert.Equal((PowerTarget.Watts(110), PowerTarget.Watts(190)), (s[0].PowerStart!.Value, s[0].PowerEnd!.Value));
        Assert.Equal(PowerTarget.Watts(240), s[2].PowerStart);
        Assert.False(s[2].IsRamp);
        Assert.Equal((PowerTarget.Watts(150), PowerTarget.Watts(100)), (s[5].PowerStart!.Value, s[5].PowerEnd!.Value));
        Assert.Equal(
            ["Warm-up: build to tempo", "Three easy minutes before block 1", "Block 1: 20 min at threshold, settle in", "Recover", "Block 2: same again", "Cool-down"],
            s.Select(x => x.Headline));
        Assert.DoesNotContain("\r", result.RawText);
    }

    [Fact]
    public void Sample_mrc_merges_identical_steady_steps()
    {
        var result = WorkoutImporter.ImportFile(Sample("tempo.mrc"));
        Assert.Equal("mrc", result.Format);
        var s = result.Workout.Steps;
        Assert.Equal([600.0, 120, 900, 300, 900, 180, 300], s.Select(x => x.DurationSec));
        Assert.Equal(PowerTarget.Percent(80), s[2].PowerStart);
        Assert.Equal((PowerTarget.Percent(60), PowerTarget.Percent(45)), (s[6].PowerStart!.Value, s[6].PowerEnd!.Value));
        Assert.Equal("Tempo block 1: 15 min at 80%", s[2].Headline);
        Assert.Equal("Tempo 2x15", result.Workout.Name);
    }

    [Fact]
    public void Header_line_decides_between_watts_and_percent()
    {
        var erg = Import(Course("0 100\n10 100"), "x.mrc");
        Assert.Equal("erg", erg.Format);
        Assert.Equal(PowerTarget.Watts(100), erg.Workout.Steps[0].PowerStart);

        var mrc = Import(Course("0 60\n10 60", "MINUTES PERCENT"), "x.erg");
        Assert.Equal("mrc", mrc.Format);
        Assert.Equal(PowerTarget.Percent(60), mrc.Workout.Steps[0].PowerStart);
    }

    [Fact]
    public void Missing_header_line_uses_the_extension_with_a_warning()
    {
        var result = Import(Course("0 60\n10 60", unitsLine: ""), "x.mrc");
        Assert.Equal("mrc", result.Format);
        Assert.Contains(result.Warnings, w => w.Contains("MINUTES"));
    }

    [Fact]
    public void Vertical_jumps_make_no_step_and_tiny_transitions_are_merged()
    {
        var result = Import(Course("0\t100\n5\t100\n5\t200\n5.01\t250\n10\t250"));
        var s = result.Workout.Steps;
        Assert.Equal(2, s.Count);
        Assert.Equal(300.6, s[0].DurationSec, 6);
        Assert.Equal(299.4, s[1].DurationSec, 6);
        Assert.Equal(600, result.Workout.TotalDurationSec, 6);
        Assert.Contains(result.Warnings, w => w.Contains("shorter than 1 s"));
    }

    [Fact]
    public void Course_text_accepts_tabs_or_spaces_and_absolute_times()
    {
        var result = Import(Course("0 100\n5 100\n5 200\n10 200", text: "[COURSE TEXT]\n0\tStart\t5\n330 Half a minute into the hard part\n[END COURSE TEXT]"));
        var s = result.Workout.Steps;
        Assert.Equal("Start", s[0].Headline);
        Assert.Equal(new WorkoutCue(30, "Half a minute into the hard part"), Assert.Single(s[1].Cues));
    }

    [Theory]
    [InlineData("0 100\n5 100\n4 100", "Line 10: time goes backwards")]
    [InlineData("0 100\nfive 100", "Line 9: expected \"minutes value\"")]
    [InlineData("0 100", "at least two")]
    [InlineData("0 100\n10 2500", "2500 W is above")]
    public void Data_errors(string data, string expected)
    {
        Assert.Contains(expected, Fails(Course(data), "x.erg").Message);
    }
}
