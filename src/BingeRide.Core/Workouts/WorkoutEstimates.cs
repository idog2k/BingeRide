using BingeRide.Core.Session;

namespace BingeRide.Core.Workouts;

/// <summary>Expected power for any step: exact for ERG, estimated for steps where the rider sets the effort.</summary>
public static class WorkoutEstimates
{
    /// <summary>Estimated share of FTP for Free steps.</summary>
    public const double FreeFtpFraction = 0.50;

    /// <summary>Estimated share of FTP for time-based Slope steps.</summary>
    public const double SlopeFtpFraction = 0.65;

    /// <summary>
    /// Expected distance: road speed for the expected watts (ERG steps on the flat, slope and free steps at
    /// their grade), plus any distance-based steps as written.
    /// </summary>
    public static double DistanceKm(Workout workout, double ftp, double massKg)
    {
        double metres = 0;
        foreach (var step in workout.Steps)
        {
            if (step.IsDistanceBased)
            {
                metres += step.DistanceM;
                continue;
            }
            if (!(step.DurationSec > 0)) continue;
            var grade = step.Kind == StepKind.Erg ? 0 : step.Grade ?? 0;
            if (!step.IsRamp)
            {
                metres += RiderPhysics.SteadySpeed(Watts(step, ftp, 0.5), grade, massKg) * step.DurationSec;
                continue;
            }
            for (double s = 0; s < step.DurationSec; s += 1)
            {
                var dt = Math.Min(1, step.DurationSec - s);
                metres += RiderPhysics.SteadySpeed(Watts(step, ftp, (s + dt / 2) / step.DurationSec), grade, massKg) * dt;
            }
        }
        return metres / 1000;
    }

    /// <summary>Watts at <paramref name="fraction"/> (0..1) through the step.</summary>
    public static double Watts(WorkoutStep step, double ftp, double fraction) =>
        step.TargetWatts(ftp, fraction) ?? (step.Kind == StepKind.Slope ? SlopeFtpFraction : FreeFtpFraction) * ftp;
}
