namespace MyWahoo.Core.Devices;

/// <summary>Which protocol is steering the trainer.</summary>
public enum ControlPlane
{
    None,
    /// <summary>Zwift's protocol: ERG, SIM and trainer-native virtual shifting (Zwift Cog trainers).</summary>
    Zwift,
    /// <summary>Standard FTMS: ERG and SIM; virtual gears emulated by scaling the SIM parameters.</summary>
    Ftms,
}

public enum TrainerControlPreference { Auto, ZwiftOnly, FtmsOnly }

/// <summary>What the rider wants the trainer to do right now. The trainer sends changes rate-limited.</summary>
public abstract record TrainerTarget;

public sealed record ErgTarget(int Watts) : TrainerTarget;

/// <summary>SIM at <paramref name="GradePct"/> in virtual gear <paramref name="Gear"/> (1..24).</summary>
public sealed record SimTarget(double GradePct, int Gear) : TrainerTarget;

public readonly record struct TrainerData(int? PowerW, double? CadenceRpm, int? HeartRate, DateTimeOffset Timestamp);

public sealed record TrainerOptions
{
    public TrainerControlPreference Preference { get; init; } = TrainerControlPreference.Auto;

    /// <summary>On the Zwift plane, send ERG over FTMS instead (diagnostic switch for hardware testing).</summary>
    public bool ErgViaFtms { get; init; }

    public double RiderKg { get; init; } = 75;
    public double BikeKg { get; init; } = 9;

    /// <summary>Real chainring/cog ratio; only used to emulate gears over FTMS.</summary>
    public double PhysicalGearRatio { get; init; } = 3.0;

    /// <summary>Tests drive <c>TickAsync</c> themselves instead of a background loop.</summary>
    internal bool ManualTick { get; init; }

    internal TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(4);
}

public interface ITrainer : IAsyncDisposable
{
    string Name { get; }
    ulong Address { get; }
    ControlPlane ControlPlane { get; }
    bool IsConnected { get; }

    /// <summary>Gear ratio the trainer reported back (Zwift plane only).</summary>
    double? ReportedGearRatio { get; }

    event EventHandler<TrainerData>? DataReceived;
    event EventHandler<string>? Log;
    event EventHandler? Disconnected;

    void SetTarget(TrainerTarget target);
}
