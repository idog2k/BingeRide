namespace BingeRide.Workouts.Parsers;

// Pulls a workout out of arbitrary pasted chat text. Candidates are every fenced code block plus
// the whole text; each is identified by content, parsed and validated. The last passing fenced
// block wins (then the whole text); otherwise the most relevant errors are returned.
internal static class PasteExtractor
{
    const string FallbackName = "Pasted workout";
    const int MaxErrors = 8;

    public static bool TryExtract(string? text, out ImportResult? result, out IReadOnlyList<string> errors)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            errors = ["The pasted text is empty."];
            return false;
        }

        var normalized = TextUtil.Normalize(text);
        var blocks = FencedBlocks(normalized);
        var candidates = blocks.Select((b, i) => (Text: b, Block: (int?)(i + 1))).Append((Text: normalized, Block: null));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var passed = new List<(ImportResult Result, int? Block)>();
        var failed = new List<(string Message, int? Block)>();
        foreach (var (candidate, block) in candidates)
        {
            if (FormatDetector.Identify(candidate) is not { } found) continue;
            if (!seen.Add(found.Region.Trim())) continue; // the whole text often re-finds a fenced block
            try
            {
                passed.Add((WorkoutImporter.ParseFormat(found.Format, found.Region, FallbackName), block));
            }
            catch (WorkoutFormatException ex)
            {
                var where = $"{Where(block, blocks.Count)} ({FormatDetector.DisplayName(found.Format)})";
                failed.AddRange(ex.Errors.Select(e => ($"{where}: {e}", block)));
            }
        }

        if (passed.Count > 0)
        {
            var lastFenced = passed.FindLastIndex(p => p.Block is not null);
            var (chosen, chosenBlock) = passed[lastFenced >= 0 ? lastFenced : passed.Count - 1];
            var warnings = chosen.Warnings.ToList();
            if (passed.Count > 1)
                warnings.Insert(0, $"Found {passed.Count} workouts in the pasted text; using {Where(chosenBlock, blocks.Count).ToLowerInvariant()}.");
            result = chosen with { Warnings = warnings };
            errors = [];
            return true;
        }

        errors = failed.Count == 0
            ? ["No workout found in the pasted text. Paste a BingeRide JSON workout, a Zwift .zwo, an ERG/MRC file or intervals.icu steps (\"- 10m 65%\")."]
            : [.. failed.OrderByDescending(f => f.Block ?? 0).Select(f => f.Message).Take(MaxErrors)]; // later blocks first
        return false;
    }

    static string Where(int? block, int blockCount) => block switch
    {
        null => "The pasted text",
        _ when blockCount == 1 => "The code block",
        _ => $"Code block {block}",
    };

    // Contents of every ``` or ~~~ fenced block (any language tag); an unclosed fence runs to the end.
    internal static List<string> FencedBlocks(string text)
    {
        var blocks = new List<string>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (Fence(lines[i]) is not { } fence) continue;
            var (fenceChar, fenceLength) = fence;
            var body = new List<string>();
            var j = i + 1;
            for (; j < lines.Length && !IsClosingFence(lines[j], fenceChar, fenceLength); j++) body.Add(lines[j]);
            blocks.Add(string.Join('\n', body));
            i = j;
        }
        return blocks;
    }

    static (char Char, int Length)? Fence(string line)
    {
        var t = line.TrimStart();
        if (t.Length < 3 || t[0] is not ('`' or '~')) return null;
        var c = t[0];
        var n = 0;
        while (n < t.Length && t[n] == c) n++;
        if (n < 3 || (c == '`' && t.AsSpan(n).Contains('`'))) return null;
        return (c, n);
    }

    static bool IsClosingFence(string line, char fenceChar, int fenceLength)
    {
        var t = line.Trim();
        return t.Length >= fenceLength && t.AsSpan().TrimStart(fenceChar).IsEmpty;
    }
}
