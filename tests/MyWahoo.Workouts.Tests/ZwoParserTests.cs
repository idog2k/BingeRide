using static MyWahoo.Workouts.Tests.TestUtil;

namespace MyWahoo.Workouts.Tests;

public class ZwoParserTests
{
    static string Zwo(string body, string extra = "") => $"""
        <workout_file>
            <name>Test</name>
            {extra}
            <workout>
                {body}
            </workout>
        </workout_file>
        """;

    static IReadOnlyList<WorkoutStep> Steps(string body) => Import(Zwo(body), "t.zwo").Workout.Steps;

    [Fact]
    public void Sample_sweet_spot_imports_with_intervals_cues_and_metadata()
    {
        var result = WorkoutImporter.ImportFile(Sample("sweet-spot-45.zwo"));
        Assert.Equal("zwo", result.Format);
        Assert.Empty(result.Warnings);
        var w = result.Workout;
        Assert.Equal("Sweet Spot 3x8", w.Name);
        Assert.Equal("MyWahoo", w.Author);
        Assert.Equal(["sweetspot", "45min"], w.Tags);
        Assert.Equal(2700, w.TotalDurationSec);

        var s = w.Steps;
        Assert.Equal(9, s.Count);
        Assert.Equal((PowerTarget.Percent(45), PowerTarget.Percent(75)), (s[0].PowerStart!.Value, s[0].PowerEnd!.Value));
        Assert.Equal([(0.0, "Easy spin, build gradually"), (480.0, "One opener coming up")], s[0].Cues.Select(c => (c.OffsetSec, c.Text)));
        Assert.Equal(85, s[0].Cadence);
        Assert.Equal(PowerTarget.Percent(100), s[1].PowerStart);

        Assert.Equal(["1/3", "1/3", "2/3", "2/3", "3/3", "3/3"], s.Skip(2).Take(6).Select(x => x.RepeatLabel));
        Assert.Equal([480.0, 120.0], s.Skip(2).Take(2).Select(x => x.DurationSec));
        Assert.Equal(PowerTarget.Percent(90), s[2].PowerStart);
        Assert.Equal(PowerTarget.Percent(55), s[3].PowerStart);
        Assert.Equal((90, 85), (s[2].Cadence!.Value, s[3].Cadence!.Value));
        Assert.Equal([(0.0, "Sweet spot: smooth and steady"), (240.0, "Halfway through the first block")], s[2].Cues.Select(c => (c.OffsetSec, c.Text)));
        Assert.Empty(s[4].Cues);

        Assert.Equal((PowerTarget.Percent(65), PowerTarget.Percent(45)), (s[8].PowerStart!.Value, s[8].PowerEnd!.Value));
        Assert.Equal("Cool-down: nice work", s[8].Headline);
    }

    [Fact]
    public void Warmup_always_climbs_and_cooldown_always_descends()
    {
        var s = Steps("""
            <Warmup Duration="300" PowerLow="0.75" PowerHigh="0.25"/>
            <Cooldown Duration="300" PowerLow="0.3" PowerHigh="0.7"/>
            <Ramp Duration="120" PowerLow="0.8" PowerHigh="0.5"/>
            """);
        Assert.Equal((PowerTarget.Percent(25), PowerTarget.Percent(75)), (s[0].PowerStart!.Value, s[0].PowerEnd!.Value));
        Assert.Equal((PowerTarget.Percent(70), PowerTarget.Percent(30)), (s[1].PowerStart!.Value, s[1].PowerEnd!.Value));
        Assert.Equal((PowerTarget.Percent(80), PowerTarget.Percent(50)), (s[2].PowerStart!.Value, s[2].PowerEnd!.Value)); // Ramp: as written
    }

    [Fact]
    public void Steady_variants_case_insensitive_attributes_and_solidstate()
    {
        var s = Steps("""
            <SteadyState Duration="60" PowerLow="0.7" PowerHigh="0.8"/>
            <steadystate duration="60" power="0.65" cadence="88"/>
            <SolidState Duration="60" Power="1.1"/>
            """);
        Assert.Equal(PowerTarget.Percent(75), s[0].PowerStart);
        Assert.Equal(PowerTarget.Percent(65), s[1].PowerStart);
        Assert.Equal(88, s[1].Cadence);
        Assert.Equal(PowerTarget.Percent(110), s[2].PowerStart);
        Assert.All(s, x => Assert.False(x.IsRamp));
    }

    [Fact]
    public void IntervalsT_low_high_averages_and_auuki_slopes()
    {
        var s = Steps("""
            <IntervalsT Repeat="2" OnDuration="60" OffDuration="30" PowerOnLow="1.0" PowerOnHigh="1.2" PowerOffLow="0.4" PowerOffHigh="0.6"/>
            <IntervalsT Repeat="2" OnDuration="120" OffDuration="60" OnSlope="8" OffSlope="0"/>
            """);
        Assert.Equal(8, s.Count);
        Assert.Equal(PowerTarget.Percent(110), s[0].PowerStart);
        Assert.Equal(PowerTarget.Percent(50), s[1].PowerStart);
        Assert.Equal((StepKind.Slope, 8.0, 120.0), (s[4].Kind, s[4].Grade!.Value, s[4].DurationSec));
        Assert.Equal((StepKind.Slope, 0.0, 60.0), (s[5].Kind, s[5].Grade!.Value, s[5].DurationSec));
        Assert.Equal("2/2", s[7].RepeatLabel);
    }

    [Fact]
    public void FreeRide_MaxEffort_and_slope_extension()
    {
        var s = Steps("""
            <FreeRide Duration="600" FlatRoad="1"/>
            <FreeRide Duration="300"/>
            <MaxEffort Duration="20"/>
            <SteadyState Duration="240" Power="0.8" Slope="5.0"/>
            <FreeRide Duration="180" Slope="-2"/>
            """);
        Assert.Equal((StepKind.Free, 0.0), (s[0].Kind, s[0].Grade!.Value));
        Assert.Null(s[1].Grade);
        Assert.Equal(StepKind.Free, s[2].Kind);
        Assert.Equal("Max effort!", s[2].Headline);
        Assert.Equal((StepKind.Slope, 5.0), (s[3].Kind, s[3].Grade!.Value));
        Assert.Null(s[3].PowerStart);
        Assert.Equal((StepKind.Slope, -2.0), (s[4].Kind, s[4].Grade!.Value));
    }

    [Fact]
    public void TextEvent_spellings_and_ftpOverride()
    {
        var result = Import(Zwo("""
            <SteadyState Duration="60" Power="0.5">
                <TextEvent TimeOffset="10" Message="Ten seconds in"/>
            </SteadyState>
            <SteadyState Duration="60" Power="0.5"/>
            """, "<ftpOverride>230</ftpOverride><author>Me</author>"));
        Assert.Equal(new WorkoutCue(10, "Ten seconds in"), Assert.Single(result.Workout.Steps[0].Cues));
        Assert.Equal(230, result.Workout.AuthorFtp);
    }

    [Fact]
    public void Missing_name_falls_back_to_file_name()
    {
        var result = Import("""<workout_file><workout><SteadyState Duration="60" Power="0.5"/></workout></workout_file>""", "base-miles.zwo");
        Assert.Equal("base-miles", result.Workout.Name);
    }

    [Theory]
    [InlineData("""<SteadyState Power="0.5"/>""", "line 5: <SteadyState> needs a Duration")]
    [InlineData("""<SteadyState Duration="60"/>""", "needs a Power attribute")]
    [InlineData("""<SteadyState Duration="abc" Power="0.5"/>""", "Duration=\"abc\" is not a number")]
    [InlineData("""<Ramp Duration="60" PowerLow="0.5"/>""", "<Ramp> needs PowerHigh")]
    [InlineData("""<IntervalsT Repeat="3" OnDuration="60" OffDuration="60" OnPower="1.2"/>""", "needs OffPower")]
    [InlineData("""<Sprint Duration="60"/>""", "<Sprint> isn't a supported workout element")]
    [InlineData("""<SteadyState Duration="60" Power="3.5"/>""", "350 % FTP is above")]
    public void Element_errors_are_reported_with_line_numbers(string body, string expected)
    {
        Assert.Contains(expected, Fails(Zwo(body), "t.zwo").Message);
    }

    [Fact]
    public void Broken_xml_reports_line()
    {
        var ex = Fails("<workout_file>\n<workout>\n<SteadyState Duration=\"60\" Power=\"0.5\">\n</workout>\n</workout_file>", "t.zwo");
        Assert.Contains("isn't valid XML (line 4", ex.Message);
    }

    [Fact]
    public void Wrong_root_and_empty_workout_are_errors()
    {
        Assert.Contains("expected <workout_file>", Fails("<other/>", "t.zwo").Message);
        Assert.Contains("no steps", Fails("<workout_file><workout/></workout_file>", "t.zwo").Message);
    }
}
