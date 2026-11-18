namespace BingeRide.Core.Session;

/// <summary>Totals for a stretch of a ride (the whole ride, or the part a chart is zoomed into).</summary>
public readonly record struct RangeStats(
    int Seconds,
    double AvgPowerW,
    int MaxPowerW,
    double NormalizedPowerW,
    double? AvgHeartRate,
    int? MaxHeartRate,
    double AvgCadence,
    int MaxCadence,
    double DistanceM,
    double WorkKj);

/// <summary>
/// A recorded ride's 1 Hz samples, indexed by riding time (sample i = second i of the ride timer, pauses left out),
/// with running sums so averages over any stretch are cheap enough to recompute on every mouse move.
/// </summary>
public sealed class RideTrack
{
    private readonly long[] _power, _cadence, _pedalling, _hr, _hrCount;

    public RideTrack(IReadOnlyList<RideSample> samples)
    {
        Samples = samples;
        var n = samples.Count;
        _power = new long[n + 1];
        _cadence = new long[n + 1];
        _pedalling = new long[n + 1];
        _hr = new long[n + 1];
        _hrCount = new long[n + 1];
        for (var i = 0; i < n; i++)
        {
            var s = samples[i];
            _power[i + 1] = _power[i] + s.PowerW;
            _cadence[i + 1] = _cadence[i] + s.CadenceRpm;
            _pedalling[i + 1] = _pedalling[i] + (s.CadenceRpm > 0 ? 1 : 0);
            _hr[i + 1] = _hr[i] + (s.HeartRate is int hr and > 0 ? hr : 0);
            _hrCount[i + 1] = _hrCount[i] + (s.HeartRate is > 0 ? 1 : 0);
            MaxPowerW = Math.Max(MaxPowerW, s.PowerW);
            MaxCadence = Math.Max(MaxCadence, s.CadenceRpm);
            if (s.HeartRate is int bpm and > 0)
            {
                MinHeartRate = Math.Min(MinHeartRate ?? bpm, bpm);
                MaxHeartRate = Math.Max(MaxHeartRate ?? bpm, bpm);
            }
        }
    }

    public IReadOnlyList<RideSample> Samples { get; }

    /// <summary>Riding seconds (one sample per second while the ride timer runs).</summary>
    public int Count => Samples.Count;

    public int MaxPowerW { get; }
    public int MaxCadence { get; }
    public int? MinHeartRate { get; }
    public int? MaxHeartRate { get; }
    public bool HasHeartRate => MaxHeartRate is not null;

    /// <summary>Mean power over samples [from, to), coasting included (what a chart column plots).</summary>
    public double MeanPower(int from, int to) => Mean(_power, from, to);

    /// <summary>Mean cadence over samples [from, to), coasting (0 rpm) included.</summary>
    public double MeanCadence(int from, int to) => Mean(_cadence, from, to);

    /// <summary>Mean heart rate over the samples in [from, to) that have one; null when none do.</summary>
    public double? MeanHeartRate(int from, int to)
    {
        (from, to) = Clamp(from, to);
        var count = _hrCount[to] - _hrCount[from];
        return count > 0 ? (double)(_hr[to] - _hr[from]) / count : null;
    }

    public RangeStats Stats(int from, int to)
    {
        (from, to) = Clamp(from, to);
        var n = to - from;
        if (n <= 0) return default;
        int maxPower = 0, maxCadence = 0;
        int? maxHr = null;
        for (var i = from; i < to; i++)
        {
            var s = Samples[i];
            maxPower = Math.Max(maxPower, s.PowerW);
            maxCadence = Math.Max(maxCadence, s.CadenceRpm);
            if (s.HeartRate is int hr and > 0) maxHr = Math.Max(maxHr ?? 0, hr);
        }
        var pedalling = _pedalling[to] - _pedalling[from];
        var distanceBefore = from > 0 ? Samples[from - 1].DistanceM : 0;
        return new RangeStats(
            n,
            MeanPower(from, to),
            maxPower,
            RideRecording.NormalizedPower(Samples, from, n),
            MeanHeartRate(from, to),
            maxHr,
            // Like the ride summary: average cadence while pedalling.
            pedalling > 0 ? (double)(_cadence[to] - _cadence[from]) / pedalling : 0,
            maxCadence,
            Math.Max(0, Samples[to - 1].DistanceM - distanceBefore),
            (_power[to] - _power[from]) / 1000.0);
    }

    private double Mean(long[] sums, int from, int to)
    {
        (from, to) = Clamp(from, to);
        return to > from ? (double)(sums[to] - sums[from]) / (to - from) : 0;
    }

    private (int From, int To) Clamp(int from, int to)
    {
        from = Math.Clamp(from, 0, Count);
        return (from, Math.Clamp(to, from, Count));
    }
}
