namespace BingeRide.Core.Workouts;

/// <summary>How the trainer is driven during a step.</summary>
public enum StepKind
{
    /// <summary>ERG: the trainer holds a power target (steady or ramp).</summary>
    Erg,
    /// <summary>SIM at a prescribed road grade; the rider shifts virtual gears.</summary>
    Slope,
    /// <summary>No target; SIM at <see cref="WorkoutStep.Grade"/> (default 0 %) and the rider shifts virtual gears.</summary>
    Free,
}

public enum PowerUnit { PercentFtp, Watts }

/// <summary>A power target expressed either as % of FTP (75 = 75 %) or as absolute watts.</summary>
public readonly record struct PowerTarget(double Value, PowerUnit Unit)
{
    public static PowerTarget Percent(double percentFtp) => new(percentFtp, PowerUnit.PercentFtp);
    public static PowerTarget Watts(double watts) => new(watts, PowerUnit.Watts);

    public double ToWatts(double ftp) => Unit == PowerUnit.PercentFtp ? Value * ftp / 100.0 : Value;

    public override string ToString() => Unit == PowerUnit.PercentFtp ? $"{Value:0.#}% FTP" : $"{Value:0} W";
}

/// <summary>A text cue shown <see cref="OffsetSec"/> seconds after its step starts.</summary>
public sealed record WorkoutCue(double OffsetSec, string Text);

/// <summary>
/// One flattened workout step. Repeats are expanded by the importers, so a player can walk
/// <see cref="Workout.Steps"/> linearly. Exactly one of <see cref="DurationSec"/> or
/// <see cref="DistanceM"/> is positive; distance-based steps are only valid for Slope/Free
/// (e.g. GPX route segments).
/// </summary>
public sealed record WorkoutStep
{
    public required StepKind Kind { get; init; }

    /// <summary>Length in seconds for time-based steps; 0 for distance-based steps.</summary>
    public double DurationSec { get; init; }

    /// <summary>Length in metres for distance-based steps; 0 for time-based steps.</summary>
    public double DistanceM { get; init; }

    /// <summary>ERG target at the start of the step (required for <see cref="StepKind.Erg"/>).</summary>
    public PowerTarget? PowerStart { get; init; }

    /// <summary>ERG target at the end of the step; equal to <see cref="PowerStart"/> for steady steps.</summary>
    public PowerTarget? PowerEnd { get; init; }

    /// <summary>Road grade in % (positive = uphill). Required for Slope, optional for Free (null = 0 %).</summary>
    public double? Grade { get; init; }

    /// <summary>Suggested virtual gear (1..24) applied when the step starts; null = keep the current gear.</summary>
    public int? Gear { get; init; }

    /// <summary>Cadence cue in rpm (not enforced).</summary>
    public int? Cadence { get; init; }

    /// <summary>Cues for this step; a cue at offset 0 is the step's headline text.</summary>
    public IReadOnlyList<WorkoutCue> Cues { get; init; } = [];

    /// <summary>For steps expanded from a repeat block: e.g. "2/5". Null otherwise.</summary>
    public string? RepeatLabel { get; init; }

    public bool IsDistanceBased => DistanceM > 0 && DurationSec <= 0;

    public bool IsRamp => Kind == StepKind.Erg && PowerStart is { } s && PowerEnd is { } e && s != e;

    /// <summary>ERG target in watts at <paramref name="fraction"/> (0..1) through the step.</summary>
    public double? TargetWatts(double ftp, double fraction)
    {
        if (Kind != StepKind.Erg || PowerStart is not { } start) return null;
        var end = PowerEnd ?? start;
        var a = start.ToWatts(ftp);
        var b = end.ToWatts(ftp);
        return a + (b - a) * Math.Clamp(fraction, 0, 1);
    }

    public string? Headline => Cues.FirstOrDefault(c => c.OffsetSec <= 0)?.Text;
}

public sealed record Workout
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }

    /// <summary>FTP the author assumed (informational; %FTP targets always scale to the rider's current FTP).</summary>
    public double? AuthorFtp { get; init; }

    public required IReadOnlyList<WorkoutStep> Steps { get; init; }

    /// <summary>Importer id, e.g. "json", "zwo", "erg", "mrc", "icu", "fit", "gpx".</summary>
    public string SourceFormat { get; init; } = "json";

    public string? SourcePath { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public double TotalDurationSec => Steps.Sum(s => s.DurationSec);
    public double TotalDistanceM => Steps.Sum(s => s.DistanceM);
    public bool HasDistanceSteps => Steps.Any(s => s.IsDistanceBased);
}
