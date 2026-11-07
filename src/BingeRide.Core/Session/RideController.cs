using BingeRide.Core.Devices;
using BingeRide.Core.Protocol;
using BingeRide.Core.Settings;
using BingeRide.Core.Workouts;

namespace BingeRide.Core.Session;

public enum RideMode { Erg, Slope, Free, Workout }

/// <summary>
/// The ride's brain: modes, gears, ERG/grade targets, workout progression, live stats, and the ride
/// recorder (totals only accumulate between <see cref="StartRide"/> and <see cref="FinishRide"/>).
/// Everything except <see cref="OnTrainerData"/> must be called from the UI thread;
/// <see cref="Tick"/> is expected about 4×/s.
/// </summary>
public sealed class RideController
{
    private static readonly TimeSpan PowerWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AutoPauseAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CueDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MotivationDuration = TimeSpan.FromSeconds(30);

    private readonly Lock _samplesLock = new();
    private readonly Queue<(DateTimeOffset At, int Watts)> _powerSamples = new();
    private (double Rpm, DateTimeOffset At)? _cadence;
    private (int Watts, DateTimeOffset At)? _latestPower;
    private (int Bpm, DateTimeOffset At)? _heartRate;
    private (int Bpm, DateTimeOffset At)? _sensorHeartRate;
    private double _heartBeatsWeighted, _heartSeconds;
    private DateTimeOffset? _lastTick;
    private DateTimeOffset _lastPedalling;
    private DateTimeOffset _nextSampleAt;
    private bool _timerRunning;
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
    public bool IsAutoPaused { get; private set; }

    public RideState State { get; private set; } = RideState.Idle;
    public bool IsPaused => State == RideState.Paused;
    public bool IsRideActive => State is RideState.Recording or RideState.Paused;

    /// <summary>The current (or just finished) ride.</summary>
    public RideRecording? Recording { get; private set; }

    /// <summary>Running time of the current ride (what the REC timer shows).</summary>
    public double RideTimerSeconds => State == RideState.Idle ? 0 : Recording?.TimerSeconds(_lastTick) ?? 0;

    /// <summary>Raised when a ride is finished, with its samples, for saving.</summary>
    public event EventHandler<RideRecording>? RideFinished;

    public double PowerW { get; private set; }
    public double CadenceRpm { get; private set; }
    public int? HeartRate { get; private set; }
    public int? AverageHeartRate => _heartSeconds > 0 ? (int)Math.Round(_heartBeatsWeighted / _heartSeconds) : null;
    public int? MaxHeartRate { get; private set; }
    public double SpeedKmh => _speedMps * 3.6;
    public double DistanceM { get; private set; }
    public double RideSeconds { get; private set; }
    public double WorkKj { get; private set; }
    public double AveragePowerW => RideSeconds > 0 ? WorkKj * 1000 / RideSeconds : 0;
    public double Kcal => FoodMenu.KcalFromKj(WorkKj);
    public FoodProgress Food => FoodMenu.Progress(Kcal);
    private int _foodsAnnounced;
    private int _landmarksAnnounced, _lifetimeLandmarksAnnounced;
    private readonly Queue<string> _pendingCues = new();

    /// <summary>Distance of all saved rides (km), so all-time milestones can be celebrated mid-ride.</summary>
    public double LifetimeKmBefore { get; set; }

    public double LifetimeKm => LifetimeKmBefore + (IsRideActive ? DistanceM / 1000 : 0);

    public TrainerTarget? CurrentTarget { get; private set; }
    public string? Cue { get; private set; }
    private DateTimeOffset _cueUntil;

    /// <summary>The motivation sentence on screen now, if any. Not a <see cref="Cue"/>: it must never delay a workout instruction.</summary>
    public string? Motivation { get; private set; }
    private readonly MotivationDeck _motivationDeck = new(MotivationSentences.All);
    private DateTimeOffset _motivationUntil;
    private double? _lastMotivationAt; // in RideSeconds, so stopping doesn't count towards the next sentence

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
        }
        Mode = mode;
        Changed?.Invoke(this, EventArgs.Empty);
        PushTarget();
    }

    public void StartWorkout(Workout workout)
    {
        Player = new WorkoutPlayer(workout);
        WorkoutIntensityPct = 100;
        Mode = RideMode.Workout;
        // A workout is always recorded: start the ride (or resume it) if needed.
        if (!IsRideActive) StartRide();
        else if (IsPaused) TogglePause();
        Recording!.WorkoutName ??= workout.Name;
        Apply(Player.Start());
        Changed?.Invoke(this, EventArgs.Empty);
        PushTarget();
    }

    public void StopWorkout() => SetMode(ManualMode);

    /// <summary>Starts recording a ride: totals reset and from now on accumulate, 1 Hz samples are kept for the activity file.</summary>
    public void StartRide(DateTimeOffset? at = null)
    {
        if (IsRideActive) return;
        var now = at ?? _lastTick ?? DateTimeOffset.UtcNow;
        DistanceM = RideSeconds = WorkKj = _heartBeatsWeighted = _heartSeconds = 0;
        MaxHeartRate = null;
        _foodsAnnounced = 0;
        _landmarksAnnounced = 0;
        _lifetimeLandmarksAnnounced = Landmarks.CountReached(LifetimeKmBefore);
        _pendingCues.Clear();
        Motivation = null;
        _lastMotivationAt = null;
        Recording = new RideRecording { StartTime = now, Ftp = Settings.Ftp, WorkoutName = Player?.Workout.Name };
        State = RideState.Recording;
        _lastPedalling = now; // grace period before auto-pause
        _timerRunning = false;
        SetTimer(true, now);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Start ↔ pause ↔ resume (the Space key / a Click button).</summary>
    public void TogglePause()
    {
        var now = _lastTick ?? DateTimeOffset.UtcNow;
        switch (State)
        {
            case RideState.Recording:
                State = RideState.Paused;
                SetTimer(false, now);
                break;
            case RideState.Paused:
                State = RideState.Recording;
                _lastPedalling = now;
                SetTimer(true, now);
                break;
            default:
                StartRide(now);
                break;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops recording (and any workout) and raises <see cref="RideFinished"/>.</summary>
    public RideRecording? FinishRide()
    {
        if (!IsRideActive || Recording is null) return null;
        var now = _lastTick ?? DateTimeOffset.UtcNow;
        SetTimer(false, now);
        Recording.EndTime = now;
        State = RideState.Finished;
        Motivation = null;
        if (Mode == RideMode.Workout) StopWorkout();
        Changed?.Invoke(this, EventArgs.Empty);
        RideFinished?.Invoke(this, Recording);
        return Recording;
    }

    private void SetTimer(bool running, DateTimeOffset now)
    {
        if (running == _timerRunning || Recording is null) return;
        _timerRunning = running;
        Recording.TimerEvents.Add(new TimerEvent(now, running));
        if (running) _nextSampleAt = now;
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

    /// <summary>Thread-safe: a reading from a dedicated heart-rate sensor (strap or broadcasting watch).</summary>
    public void OnHeartRate(int bpm, DateTimeOffset at)
    {
        lock (_samplesLock) _sensorHeartRate = (bpm, at);
    }

    public void Tick(DateTimeOffset now)
    {
        var dt = _lastTick is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 2) : 0;
        _lastTick = now;
        UpdateLiveValues(now);

        // Judge pedalling on the latest readings, not the 3 s average, so auto-pause reacts promptly.
        var pedalling = _instantPower > 5 || CadenceRpm > 5;
        if (pedalling) _lastPedalling = now;
        IsAutoPaused = Settings.AutoPause && State == RideState.Recording && !pedalling && now - _lastPedalling >= AutoPauseAfter;
        var running = State == RideState.Recording && !IsAutoPaused;
        SetTimer(running, now);

        var targetSpeed = RiderPhysics.SteadySpeed(PowerW, EffectiveGrade, Settings.RiderKg + Settings.BikeKg);
        _speedMps = RiderPhysics.Approach(_speedMps, targetSpeed, dt);
        var distance = _speedMps * dt;
        if (running)
        {
            DistanceM += distance;
            if (pedalling)
            {
                RideSeconds += dt;
                WorkKj += PowerW * dt / 1000;
                if (HeartRate is { } bpm)
                {
                    _heartBeatsWeighted += bpm * dt;
                    _heartSeconds += dt;
                }
            }
            if (HeartRate is { } current && current > (MaxHeartRate ?? 0)) MaxHeartRate = current;

            // Exactly one sample per second of running time, stamped on the second grid.
            while (now >= _nextSampleAt)
            {
                Recording!.Samples.Add(new RideSample(_nextSampleAt, _instantPower, (int)Math.Round(CadenceRpm), HeartRate, _speedMps, DistanceM));
                _nextSampleAt += TimeSpan.FromSeconds(1);
            }

            if (Mode == RideMode.Workout && Player is { IsFinished: false } player)
                Apply(player.Advance(dt, distance));
        }

        if (running && Settings.ShowFood)
        {
            var burned = Food.Burned;
            if (burned.Count > _foodsAnnounced)
            {
                var food = burned[^1];
                _foodsAnnounced = burned.Count;
                _pendingCues.Enqueue($"{food.Emoji} {food.Name} burned! (≈ {Kcal:0} kcal so far)");
            }
        }
        if (running && Settings.ShowLandmarks) AnnounceLandmarks();
        UpdateMotivation(now, running && pedalling);

        if (Cue is not null && now >= _cueUntil) Cue = null;
        // Food and distance news wait for any workout instruction on screen to finish, then take turns.
        if (_pendingCues.Count > 0 && Cue is null) ShowCue(_pendingCues.Dequeue());
        PushTarget();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AnnounceLandmarks()
    {
        var km = DistanceM / 1000;
        var reached = Landmarks.CountReached(km);
        if (reached > _landmarksAnnounced)
        {
            var l = Landmarks.All[reached - 1];
            _landmarksAnnounced = reached;
            _pendingCues.Enqueue($"{l.Emoji} You just rode {l.Name} ({l.Km:0.#} km)!");
        }
        // All-time milestones only when earlier rides made the difference (otherwise it's the same news twice).
        var lifetime = Landmarks.CountReached(LifetimeKmBefore + km);
        if (lifetime > _lifetimeLandmarksAnnounced)
        {
            var l = Landmarks.All[lifetime - 1];
            _lifetimeLandmarksAnnounced = lifetime;
            if (LifetimeKmBefore > 0 && l.Km > km)
                _pendingCues.Enqueue($"🏆 All your rides together: {l.Emoji} {l.Name} ({l.Km:#,0} km)!");
        }
    }

    /// <summary>The first sentence comes with the first pedal stroke, then one every few minutes of riding.</summary>
    private void UpdateMotivation(DateTimeOffset now, bool riding)
    {
        if (!Settings.ShowMotivation || now >= _motivationUntil) Motivation = null;
        if (!riding || !Settings.ShowMotivation) return;
        if (_lastMotivationAt is { } last && RideSeconds - last < Math.Max(1, Settings.MotivationMinutes) * 60) return;
        _lastMotivationAt = RideSeconds;
        Motivation = _motivationDeck.Next();
        _motivationUntil = now + MotivationDuration;
    }

    private void UpdateLiveValues(DateTimeOffset now)
    {
        lock (_samplesLock)
        {
            while (_powerSamples.Count > 0 && now - _powerSamples.Peek().At > PowerWindow) _powerSamples.Dequeue();
            PowerW = _powerSamples.Count > 0 ? _powerSamples.Average(s => s.Watts) : 0;
            CadenceRpm = _cadence is { } c && now - c.At <= StaleAfter ? c.Rpm : 0;
            _instantPower = _latestPower is { } lp && now - lp.At <= StaleAfter ? lp.Watts : 0;
            // A dedicated sensor wins; the trainer's relayed HR is the fallback.
            HeartRate = _sensorHeartRate is { } s && now - s.At <= StaleAfter ? s.Bpm
                : _heartRate is { } h && now - h.At <= StaleAfter ? h.Bpm
                : null;
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
