using Dynastream.Fit;
using FitDateTime = Dynastream.Fit.DateTime;
using FitFile = Dynastream.Fit.File;

namespace BingeRide.Workouts.Tests;

public class FitParserTests
{
    // Encodes a FIT workout file with the Garmin SDK (file id + workout + steps).
    static byte[] EncodeWorkout(string? name, IReadOnlyList<WorkoutStepMesg> steps, FitFile type = FitFile.Workout)
    {
        using var stream = new MemoryStream();
        var encoder = new Encode(ProtocolVersion.V20);
        encoder.Open(stream);

        var fileId = new FileIdMesg();
        fileId.SetType(type);
        fileId.SetManufacturer(Manufacturer.Development);
        fileId.SetProduct(1);
        fileId.SetSerialNumber(12345);
        fileId.SetTimeCreated(new FitDateTime(System.DateTime.UtcNow));
        encoder.Write(fileId);

        var workout = new WorkoutMesg();
        if (name is not null) workout.SetWktName(name);
        workout.SetSport(Sport.Cycling);
        workout.SetNumValidSteps((ushort)steps.Count);
        encoder.Write(workout);

        for (var i = 0; i < steps.Count; i++)
        {
            steps[i].SetMessageIndex((ushort)i);
            encoder.Write(steps[i]);
        }
        encoder.Close();
        return stream.ToArray();
    }

    static WorkoutStepMesg Step(WktStepDuration duration, uint durationValue, WktStepTarget target, uint targetValue = 0,
        uint? low = null, uint? high = null, string? name = null, Intensity? intensity = null, string? notes = null)
    {
        var m = new WorkoutStepMesg();
        m.SetDurationType(duration);
        m.SetDurationValue(durationValue);
        m.SetTargetType(target);
        m.SetTargetValue(targetValue);
        if (low is not null) m.SetCustomTargetValueLow(low);
        if (high is not null) m.SetCustomTargetValueHigh(high);
        if (name is not null) m.SetWktStepName(name);
        if (intensity is not null) m.SetIntensity(intensity);
        if (notes is not null) m.SetNotes(notes);
        return m;
    }

    static WorkoutStepMesg Timed(uint seconds, uint low, uint high, string? name = null, Intensity? intensity = null) =>
        Step(WktStepDuration.Time, seconds * 1000, WktStepTarget.Power, 0, low, high, name, intensity);

    static WorkoutStepMesg Repeat(uint fromIndex, uint times) =>
        Step(WktStepDuration.RepeatUntilStepsCmplt, fromIndex, WktStepTarget.Open, times);

    [Fact]
    public void Round_trips_a_workout_with_repeats_percent_and_watt_targets()
    {
        var data = EncodeWorkout("Garmin 4x3", [
            Timed(600, 50, 70, intensity: Intensity.Warmup),                                     // 0: 60 % FTP
            Timed(180, 1250, 1290, name: "Hard"),                                                // 1: 270 W
            Step(WktStepDuration.Time, 120_000, WktStepTarget.Power, 2, name: "Easy", notes: "Spin"), // 2: Z2 = 65 %
            Repeat(fromIndex: 1, times: 4),                                                      // 3: steps 1-2, 4 times in total
            Step(WktStepDuration.Open, 0, WktStepTarget.Open),                                   // 4: lap button
            Step(WktStepDuration.Time, 300_000, WktStepTarget.HeartRate, 2, name: "HR cool-down"), // 5: HR → free
            Step(WktStepDuration.Distance, 100_000, WktStepTarget.Open),                         // 6: 1 km free
        ]);

        var result = WorkoutImporter.ImportBytes(data, "garmin.fit");
        Assert.Equal("fit", result.Format);
        Assert.Equal("", result.RawText);
        var w = result.Workout;
        Assert.Equal("Garmin 4x3", w.Name);
        Assert.Equal("fit", w.SourceFormat);

        var s = w.Steps;
        Assert.Equal(12, s.Count);
        Assert.Equal((PowerTarget.Percent(60), 600.0, "Warm-up"), (s[0].PowerStart!.Value, s[0].DurationSec, s[0].Headline));
        Assert.Equal([null, "1/4", "1/4", "2/4", "2/4", "3/4", "3/4", "4/4", "4/4", null, null, null], s.Select(x => x.RepeatLabel));
        Assert.Equal((PowerTarget.Watts(270), "Hard"), (s[1].PowerStart!.Value, s[1].Headline));
        Assert.Equal(PowerTarget.Percent(65), s[2].PowerStart);
        Assert.Equal(["Easy", "Spin"], s[2].Cues.Select(c => c.Text));
        Assert.Equal(PowerTarget.Watts(270), s[7].PowerStart);

        Assert.Equal((StepKind.Free, 300.0), (s[9].Kind, s[9].DurationSec));
        Assert.Null(s[9].Headline); // unnamed: no leftovers from the SDK's shared templates
        Assert.Null(s[11].Headline);
        Assert.Equal((StepKind.Free, "HR cool-down"), (s[10].Kind, s[10].Headline));
        Assert.Equal((StepKind.Free, 1000.0, true), (s[11].Kind, s[11].DistanceM, s[11].IsDistanceBased));

        Assert.Contains(result.Warnings, x => x.Contains("lap-button"));
        Assert.Contains(result.Warnings, x => x.Contains("HeartRate targets"));
        Assert.Equal(600 + 4 * 300 + 300 + 300, w.TotalDurationSec);
    }

    [Fact]
    public void Missing_workout_name_falls_back_to_file_name_and_sniffing_ignores_extension()
    {
        var data = EncodeWorkout(null, [Timed(60, 100, 100)]);
        var result = WorkoutImporter.ImportBytes(data, "Downloaded Workout.bin");
        Assert.Equal("Downloaded Workout", result.Workout.Name);
        Assert.Equal(PowerTarget.Percent(100), result.Workout.Steps[0].PowerStart);
    }

    [Fact]
    public void Imports_from_a_file_path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bingeride-{Guid.NewGuid():N}.fit");
        try
        {
            System.IO.File.WriteAllBytes(path, EncodeWorkout("From disk", [Timed(300, 1200, 1200)]));
            var result = WorkoutImporter.ImportFile(path);
            Assert.Equal(PowerTarget.Watts(200), result.Workout.Steps[0].PowerStart);
            Assert.Equal(Path.GetFullPath(path), result.Workout.SourcePath);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Activity_files_are_rejected()
    {
        var data = EncodeWorkout("Ride", [Timed(60, 100, 100)], FitFile.Activity);
        var ex = Assert.Throws<WorkoutFormatException>(() => WorkoutImporter.ImportBytes(data, "ride.fit"));
        Assert.Contains("not a workout", ex.Message);
    }

    [Fact]
    public void Distance_based_power_steps_are_rejected()
    {
        var data = EncodeWorkout("x", [Step(WktStepDuration.Distance, 100_000, WktStepTarget.Power, 0, 80, 90)]);
        var ex = Assert.Throws<WorkoutFormatException>(() => WorkoutImporter.ImportBytes(data, "x.fit"));
        Assert.Contains("Step 1: distance-based power steps", ex.Message);
    }

    [Fact]
    public void Repeat_must_point_backwards()
    {
        var data = EncodeWorkout("x", [Timed(60, 100, 100), Repeat(fromIndex: 5, times: 2)]);
        var ex = Assert.Throws<WorkoutFormatException>(() => WorkoutImporter.ImportBytes(data, "x.fit"));
        Assert.Contains("Step 2: the repeat points at step 6", ex.Message);
    }

    [Fact]
    public void Mixed_units_in_a_power_range_are_rejected()
    {
        var data = EncodeWorkout("x", [Timed(60, 90, 1200)]);
        Assert.Contains("mixes %FTP and watts", Assert.Throws<WorkoutFormatException>(() => WorkoutImporter.ImportBytes(data, "x.fit")).Message);
    }

    [Fact]
    public void Garbage_with_a_fit_extension_is_a_format_error()
    {
        var ex = Assert.Throws<WorkoutFormatException>(() => WorkoutImporter.ImportBytes("hello world, not a fit file"u8.ToArray(), "x.fit"));
        Assert.Contains("isn't a FIT file", ex.Message);
    }

    [Fact]
    public void Corrupted_fit_data_is_a_format_error()
    {
        var data = EncodeWorkout("x", [Timed(60, 100, 100), Timed(60, 50, 50)]);
        Array.Resize(ref data, data.Length - 20);
        Assert.Throws<WorkoutFormatException>(() => WorkoutImporter.ImportBytes(data, "x.fit"));
    }
}
