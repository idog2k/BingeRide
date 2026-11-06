using BingeRide.Core.Devices;
using BingeRide.Core.Session;
using BingeRide.Core.Settings;
using BingeRide.Core.Workouts;
using Xunit;

namespace BingeRide.Core.Tests;

public class RideRecordingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);

    /// <summary>Feeds trainer data and ticks at 4 Hz from <paramref name="from"/> for <paramref name="seconds"/>.</summary>
    private static DateTimeOffset Ride(RideController c, DateTimeOffset from, double seconds, int watts = 150, int? hr = 130)
    {
        var n = (int)(seconds * 4);
        for (var i = 1; i <= n; i++)
        {
            var now = from.AddSeconds(i / 4.0);
            c.OnTrainerData(new TrainerData(watts, watts > 0 ? 85 : 0, hr, now));
            c.Tick(now);
        }
        return from.AddSeconds(n / 4.0);
    }

    [Fact]
    public void Nothing_accumulates_until_the_ride_is_started()
    {
        var c = new RideController(new AppSettings());
        Ride(c, T0, 30);
        Assert.Equal(RideState.Idle, c.State);
        Assert.Equal(0, c.RideSeconds);
        Assert.Equal(0, c.DistanceM);
        Assert.Null(c.Recording);
        Assert.True(c.PowerW > 0); // live values still show
    }

    [Fact]
    public void Records_one_sample_per_second_and_finishes()
    {
        var c = new RideController(new AppSettings { Ftp = 150 });
        c.Tick(T0);
        c.StartRide();
        var end = Ride(c, T0, 120);
        RideRecording? finished = null;
        c.RideFinished += (_, r) => finished = r;
        c.FinishRide();

        Assert.Equal(RideState.Finished, c.State);
        Assert.NotNull(finished);
        Assert.InRange(finished!.Samples.Count, 120, 121);
        Assert.Equal(end, finished.EndTime);
        Assert.All(finished.Samples.Zip(finished.Samples.Skip(1)), p => Assert.Equal(TimeSpan.FromSeconds(1), p.Second.Time - p.First.Time));
        Assert.Equal([true, false], finished.TimerEvents.Select(e => e.Running));
        var s = finished.Summarize();
        Assert.InRange(s.AvgPowerW, 149, 151);
        Assert.InRange(s.DistanceM, 600, 1100);
        Assert.Equal(130, s.AvgHeartRate);
        Assert.InRange(c.RideSeconds, 119, 121); // totals stay on screen after finishing
    }

    [Fact]
    public void Manual_pause_stops_time_and_samples()
    {
        var c = new RideController(new AppSettings());
        c.Tick(T0);
        c.TogglePause(); // Idle → recording (Space / Click)
        Assert.Equal(RideState.Recording, c.State);
        var t = Ride(c, T0, 60);
        c.TogglePause();
        Assert.True(c.IsPaused);
        t = Ride(c, t, 60);
        c.TogglePause();
        Ride(c, t, 60);
        var rec = c.FinishRide()!;
        Assert.InRange(rec.Samples.Count, 119, 122);
        Assert.InRange(c.RideSeconds, 119, 121);
        Assert.Equal([true, false, true, false], rec.TimerEvents.Select(e => e.Running));
        Assert.InRange(rec.Summarize().ElapsedSec, 179, 181);
    }

    [Fact]
    public void Auto_pause_stops_the_timer_when_pedalling_stops()
    {
        var c = new RideController(new AppSettings { AutoPause = true });
        c.Tick(T0);
        c.StartRide();
        var t = Ride(c, T0, 30);
        t = Ride(c, t, 30, watts: 0, hr: null); // stopped for 30 s
        Assert.True(c.IsAutoPaused);
        Ride(c, t, 30);
        Assert.False(c.IsAutoPaused);
        var rec = c.FinishRide()!;
        Assert.InRange(rec.Samples.Count, 60, 65); // the 3 s grace before auto-pause is recorded
        Assert.Equal([true, false, true, false], rec.TimerEvents.Select(e => e.Running));
    }

    [Fact]
    public void Starting_a_workout_starts_recording_and_finishing_ends_the_workout()
    {
        var c = new RideController(new AppSettings());
        c.Tick(T0);
        var workout = new Workout { Name = "Openers", Steps = [new WorkoutStep { Kind = StepKind.Erg, DurationSec = 600, PowerStart = PowerTarget.Watts(120) }] };
        c.StartWorkout(workout);
        Assert.Equal(RideState.Recording, c.State);
        Assert.Equal("Openers", c.Recording!.WorkoutName);
        Ride(c, T0, 10);
        c.FinishRide();
        Assert.Equal(RideMode.Free, c.Mode);
        Assert.Null(c.Player);
    }

    [Fact]
    public void A_new_ride_resets_the_totals()
    {
        var c = new RideController(new AppSettings());
        c.Tick(T0);
        c.StartRide();
        var t = Ride(c, T0, 60);
        c.FinishRide();
        c.StartRide();
        Assert.Equal(0, c.RideSeconds);
        Assert.Equal(0, c.WorkKj);
        Assert.Null(c.MaxHeartRate);
        Ride(c, t, 10);
        Assert.InRange(c.Recording!.Samples.Count, 10, 11);
    }
}
