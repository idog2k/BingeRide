namespace MyWahoo.Workouts.Tests;

public class PasteExtractionTests
{
    static ImportResult Extracts(string text)
    {
        var ok = WorkoutImporter.TryImportFromPastedText(text, out var result, out var errors);
        Assert.True(ok, string.Join("\n", errors));
        Assert.Empty(errors);
        return result!;
    }

    static IReadOnlyList<string> FailsToExtract(string text)
    {
        Assert.False(WorkoutImporter.TryImportFromPastedText(text, out var result, out var errors));
        Assert.Null(result);
        Assert.NotEmpty(errors);
        return errors;
    }

    [Fact]
    public void Prose_with_a_json_block()
    {
        var result = Extracts("""
            Here's a 30-minute session based on your FTP of 240 W. It's built around 3 x 5 min at threshold.

            ```json
            {
              "format": "mywahoo-workout",
              "version": 1,
              "name": "Threshold 3x5",
              "power_unit": "pct_ftp",
              "steps": [
                {"type": "ramp", "sec": 300, "from": 50, "to": 75, "text": "Warm up"},
                {"type": "repeat", "times": 3, "steps": [
                  {"type": "steady", "sec": 300, "power": 100},
                  {"type": "steady", "sec": 120, "power": 50}
                ]}
              ]
            }
            ```

            Tips:
            - Keep cadence around 90 rpm
            - 2 minutes easy between reps is enough
            """);
        Assert.Equal("json", result.Format);
        Assert.Equal("Threshold 3x5", result.Workout.Name);
        Assert.Equal(7, result.Workout.Steps.Count);
        Assert.StartsWith("{", result.RawText);
        Assert.EndsWith("}", result.RawText.TrimEnd());
        Assert.Equal(result.Workout.Name, WorkoutImporter.ImportText(result.RawText, "saved.json").Workout.Name);
    }

    [Fact]
    public void Zwo_in_an_xml_fence()
    {
        var result = Extracts("""
            Sure! Import this into Zwift or MyWahoo:

            ```xml
            <?xml version="1.0" encoding="UTF-8"?>
            <workout_file>
              <name>Zwift sweet spot</name>
              <workout>
                <Warmup Duration="300" PowerLow="0.4" PowerHigh="0.7"/>
                <IntervalsT Repeat="2" OnDuration="600" OffDuration="180" OnPower="0.9" OffPower="0.5"/>
              </workout>
            </workout_file>
            ```
            """);
        Assert.Equal("zwo", result.Format);
        Assert.Equal("Zwift sweet spot", result.Workout.Name);
        Assert.Equal(5, result.Workout.Steps.Count);
    }

    [Fact]
    public void Unfenced_json_from_a_copy_button()
    {
        var result = Extracts("""{"format":"mywahoo-workout","version":1,"name":"Copied","power_unit":"pct_ftp","steps":[{"type":"steady","sec":600,"power":65}]}""");
        Assert.Equal("Copied", result.Workout.Name);
    }

    [Fact]
    public void Unfenced_json_surrounded_by_prose()
    {
        var result = Extracts("""
            Paste this into the app: {"format":"mywahoo-workout","version":1,"name":"Inline","steps":[{"type":"steady","sec":600,"power":65}]} and enjoy!
            """);
        Assert.Equal("Inline", result.Workout.Name);
    }

    [Fact]
    public void First_block_broken_second_block_used()
    {
        var result = Extracts("""
            First attempt:
            ```json
            {"format":"mywahoo-workout","version":1,"name":"Broken" "steps":[{"type":"steady","sec":600,"power":65}]}
            ```
            Sorry, that had a typo. Corrected:
            ```json
            {"format":"mywahoo-workout","version":1,"name":"Fixed","steps":[{"type":"steady","sec":600,"power":65}]}
            ```
            """);
        Assert.Equal("Fixed", result.Workout.Name);
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("Found"));
    }

    [Fact]
    public void Several_valid_blocks_prefer_the_last_one()
    {
        var result = Extracts("""
            Option A:
            ```
            - 10m 60%
            - 20m 75%
            ```
            Option B (harder):
            ```json
            {"format":"mywahoo-workout","version":1,"name":"Option B","power_unit":"pct_ftp","steps":[{"type":"steady","sec":1800,"power":85}]}
            ```
            """);
        Assert.Equal("Option B", result.Workout.Name);
        Assert.Contains(result.Warnings, w => w.Contains("Found 2 workouts") && w.Contains("code block 2"));
    }

    [Fact]
    public void Unfenced_intervals_icu_steps_inside_a_chat_answer()
    {
        var result = Extracts("""
            Here's the plan for today (about 50 minutes):

            Main set 3x
            - 8m 95%
            - 4m 55%

            - 10m ramp 60-40%

            Have fun, and drink plenty!
            """);
        Assert.Equal("icu", result.Format);
        Assert.Equal(7, result.Workout.Steps.Count);
        Assert.Equal("Main set", result.Workout.Steps[0].Headline);
        Assert.Equal("Pasted workout", result.Workout.Name);
    }

    [Fact]
    public void Erg_in_a_fence_with_crlf_bom_and_nbsp()
    {
        var text = "﻿Here you go:\r\n```\r\n[COURSE HEADER]\r\nFILE NAME = Pasted ERG\r\nMINUTES WATTS\r\n[END COURSE HEADER]\r\n[COURSE DATA]\r\n0\t150\r\n10\t150\r\n[END COURSE DATA]\r\n```\r\n";
        var result = Extracts(text);
        Assert.Equal("erg", result.Format);
        Assert.Equal("Pasted ERG", result.Workout.Name);
        Assert.Equal(PowerTarget.Watts(150), result.Workout.Steps[0].PowerStart);
    }

    [Fact]
    public void Smart_quotes_from_a_rich_text_copy_are_straightened()
    {
        var result = Extracts("{“format”: “mywahoo-workout”, “version”: 1, “name”: “Smart”, “steps”: [{“type”: “steady”, “sec”: 300, “power”: 70, “text”: “Don’t stop”}]}");
        Assert.Equal("Smart", result.Workout.Name);
        Assert.Equal("Don't stop", result.Workout.Steps[0].Headline);
    }

    [Fact]
    public void Curly_quotes_inside_json_strings_are_kept()
    {
        var result = Extracts("{\"format\":\"mywahoo-workout\",\"version\":1,\"name\":\"Say “hi”\",\"steps\":[{\"type\":\"steady\",\"sec\":300,\"power\":70}]}");
        Assert.Equal("Say “hi”", result.Workout.Name);
    }

    [Fact]
    public void Broken_only_block_reports_its_parse_error()
    {
        var errors = FailsToExtract("""
            ```json
            {"name":"x","steps":[{"type":"steady","sec":60}]}
            ```
            """);
        Assert.Contains(errors, e => e.StartsWith("The code block (MyWahoo JSON): steps[0] (steady): \"power\" is required"));
    }

    [Fact]
    public void Errors_from_later_blocks_come_first()
    {
        var errors = FailsToExtract("""
            ```json
            {"name":"A","steps":[{"type":"steady","sec":60}]}
            ```
            ```json
            {"name":"B","steps":[{"type":"bogus","sec":60}]}
            ```
            """);
        Assert.StartsWith("Code block 2", errors[0]);
        Assert.Contains(errors, e => e.StartsWith("Code block 1"));
    }

    [Theory]
    [InlineData("Just do 20 minutes easy today and rest tomorrow.")]
    [InlineData("- 10m 60%")]
    [InlineData("```python\nprint('hello')\n```")]
    [InlineData("   ")]
    public void Text_without_a_workout(string text)
    {
        var errors = FailsToExtract(text);
        Assert.Single(errors);
    }
}
