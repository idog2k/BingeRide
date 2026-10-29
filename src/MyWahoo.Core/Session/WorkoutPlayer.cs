using MyWahoo.Core.Workouts;

namespace MyWahoo.Core.Session;

public abstract record PlayerEvent;
public sealed record StepStarted(int Index, WorkoutStep Step) : PlayerEvent;
public sealed record CueReached(string Text) : PlayerEvent;
public sealed record WorkoutFinished : PlayerEvent;

/// <summary>Walks a flattened workout by elapsed time (and distance, for route steps).</summary>
public sealed class WorkoutPlayer
{
    private readonly HashSet<int> _firedCues = [];

    public WorkoutPlayer(Workout workout)
    {
        if (workout.Steps.Count == 0) throw new ArgumentException("Workout has no steps", nameof(workout));
        Workout = workout;
    }

    public Workout Workout { get; }
    public int StepIndex { get; private set; }
    public double StepElapsedSec { get; private set; }
    public double StepDistanceM { get; private set; }
    public double ElapsedSec { get; private set; }
    public bool IsFinished { get; private set; }
    public bool HasStarted { get; private set; }

    public WorkoutStep? CurrentStep => IsFinished ? null : Workout.Steps[StepIndex];
    public WorkoutStep? NextStep => StepIndex + 1 < Workout.Steps.Count ? Workout.Steps[StepIndex + 1] : null;

    /// <summary>Progress through the current step, 0..1.</summary>
    public double StepFraction => CurrentStep switch
    {
        null => 1,
        { IsDistanceBased: true } s => Math.Clamp(StepDistanceM / s.DistanceM, 0, 1),
        var s => s.DurationSec > 0 ? Math.Clamp(StepElapsedSec / s.DurationSec, 0, 1) : 1,
    };

    /// <summary>Seconds (or metres for distance steps) left in the current step.</summary>
    public double StepRemaining => CurrentStep switch
    {
        null => 0,
        { IsDistanceBased: true } s => Math.Max(0, s.DistanceM - StepDistanceM),
        var s => Math.Max(0, s.DurationSec - StepElapsedSec),
    };

    /// <summary>Overall progress (by time, or by distance when the workout is a route).</summary>
    public double Progress
    {
        get
        {
            if (IsFinished) return 1;
            var byDistance = Workout.HasDistanceSteps;
            double Size(WorkoutStep s) => byDistance ? s.DistanceM + s.DurationSec * 8 : s.DurationSec; // ~8 m/s stands in for time steps on routes
            var total = Workout.Steps.Sum(Size);
            if (total <= 0) return 0;
            var done = Workout.Steps.Take(StepIndex).Sum(Size) + Size(Workout.Steps[StepIndex]) * StepFraction;
            return Math.Clamp(done / total, 0, 1);
        }
    }

    /// <summary>Starts the workout: emits the first step and its headline cue.</summary>
    public IReadOnlyList<PlayerEvent> Start()
    {
        if (HasStarted) return [];
        HasStarted = true;
        var events = new List<PlayerEvent>();
        EnterStep(0, events);
        return events;
    }

    /// <summary>Moves forward by <paramref name="dtSec"/> of riding and <paramref name="distanceM"/> of virtual distance.</summary>
    public IReadOnlyList<PlayerEvent> Advance(double dtSec, double distanceM)
    {
        var events = new List<PlayerEvent>();
        if (!HasStarted) events.AddRange(Start());
        if (IsFinished || (dtSec <= 0 && distanceM <= 0)) return events;

        ElapsedSec += dtSec;
        var guard = 0;
        while (!IsFinished && guard++ < 10_000)
        {
            var step = Workout.Steps[StepIndex];
            if (step.IsDistanceBased)
            {
                StepElapsedSec += dtSec;
                dtSec = 0;
                var left = step.DistanceM - StepDistanceM;
                if (distanceM < left)
                {
                    StepDistanceM += distanceM;
                    break;
                }
                distanceM -= left;
            }
            else
            {
                StepDistanceM += distanceM;
                distanceM = 0;
                var left = step.DurationSec - StepElapsedSec;
                if (dtSec < left)
                {
                    StepElapsedSec += dtSec;
                    break;
                }
                dtSec -= left;
            }
            if (StepIndex + 1 >= Workout.Steps.Count)
            {
                StepElapsedSec = step.DurationSec;
                StepDistanceM = step.DistanceM;
                IsFinished = true;
                events.Add(new WorkoutFinished());
                return events;
            }
            EnterStep(StepIndex + 1, events);
        }
        FireCues(events);
        return events;
    }

    public IReadOnlyList<PlayerEvent> SkipStep()
    {
        var events = new List<PlayerEvent>();
        if (IsFinished) return events;
        if (StepIndex + 1 >= Workout.Steps.Count)
        {
            IsFinished = true;
            events.Add(new WorkoutFinished());
        }
        else
        {
            EnterStep(StepIndex + 1, events);
        }
        return events;
    }

    public IReadOnlyList<PlayerEvent> PreviousStep()
    {
        var events = new List<PlayerEvent>();
        IsFinished = false;
        // Like a music player: restart the step unless we're right at its beginning.
        EnterStep(StepElapsedSec > 3 || StepIndex == 0 ? StepIndex : StepIndex - 1, events);
        return events;
    }

    /// <summary>ERG watts for the current point of the workout at the rider's FTP, or null outside ERG steps.</summary>
    public double? TargetWatts(double ftp) => CurrentStep?.TargetWatts(ftp, StepFraction);

    private void EnterStep(int index, List<PlayerEvent> events)
    {
        StepIndex = index;
        StepElapsedSec = 0;
        StepDistanceM = 0;
        _firedCues.Clear();
        events.Add(new StepStarted(index, Workout.Steps[index]));
        FireCues(events);
    }

    private void FireCues(List<PlayerEvent> events)
    {
        var step = Workout.Steps[StepIndex];
        for (var i = 0; i < step.Cues.Count; i++)
        {
            if (step.Cues[i].OffsetSec <= StepElapsedSec && _firedCues.Add(i))
                events.Add(new CueReached(step.Cues[i].Text));
        }
    }
}
