using System.Text;
using static MyWahoo.Workouts.Tests.TestUtil;

namespace MyWahoo.Workouts.Tests;

public class ImporterTests
{
    const string ZwoText = """<workout_file><name>Z</name><workout><SteadyState Duration="60" Power="0.5"/></workout></workout_file>""";

    [Fact]
    public void Supported_extensions()
    {
        Assert.Equal([".json", ".zwo", ".erg", ".mrc", ".txt", ".fit", ".gpx", ".tcx"], WorkoutImporter.SupportedExtensions);
        Assert.All(WorkoutImporter.SupportedExtensions, e => Assert.StartsWith(".", e));
    }

    [Theory]
    [InlineData("json", ".json")]
    [InlineData("icu", ".txt")]
    [InlineData("mrc", ".mrc")]
    [InlineData("fit", ".fit")]
    public void File_extension_for_saving(string format, string extension) =>
        Assert.Equal(extension, WorkoutImporter.GetFileExtension(format));

    [Fact]
    public void Extension_mismatch_falls_back_to_sniffing()
    {
        Assert.Equal("zwo", Import(ZwoText, "wrong.json").Format);
        Assert.Equal("zwo", Import(ZwoText, "download.xml").Format);
        Assert.Equal("zwo", Import(ZwoText).Format);
    }

    [Fact]
    public void Txt_files_may_hold_any_text_format()
    {
        Assert.Equal("json", Import(Json("""{"type":"steady","sec":60,"power":50}"""), "notes.txt").Format);
        Assert.Equal("icu", Import("- 10m 60%", "notes.txt").Format);
    }

    [Fact]
    public void Extension_error_is_kept_when_sniffing_also_fails()
    {
        var ex = Fails("<workout_file><workout><Bogus/></workout_file>", "x.json");
        Assert.Contains("Invalid JSON", ex.Message);
    }

    [Fact]
    public void Unrecognised_text_is_an_error()
    {
        Assert.Contains("Unrecognised workout format", Fails("hello there").Message);
        Assert.Contains("binary", Fails("anything", "x.fit").Message);
        Assert.Contains("empty", Fails(" \n ", "x.json").Message);
    }

    [Fact]
    public void Bytes_with_bom_and_latin1_are_decoded()
    {
        var json = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Json("""{"type":"steady","sec":60,"power":50,"text":"Café"}"""))).ToArray();
        Assert.Equal("Café", WorkoutImporter.ImportBytes(json, "x.json").Workout.Steps[0].Headline);

        var erg = Encoding.Latin1.GetBytes("[COURSE HEADER]\nDESCRIPTION = Échauffement\nMINUTES WATTS\n[END COURSE HEADER]\n[COURSE DATA]\n0 100\n5 100\n[END COURSE DATA]\n");
        var result = WorkoutImporter.ImportBytes(erg, "legacy.erg");
        Assert.Equal("Échauffement", result.Workout.Description);
        Assert.Equal("legacy", result.Workout.Name);

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(ZwoText)).ToArray();
        Assert.Equal("zwo", WorkoutImporter.ImportBytes(utf16, "x.zwo").Format);
    }

    [Fact]
    public void Import_file_sets_source_path_and_reports_missing_files()
    {
        var path = Sample("tempo.mrc");
        Assert.Equal(Path.GetFullPath(path), WorkoutImporter.ImportFile(path).Workout.SourcePath);
        Assert.Throws<FileNotFoundException>(() => WorkoutImporter.ImportFile(Sample("missing.json")));
    }

    [Fact]
    public void Empty_bytes_are_an_error() =>
        Assert.Throws<WorkoutFormatException>(() => WorkoutImporter.ImportBytes([], "x.json"));

    [Fact]
    public void Leading_blank_lines_before_an_xml_declaration_are_fine()
    {
        var result = Import("\n\n<?xml version=\"1.0\"?>\n" + ZwoText, "x.zwo");
        Assert.Equal("Z", result.Workout.Name);
    }

    [Fact]
    public void Huge_repeat_counts_are_rejected_before_expansion()
    {
        var ex = Fails(Json("""{"type":"repeat","times":500,"steps":[{"type":"steady","sec":1,"power":50},{"type":"steady","sec":1,"power":60},{"type":"steady","sec":1,"power":70},{"type":"steady","sec":1,"power":80},{"type":"steady","sec":1,"power":90},{"type":"steady","sec":1,"power":95},{"type":"steady","sec":1,"power":99},{"type":"steady","sec":1,"power":100},{"type":"steady","sec":1,"power":101},{"type":"steady","sec":1,"power":102},{"type":"steady","sec":1,"power":103}]}"""));
        Assert.Contains("more than 5000 steps", ex.Message);
    }
}
