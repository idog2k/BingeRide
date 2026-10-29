using Dynastream.Fit;
using MyWahoo.Core.Session;
using FitDateTime = Dynastream.Fit.DateTime;
using FitFile = Dynastream.Fit.File;

namespace MyWahoo.Workouts.Export;

/// <summary>
/// Writes a recorded ride as a FIT activity (indoor cycling): 1 Hz records with power, cadence, heart rate,
/// speed and distance, timer start/stop events for pauses, and one lap/session with the ride totals.
/// Strava, Garmin Connect, TrainingPeaks and intervals.icu all import it.
/// </summary>
public static class FitActivityWriter
{
    public static byte[] ToBytes(RideRecording ride)
    {
        using var stream = new MemoryStream();
        Write(ride, stream);
        return stream.ToArray();
    }

    /// <param name="stream">Must be seekable: the encoder rewrites the header and CRC at the end.</param>
    public static void Write(RideRecording ride, Stream stream)
    {
        var summary = ride.Summarize();
        var start = Fit(ride.StartTime);
        var end = Fit(ride.EndTime > ride.StartTime ? ride.EndTime : ride.StartTime.AddSeconds(Math.Max(1, summary.ElapsedSec)));

        var encoder = new Encode(ProtocolVersion.V20);
        encoder.Open(stream);

        var fileId = new FileIdMesg();
        fileId.SetType(FitFile.Activity);
        fileId.SetManufacturer(Manufacturer.Development);
        fileId.SetProduct(1);
        fileId.SetSerialNumber(0x4D57);
        fileId.SetTimeCreated(start);
        encoder.Write(fileId);

        var device = new DeviceInfoMesg();
        device.SetTimestamp(start);
        device.SetDeviceIndex(DeviceIndex.Creator);
        device.SetManufacturer(Manufacturer.Development);
        device.SetProduct(1);
        device.SetSoftwareVersion(0.1f);
        encoder.Write(device);

        // Records and timer events, merged in time order (events first on ties so a start precedes its first record).
        var timeline = ride.TimerEvents.Select(e => (e.Time, Order: 0, Mesg: (Mesg)TimerEvent(e)))
            .Concat(ride.Samples.Select(s => (s.Time, Order: 1, Mesg: (Mesg)Record(s))))
            .OrderBy(x => x.Time).ThenBy(x => x.Order);
        foreach (var item in timeline) encoder.Write(item.Mesg);

        var lap = new LapMesg();
        lap.SetMessageIndex(0);
        lap.SetTimestamp(end);
        lap.SetStartTime(start);
        lap.SetEvent(Event.Lap);
        lap.SetEventType(EventType.Stop);
        lap.SetLapTrigger(LapTrigger.SessionEnd);
        lap.SetSport(Sport.Cycling);
        lap.SetSubSport(SubSport.IndoorCycling);
        lap.SetTotalElapsedTime((float)summary.ElapsedSec);
        lap.SetTotalTimerTime((float)summary.MovingSec);
        lap.SetTotalDistance((float)summary.DistanceM);
        lap.SetTotalWork((uint)Math.Round(summary.WorkKj * 1000));
        lap.SetTotalCalories((ushort)Math.Round(summary.WorkKj)); // ~1 kcal per kJ of work for cycling
        lap.SetAvgPower((ushort)Math.Round(summary.AvgPowerW));
        lap.SetMaxPower((ushort)summary.MaxPowerW);
        lap.SetNormalizedPower((ushort)Math.Round(summary.NormalizedPowerW));
        lap.SetAvgCadence((byte)Math.Round(summary.AvgCadence));
        if (summary.AvgHeartRate is { } avgHr) lap.SetAvgHeartRate((byte)avgHr);
        if (summary.MaxHeartRate is { } maxHr) lap.SetMaxHeartRate((byte)maxHr);
        if (summary.MovingSec > 0) lap.SetAvgSpeed((float)(summary.DistanceM / summary.MovingSec));
        encoder.Write(lap);

        var session = new SessionMesg();
        session.SetMessageIndex(0);
        session.SetTimestamp(end);
        session.SetStartTime(start);
        session.SetEvent(Event.Session);
        session.SetEventType(EventType.Stop);
        session.SetTrigger(SessionTrigger.ActivityEnd);
        session.SetSport(Sport.Cycling);
        session.SetSubSport(SubSport.IndoorCycling);
        session.SetFirstLapIndex(0);
        session.SetNumLaps(1);
        session.SetTotalElapsedTime((float)summary.ElapsedSec);
        session.SetTotalTimerTime((float)summary.MovingSec);
        session.SetTotalDistance((float)summary.DistanceM);
        session.SetTotalWork((uint)Math.Round(summary.WorkKj * 1000));
        session.SetTotalCalories((ushort)Math.Round(summary.WorkKj));
        session.SetAvgPower((ushort)Math.Round(summary.AvgPowerW));
        session.SetMaxPower((ushort)summary.MaxPowerW);
        session.SetNormalizedPower((ushort)Math.Round(summary.NormalizedPowerW));
        session.SetAvgCadence((byte)Math.Round(summary.AvgCadence));
        if (summary.AvgHeartRate is { } sAvgHr) session.SetAvgHeartRate((byte)sAvgHr);
        if (summary.MaxHeartRate is { } sMaxHr) session.SetMaxHeartRate((byte)sMaxHr);
        if (summary.MovingSec > 0) session.SetAvgSpeed((float)(summary.DistanceM / summary.MovingSec));
        if (summary.Ftp > 0)
        {
            session.SetThresholdPower((ushort)Math.Round(summary.Ftp));
            session.SetIntensityFactor((float)summary.IntensityFactor);
            session.SetTrainingStressScore((float)summary.Tss);
        }
        encoder.Write(session);

        var activity = new ActivityMesg();
        activity.SetTimestamp(end);
        activity.SetTotalTimerTime((float)summary.MovingSec);
        activity.SetNumSessions(1);
        activity.SetType(Activity.Manual);
        activity.SetEvent(Event.Activity);
        activity.SetEventType(EventType.Stop);
        var localEnd = ride.EndTime > ride.StartTime ? ride.EndTime : ride.StartTime;
        activity.SetLocalTimestamp(new FitDateTime(localEnd.ToLocalTime().DateTime).GetTimeStamp());
        encoder.Write(activity);

        encoder.Close();
    }

    private static FitDateTime Fit(DateTimeOffset t) => new(t.UtcDateTime);

    private static EventMesg TimerEvent(MyWahoo.Core.Session.TimerEvent e)
    {
        var m = new EventMesg();
        m.SetTimestamp(Fit(e.Time));
        m.SetEvent(Event.Timer);
        m.SetEventType(e.Running ? EventType.Start : EventType.StopAll);
        return m;
    }

    private static RecordMesg Record(RideSample s)
    {
        var m = new RecordMesg();
        m.SetTimestamp(Fit(s.Time));
        m.SetPower((ushort)Math.Clamp(s.PowerW, 0, 4000));
        m.SetCadence((byte)Math.Clamp(s.CadenceRpm, 0, 254));
        if (s.HeartRate is { } hr) m.SetHeartRate((byte)Math.Clamp(hr, 0, 254));
        m.SetSpeed((float)s.SpeedMps);
        m.SetDistance((float)s.DistanceM);
        return m;
    }
}
