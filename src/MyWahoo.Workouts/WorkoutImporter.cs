using System.Diagnostics.CodeAnalysis;
using System.Text;
using MyWahoo.Core.Workouts;
using MyWahoo.Workouts.Parsers;

namespace MyWahoo.Workouts;

/// <summary>
/// Imports workouts from files, bytes, text or pasted chat answers. Every result is a flattened
/// <see cref="Workout"/> (repeats expanded with "i/n" labels) that has passed <see cref="WorkoutValidator"/>.
/// </summary>
public static class WorkoutImporter
{
    const long MaxFileBytes = 32L * 1024 * 1024;
    const string DefaultName = "Untitled workout";
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>File extensions the importer understands (lower case, with the dot).</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = [".json", ".zwo", ".erg", ".mrc", ".txt", ".fit", ".gpx", ".tcx"];

    /// <summary>
    /// Imports a workout file, choosing the parser by extension and falling back to content sniffing.
    /// Sets <see cref="Workout.SourcePath"/>. Throws <see cref="WorkoutFormatException"/> for bad content
    /// and <see cref="IOException"/> for I/O problems.
    /// </summary>
    public static ImportResult ImportFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxFileBytes)
            throw new WorkoutFormatException($"\"{info.Name}\" is {info.Length / (1024 * 1024)} MB; workout files must be under {MaxFileBytes / (1024 * 1024)} MB.");
        var result = ImportBytes(File.ReadAllBytes(path), Path.GetFileName(path));
        return result with { Workout = result.Workout with { SourcePath = Path.GetFullPath(path) } };
    }

    /// <summary>Imports file content; <paramref name="fileName"/> selects the parser and supplies the fallback name.</summary>
    public static ImportResult ImportBytes(byte[] data, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > MaxFileBytes) throw new WorkoutFormatException("The file is too large to be a workout.");
        if (data.Length == 0) throw new WorkoutFormatException("The file is empty.");

        if (FitWorkoutParser.LooksLikeFit(data) || Extension(fileName) == ".fit")
        {
            var warnings = new List<string>();
            var workout = FitWorkoutParser.Parse(data, FallbackName(fileName), warnings);
            return Finish(workout, "", warnings);
        }
        return ImportTextCore(DecodeText(data), fileName);
    }

    /// <summary>
    /// Imports one complete workout document of a known format. <paramref name="fileNameHint"/>'s
    /// extension (if any) selects the parser; otherwise the format is sniffed from the content.
    /// </summary>
    public static ImportResult ImportText(string text, string? fileNameHint = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Extension(fileNameHint) == ".fit")
            throw new WorkoutFormatException("FIT workouts are binary files; import them with ImportFile or ImportBytes.");
        return ImportTextCore(text, fileNameHint);
    }

    /// <summary>
    /// Extracts a workout from arbitrary pasted text (e.g. a chat answer): every fenced code block and
    /// the whole text are tried, and the last fenced block that parses and validates wins.
    /// On failure <paramref name="errors"/> holds the most relevant parse errors.
    /// </summary>
    public static bool TryImportFromPastedText(string text, [NotNullWhen(true)] out ImportResult? result, out IReadOnlyList<string> errors) =>
        PasteExtractor.TryExtract(text, out result, out errors);

    /// <summary>File extension (with dot) to use when saving <see cref="ImportResult.RawText"/> of the given format.</summary>
    public static string GetFileExtension(string format) => format switch
    {
        "icu" => ".txt",
        "json" or "zwo" or "erg" or "mrc" or "fit" or "gpx" or "tcx" => "." + format,
        _ => throw new ArgumentException($"Unknown workout format \"{format}\".", nameof(format)),
    };

    static ImportResult ImportTextCore(string text, string? fileName)
    {
        text = TextUtil.Normalize(text);
        if (string.IsNullOrWhiteSpace(text)) throw new WorkoutFormatException("The workout text is empty.");

        var fallback = FallbackName(fileName);
        var sniffed = FormatDetector.Detect(text);
        var byExtension = FormatDetector.FromExtension(Extension(fileName));

        // .txt is a generic container: trust the content first, then default to intervals.icu text.
        if (byExtension == IcuTextParser.Format) byExtension = sniffed ?? IcuTextParser.Format;
        if (byExtension is null)
        {
            return sniffed is null
                ? throw new WorkoutFormatException(
                    "Unrecognised workout format. Supported: MyWahoo JSON, Zwift .zwo, .erg/.mrc, intervals.icu text, .fit, .gpx and .tcx.")
                : ParseFormat(sniffed, text, fallback);
        }

        try
        {
            return ParseFormat(byExtension, text, fallback);
        }
        catch (WorkoutFormatException) when (sniffed is not null && Family(sniffed) != Family(byExtension))
        {
            try
            {
                return ParseFormat(sniffed, text, fallback);
            }
            catch (WorkoutFormatException)
            {
                // Report the error for the format the extension promised.
            }
            throw;
        }
    }

    // Parses normalised text as `format`, retrying once with straightened curly double quotes.
    internal static ImportResult ParseFormat(string format, string text, string fallbackName)
    {
        try
        {
            return ParseOnce(format, text, fallbackName);
        }
        catch (WorkoutFormatException) when (TextUtil.HasCurlyDoubleQuotes(text))
        {
            try
            {
                return ParseOnce(format, TextUtil.StraightenDoubleQuotes(text), fallbackName);
            }
            catch (WorkoutFormatException)
            {
                // Keep the original error; it refers to the text the user actually supplied.
            }
            throw;
        }
    }

    static ImportResult ParseOnce(string format, string text, string fallbackName)
    {
        var warnings = new List<string>();
        var workout = format switch
        {
            JsonWorkoutParser.Format => JsonWorkoutParser.Parse(text, fallbackName, warnings),
            ZwoParser.Format => ZwoParser.Parse(text, fallbackName, warnings),
            ErgMrcParser.ErgFormat or ErgMrcParser.MrcFormat => ErgMrcParser.Parse(text, fallbackName, format, warnings),
            IcuTextParser.Format => IcuTextParser.Parse(text, fallbackName, warnings),
            GpxParser.Format => GpxParser.Parse(text, fallbackName, warnings),
            TcxParser.Format => TcxParser.Parse(text, fallbackName, warnings),
            _ => throw new WorkoutFormatException($"\"{format}\" is not a text workout format."),
        };
        return Finish(workout, text, warnings);
    }

    static ImportResult Finish(Workout workout, string rawText, List<string> warnings)
    {
        var errors = WorkoutValidator.Validate(workout);
        if (errors.Count > 0) throw new WorkoutFormatException(errors);
        return new ImportResult(workout, workout.SourceFormat, rawText, warnings);
    }

    static string Family(string format) => format == ErgMrcParser.MrcFormat ? ErgMrcParser.ErgFormat : format;

    static string? Extension(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null : Path.GetExtension(fileName.Trim()).ToLowerInvariant() is { Length: > 0 } ext ? ext : null;

    static string FallbackName(string? fileName) => TextUtil.BaseName(fileName) ?? DefaultName;

    // BOM-aware decoding; strict UTF-8 with a Latin-1 fallback for legacy ERG/MRC files.
    static string DecodeText(byte[] data)
    {
        ReadOnlySpan<byte> span = data;
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8.GetString(span[3..]);
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Encoding.Unicode.GetString(span[2..]);
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Encoding.BigEndianUnicode.GetString(span[2..]);
        try
        {
            return StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(span);
        }
    }
}
