using static MyWahoo.Workouts.Tests.TestUtil;

namespace MyWahoo.Workouts.Tests;

public class SampleTests
{
    public static TheoryData<string> SampleFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(SamplesDir).Order(StringComparer.Ordinal)) data.Add(Path.GetFileName(path));
        return data;
    }

    [Fact]
    public void Every_expected_sample_is_present()
    {
        var names = Directory.EnumerateFiles(SamplesDir).Select(p => Path.GetFileName(p)).ToHashSet();
        string[] expected =
            ["vo2max-60min.json", "sweet-spot-45.zwo", "threshold-2x20.erg", "tempo.mrc", "over-unders.txt", "hill-repeats-slope.json", "rolling-hills.gpx"];
        Assert.All(expected, e => Assert.Contains(e, names));
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Every_sample_imports_cleanly(string fileName)
    {
        var result = WorkoutImporter.ImportFile(Sample(fileName));
        Assert.Empty(result.Warnings);
        Assert.Empty(WorkoutValidator.Validate(result.Workout));
        Assert.False(string.IsNullOrWhiteSpace(result.Workout.Name));
        Assert.Equal(WorkoutImporter.GetFileExtension(result.Format), Path.GetExtension(fileName));

        // RawText round-trips: saving it to the library and re-importing gives the same workout.
        var again = WorkoutImporter.ImportText(result.RawText, fileName).Workout;
        Assert.Equal(result.Workout.Steps, again.Steps, StepComparer.Instance);
    }

    [Fact]
    public void Vo2max_sample_totals_one_hour()
    {
        var w = WorkoutImporter.ImportFile(Sample("vo2max-60min.json")).Workout;
        Assert.Equal("VO2max 5x3' @120%", w.Name);
        Assert.Equal(140, w.AuthorFtp);
        Assert.Equal(["vo2max", "intervals"], w.Tags);
        Assert.Equal(3600, w.TotalDurationSec);
        // 1 ramp + 3x2 openers + 1 settle + 5x2 reps + 1 ramp + 1 free = 20 flattened steps.
        Assert.Equal(20, w.Steps.Count);
        Assert.Equal("5/5", w.Steps[17].RepeatLabel);
        Assert.Equal((PowerTarget.Percent(120), 95), (w.Steps[8].PowerStart!.Value, w.Steps[8].Cadence!.Value));
        Assert.Equal("VO2 rep: smooth, high cadence", w.Steps[8].Headline);
        Assert.Equal(StepKind.Free, w.Steps[^1].Kind);
    }

    [Fact]
    public void Hill_repeats_sample_mixes_erg_slope_and_free()
    {
        var w = WorkoutImporter.ImportFile(Sample("hill-repeats-slope.json")).Workout;
        Assert.Equal(12, w.Steps.Count); // ramp + free + 4x(slope, free) + 1 km slope + free
        Assert.Equal(2700, w.TotalDurationSec);
        Assert.Equal(1000, w.TotalDistanceM);
        var climb = w.Steps[2];
        Assert.Equal((StepKind.Slope, 6.0, 8, "1/4"), (climb.Kind, climb.Grade!.Value, climb.Gear!.Value, climb.RepeatLabel));
        Assert.Equal(["Climb: 6%, stay seated", "Hill repeats", "Halfway: shift up one if you can"], climb.Cues.Select(c => c.Text));
        Assert.Equal(12, w.Steps[3].Gear);
        Assert.True(w.Steps[^2].IsDistanceBased);
    }

    sealed class StepComparer : IEqualityComparer<WorkoutStep>
    {
        public static readonly StepComparer Instance = new();

        static readonly WorkoutCue[] NoCues = [];

        public bool Equals(WorkoutStep? x, WorkoutStep? y) =>
            x is not null && y is not null
            && x with { Cues = NoCues } == y with { Cues = NoCues }
            && x.Cues.SequenceEqual(y.Cues);

        public int GetHashCode(WorkoutStep obj) => obj.Kind.GetHashCode();
    }
}
