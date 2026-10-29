namespace MyWahoo.Workouts.Parsers;

// Coggan power zones as %FTP midpoints (used by intervals.icu text and FIT zone targets).
internal static class PowerZones
{
    static readonly double[] Midpoints = [50, 65, 83, 98, 113, 135, 160];

    public static bool TryGetMidpoint(int zone, out double percentFtp)
    {
        var ok = zone is >= 1 and <= 7;
        percentFtp = ok ? Midpoints[zone - 1] : 0;
        return ok;
    }
}
