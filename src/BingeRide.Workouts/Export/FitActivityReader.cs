using Dynastream.Fit;
using BingeRide.Core.Session;
using FitDateTime = Dynastream.Fit.DateTime;

namespace BingeRide.Workouts.Export;

/// <summary>
/// Reads a FIT activity back into a <see cref="RideRecording"/> (records and timer events) so a saved ride can be
/// charted. Built for the files <see cref="FitActivityWriter"/> writes; other apps' activities work as long as
/// they have records with timestamps.
/// </summary>
public static class FitActivityReader
{
    public static RideRecording Read(string path)
    {
        using var file = System.IO.File.OpenRead(path);
        return Read(file);
    }

    /// <exception cref="InvalidDataException">The stream isn't a readable FIT file.</exception>
    public static RideRecording Read(Stream stream)
    {
        var samples = new List<RideSample>();
        var timer = new List<TimerEvent>();
        System.DateTimeOffset? start = null, end = null;
        double ftp = 0;

        var decoder = new Decode();
        var broadcaster = new MesgBroadcaster();
        decoder.MesgEvent += broadcaster.OnMesg;
        decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
        broadcaster.RecordMesgEvent += (_, e) =>
        {
            var r = (RecordMesg)e.mesg;
            if (r.GetTimestamp() is not { } ts) return;
            // Fields a record leaves out can come back as FIT's "invalid" value (all bits set) rather than null.
            samples.Add(new RideSample(
                Time(ts),
                r.GetPower() is { } watts and < ushort.MaxValue ? watts : 0,
                r.GetCadence() is { } rpm and < byte.MaxValue ? rpm : 0,
                r.GetHeartRate() is { } hr and > 0 and < byte.MaxValue ? hr : null,
                Valid(r.GetEnhancedSpeed()) ?? Valid(r.GetSpeed()) ?? 0,
                Valid(r.GetDistance()) ?? 0));
        };
        broadcaster.EventMesgEvent += (_, e) =>
        {
            var m = (EventMesg)e.mesg;
            if (m.GetEvent() != Event.Timer || m.GetTimestamp() is not { } ts) return;
            if (m.GetEventType() is EventType.Start) timer.Add(new TimerEvent(Time(ts), true));
            else if (m.GetEventType() is EventType.Stop or EventType.StopAll or EventType.StopDisable or EventType.StopDisableAll)
                timer.Add(new TimerEvent(Time(ts), false));
        };
        broadcaster.SessionMesgEvent += (_, e) =>
        {
            var s = (SessionMesg)e.mesg;
            if (s.GetStartTime() is { } st) start ??= Time(st);
            if (s.GetTimestamp() is { } ts) end = Time(ts);
            if (s.GetThresholdPower() is { } tp and > 0) ftp = tp;
        };

        try
        {
            if (!decoder.IsFIT(stream)) throw new InvalidDataException("This isn't a FIT file.");
            stream.Position = 0;
            decoder.Read(stream);
        }
        catch (FitException ex)
        {
            // A ride cut short (e.g. a crash mid-save) still has its records up to the damage: chart those.
            if (samples.Count == 0) throw new InvalidDataException($"The FIT file can't be read: {ex.Message}", ex);
        }

        samples.Sort((a, b) => a.Time.CompareTo(b.Time));
        var startTime = start ?? (samples.Count > 0 ? samples[0].Time : timer.Count > 0 ? timer[0].Time : default);
        var ride = new RideRecording { StartTime = startTime, Ftp = ftp };
        ride.Samples.AddRange(samples);
        ride.TimerEvents.AddRange(timer.OrderBy(t => t.Time));
        ride.EndTime = end ?? (samples.Count > 0 ? samples[^1].Time : startTime);
        return ride;
    }

    private static double? Valid(float? value) => value is { } v && float.IsFinite(v) && v >= 0 && v < 1e7 ? v : null;

    private static System.DateTimeOffset Time(FitDateTime t) => new(System.DateTime.SpecifyKind(t.GetDateTime(), DateTimeKind.Utc));
}
