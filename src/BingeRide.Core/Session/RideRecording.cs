namespace BingeRide.Core.Session;

public enum RideState { Idle, Recording, Paused, Finished }

/// <summary>One recorded second of riding.</summary>
public readonly record struct RideSample(DateTimeOffset Time, int PowerW, int CadenceRpm, int? HeartRate, double SpeedMps, double DistanceM);

/// <summary>The ride timer starting or stopping (start, manual pause, auto-pause, finish).</summary>
public readonly record struct TimerEvent(DateTimeOffset Time, bool Running);

public sealed record RideSummary(
    DateTimeOffset Start,
    double ElapsedSec,
    double MovingSec,
    double DistanceM,
    double WorkKj,
    double AvgPowerW,
    int MaxPowerW,
    double NormalizedPowerW,
    double IntensityFactor,
    double Tss,
    double AvgCadence,
    int? AvgHeartRate,
    int? MaxHeartRate,
    string? WorkoutName,
    double Ftp);

/// <summary>Everything needed to save a ride as an activity file (1 Hz samples while the timer runs).</summary>
public sealed class RideRecording
{
    public required DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; set; }
    public required double Ftp { get; init; }
    public string? WorkoutName { get; set; }
    public List<RideSample> Samples { get; } = [];
    public List<TimerEvent> TimerEvents { get; } = [];

    /// <summary>Seconds the ride timer has run (excludes manual and auto pauses), up to <paramref name="now"/> if still running.</summary>
    public double TimerSeconds(DateTimeOffset? now = null)
    {
        double total = 0;
        DateTimeOffset? runningSince = null;
        foreach (var e in TimerEvents)
        {
            if (e.Running) runningSince ??= e.Time;
            else if (runningSince is { } since)
            {
                total += (e.Time - since).TotalSeconds;
                runningSince = null;
            }
        }
        if (runningSince is { } open && now is { } n && n > open) total += (n - open).TotalSeconds;
        return total;
    }

    public RideSummary Summarize()
    {
        var s = Samples;
        var end = EndTime > StartTime ? EndTime : s.Count > 0 ? s[^1].Time : StartTime;
        var moving = TimerSeconds(end);
        var avgPower = s.Count > 0 ? s.Average(x => x.PowerW) : 0;
        var np = NormalizedPower(s);
        var intensity = Ftp > 0 ? np / Ftp : 0;
        var pedalling = s.Where(x => x.CadenceRpm > 0).ToList();
        var withHr = s.Where(x => x.HeartRate is > 0).Select(x => x.HeartRate!.Value).ToList();
        return new RideSummary(
            StartTime,
            ElapsedSec: (end - StartTime).TotalSeconds,
            MovingSec: moving,
            DistanceM: s.Count > 0 ? s[^1].DistanceM : 0,
            WorkKj: s.Sum(x => x.PowerW) / 1000.0,
            AvgPowerW: avgPower,
            MaxPowerW: s.Count > 0 ? s.Max(x => x.PowerW) : 0,
            NormalizedPowerW: np,
            IntensityFactor: intensity,
            Tss: Ftp > 0 ? moving * np * intensity / (Ftp * 3600) * 100 : 0,
            AvgCadence: pedalling.Count > 0 ? pedalling.Average(x => x.CadenceRpm) : 0,
            AvgHeartRate: withHr.Count > 0 ? (int)Math.Round(withHr.Average()) : null,
            MaxHeartRate: withHr.Count > 0 ? withHr.Max() : null,
            WorkoutName,
            Ftp);
    }

    /// <summary>4th-power mean of 30 s rolling averages (plain average for rides under 30 s).</summary>
    private static double NormalizedPower(List<RideSample> s)
    {
        if (s.Count == 0) return 0;
        if (s.Count < 30) return s.Average(x => x.PowerW);
        double window = 0, sum4 = 0;
        for (var i = 0; i < s.Count; i++)
        {
            window += s[i].PowerW;
            if (i >= 30) window -= s[i - 30].PowerW;
            if (i >= 29) sum4 += Math.Pow(window / 30, 4);
        }
        return Math.Pow(sum4 / (s.Count - 29), 0.25);
    }
}
