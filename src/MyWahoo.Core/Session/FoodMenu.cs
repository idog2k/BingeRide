using MyWahoo.Core.Workouts;

namespace MyWahoo.Core.Session;

public sealed record Food(string Emoji, string Name, int Kcal);

/// <summary>A food "burned" at <see cref="TimeSec"/> into a workout (for the workout chart).</summary>
public readonly record struct FoodMarker(double TimeSec, Food Food);

/// <summary>Foods fully burned so far, and how far the next one is.</summary>
public sealed record FoodProgress(IReadOnlyList<Food> Burned, Food Next, double KcalToNext, double Kcal);

/// <summary>
/// Turns ride energy into food: you "eat through" <see cref="Sequence"/> in order (it repeats), so a hard
/// interval burns food faster. Small items come first so even a short ride earns something.
/// </summary>
public static class FoodMenu
{
    // Typical portions; good enough for fun, not for nutrition.
    public static readonly IReadOnlyList<Food> Sequence =
    [
        new("🍎", "Apple", 95),
        new("🍌", "Banana", 105),
        new("🍪", "Cookie", 150),
        new("🍕", "Slice of pizza", 285),
        new("🍩", "Donut", 250),
        new("🍔", "Burger", 550),
        new("🍟", "Fries", 365),
        new("🍫", "Chocolate bar", 230),
        new("🍦", "Ice cream", 270),
        new("🥐", "Croissant", 230),
        new("🍺", "Beer", 210),
        new("🥙", "Pita sandwich", 550),
        new("🧁", "Cupcake", 300),
        new("🥤", "Soda", 140),
        new("🍰", "Slice of cake", 350),
    ];

    /// <summary>
    /// Food energy behind the mechanical work: muscles are ~24 % efficient and 1 kcal = 4.184 kJ,
    /// so the kcal you burn are almost exactly the kJ the trainer measures.
    /// </summary>
    public static double KcalFromKj(double workKj) => workKj / (4.184 * 0.24);

    public static Food At(int index) => Sequence[index % Sequence.Count];

    public static FoodProgress Progress(double kcal)
    {
        var burned = new List<Food>();
        double eaten = 0;
        var i = 0;
        while (eaten + At(i).Kcal <= kcal)
        {
            eaten += At(i).Kcal;
            burned.Add(At(i++));
        }
        return new FoodProgress(burned, At(i), eaten + At(i).Kcal - kcal, kcal);
    }

    /// <summary>
    /// Where each food is burned along a workout at the rider's FTP (time-based steps; free and slope
    /// steps use the same estimates as the workout stats).
    /// </summary>
    public static IReadOnlyList<FoodMarker> Markers(Workout workout, double ftp, double intensityPct = 100)
    {
        var markers = new List<FoodMarker>();
        double kcal = 0, t = 0;
        var index = 0;
        var next = (double)At(0).Kcal;
        foreach (var step in workout.Steps)
        {
            if (!(step.DurationSec > 0)) continue;
            for (double s = 0; s < step.DurationSec; s += 1)
            {
                var dt = Math.Min(1, step.DurationSec - s);
                var watts = WorkoutEstimates.Watts(step, ftp, (s + dt / 2) / step.DurationSec) * intensityPct / 100;
                kcal += KcalFromKj(watts * dt / 1000);
                t += dt;
                while (kcal >= next)
                {
                    markers.Add(new FoodMarker(t, At(index)));
                    next += At(++index).Kcal;
                }
            }
        }
        return markers;
    }
}
