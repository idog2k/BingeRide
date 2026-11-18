using BingeRide.Core.Session;
using BingeRide.Workouts.Export;

namespace BingeRide.Workouts.Tests;

public class FitActivityReaderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 18, 0, 0, TimeSpan.Zero);

    /// <summary>5 min of varying power with a 60 s pause after 2 min; heart rate drops out for 10 s.</summary>
    private static RideRecording Ride()
    {
        var ride = new RideRecording { StartTime = T0, Ftp = 180 };
        ride.TimerEvents.Add(new TimerEvent(T0, true));
        var distance = 0.0;
        for (var i = 0; i < 300; i++)
        {
            var t = i < 120 ? T0.AddSeconds(i) : T0.AddSeconds(i + 60);
            if (i == 120)
            {
                ride.TimerEvents.Add(new TimerEvent(T0.AddSeconds(120), false));
                ride.TimerEvents.Add(new TimerEvent(t, true));
            }
            distance += 8.5;
            int? hr = i is >= 200 and < 210 ? null : 120 + i / 10;
            ride.Samples.Add(new RideSample(t, 100 + i % 50 * 4, i % 7 == 0 ? 0 : 80 + i % 20, hr, 8.5, distance));
        }
        ride.TimerEvents.Add(new TimerEvent(T0.AddSeconds(360), false));
        ride.EndTime = T0.AddSeconds(360);
        return ride;
    }

    [Fact]
    public void Reads_back_what_the_writer_saved()
    {
        var original = Ride();
        var read = FitActivityReader.Read(new MemoryStream(FitActivityWriter.ToBytes(original)));

        Assert.Equal(original.StartTime, read.StartTime);
        Assert.Equal(original.EndTime, read.EndTime);
        Assert.Equal(180, read.Ftp);
        Assert.Equal(original.Samples.Count, read.Samples.Count);
        for (var i = 0; i < original.Samples.Count; i++)
        {
            var (a, b) = (original.Samples[i], read.Samples[i]);
            Assert.Equal(a.Time, b.Time);
            Assert.Equal(a.PowerW, b.PowerW);
            Assert.Equal(a.CadenceRpm, b.CadenceRpm);
            Assert.Equal(a.HeartRate, b.HeartRate);
            Assert.Equal(a.SpeedMps, b.SpeedMps, 2);
            Assert.Equal(a.DistanceM, b.DistanceM, 1);
        }
        Assert.Equal(original.TimerEvents, read.TimerEvents);
        Assert.Equal(original.Summarize() with { WorkoutName = null }, read.Summarize(), new SummaryComparer());
    }

    [Fact]
    public void Rejects_a_file_that_is_not_fit()
    {
        Assert.Throws<InvalidDataException>(() => FitActivityReader.Read(new MemoryStream("not a ride"u8.ToArray())));
    }

    [Fact]
    public void A_truncated_file_still_gives_the_records_before_the_damage()
    {
        var bytes = FitActivityWriter.ToBytes(Ride());
        var read = FitActivityReader.Read(new MemoryStream(bytes[..(bytes.Length / 2)]));
        Assert.InRange(read.Samples.Count, 50, 299);
        Assert.Equal(T0, read.Samples[0].Time);
    }

    /// <summary>Totals agree to FIT precision (speed and distance are stored scaled).</summary>
    private sealed class SummaryComparer : IEqualityComparer<RideSummary>
    {
        public bool Equals(RideSummary? a, RideSummary? b) =>
            a is not null && b is not null
            && a.Start == b.Start && a.ElapsedSec == b.ElapsedSec && a.MovingSec == b.MovingSec
            && Math.Abs(a.DistanceM - b.DistanceM) < 0.1 && a.WorkKj == b.WorkKj && a.AvgPowerW == b.AvgPowerW
            && a.MaxPowerW == b.MaxPowerW && a.NormalizedPowerW == b.NormalizedPowerW && a.Tss == b.Tss
            && a.AvgCadence == b.AvgCadence && a.AvgHeartRate == b.AvgHeartRate && a.MaxHeartRate == b.MaxHeartRate;

        public int GetHashCode(RideSummary obj) => 0;
    }
}
