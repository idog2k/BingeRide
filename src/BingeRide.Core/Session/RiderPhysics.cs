namespace BingeRide.Core.Session;

/// <summary>Road physics used to turn power into a virtual speed/distance (the trainer's own speed ignores virtual gears).</summary>
public static class RiderPhysics
{
    public const double Gravity = 9.80665;
    public const double AirDensity = 1.225;
    public const double CdA = 0.32;
    public const double Crr = 0.004;
    public const double MaxSpeed = 30; // m/s, caps steep descents

    /// <summary>Steady-state speed (m/s) where <paramref name="watts"/> balances gravity, rolling and air resistance.</summary>
    public static double SteadySpeed(double watts, double gradePct, double massKg)
    {
        var theta = Math.Atan(gradePct / 100);
        var linear = massKg * Gravity * (Math.Sin(theta) + Crr * Math.Cos(theta));
        var cubic = 0.5 * AirDensity * CdA;
        double Surplus(double v) => Math.Max(0, watts) - linear * v - cubic * v * v * v;

        if (Surplus(MaxSpeed) >= 0) return MaxSpeed;
        if (watts <= 0 && linear >= 0) return 0;
        double lo = 0, hi = MaxSpeed;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            if (Surplus(mid) >= 0) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>Power (W) needed to hold <paramref name="speedMps"/> on <paramref name="gradePct"/>.</summary>
    public static double PowerForSpeed(double speedMps, double gradePct, double massKg)
    {
        var theta = Math.Atan(gradePct / 100);
        var force = massKg * Gravity * (Math.Sin(theta) + Crr * Math.Cos(theta)) + 0.5 * AirDensity * CdA * speedMps * speedMps;
        return Math.Max(0, force * speedMps);
    }

    /// <summary>First-order lag towards the steady speed, standing in for the bike's inertia.</summary>
    public static double Approach(double current, double target, double dtSec, double tauSec = 4) =>
        current + (target - current) * (1 - Math.Exp(-dtSec / tauSec));
}
