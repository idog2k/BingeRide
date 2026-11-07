namespace BingeRide.Core.Session;

/// <summary>
/// The sentences shown during a ride, from the embedded motivation.txt (one per line). They were written for
/// this app. To fit any moment of a ride, they give no orders like "go harder" and don't say how much is left.
/// </summary>
public static class MotivationSentences
{
    /// <summary>What still fits two lines of the overlay.</summary>
    public const int MaxLength = 80;

    private const string ResourceName = "BingeRide.Core.motivation.txt";

    private static readonly Lazy<IReadOnlyList<string>> Loaded = new(Load);

    public static IReadOnlyList<string> All => Loaded.Value;

    private static List<string> Load()
    {
        using var stream = typeof(MotivationSentences).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}");
        using var reader = new StreamReader(stream);
        var sentences = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line)) sentences.Add(line.Trim());
        }
        return sentences;
    }
}

/// <summary>Deals sentences in random order: none comes back until all of them were shown.</summary>
public sealed class MotivationDeck(IReadOnlyList<string> sentences, Random? random = null)
{
    private readonly string[] _cards = [.. sentences];
    private readonly Random _random = random ?? Random.Shared;
    private int _next = sentences.Count; // the first call shuffles

    public string Next()
    {
        if (_next >= _cards.Length)
        {
            _random.Shuffle(_cards);
            _next = 0;
        }
        return _cards[_next++];
    }
}
