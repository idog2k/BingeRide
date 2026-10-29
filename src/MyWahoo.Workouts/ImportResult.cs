using MyWahoo.Core.Workouts;

namespace MyWahoo.Workouts;

/// <summary>
/// A successfully imported workout.
/// </summary>
/// <param name="Workout">The flattened, validated workout.</param>
/// <param name="Format">Importer id: "json", "zwo", "erg", "mrc", "icu", "fit", "gpx" or "tcx".</param>
/// <param name="RawText">The exact (normalised) text that parsed, so it can be saved to the library; empty for FIT.</param>
/// <param name="Warnings">Non-fatal issues found while reading (lenient fixes, ignored properties…).</param>
public sealed record ImportResult(Workout Workout, string Format, string RawText, IReadOnlyList<string> Warnings);
