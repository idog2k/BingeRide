using BingeRide.Core.Ble;
using BingeRide.Core.Protocol;

namespace BingeRide.Core.Devices;

public sealed record ClickOptions
{
    /// <summary>A lone Click v2 right puck sleeps ~65 s after its last frame; reboot it after 55 s of silence.</summary>
    public bool ResetIdleRightPuck { get; init; } = true;

    /// <summary>Reboot a locked v2 left puck to get ~60 s more of "−" presses (2–3 s gap each time).</summary>
    public bool RebootLockedLeftPuck { get; init; }

    internal bool ManualTick { get; init; }
}

/// <summary>
/// One Zwift Click puck (v1, or either v2 puck), read directly in plain-text mode.
/// Raises <see cref="ButtonsPressed"/> once per press (edge-triggered).
/// </summary>
public sealed class ZwiftClickDevice : IAsyncDisposable
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleResetAfter = TimeSpan.FromSeconds(55);

    private readonly IBlePeripheral _peripheral;
    private readonly IGattCharacteristic _rx;
    private readonly ClickOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly ClickButtons _ownButtons;

    private ClickButtons _held;
    private DateTimeOffset _lastFrame = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastKeepAlive = DateTimeOffset.UtcNow;
    private bool _challengePending;
    private bool _resetPending;
    private bool _disconnected;

    private ZwiftClickDevice(IBlePeripheral peripheral, IGattCharacteristic rx, ZwiftControllerKind kind, ClickOptions options)
    {
        _peripheral = peripheral;
        _rx = rx;
        Kind = kind;
        _options = options;
        _ownButtons = kind switch
        {
            ZwiftControllerKind.ClickV2Left => ZwiftClickProtocol.LeftPuckButtons,
            ZwiftControllerKind.ClickV2Right => ZwiftClickProtocol.RightPuckButtons,
            _ => (ClickButtons)uint.MaxValue,
        };
        _peripheral.Disconnected += OnPeripheralDisconnected;
    }

    public ulong Address => _peripheral.Address;
    public ZwiftControllerKind Kind { get; }
    public bool IsConnected => !_disconnected && _peripheral.IsConnected;
    public bool IsLocked { get; private set; }

    public string DisplayName => Kind switch
    {
        ZwiftControllerKind.ClickV1 => "Zwift Click",
        ZwiftControllerKind.ClickV2Right => "Click v2 (+)",
        ZwiftControllerKind.ClickV2Left => "Click v2 (−)",
        _ => _peripheral.Name ?? "Zwift Click",
    };

    /// <summary>Newly pressed buttons (one event per press, never for releases).</summary>
    public event EventHandler<ClickButtons>? ButtonsPressed;
    public event EventHandler<bool>? LockChanged;
    public event EventHandler<string>? Log;
    public event EventHandler? Disconnected;

    public static async Task<ZwiftClickDevice> ConnectAsync(IBleAdapter adapter, ulong address, ZwiftControllerKind kind, ClickOptions options, CancellationToken ct = default)
    {
        var peripheral = await adapter.ConnectAsync(address, ct);
        try
        {
            // v1 uses the legacy 128-bit service; v2 moved to FC82 (same characteristics).
            var onFc82 = await peripheral.GetCharacteristicAsync([ZwiftUuids.ServiceFc82], ZwiftUuids.SyncRx, ct) is not null;
            if (kind == ZwiftControllerKind.Unknown && !onFc82) kind = ZwiftControllerKind.ClickV1;

            var async = await peripheral.GetCharacteristicAsync(ZwiftUuids.Services, ZwiftUuids.Async, ct);
            var rx = await peripheral.GetCharacteristicAsync(ZwiftUuids.Services, ZwiftUuids.SyncRx, ct);
            var tx = await peripheral.GetCharacteristicAsync(ZwiftUuids.Services, ZwiftUuids.SyncTx, ct);
            if (async is null || rx is null || tx is null)
                throw new BleException("Zwift Click service not found");

            var device = new ZwiftClickDevice(peripheral, rx, kind, options);
            await async.SubscribeAsync(device.OnMessage, ct);
            await tx.SubscribeAsync(device.OnMessage, ct);
            await device.HandshakeAsync(onFc82, ct);
            if (!options.ManualTick) _ = device.RunLoopAsync();
            return device;
        }
        catch
        {
            await peripheral.DisposeAsync();
            throw;
        }
    }

    private bool IsV2 => Kind is ZwiftControllerKind.ClickV2Left or ZwiftControllerKind.ClickV2Right;

    private byte[] RideOn => IsV2 ? ZwiftClickProtocol.RideOnV2 : ZwiftClickProtocol.RideOnV1;

    private async Task HandshakeAsync(bool onFc82, CancellationToken ct)
    {
        await _rx.WritePreferNoResponseAsync(RideOn, ct);
        if (!IsV2 && !onFc82) return;
        // Sequence verified on Click v2 hardware (zwiftboard): RideOn 02 03, then two GETs 100 ms apart.
        await Task.Delay(100, ct);
        await _rx.WritePreferNoResponseAsync(ZwiftClickProtocol.GetDeviceInfo, ct);
        await Task.Delay(100, ct);
        await _rx.WritePreferNoResponseAsync(ZwiftClickProtocol.GetConfigPage, ct);
    }

    private async Task RunLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
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
                    // A failed write is the most reliable sign the puck has gone to sleep.
                    Log?.Invoke(this, $"{DisplayName}: write failed ({ex.Message})");
                    OnPeripheralDisconnected(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task TickAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (_resetPending) return;

        if (_challengePending)
        {
            _challengePending = false;
            await _rx.WritePreferNoResponseAsync(ZwiftClickProtocol.ChallengeReply, ct);
        }

        var idleRightPuck = Kind == ZwiftControllerKind.ClickV2Right && _options.ResetIdleRightPuck && now - _lastFrame >= IdleResetAfter;
        var lockedLeftPuck = Kind == ZwiftControllerKind.ClickV2Left && _options.RebootLockedLeftPuck && IsLocked;
        if (idleRightPuck || lockedLeftPuck)
        {
            // Opcode RESET: the puck reboots and re-advertises within ~3 s; the device manager reconnects it.
            _resetPending = true;
            Log?.Invoke(this, $"{DisplayName}: rebooting ({(idleRightPuck ? "idle" : "locked")})");
            await _rx.WritePreferNoResponseAsync(ZwiftClickProtocol.Reset, ct);
            return;
        }

        if (now - _lastKeepAlive >= KeepAliveInterval)
        {
            _lastKeepAlive = now;
            await _rx.WritePreferNoResponseAsync(RideOn, ct);
        }
    }

    internal void OnMessage(byte[] data)
    {
        var message = ZwiftClickProtocol.Parse(data);
        switch (message.Kind)
        {
            case ClickMessageKind.Buttons:
            {
                _lastFrame = DateTimeOffset.UtcNow;
                var pressed = message.Pressed & _ownButtons;
                var fresh = pressed & ~_held;
                _held = pressed;
                if (fresh != ClickButtons.None) ButtonsPressed?.Invoke(this, fresh);
                break;
            }
            case ClickMessageKind.Challenge:
                _challengePending = true;
                break;
            case ClickMessageKind.LockStatus when message.Locked != IsLocked:
                IsLocked = message.Locked;
                Log?.Invoke(this, IsLocked
                    ? $"{DisplayName} is locked: open the Zwift app once to unlock it for ~24 h, or use B on the right puck as gear-down"
                    : $"{DisplayName} unlocked");
                LockChanged?.Invoke(this, IsLocked);
                break;
            // Replies to our keep-alives don't count as activity: the right puck sleeps regardless of them.
        }
    }

    private void OnPeripheralDisconnected(object? sender, EventArgs e)
    {
        if (_disconnected) return;
        _disconnected = true;
        _cts.Cancel();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _peripheral.Disconnected -= OnPeripheralDisconnected;
        _disconnected = true;
        _cts.Cancel();
        await _peripheral.DisposeAsync();
        _cts.Dispose();
    }
}
