using MyWahoo.Core.Devices;
using MyWahoo.Core.Protocol;
using MyWahoo.Core.Settings;
using MyWahoo.Core.Workouts;

namespace MyWahoo.Core.Session;

public enum RideMode { Erg, Slope, Free, Workout }

/// <summary>
/// The ride's brain: modes, gears, ERG/grade targets, workout progression, live stats.
/// Everything except <see cref="OnTrainerData"/> must be called from the UI thread;
/// <see cref="Tick"/> is expected about 4×/s.
/// </summary>
public sealed class RideController
{
    private static readonly TimeSpan PowerWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AutoPauseAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CueDuration = TimeSpan.FromSeconds(10);

    private readonly Lock _samplesLock = new();
    private readonly Queue<(DateTimeOffset At, int Watts)> _powerSamples = new();
    private (double Rpm, DateTimeOffset At)? _cadence;
    private (int Watts, DateTimeOffset At)? _latestPower;
    private (int Bpm, DateTimeOffset At)? _heartRate;
    private DateTimeOffset? _lastTick;
    private DateTimeOffset _lastPedalling;
    private ITrainer? _trainer;

    public RideController(AppSettings settings)
    {
        Settings = settings;
        Gear = GearTable.Clamp(settings.StartGear);
        ErgWatts = (int)Math.Round(settings.Ftp * 0.6 / 5) * 5;
    }

    public AppSettings Settings { get; }

    public RideMode Mode { get; private set; } = RideMode.Free;

    /// <summary>The manual mode to return to when a workout ends.</summary>
    public RideMode ManualMode { get; private set; } = RideMode.Free;

    public int ErgWatts { get; private set; }
    public double ManualGrade { get; private set; }
    public int Gear { get; private set; }
    public double GearRatio => GearTable.Ratio(Gear);

    public WorkoutPlayer? Player { get; private set; }
    public double WorkoutIntensityPct { get; private set; } = 100;
    public bool IsWorkoutPaused { get; private set; }
    public bool IsAutoPaused { get; private set; }

    public double PowerW { get; private set; }
    public double CadenceRpm { get; private set; }
    public int? HeartRate { get; private set; }
    public double SpeedKmh => _speedMps * 3.6;
    public double DistanceM { get; private set; }
    public double RideSeconds { get; private set; }
    public double WorkKj { get; private set; }
    public double AveragePowerW => RideSeconds > 0 ? WorkKj * 1000 / RideSeconds : 0;

    public TrainerTarget? CurrentTarget { get; private set; }
    public string? Cue { get; private set; }
    private DateTimeOffset _cueUntil;
    private double _speedMps;
    private int _instantPower;

    /// <summary>Raised after every tick and every rider action.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a step starts or a workout cue fires (for a sound or toast).</summary>
    public event EventHandler<string>? CueRaised;

    public ITrainer? Trainer
    {
        get => _trainer;
        set
        {
            _trainer = value;
            PushTarget();
        }
    }

    /// <summary>The step kind currently in effect (manual mode or workout step).</summary>
    public StepKind EffectiveKind => Mode switch
    {
        RideMode.Erg => StepKind.Erg,
        RideMode.Slope => StepKind.Slope,
        RideMode.Free => StepKind.Free,
        _ => Player?.CurrentStep?.Kind ?? StepKind.Free,
    };

    /// <summary>Grade currently simulated (0 in ERG).</summary>
    public double EffectiveGrade => Mode switch
    {
        RideMode.Slope => ManualGrade,
        RideMode.Workout when Player?.CurrentStep is { Kind: not StepKind.Erg } s => s.Grade ?? 0,
        _ => 0,
    };

    /// <summary>ERG watts currently targeted, or null when not in ERG.</summary>
    public int? EffectiveErgWatts => Mode switch
    {
        RideMode.Erg => ErgWatts,
        RideMode.Workout when Player?.TargetWatts(Settings.Ftp) is { } w => (int)Math.Round(w * WorkoutIntensityPct / 100),
        _ => null,
    };

    public void SetMode(RideMode mode)
    {
        if (mode == RideMode.Workout)
        {
            if (Player is null) return;
        }
        else
        {
            ManualMode = mode;
            Player = null;
            IsWorkoutPaused = false;
        }
        Mode = mode;
        Changed?.Invoke(this, EventArgs.Empty);
        PushTarget();
    }

    public void StartWorkout(Workout workout)
    {
        Player = new WorkoutPlayer(workout);
        WorkoutIntensityPct = 100;
        IsWorkoutPaused = false;
        Mode = RideMode.Workout;
        Apply(Player.Start());
        Changed?.Invoke(this, EventArgs.Empty);
        PushTarget();
    }

    public void StopWorkout() => SetMode(ManualMode);

    public void TogglePause()
    {
        if (Mode != RideMode.Workout || Player is null) return;
        IsWorkoutPaused = !IsWorkoutPaused;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SkipStep()
    {
        if (Player is null) return;
        Apply(Player.SkipStep());
        Changed?.Invoke(this, EventArgs.Empty);
        PushTarget();
    }

    public void PreviousStep()
    {
        if (Player is null) return;
        Apply(Player.PreviousStep());
        Changed?.Invoke(this, EventArgs.Empty);
        PushTarget();
    }

    /// <summary>Context-sensitive "+": more watts in ERG (or a harder workout), a harder gear otherwise.</summary>
    public void Up() => Nudge(+1);

    public void Down() => Nudge(-1);

    private void Nudge(int direction)
    {
        switch (Mode, EffectiveKind)
        {
            case (RideMode.Erg, _):
                SetErgWatts(ErgWatts + direction * Settings.ErgStepWatts);
                break;
            case (RideMode.Workout, StepKind.Erg):
                WorkoutIntensityPct = Math.Clamp(WorkoutIntensityPct + direction * Settings.WorkoutIntensityStepPct, 50, 150);
                Changed?.Invoke(this, EventArgs.Empty);
                PushTarget();
                break;
            default:
                SetGear(Gear + direction);
                break;
        }
    }

    public void SetGear(int gear)
    {
        Gear = GearTable.Clamp(gear);
        Changed?.Invoke(this, EventArgs.Empty);
        PushTarget();
    }

    public void SetErgWatts(int watts)
    {
        ErgWatts = Math.Clamp(watts, 0, 2000);
        Changed?.Invoke(this, EventArgs.Empty);
        PushTarget();
    }

    public void SetGrade(double gradePct)
    {
        ManualGrade = Math.Round(Math.Clamp(gradePct, -10, 20), 1);
        Changed?.Invoke(this, EventArgs.Empty);
        PushTarget();
    }

    public void AdjustGrade(int direction) => SetGrade(ManualGrade + direction * Settings.GradeStepPct);

    public void SetWorkoutIntensity(double pct)
    {
        WorkoutIntensityPct = Math.Clamp(pct, 50, 150);
        Changed?.Invoke(this, EventArgs.Empty);
        PushTarget();
    }

    /// <summary>Applies the configured action for each newly pressed Click button.</summary>
    public void OnClick(ClickButtons pressed)
    {
        foreach (var button in Enum.GetValues<ClickButtons>())
        {
            if (button == ClickButtons.None || !pressed.HasFlag(button)) continue;
            if (Settings.ClickMapping.TryGetValue(button, out var action)) Do(action);
        }
    }

    /// <summary>Raised for <see cref="ClickAction.ToggleCompact"/>; the UI owns the window.</summary>
    public event EventHandler? ToggleCompactRequested;

    public void Do(ClickAction action)
    {
        switch (action)
        {
            case ClickAction.Up: Up(); break;
            case ClickAction.Down: Down(); break;
            case ClickAction.PauseResume: TogglePause(); break;
            case ClickAction.SkipStep: SkipStep(); break;
            case ClickAction.ToggleCompact: ToggleCompactRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }

    /// <summary>Thread-safe: called from the Bluetooth thread.</summary>
    public void OnTrainerData(TrainerData data)
    {
        lock (_samplesLock)
        {
            if (data.PowerW is { } p)
            {
                _powerSamples.Enqueue((data.Timestamp, p));
                _latestPower = (p, data.Timestamp);
            }
            if (data.CadenceRpm is { } c) _cadence = (c, data.Timestamp);
            if (data.HeartRate is { } hr) _heartRate = (hr, data.Timestamp);
        }
    }

    public void Tick(DateTimeOffset now)
    {
        var dt = _lastTick is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 2) : 0;
        _lastTick = now;
        UpdateLiveValues(now);

        // Judge pedalling on the latest readings, not the 3 s average, so auto-pause reacts promptly.
        var pedalling = _instantPower > 5 || CadenceRpm > 5;
        if (pedalling) _lastPedalling = now;
        IsAutoPaused = Settings.AutoPause && Mode == RideMode.Workout && !pedalling && now - _lastPedalling >= AutoPauseAfter;

        var targetSpeed = RiderPhysics.SteadySpeed(PowerW, EffectiveGrade, Settings.RiderKg + Settings.BikeKg);
        _speedMps = RiderPhysics.Approach(_speedMps, targetSpeed, dt);
        var distance = _speedMps * dt;
        DistanceM += distance;
        if (pedalling)
        {
            RideSeconds += dt;
            WorkKj += PowerW * dt / 1000;
        }

        if (Mode == RideMode.Workout && Player is { IsFinished: false } player && !IsWorkoutPaused && !IsAutoPaused)
            Apply(player.Advance(dt, distance));

        if (Cue is not null && now >= _cueUntil) Cue = null;
        PushTarget();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateLiveValues(DateTimeOffset now)
    {
        lock (_samplesLock)
        {
            while (_powerSamples.Count > 0 && now - _powerSamples.Peek().At > PowerWindow) _powerSamples.Dequeue();
            PowerW = _powerSamples.Count > 0 ? _powerSamples.Average(s => s.Watts) : 0;
            CadenceRpm = _cadence is { } c && now - c.At <= StaleAfter ? c.Rpm : 0;
            _instantPower = _latestPower is { } lp && now - lp.At <= StaleAfter ? lp.Watts : 0;
            HeartRate = _heartRate is { } h && now - h.At <= StaleAfter ? h.Bpm : null;
        }
    }

    private void Apply(IReadOnlyList<PlayerEvent> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case StepStarted { Step.Gear: { } gear }:
                    Gear = GearTable.Clamp(gear);
                    break;
                case CueReached cue:
                    ShowCue(cue.Text);
                    break;
                case WorkoutFinished:
                    ShowCue("Workout complete. Nice work!");
                    break;
            }
        }
    }

    private void ShowCue(string text)
    {
        Cue = text;
        _cueUntil = (_lastTick ?? DateTimeOffset.UtcNow) + CueDuration;
        CueRaised?.Invoke(this, text);
    }

    private void PushTarget()
    {
        TrainerTarget target = EffectiveErgWatts is { } watts && EffectiveKind == StepKind.Erg
            ? new ErgTarget(watts)
            : new SimTarget(EffectiveGrade, Gear);
        CurrentTarget = target;
        _trainer?.SetTarget(target);
    }
}
