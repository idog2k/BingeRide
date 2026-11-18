using BingeRide.Core.Session;
using Xunit;

namespace BingeRide.Core.Tests;

public class RideTrackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);

    /// <summary>10 min: 5 min at 100 W, then 5 min at 200 W; coasting every 10th second; no heart rate in minute 3.</summary>
    private static RideRecording Ride()
    {
        var ride = new RideRecording { StartTime = T0, Ftp = 200 };
        ride.TimerEvents.Add(new TimerEvent(T0, true));
        for (var i = 0; i < 600; i++)
        {
            var watts = i < 300 ? 100 : 200;
            int? hr = i is >= 120 and < 180 ? null : 120 + i / 60;
            ride.Samples.Add(new RideSample(T0.AddSeconds(i), watts, i % 10 == 0 ? 0 : 90, hr, 8, 8.0 * (i + 1)));
        }
        ride.EndTime = T0.AddSeconds(600);
        ride.TimerEvents.Add(new TimerEvent(ride.EndTime, false));
        return ride;
    }

    [Fact]
    public void Whole_ride_stats_match_the_ride_summary()
    {
        var ride = Ride();
        var summary = ride.Summarize();
        var stats = new RideTrack(ride.Samples).Stats(0, 600);

        Assert.Equal(600, stats.Seconds);
        Assert.Equal(summary.AvgPowerW, stats.AvgPowerW, 6);
        Assert.Equal(summary.MaxPowerW, stats.MaxPowerW);
        Assert.Equal(summary.NormalizedPowerW, stats.NormalizedPowerW, 6);
        Assert.Equal(summary.AvgCadence, stats.AvgCadence, 6);
        Assert.Equal(summary.AvgHeartRate, (int)Math.Round(stats.AvgHeartRate!.Value));
        Assert.Equal(summary.MaxHeartRate, stats.MaxHeartRate);
        Assert.Equal(summary.DistanceM, stats.DistanceM, 6);
        Assert.Equal(summary.WorkKj, stats.WorkKj, 6);
    }

    [Fact]
    public void A_stretch_has_its_own_averages()
    {
        var track = new RideTrack(Ride().Samples);

        var second = track.Stats(300, 600);
        Assert.Equal(200, second.AvgPowerW);
        Assert.Equal(200, second.NormalizedPowerW, 6);
        Assert.Equal(60, second.WorkKj, 6);
        Assert.Equal(300 * 8, second.DistanceM, 6);
        Assert.Equal(90, second.AvgCadence); // coasting seconds left out, like the summary
        Assert.Equal(90, second.MaxCadence);

        Assert.Equal(150, track.MeanPower(290, 310));
        Assert.Equal(81, track.MeanCadence(0, 10)); // the plotted mean includes coasting
        Assert.Null(track.MeanHeartRate(120, 180)); // the strap dropped out
        Assert.Equal(123, track.MeanHeartRate(120, 181)); // only second 180 has one
        Assert.Null(track.Stats(130, 170).AvgHeartRate);
    }

    [Fact]
    public void Out_of_range_requests_are_clamped()
    {
        var track = new RideTrack(Ride().Samples);
        Assert.Equal(600, track.Stats(-50, 10_000).Seconds);
        Assert.Equal(default, track.Stats(700, 800));
        Assert.Equal(0, track.MeanPower(10, 10));
        Assert.Equal(200, track.MaxPowerW);
        Assert.Equal(120, track.MinHeartRate);
        Assert.Equal(129, track.MaxHeartRate);
    }
}
