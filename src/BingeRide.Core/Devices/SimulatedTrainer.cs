using BingeRide.Core.Protocol;
using BingeRide.Core.Session;

namespace BingeRide.Core.Devices;

/// <summary>
/// A pretend trainer for demo mode and screenshots: a rider spinning ~88 rpm who holds ERG targets,
/// and in SIM produces the power the virtual gear and grade demand.
/// </summary>
public sealed class SimulatedTrainer : ITrainer
{
    private const double WheelCircumferenceM = 2.105;
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _random = new(7);
    private readonly double _massKg;
    private TrainerTarget? _target;
    private double _power = 100;

    public SimulatedTrainer(double massKg = 84, bool autoRun = true)
    {
        _massKg = massKg;
        if (autoRun) _ = RunAsync();
    }

    public string Name => "Demo trainer";
    public ulong Address => 0;
    public ControlPlane ControlPlane => ControlPlane.Zwift;
    public bool IsConnected => !_cts.IsCancellationRequested;
    public double? ReportedGearRatio => (_target as SimTarget) is { } s ? GearTable.Ratio(s.Gear) : null;

    public event EventHandler<TrainerData>? DataReceived;
    public event EventHandler<string>? Log;
    public event EventHandler? Disconnected;

    public void SetTarget(TrainerTarget target) => _target = target;

    /// <summary>Produces one second of rider data.</summary>
    public TrainerData Sample(DateTimeOffset now)
    {
        var cadence = 86 + _random.NextDouble() * 5;
        var wanted = _target switch
        {
            ErgTarget erg => erg.Watts,
            SimTarget sim => RiderPhysics.PowerForSpeed(cadence / 60 * GearTable.Ratio(sim.Gear) * WheelCircumferenceM, sim.GradePct, _massKg),
            _ => 100,
        };
        _power += (wanted - _power) * 0.5;
        var power = Math.Max(0, _power + _random.Next(-4, 5));
        return new TrainerData((int)Math.Round(power), Math.Round(cadence), 120 + (int)(power / 8), now);
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            Log?.Invoke(this, "Demo trainer running");
            while (await timer.WaitForNextTickAsync(_cts.Token))
                DataReceived?.Invoke(this, Sample(DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
        return ValueTask.CompletedTask;
    }
}
