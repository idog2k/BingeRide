namespace MyWahoo.Core.Protocol;

/// <summary>Zwift's 24 virtual gears (absolute chainring/cog ratios) as Zwift sends them to the trainer.</summary>
public static class GearTable
{
    public static readonly IReadOnlyList<double> Ratios =
    [
        0.75, 0.87, 0.99, 1.11, 1.23, 1.38, 1.53, 1.68, 1.86, 2.04, 2.22, 2.40,
        2.61, 2.82, 3.03, 3.24, 3.49, 3.74, 3.99, 4.24, 4.54, 4.84, 5.14, 5.49,
    ];

    public const int Count = 24;
    public const int DefaultGear = 12;

    /// <summary>Ratio for a 1-based gear number (clamped to 1..24).</summary>
    public static double Ratio(int gear) => Ratios[Clamp(gear) - 1];

    public static int Clamp(int gear) => Math.Clamp(gear, 1, Count);
}
