using BingeRide.Core.Workouts;

namespace BingeRide.Workouts;

/// <summary>Preview numbers for a workout at a given FTP.</summary>
/// <param name="TotalSeconds">Sum of time-based step durations.</param>
/// <param name="TotalMeters">Sum of distance-based step lengths.</param>
/// <param name="AverageWatts">Time-weighted average target (estimates included) over time-based steps.</param>
/// <param name="NormalizedPower">NP: 30 s rolling average, 4th-power mean, 4th root (sampled per second).</param>
/// <param name="IntensityFactor">NP / FTP.</param>
/// <param name="Tss">Training Stress Score: hours × IF² × 100.</param>
/// <param name="MaxTargetWatts">Highest exact ERG target in watts (0 if the workout has no ERG steps).</param>
/// <param name="EstimatedSeconds">Seconds whose power was estimated (Free at 50 % FTP, time-based Slope at 65 % FTP).</param>
public sealed record WorkoutSummary(
    double TotalSeconds,
    double TotalMeters,
    double AverageWatts,
    double NormalizedPower,
    double IntensityFactor,
    double Tss,
    double MaxTargetWatts,
    double EstimatedSeconds);

/// <summary>Fast workout statistics for preview panels.</summary>
public static class WorkoutStats
{
    /// <summary>Estimated share of FTP for Free steps.</summary>
    public const double FreeFtpFraction = WorkoutEstimates.FreeFtpFraction;

    /// <summary>Estimated share of FTP for time-based Slope steps.</summary>
    public const double SlopeFtpFraction = WorkoutEstimates.SlopeFtpFraction;

    const int NpWindow = 30;

    /// <summary>
    /// Computes the summary at <paramref name="ftp"/> watts. ERG steps count exactly; Free and
    /// time-based Slope steps are estimated; distance-based steps only contribute to TotalMeters.
    /// </summary>
    public static WorkoutSummary Compute(Workout workout, double ftp)
    {
        ArgumentNullException.ThrowIfNull(workout);
        if (!(ftp > 0) || !double.IsFinite(ftp))
            throw new ArgumentOutOfRangeException(nameof(ftp), ftp, "FTP must be a positive number of watts.");

        var steps = workout.Steps;
        double seconds = 0, metres = 0, work = 0, maxWatts = 0, estimated = 0;
        foreach (var step in steps)
        {
            if (step.DistanceM > 0) metres += step.DistanceM;
            if (!(step.DurationSec > 0)) continue;
            var (start, end, exact) = Watts(step, ftp);
            seconds += step.DurationSec;
            work += (start + end) / 2 * step.DurationSec;
            if (exact) maxWatts = Math.Max(maxWatts, Math.Max(start, end));
            else estimated += step.DurationSec;
        }

        var average = seconds > 0 ? work / seconds : 0;
        var np = NormalizedPower(steps, ftp, seconds, average);
        var intensity = np / ftp;
        var tss = seconds / 3600 * intensity * intensity * 100;
        return new WorkoutSummary(seconds, metres, average, np, intensity, tss, maxWatts, estimated);
    }

    static (double Start, double End, bool Exact) Watts(WorkoutStep step, double ftp)
    {
        if (step.Kind == StepKind.Erg && step.PowerStart is { } p)
            return (p.ToWatts(ftp), (step.PowerEnd ?? p).ToWatts(ftp), true);
        var estimate = (step.Kind == StepKind.Slope ? SlopeFtpFraction : FreeFtpFraction) * ftp;
        return (estimate, estimate, false);
    }

    static double NormalizedPower(IReadOnlyList<WorkoutStep> steps, double ftp, double totalSeconds, double average)
    {
        var samples = (int)Math.Round(totalSeconds, MidpointRounding.AwayFromZero);
        if (samples < NpWindow) return average;

        Span<double> window = stackalloc double[NpWindow];
        double windowSum = 0, sum4 = 0;
        var count = 0;
        var index = -1;
        double stepStart = 0, stepEnd = 0, duration = 1, a = 0, b = 0;
        for (var i = 0; i < samples; i++)
        {
            var t = i + 0.5; // sample each second at its midpoint
            while (t >= stepEnd && index < steps.Count - 1)
            {
                var next = steps[++index];
                if (!(next.DurationSec > 0)) continue; // distance-based: no time
                stepStart = stepEnd;
                duration = next.DurationSec;
                stepEnd = stepStart + duration;
                (a, b, _) = Watts(next, ftp);
            }
            var power = a + (b - a) * Math.Clamp((t - stepStart) / duration, 0, 1);

            var slot = i % NpWindow;
            windowSum += power - window[slot];
            window[slot] = power;
            if (i < NpWindow - 1) continue;
            var rolling = windowSum / NpWindow;
            var squared = rolling * rolling;
            sum4 += squared * squared;
            count++;
        }
        return Math.Pow(sum4 / count, 0.25);
    }
}
