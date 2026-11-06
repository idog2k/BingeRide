using BingeRide.Core.Workouts;
using BingeRide.Workouts.Parsers;

namespace BingeRide.Workouts;

/// <summary>Checks a flattened workout against the limits the player and trainer can handle.</summary>
public static class WorkoutValidator
{
    /// <summary>Longest allowed workout (time-based steps), in seconds.</summary>
    public const double MaxTotalSeconds = 6 * 3600;
    public const double MinGrade = -20, MaxGrade = 25;
    public const int MinGear = 1, MaxGear = 24;
    public const double MaxWatts = 2000, MaxPercentFtp = 300;
    const int MaxMessages = 20;

    /// <summary>Returns user-readable errors ("Step 3 (repeat 1/4): …"); empty when the workout is valid.</summary>
    public static IReadOnlyList<string> Validate(Workout workout)
    {
        ArgumentNullException.ThrowIfNull(workout);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(workout.Name)) errors.Add("The workout has no name.");
        if (workout.Steps is not { Count: > 0 } steps)
        {
            errors.Add("The workout has no steps.");
            return errors;
        }

        double total = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var at = Describe(i, step);
            CheckLength(step, at, errors);
            if (step.DurationSec > 0 && double.IsFinite(step.DurationSec)) total += step.DurationSec;

            switch (step.Kind)
            {
                case StepKind.Erg:
                    if (step.PowerStart is null) errors.Add($"{at}: ERG step has no power target.");
                    if (step.DistanceM > 0) errors.Add($"{at}: ERG steps must be time-based, not distance-based.");
                    CheckPower(step.PowerStart, at, errors);
                    if (step.PowerEnd != step.PowerStart) CheckPower(step.PowerEnd, at, errors);
                    break;
                case StepKind.Slope when step.Grade is null:
                    errors.Add($"{at}: Slope step has no grade.");
                    break;
            }

            if (step.Grade is { } grade && !(grade >= MinGrade && grade <= MaxGrade))
                errors.Add($"{at}: grade {grade:0.#} % is outside {MinGrade} % to {MaxGrade} %.");
            if (step.Gear is { } gear && gear is < MinGear or > MaxGear)
                errors.Add($"{at}: gear {gear} is outside {MinGear}-{MaxGear}.");
            if (step.Cadence is { } rpm && rpm is < 1 or > 250)
                errors.Add($"{at}: cadence {rpm} rpm isn't plausible.");
            foreach (var cue in step.Cues)
            {
                if (!(cue.OffsetSec >= 0)) errors.Add($"{at}: a cue has a negative offset.");
                if (string.IsNullOrWhiteSpace(cue.Text)) errors.Add($"{at}: a cue has no text.");
            }
        }

        if (total > MaxTotalSeconds)
            errors.Add($"The workout lasts {total / 3600:0.##} h; the maximum is {MaxTotalSeconds / 3600:0} h.");

        return errors.Count <= MaxMessages
            ? errors
            : [.. errors.Take(MaxMessages - 1), $"…and {errors.Count - MaxMessages + 1} more problems."];
    }

    static void CheckLength(WorkoutStep step, string at, List<string> errors)
    {
        var (sec, metres) = (step.DurationSec, step.DistanceM);
        if (!double.IsFinite(sec) || !double.IsFinite(metres) || sec < 0 || metres < 0)
            errors.Add($"{at}: the duration/distance is invalid.");
        else if (sec > 0 && metres > 0)
            errors.Add($"{at}: has both a duration and a distance; use one.");
        else if (sec <= 0 && metres <= 0)
            errors.Add($"{at}: has no duration.");
        else if (sec is > 0 and < 1)
            errors.Add($"{at}: lasts {sec:0.##} s; steps must be at least 1 s long.");
        else if (metres is > 0 and < 1)
            errors.Add($"{at}: is {metres:0.##} m long; distance steps must be at least 1 m.");
    }

    static void CheckPower(PowerTarget? target, string at, List<string> errors)
    {
        if (target is not { } p) return;
        if (!double.IsFinite(p.Value) || p.Value < 0)
            errors.Add($"{at}: power target {p.Value} is invalid.");
        else if (p.Unit == PowerUnit.Watts && p.Value > MaxWatts)
            errors.Add($"{at}: {p.Value:0} W is above the {MaxWatts:0} W limit.");
        else if (p.Unit == PowerUnit.PercentFtp && p.Value > MaxPercentFtp)
            errors.Add($"{at}: {p.Value:0.#} % FTP is above the {MaxPercentFtp:0} % limit.");
    }

    static string Describe(int index, WorkoutStep step)
    {
        var text = $"Step {index + 1}";
        if (step.RepeatLabel is { } label) text += $" (repeat {label})";
        if (step.Headline is { } headline) text += $" \"{TextUtil.Shorten(headline, 30)}\"";
        return text;
    }
}
