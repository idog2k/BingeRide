using MyWahoo.Core.Workouts;

namespace MyWahoo.App.ViewModels;

internal static class Fmt
{
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Distance(double metres) => metres >= 1000 ? $"{metres / 1000:0.0} km" : $"{metres:0} m";

    public static string Grade(double pct) => $"{pct:0.0}%";

    /// <summary>"Warm-up", "VO2 rep · 2/5", or a description of the step when it has no text.</summary>
    public static string StepTitle(WorkoutStep step, double ftp)
    {
        var title = step.Headline ?? step.Kind switch
        {
            StepKind.Erg when step.IsRamp => "Ramp",
            StepKind.Erg => "Steady",
            StepKind.Slope => (step.Grade ?? 0) >= 0 ? "Climb" : "Descent",
            _ => "Free ride",
        };
        return step.RepeatLabel is null ? title : $"{title} · {step.RepeatLabel}";
    }

    public static string StepTarget(WorkoutStep step, double ftp, double intensityPct = 100)
    {
        switch (step.Kind)
        {
            case StepKind.Erg:
                var a = (step.TargetWatts(ftp, 0) ?? 0) * intensityPct / 100;
                var b = (step.TargetWatts(ftp, 1) ?? 0) * intensityPct / 100;
                return Math.Abs(a - b) < 1 ? $"{a:0} W" : $"{a:0}→{b:0} W";
            case StepKind.Slope:
                return Grade(step.Grade ?? 0);
            default:
                return step.Grade is { } g && g != 0 ? $"Free · {Grade(g)}" : "Free";
        }
    }

    public static string StepLength(WorkoutStep step) =>
        step.IsDistanceBased ? Distance(step.DistanceM) : Duration(step.DurationSec);
}
