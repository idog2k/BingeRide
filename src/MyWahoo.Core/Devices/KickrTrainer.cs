using MyWahoo.Core.Ble;
using MyWahoo.Core.Protocol;

namespace MyWahoo.Core.Devices;

/// <summary>
/// A KICKR CORE / CORE 2 (or any FTMS trainer). Prefers Zwift's channel, which gives trainer-native
/// virtual shifting with the Zwift Cog; falls back to FTMS with emulated gears.
/// Commands are coalesced and rate-limited by <see cref="TickAsync"/>, which runs every 100 ms.
/// </summary>
public sealed class KickrTrainer : ITrainer
{
    private static readonly TimeSpan GradeInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErgInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GearInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GearReadbackDelay = TimeSpan.FromSeconds(1);

    private readonly IBlePeripheral _peripheral;
    private readonly TrainerOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();

    private IGattCharacteristic? _zwiftRx;
    private IGattCharacteristic? _ftmsControl;
    private TaskCompletionSource<bool>? _rideOnReply;
    private TaskCompletionSource<byte>? _ftmsReply;

    private TrainerTarget? _desired;
    private TrainerTarget? _sent;
    private DateTimeOffset _lastErgSent, _lastGradeSent, _lastGearSent, _lastAnySent;
    private DateTimeOffset? _readbackDue;
    private bool _needFullSim = true;
    private bool _ftmsControlLost;
    private bool _disconnected;

    private KickrTrainer(IBlePeripheral peripheral, TrainerOptions options)
    {
        _peripheral = peripheral;
        _options = options;
        _peripheral.Disconnected += OnPeripheralDisconnected;
    }

    public string Name => _peripheral.Name ?? "Trainer";
    public ulong Address => _peripheral.Address;
    public ControlPlane ControlPlane { get; private set; }
    public bool IsConnected => !_disconnected && _peripheral.IsConnected;
    public double? ReportedGearRatio { get; private set; }

    public event EventHandler<TrainerData>? DataReceived;
    public event EventHandler<string>? Log;
    public event EventHandler? Disconnected;

    public static async Task<KickrTrainer> ConnectAsync(IBleAdapter adapter, ulong address, TrainerOptions options, CancellationToken ct = default)
    {
        var peripheral = await adapter.ConnectAsync(address, ct);
        var trainer = new KickrTrainer(peripheral, options);
        try
        {
            await trainer.SetupAsync(ct);
        }
        catch
        {
            await trainer.DisposeAsync();
            throw;
        }
        if (!options.ManualTick) _ = trainer.RunLoopAsync();
        return trainer;
    }

    public void SetTarget(TrainerTarget target)
    {
        lock (_lock) _desired = target;
    }

    private async Task SetupAsync(CancellationToken ct)
    {
        // Live data: FTMS Indoor Bike Data is read-only and safe to subscribe whichever plane controls the trainer.
        var bikeData = await _peripheral.GetCharacteristicAsync([Ftms.Service], Ftms.IndoorBikeDataChar, ct);
        if (bikeData is not null) await bikeData.SubscribeAsync(OnIndoorBikeData, ct);

        if (_options.Preference != TrainerControlPreference.FtmsOnly && await TryZwiftHandshakeAsync(ct))
        {
            ControlPlane = ControlPlane.Zwift;
            if (_options.ErgViaFtms) await TryFtmsControlAsync(ct);
            Emit("Connected using Zwift virtual shifting");
            return;
        }
        if (_options.Preference == TrainerControlPreference.ZwiftOnly)
            throw new BleException("Trainer did not accept Zwift's protocol (virtual shifting). Check firmware or use FTMS.");

        if (!await TryFtmsControlAsync(ct))
            throw new BleException("Trainer refused control over FTMS. Close Zwift / the Wahoo app and retry.");
        ControlPlane = ControlPlane.Ftms;
        Emit("Connected using FTMS (gears emulated by the app)");
    }

    private async Task<bool> TryZwiftHandshakeAsync(CancellationToken ct)
    {
        var async = await _peripheral.GetCharacteristicAsync(ZwiftUuids.Services, ZwiftUuids.Async, ct);
        var rx = await _peripheral.GetCharacteristicAsync(ZwiftUuids.Services, ZwiftUuids.SyncRx, ct);
        var tx = await _peripheral.GetCharacteristicAsync(ZwiftUuids.Services, ZwiftUuids.SyncTx, ct);
        if (async is null || rx is null || tx is null)
        {
            Emit("Zwift service not found on trainer");
            return false;
        }
        await async.SubscribeAsync(OnZwiftMessage, ct);
        await tx.SubscribeAsync(OnZwiftMessage, ct);
        _zwiftRx = rx;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _rideOnReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await rx.WritePreferResponseAsync(ZwiftTrainerProtocol.RideOn, ct);
            var done = await Task.WhenAny(_rideOnReply.Task, Task.Delay(_options.HandshakeTimeout, ct));
            if (done == _rideOnReply.Task)
            {
                await rx.WritePreferResponseAsync(ZwiftTrainerProtocol.Init41, ct);
                return true;
            }
            Emit($"No RideOn reply (attempt {attempt}/3)");
        }
        _zwiftRx = null;
        return false;
    }

    private async Task<bool> TryFtmsControlAsync(CancellationToken ct)
    {
        _ftmsControl ??= await _peripheral.GetCharacteristicAsync([Ftms.Service], Ftms.ControlPointChar, ct);
        if (_ftmsControl is null) return false;
        if (!_ftmsSubscribed)
        {
            await _ftmsControl.SubscribeAsync(OnFtmsControlResponse, ct);
            var status = await _peripheral.GetCharacteristicAsync([Ftms.Service], Ftms.MachineStatusChar, ct);
            if (status is not null) await status.SubscribeAsync(OnFtmsStatus, ct);
            _ftmsSubscribed = true;
        }
        var result = await FtmsCommandAsync(Ftms.RequestControl(), ct);
        if (result != 0x01)
        {
            Emit($"FTMS request control failed (result {result})");
            return false;
        }
        await FtmsCommandAsync(Ftms.StartResume(), ct);
        _ftmsControlLost = false;
        return true;
    }

    private bool _ftmsSubscribed;

    /// <summary>Writes an FTMS control point command and waits briefly for its result code (0 = no reply).</summary>
    private async Task<byte> FtmsCommandAsync(byte[] command, CancellationToken ct)
    {
        _ftmsReply = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _ftmsControl!.WriteAsync(command, withResponse: true, ct);
        var done = await Task.WhenAny(_ftmsReply.Task, Task.Delay(_options.HandshakeTimeout, ct));
        return done == _ftmsReply.Task ? _ftmsReply.Task.Result : (byte)0;
    }

    private async Task RunLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token) && IsConnected)
            {
                try
                {
                    await TickAsync(DateTimeOffset.UtcNow, _cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Emit($"Write failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Sends whatever the desired target needs, respecting per-command rate limits.</summary>
    internal async Task TickAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        TrainerTarget? desired;
        lock (_lock) desired = _desired;
        if (desired is null || !IsConnected) return;

        if (ControlPlane == ControlPlane.Ftms && _ftmsControlLost && !await TryFtmsControlAsync(ct)) return;

        switch (desired)
        {
            case ErgTarget erg:
                await TickErgAsync(erg, now, ct);
                break;
            case SimTarget sim:
                await TickSimAsync(sim, now, ct);
                break;
        }

        if (_readbackDue is { } due && now >= due && _zwiftRx is not null)
        {
            _readbackDue = null;
            await _zwiftRx.WritePreferResponseAsync(ZwiftTrainerProtocol.RequestGearRatio, ct);
        }
    }

    private async Task TickErgAsync(ErgTarget erg, DateTimeOffset now, CancellationToken ct)
    {
        var wasErg = _sent is ErgTarget;
        var changed = _sent is not ErgTarget prev || prev.Watts != erg.Watts;
        var due = changed ? !wasErg || now - _lastErgSent >= ErgInterval : now - _lastAnySent >= KeepAliveInterval;
        if (!due) return;

        if (ControlPlane == ControlPlane.Zwift && !_options.ErgViaFtms)
            await _zwiftRx!.WritePreferResponseAsync(ZwiftTrainerProtocol.SetPower(erg.Watts), ct);
        else if (changed && _ftmsControl is not null) // FTMS needs no keep-alive
            await _ftmsControl.WriteAsync(Ftms.SetTargetPower(erg.Watts), withResponse: true, ct);
        else
            return;

        _sent = erg;
        _lastErgSent = _lastAnySent = now;
        _needFullSim = true; // leaving ERG later requires the full SIM + physical state again
    }

    private async Task TickSimAsync(SimTarget sim, DateTimeOffset now, CancellationToken ct)
    {
        var prev = _sent as SimTarget;
        var gearChanged = prev is null || prev.Gear != sim.Gear;
        var gradeChanged = prev is null || Math.Abs(prev.GradePct - sim.GradePct) >= 0.05;
        var ratio = GearTable.Ratio(sim.Gear);

        if (ControlPlane == ControlPlane.Zwift)
        {
            var rx = _zwiftRx!;
            if (_needFullSim || prev is null)
            {
                await rx.WritePreferResponseAsync(ZwiftTrainerProtocol.SetSimulation(sim.GradePct), ct);
                await rx.WritePreferResponseAsync(ZwiftTrainerProtocol.SetPhysical(ratio, _options.BikeKg, _options.RiderKg), ct);
                _needFullSim = false;
                _lastGradeSent = _lastGearSent = _lastAnySent = now;
                _readbackDue = now + GearReadbackDelay;
                _sent = sim;
                return;
            }
            var sentSomething = false;
            if (gearChanged && now - _lastGearSent >= GearInterval)
            {
                await rx.WritePreferResponseAsync(ZwiftTrainerProtocol.SetPhysical(ratio), ct);
                _lastGearSent = now;
                _readbackDue = now + GearReadbackDelay;
                prev = prev with { Gear = sim.Gear };
                sentSomething = true;
            }
            if ((gradeChanged && now - _lastGradeSent >= GradeInterval) || now - _lastAnySent >= KeepAliveInterval)
            {
                await rx.WritePreferResponseAsync(ZwiftTrainerProtocol.SetIncline(sim.GradePct), ct);
                _lastGradeSent = now;
                prev = prev with { GradePct = sim.GradePct };
                sentSomething = true;
            }
            if (sentSomething)
            {
                _lastAnySent = now;
                _sent = prev;
            }
            return;
        }

        // FTMS: one SIM write carries grade and the gear emulation, so send when either changed.
        var due = prev is null || (gearChanged && now - _lastGearSent >= GearInterval) || (gradeChanged && now - _lastGradeSent >= GradeInterval);
        if (!due || _ftmsControl is null) return;
        var r = ratio / _options.PhysicalGearRatio;
        var (grade, crr, cw) = Ftms.ScaleForVirtualGear(sim.GradePct, ZwiftTrainerProtocol.DefaultCrr, ZwiftTrainerProtocol.DefaultCwa, r);
        await _ftmsControl.WriteAsync(Ftms.SetSimulation(grade, crr, cw), withResponse: true, ct);
        _lastGradeSent = _lastGearSent = _lastAnySent = now;
        _sent = sim;
    }

    private void OnZwiftMessage(byte[] data)
    {
        if (data.Length == 0) return;
        if (ZwiftTrainerProtocol.IsRideOnReply(data))
        {
            _rideOnReply?.TrySetResult(true);
        }
        else if (ZwiftTrainerProtocol.TryParseRidingData(data, out var rd))
        {
            DataReceived?.Invoke(this, new TrainerData(rd.PowerW, rd.CadenceRpm, rd.HeartRate > 0 ? rd.HeartRate : null, DateTimeOffset.UtcNow));
        }
        else if (ZwiftTrainerProtocol.TryParseGearRatio(data, out var ratio))
        {
            ReportedGearRatio = ratio;
        }
        else if (data[0] == ZwiftTrainerProtocol.TypeLostControl)
        {
            Emit("Trainer reports another app took control");
            _needFullSim = true;
        }
    }

    private void OnIndoorBikeData(byte[] data)
    {
        if (Ftms.TryParseIndoorBikeData(data, out var d))
            DataReceived?.Invoke(this, new TrainerData(d.PowerW, d.CadenceRpm, d.HeartRate > 0 ? d.HeartRate : null, DateTimeOffset.UtcNow));
    }

    private void OnFtmsControlResponse(byte[] data)
    {
        if (Ftms.TryParseResponse(data, out var op, out var result))
        {
            _ftmsReply?.TrySetResult(result);
            if (result != 0x01) Emit($"FTMS op 0x{op:X2} failed (result {result})");
        }
    }

    private void OnFtmsStatus(byte[] data)
    {
        if (data.Length > 0 && data[0] == Ftms.StatusControlLost)
        {
            Emit("FTMS control lost; re-requesting");
            _ftmsControlLost = true;
            _sent = null;
        }
    }

    private void OnPeripheralDisconnected(object? sender, EventArgs e)
    {
        if (_disconnected) return;
        _disconnected = true;
        _cts.Cancel();
        Emit("Trainer disconnected");
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void Emit(string message) => Log?.Invoke(this, message);

    public async ValueTask DisposeAsync()
    {
        _peripheral.Disconnected -= OnPeripheralDisconnected;
        _disconnected = true;
        _cts.Cancel();
        await _peripheral.DisposeAsync();
        _cts.Dispose();
    }
}
