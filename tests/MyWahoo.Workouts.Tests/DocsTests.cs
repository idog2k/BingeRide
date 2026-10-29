using MyWahoo.Workouts.Parsers;

namespace MyWahoo.Workouts.Tests;

// Keeps the examples in docs/workout-format.md importable.
public class DocsTests
{
    static string? FindDoc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "workout-format.md");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public void Every_example_in_the_format_doc_imports_without_warnings()
    {
        var doc = FindDoc();
        Assert.NotNull(doc);
        var blocks = PasteExtractor.FencedBlocks(File.ReadAllText(doc));
        Assert.Equal(2, blocks.Count);

        var json = WorkoutImporter.ImportText(blocks[0], "example.json");
        Assert.Empty(json.Warnings);
        Assert.Equal(3000, json.Workout.TotalDurationSec);

        var icu = WorkoutImporter.ImportText(blocks[1], "example.txt");
        Assert.Equal("icu", icu.Format);
        Assert.Equal(StepKind.Slope, icu.Workout.Steps[^2].Kind);
    }
}
