using BingeRide.Core.Workouts;

namespace BingeRide.Workouts.Parsers;

// Helpers for building flattened step lists: cues, repeat expansion, cue placement.
internal static class StepBuilder
{
    // Hard cap on flattened steps, so a typo like "times": 100000 can't exhaust memory.
    public const int MaxSteps = 5000;

    // Largest repeat count any importer accepts.
    public const int MaxRepeat = 500;

    public static IReadOnlyList<WorkoutCue> Cues(string? headline, IEnumerable<WorkoutCue>? more = null)
    {
        var list = new List<WorkoutCue>();
        if (TextUtil.Blank(headline) is { } h) list.Add(new WorkoutCue(0, h));
        if (more is not null) list.AddRange(more);
        return list.Count == 0 ? [] : [.. list.OrderBy(c => c.OffsetSec)]; // OrderBy is stable
    }

    public static WorkoutStep Erg(double sec, PowerTarget start, PowerTarget end, int? cadence, IReadOnlyList<WorkoutCue> cues) => new()
    {
        Kind = StepKind.Erg,
        DurationSec = sec,
        PowerStart = start,
        PowerEnd = end,
        Cadence = cadence,
        Cues = cues,
    };

    // Inserts `cue` after any existing cues at the same or an earlier offset.
    public static WorkoutStep AddCue(WorkoutStep step, WorkoutCue cue)
    {
        var list = new List<WorkoutCue>(step.Cues.Count + 1);
        var inserted = false;
        foreach (var c in step.Cues)
        {
            if (!inserted && c.OffsetSec > cue.OffsetSec)
            {
                list.Add(cue);
                inserted = true;
            }
            list.Add(c);
        }
        if (!inserted) list.Add(cue);
        return step with { Cues = list };
    }

    // Appends `body` `times` times, labelling each copy "i/n".
    // The repeat's own text becomes an extra offset-0 cue on the very first expanded step.
    // Already-labelled steps (nested FIT repeats) get "outer · inner" labels.
    public static void AddRepeat(List<WorkoutStep> output, IReadOnlyList<WorkoutStep> body, int times, string? text)
    {
        if ((long)body.Count * times + output.Count > MaxSteps)
            throw new WorkoutFormatException($"The workout expands to more than {MaxSteps} steps; reduce the repeat counts.");
        var headline = TextUtil.Blank(text);
        for (var i = 1; i <= times; i++)
        {
            var label = $"{i}/{times}";
            for (var j = 0; j < body.Count; j++)
            {
                var step = body[j];
                step = step with { RepeatLabel = step.RepeatLabel is null ? label : $"{label} · {step.RepeatLabel}" };
                if (i == 1 && j == 0 && headline is not null) step = AddCue(step, new WorkoutCue(0, headline));
                output.Add(step);
            }
        }
    }

    // Adds a cue `offset` seconds after the start of steps[index], walking into
    // later time-based steps when the offset is past the end of that step.
    public static void PlaceCue(List<WorkoutStep> steps, int index, double offset, string? text)
    {
        if (steps.Count == 0 || TextUtil.Blank(text) is not { } cueText) return;
        index = Math.Clamp(index, 0, steps.Count - 1);
        offset = Math.Max(0, offset);
        while (index < steps.Count - 1 && steps[index].DurationSec > 0 && offset >= steps[index].DurationSec)
        {
            offset -= steps[index].DurationSec;
            index++;
        }
        steps[index] = AddCue(steps[index], new WorkoutCue(offset, cueText));
    }
}
