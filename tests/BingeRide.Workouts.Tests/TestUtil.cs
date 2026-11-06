global using BingeRide.Core.Workouts;
global using BingeRide.Workouts;
global using Xunit;

namespace BingeRide.Workouts.Tests;

internal static class TestUtil
{
    public static string SamplesDir => Path.Combine(AppContext.BaseDirectory, "samples");

    public static string Sample(string fileName) => Path.Combine(SamplesDir, fileName);

    public static ImportResult Import(string text, string? fileNameHint = null) => WorkoutImporter.ImportText(text, fileNameHint);

    public static WorkoutFormatException Fails(string text, string? fileNameHint = null) =>
        Assert.Throws<WorkoutFormatException>(() => WorkoutImporter.ImportText(text, fileNameHint));

    // A minimal valid bingeride-workout document around the given step JSON.
    public static string Json(string steps, string unit = "pct_ftp") =>
        $$"""{"format":"bingeride-workout","version":1,"name":"Test","power_unit":"{{unit}}","steps":[{{steps}}]}""";

    public static WorkoutStep Steady(double sec, double percent) =>
        new() { Kind = StepKind.Erg, DurationSec = sec, PowerStart = PowerTarget.Percent(percent), PowerEnd = PowerTarget.Percent(percent) };

    public static Workout Workout(params WorkoutStep[] steps) => new() { Name = "Test", Steps = steps };
}
