namespace BingeRide.Core.Session;

public sealed record Landmark(string Emoji, string Name, double Km)
{
    public string Label => $"{Emoji} {Name}";
}

/// <summary>Where a distance stands: the biggest trip already covered and the next one to reach.</summary>
public sealed record LandmarkProgress(Landmark? Passed, Landmark? Next, double FractionOfNext, double Km);

/// <summary>Ride distance expressed as famous trips around the world (approximate road/route distances).</summary>
public static class Landmarks
{
    // Ascending. No flag emoji: Windows doesn't draw them.
    public static readonly IReadOnlyList<Landmark> All =
    [
        new("🌉", "Golden Gate Bridge", 2.7),
        new("🏖️", "Tel Aviv Port → Jaffa", 5),
        new("🌳", "Central Park loop", 9.7),
        new("🏙️", "Tel Aviv → Herzliya", 15),
        new("🏰", "Paris → Versailles", 20),
        new("🗽", "Manhattan, end to end", 21.6),
        new("🌅", "Tel Aviv → Netanya", 31),
        new("🌊", "English Channel (Dover → Calais)", 34),
        new("🏃", "A marathon", 42.2),
        new("🕍", "Tel Aviv → Jerusalem", 65),
        new("🌷", "Amsterdam → Rotterdam", 75),
        new("🎡", "London → Brighton", 87),
        new("⚓", "Tel Aviv → Haifa", 95),
        new("🔔", "New York → Philadelphia", 150),
        new("🚴", "A Tour de France stage", 180),
        new("🏜️", "Tel Aviv → Eilat", 350),
        new("🏛️", "New York → Washington, D.C.", 365),
        new("🗼", "London → Paris", 460),
        new("🧭", "Israel end to end (Metula → Eilat)", 470),
        new("🌁", "Los Angeles → San Francisco", 615),
        new("🌆", "Chicago → New York", 1270),
        new("🏞️", "Land's End → John o' Groats", 1407),
        new("🍝", "Paris → Rome", 1420),
        new("🏆", "The whole Tour de France", 3400),
        new("🛣️", "New York → Los Angeles", 4500),
        new("🚂", "Moscow → Vladivostok", 9289),
        new("🦁", "Cape Town → Cairo", 10000),
        new("🌍", "Around the world", 40075),
        new("🌙", "To the Moon", 384400),
    ];

    /// <summary>How many trips <paramref name="km"/> covers.</summary>
    public static int CountReached(double km)
    {
        var n = 0;
        while (n < All.Count && All[n].Km <= km) n++;
        return n;
    }

    public static LandmarkProgress Progress(double km)
    {
        var n = CountReached(km);
        var passed = n > 0 ? All[n - 1] : null;
        var next = n < All.Count ? All[n] : null;
        return new LandmarkProgress(passed, next, next is null ? 1 : Math.Clamp(km / next.Km, 0, 1), km);
    }

    /// <summary>The trip closest in length (by ratio), for "≈ Tel Aviv → Haifa".</summary>
    public static Landmark Nearest(double km) =>
        All.MinBy(l => Math.Abs(Math.Log(Math.Max(km, 0.1) / l.Km)))!;

    /// <summary>"≈ ⚓ Tel Aviv → Haifa", "½ of …" or "2.3× …" for a ride or workout total.</summary>
    public static string Equivalent(double km)
    {
        if (km < 0.5) return "";
        var l = Nearest(km);
        var ratio = km / l.Km;
        return ratio switch
        {
            >= 0.85 and <= 1.15 => $"≈ {l.Label}",
            < 1 => $"{Pct(ratio)} of {l.Label}",
            _ => $"{ratio:0.#}× {l.Label}",
        };
    }

    /// <summary>"15% of ⚓ Tel Aviv → Haifa" for a ride in progress.</summary>
    public static string NextText(LandmarkProgress p) =>
        p.Next is { } next ? $"{Pct(p.FractionOfNext)} of {next.Label}" : $"past {p.Passed!.Label}!";

    /// <summary>"95%" in every culture (the "P0" format writes "95 %" in some).</summary>
    public static string Pct(double fraction) => $"{Math.Round(fraction * 100):0}%";
}
