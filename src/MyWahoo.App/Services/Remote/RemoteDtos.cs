namespace MyWahoo.App.Services.Remote;

/// <summary>What the phone page shows; sent as JSON twice a second.</summary>
public sealed record RemoteState(
    string Power,
    string Target,
    string Cadence,
    string HeartRate,
    string Speed,
    string Distance,
    string RideTime,
    string AvgPower,
    string Kcal,
    string Food,
    string NextFood,
    string Gear,
    string GearRatio,
    string Grade,
    string Mode,
    string ModeLabel,
    string ModeColor,
    string RideState,
    string RideTimer,
    /// <summary>What the big +/− buttons change: "gears", "watts" or "intensity".</summary>
    string Control,
    string ControlValue,
    RemoteDevice Trainer,
    RemoteDevice Click,
    RemoteDevice HeartRateSensor,
    string? Cue,
    RemoteWorkoutProgress? Workout,
    bool ShowFood,
    bool ShowLandmarks,
    string Landmark,
    string Lifetime,
    string LifetimeNext,
    double LifetimeFraction);

public sealed record RemoteDevice(bool Connected, string Text);

public sealed record RemoteWorkoutProgress(
    string Banner,
    string Step,
    string StepTarget,
    string StepLeft,
    string Next,
    double Progress,
    string Intensity,
    bool Paused,
    bool Finished);

public sealed record RemoteWorkout(string Id, string Name, string Summary, bool Valid, string? Error);

public sealed record RemoteRide(string Title, string Date, string Stats, string Food, string Landmark);

/// <summary>A button press from the phone, e.g. {"action":"mode","mode":"Erg"} or {"action":"startWorkout","workout":"vo2.json"}.</summary>
public sealed record RemoteCommand(string Action, string? Mode = null, string? Workout = null);

/// <summary>The app side of the remote; implementations marshal onto the UI thread.</summary>
public interface IRemoteBackend
{
    Task<RemoteState> GetStateAsync();
    Task<IReadOnlyList<RemoteWorkout>> GetWorkoutsAsync();
    Task<IReadOnlyList<RemoteRide>> GetRidesAsync();

    /// <summary>False for an unknown action or workout.</summary>
    Task<bool> ExecuteAsync(RemoteCommand command);
}
