using System.Text.Json;
using System.Text.Json.Serialization;
using MyWahoo.Core.Devices;
using MyWahoo.Core.Protocol;

namespace MyWahoo.Core.Settings;

/// <summary>What a Click button does. Up/Down are context-sensitive: ERG watts in ERG, gears otherwise.</summary>
public enum ClickAction { None, Up, Down, PauseResume, SkipStep, ToggleCompact }

public sealed class AppSettings
{
    public int Ftp { get; set; } = 140;
    public double RiderKg { get; set; } = 75;
    public double BikeKg { get; set; } = 9;

    public int ErgStepWatts { get; set; } = 5;
    public double GradeStepPct { get; set; } = 0.5;
    public double WorkoutIntensityStepPct { get; set; } = 2;
    public int StartGear { get; set; } = GearTable.DefaultGear;
    public bool AutoPause { get; set; } = true;

    public ulong? TrainerAddress { get; set; }
    public ulong? HeartRateAddress { get; set; }
    public bool HeartRateEnabled { get; set; } = true;
    public TrainerControlPreference TrainerControl { get; set; } = TrainerControlPreference.Auto;
    public bool ErgViaFtms { get; set; }

    /// <summary>Real chainring and cog; only used when gears are emulated over FTMS.</summary>
    public int PhysicalChainring { get; set; } = 42;
    public int PhysicalCog { get; set; } = 14;

    public bool ClickEnabled { get; set; } = true;
    public bool ClickResetIdleRightPuck { get; set; } = true;
    public bool ClickRebootLockedLeftPuck { get; set; }

    public Dictionary<ClickButtons, ClickAction> ClickMapping { get; set; } = DefaultClickMapping();

    /// <summary>Background opacity of the compact overlay (0.15..1).</summary>
    public double OverlayOpacity { get; set; } = 0.7;
    public double OverlayScale { get; set; } = 1.0;
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    public bool StartCompact { get; set; }

    public string? WorkoutsFolder { get; set; }

    [JsonIgnore]
    public string ResolvedWorkoutsFolder => string.IsNullOrWhiteSpace(WorkoutsFolder) ? DefaultWorkoutsFolder : WorkoutsFolder;

    public static string DefaultWorkoutsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyWahoo", "Workouts");

    /// <summary>Where finished rides are saved as .fit files (with a .json summary each).</summary>
    public string? RidesFolder { get; set; }

    [JsonIgnore]
    public string ResolvedRidesFolder => string.IsNullOrWhiteSpace(RidesFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyWahoo", "Rides")
        : RidesFolder;

    public static Dictionary<ClickButtons, ClickAction> DefaultClickMapping() => new()
    {
        [ClickButtons.Plus] = ClickAction.Up,
        [ClickButtons.Minus] = ClickAction.Down,
        // Click v2's "−" puck locks unless Zwift unlocked it recently, so B on the "+" puck also shifts down.
        [ClickButtons.B] = ClickAction.Down,
        [ClickButtons.Up] = ClickAction.Up,
        [ClickButtons.Down] = ClickAction.Down,
        [ClickButtons.A] = ClickAction.None,
        [ClickButtons.Y] = ClickAction.None,
        [ClickButtons.Z] = ClickAction.None,
        [ClickButtons.Left] = ClickAction.None,
        [ClickButtons.Right] = ClickAction.None,
    };

    public TrainerOptions ToTrainerOptions() => new()
    {
        Preference = TrainerControl,
        ErgViaFtms = ErgViaFtms,
        RiderKg = RiderKg,
        BikeKg = BikeKg,
        PhysicalGearRatio = PhysicalCog > 0 ? (double)PhysicalChainring / PhysicalCog : 3.0,
    };

    public ClickOptions ToClickOptions() => new()
    {
        ResetIdleRightPuck = ClickResetIdleRightPuck,
        RebootLockedLeftPuck = ClickRebootLockedLeftPuck,
    };
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyWahoo", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // A hand-edited file that no longer parses shouldn't stop the app; keep a copy and start fresh.
            File.Copy(path, path + ".bad", overwrite: true);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
