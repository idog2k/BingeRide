using Dynastream.Fit;
using BingeRide.Core.Session;
using BingeRide.Workouts.Export;
using FitFile = Dynastream.Fit.File;

namespace BingeRide.Workouts.Tests;

public class FitActivityWriterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 18, 0, 0, TimeSpan.Zero);

    /// <summary>10 min at 150 W / 85 rpm / HR 130→150, with a 60 s pause in the middle.</summary>
    private static RideRecording Ride()
    {
        var ride = new RideRecording { StartTime = T0, Ftp = 140, WorkoutName = "Test ride" };
        ride.TimerEvents.Add(new TimerEvent(T0, true));
        var distance = 0.0;
        for (var i = 0; i < 600; i++)
        {
            var t = i < 300 ? T0.AddSeconds(i) : T0.AddSeconds(i + 60);
            if (i == 300)
            {
                ride.TimerEvents.Add(new TimerEvent(T0.AddSeconds(300), false));
                ride.TimerEvents.Add(new TimerEvent(t, true));
            }
            distance += 9;
            ride.Samples.Add(new RideSample(t, 150, 85, 130 + i / 30, 9, distance));
        }
        ride.TimerEvents.Add(new TimerEvent(T0.AddSeconds(660), false));
        ride.EndTime = T0.AddSeconds(660);
        return ride;
    }

    [Fact]
    public void Writes_a_valid_indoor_cycling_activity()
    {
        var bytes = FitActivityWriter.ToBytes(Ride());

        var decoder = new Decode();
        var broadcaster = new MesgBroadcaster();
        decoder.MesgEvent += broadcaster.OnMesg;
        FileIdMesg? fileId = null;
        SessionMesg? session = null;
        ActivityMesg? activity = null;
        var records = new List<RecordMesg>();
        var timer = new List<EventType?>();
        broadcaster.FileIdMesgEvent += (_, e) => fileId = (FileIdMesg)e.mesg;
        broadcaster.SessionMesgEvent += (_, e) => session = (SessionMesg)e.mesg;
        broadcaster.ActivityMesgEvent += (_, e) => activity = (ActivityMesg)e.mesg;
        broadcaster.RecordMesgEvent += (_, e) => records.Add((RecordMesg)e.mesg);
        broadcaster.EventMesgEvent += (_, e) => timer.Add(((EventMesg)e.mesg).GetEventType());

        using var stream = new MemoryStream(bytes);
        Assert.True(decoder.IsFIT(stream));
        Assert.True(decoder.CheckIntegrity(stream));
        Assert.True(decoder.Read(stream));

        Assert.Equal(FitFile.Activity, fileId!.GetType());
        Assert.Equal(600, records.Count);
        Assert.Equal((ushort)150, records[0].GetPower());
        Assert.Equal((byte)85, records[0].GetCadence());
        Assert.Equal((byte)149, records[^1].GetHeartRate());
        Assert.Equal(5400, records[^1].GetDistance()!.Value, 1);
        Assert.Equal([EventType.Start, EventType.StopAll, EventType.Start, EventType.StopAll], timer);

        Assert.Equal(Sport.Cycling, session!.GetSport());
        Assert.Equal(SubSport.IndoorCycling, session.GetSubSport());
        Assert.Equal(660, session.GetTotalElapsedTime()!.Value, 0);
        Assert.Equal(600, session.GetTotalTimerTime()!.Value, 0);
        Assert.Equal((ushort)150, session.GetAvgPower());
        Assert.Equal((ushort)150, session.GetNormalizedPower());
        Assert.Equal((byte)149, session.GetMaxHeartRate());
        Assert.Equal(90_000u, session.GetTotalWork());
        Assert.Equal((ushort)140, session.GetThresholdPower());
        Assert.Equal((ushort)1, activity!.GetNumSessions());
    }

    [Fact]
    public void Summary_matches_the_samples()
    {
        var s = Ride().Summarize();
        Assert.Equal(600, s.MovingSec);
        Assert.Equal(660, s.ElapsedSec);
        Assert.Equal(90, s.WorkKj, 3);
        Assert.Equal(150, s.NormalizedPowerW, 6);
        Assert.Equal(150 / 140.0, s.IntensityFactor, 6);
        Assert.Equal(140, s.AvgHeartRate); // 130…149, 30 s each: 139.5
        Assert.Equal(149, s.MaxHeartRate);
        Assert.Equal("Test ride", s.WorkoutName);
    }
}
