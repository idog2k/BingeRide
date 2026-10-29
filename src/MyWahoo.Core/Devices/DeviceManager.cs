using System.Collections.Concurrent;
using MyWahoo.Core.Ble;
using MyWahoo.Core.Protocol;

namespace MyWahoo.Core.Devices;

public enum DeviceRole { Trainer, Click, HeartRate }

public sealed record DiscoveredDevice(ulong Address, string Name, DeviceRole Role, short Rssi, DateTimeOffset LastSeen, ZwiftControllerKind ClickKind)
{
    /// <summary>Clicks connect on their own; trainers and HR sensors can be picked manually.</summary>
    public bool CanConnect => Role != DeviceRole.Click;
}

/// <summary>
/// Scans continuously, keeps one trainer, one heart-rate sensor and any number of Zwift Click pucks connected, and reconnects them
/// when they advertise again (never by blindly reconnecting: that wedges the Windows BLE stack).
/// Connections are serialised. Events are raised on background threads.
/// </summary>
public sealed class DeviceManager : IAsyncDisposable
{
    private readonly IBleAdapter _adapter;
    private readonly Func<TrainerOptions> _trainerOptions;
    private readonly Func<ClickOptions> _clickOptions;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, DiscoveredDevice> _discovered = new();
    private readonly ConcurrentDictionary<ulong, ZwiftClickDevice> _clicks = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _cooldownUntil = new();
    private readonly ConcurrentDictionary<ulong, int> _failures = new();
    private bool _disposed;

    public DeviceManager(IBleAdapter adapter, Func<TrainerOptions> trainerOptions, Func<ClickOptions> clickOptions)
    {
        _adapter = adapter;
        _trainerOptions = trainerOptions;
        _clickOptions = clickOptions;
        _adapter.AdvertisementReceived += OnAdvertisement;
    }

    /// <summary>Trainer to auto-connect to; null = first KICKR seen.</summary>
    public ulong? PreferredTrainer { get; set; }

    public bool AutoConnectTrainer { get; set; } = true;
    public bool ClicksEnabled { get; set; } = true;

    /// <summary>Heart-rate sensor to auto-connect to; null = first one seen.</summary>
    public ulong? PreferredHeartRate { get; set; }
    public bool HeartRateEnabled { get; set; } = true;

    public ITrainer? Trainer { get; private set; }
    public IReadOnlyCollection<ZwiftClickDevice> Clicks => _clicks.Values.ToList();
    public IReadOnlyCollection<DiscoveredDevice> Discovered => _discovered.Values.OrderBy(d => d.Role).ThenByDescending(d => d.Rssi).ToList();
    public string? TrainerStatus { get; private set; } = "Searching for trainer…";
    public HeartRateMonitor? HeartRate { get; private set; }

    public event EventHandler? TrainerChanged;
    public event EventHandler? ClicksChanged;
    public event EventHandler? DiscoveredChanged;
    public event EventHandler<ClickButtons>? ClickPressed;
    public event EventHandler? HeartRateChanged;

    /// <summary>Beats per minute from the heart-rate sensor (Bluetooth thread).</summary>
    public event EventHandler<int>? HeartRateReceived;
    public event EventHandler<string>? Log;

    public void Start() => _adapter.StartScan();

    /// <summary>Connects to a specific trainer now (e.g. picked from the list) and remembers it.</summary>
    public async Task ConnectTrainerAsync(ulong address, CancellationToken ct = default)
    {
        PreferredTrainer = address;
        if (Trainer is { } old && old.Address != address)
        {
            Trainer = null;
            await old.DisposeAsync();
        }
        await ConnectTrainerCoreAsync(address, ct);
    }

    public async Task DisconnectTrainerAsync()
    {
        if (Trainer is { } t)
        {
            Trainer = null;
            AutoConnectTrainer = false;
            TrainerStatus = "Disconnected";
            await t.DisposeAsync();
            TrainerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Switches to a specific heart-rate sensor (e.g. picked from the list) and remembers it.</summary>
    public async Task ConnectHeartRateAsync(ulong address, CancellationToken ct = default)
    {
        PreferredHeartRate = address;
        HeartRateEnabled = true;
        if (HeartRate is { } old && old.Address != address)
        {
            HeartRate = null;
            await old.DisposeAsync();
        }
        _cooldownUntil.TryRemove(address, out _);
        await ConnectHeartRateCoreAsync(address, ct);
    }

    private void OnAdvertisement(object? sender, BleAdvertisement adv)
    {
        if (_disposed) return;
        var role = Classify(adv, out var clickKind);
        if (role is null) return;

        var name = adv.LocalName is { Length: > 0 } n ? n : role switch
        {
            DeviceRole.Click => "Zwift Click",
            DeviceRole.HeartRate => "Heart rate sensor",
            _ => "Trainer",
        };
        var isNew = !_discovered.ContainsKey(adv.Address);
        _discovered[adv.Address] = new DiscoveredDevice(adv.Address, name, role.Value, adv.Rssi, adv.Timestamp, clickKind);
        if (isNew) DiscoveredChanged?.Invoke(this, EventArgs.Empty);

        if (_cooldownUntil.TryGetValue(adv.Address, out var until) && adv.Timestamp < until) return;

        if (role == DeviceRole.Trainer && Trainer is null && AutoConnectTrainer &&
            (PreferredTrainer is null ? name.StartsWith("KICKR", StringComparison.OrdinalIgnoreCase) : PreferredTrainer == adv.Address))
        {
            _ = ConnectTrainerCoreAsync(adv.Address, CancellationToken.None);
        }
        else if (role == DeviceRole.Click && ClicksEnabled && !_clicks.ContainsKey(adv.Address))
        {
            _ = ConnectClickAsync(adv.Address, clickKind);
        }
        else if (role == DeviceRole.HeartRate && HeartRate is null && HeartRateEnabled &&
                 (PreferredHeartRate is null || PreferredHeartRate == adv.Address))
        {
            _ = ConnectHeartRateCoreAsync(adv.Address, CancellationToken.None);
        }
    }

    internal static DeviceRole? Classify(BleAdvertisement adv, out ZwiftControllerKind clickKind)
    {
        clickKind = ZwiftControllerKind.Unknown;
        if (ZwiftClickProtocol.Identify(adv.ManufacturerData, adv.LocalName) is { } kind)
        {
            clickKind = kind;
            return DeviceRole.Click;
        }
        var name = adv.LocalName ?? "";
        if (name.StartsWith("KICKR", StringComparison.OrdinalIgnoreCase) || adv.ServiceUuids.Contains(Ftms.Service))
            return DeviceRole.Trainer;
        // Straps and watches in "broadcast heart rate" mode advertise the standard HR service.
        if (adv.ServiceUuids.Contains(HeartRateService.Service))
            return DeviceRole.HeartRate;
        return null;
    }

    private async Task ConnectTrainerCoreAsync(ulong address, CancellationToken ct)
    {
        if (!await _connectGate.WaitAsync(0, ct)) return; // another connect in progress; the next advert retries
        try
        {
            if (Trainer is not null || _disposed) return;
            TrainerStatus = "Connecting…";
            TrainerChanged?.Invoke(this, EventArgs.Empty);
            var trainer = await KickrTrainer.ConnectAsync(_adapter, address, _trainerOptions(), ct);
            trainer.Log += (_, m) => Log?.Invoke(this, m);
            trainer.Disconnected += (_, _) =>
            {
                if (Trainer != trainer) return;
                Trainer = null;
                TrainerStatus = "Reconnecting…";
                Cooldown(address, TimeSpan.FromSeconds(2));
                TrainerChanged?.Invoke(this, EventArgs.Empty);
            };
            Trainer = trainer;
            PreferredTrainer = address;
            _failures.TryRemove(address, out _);
            TrainerStatus = trainer.ControlPlane == ControlPlane.Zwift ? "Connected · virtual shifting" : "Connected · FTMS";
        }
        catch (Exception ex)
        {
            TrainerStatus = $"Connect failed: {ex.Message}";
            Log?.Invoke(this, TrainerStatus);
            Backoff(address);
        }
        finally
        {
            _connectGate.Release();
            TrainerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ConnectClickAsync(ulong address, ZwiftControllerKind kind)
    {
        if (!await _connectGate.WaitAsync(0)) return;
        try
        {
            if (_clicks.ContainsKey(address) || _disposed) return;
            var click = await ZwiftClickDevice.ConnectAsync(_adapter, address, kind, _clickOptions());
            click.ButtonsPressed += (_, b) => ClickPressed?.Invoke(this, b);
            click.Log += (_, m) => Log?.Invoke(this, m);
            click.LockChanged += (_, _) => ClicksChanged?.Invoke(this, EventArgs.Empty);
            click.Disconnected += async (_, _) =>
            {
                if (_clicks.TryRemove(address, out _))
                {
                    Cooldown(address, TimeSpan.FromSeconds(1));
                    ClicksChanged?.Invoke(this, EventArgs.Empty);
                    await click.DisposeAsync();
                }
            };
            _clicks[address] = click;
            _failures.TryRemove(address, out _);
            Log?.Invoke(this, $"{click.DisplayName} connected");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"Zwift Click connect failed: {ex.Message}");
            Backoff(address);
        }
        finally
        {
            _connectGate.Release();
            ClicksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ConnectHeartRateCoreAsync(ulong address, CancellationToken ct)
    {
        if (!await _connectGate.WaitAsync(0, ct)) return;
        try
        {
            if (HeartRate is not null || _disposed) return;
            var monitor = await HeartRateMonitor.ConnectAsync(_adapter, address, ct);
            monitor.HeartRateReceived += (_, bpm) => HeartRateReceived?.Invoke(this, bpm);
            monitor.Disconnected += async (_, _) =>
            {
                if (HeartRate != monitor) return;
                HeartRate = null;
                Cooldown(address, TimeSpan.FromSeconds(2));
                Log?.Invoke(this, $"{monitor.Name} disconnected");
                HeartRateChanged?.Invoke(this, EventArgs.Empty);
                await monitor.DisposeAsync();
            };
            HeartRate = monitor;
            PreferredHeartRate = address;
            _failures.TryRemove(address, out _);
            Log?.Invoke(this, $"Heart rate: {monitor.Name} connected");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"Heart rate connect failed: {ex.Message}");
            Backoff(address);
        }
        finally
        {
            _connectGate.Release();
            HeartRateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Waits longer after each failed connect (5 s, 10 s, 20 s … 60 s) so a stubborn device isn't hammered.</summary>
    private void Backoff(ulong address)
    {
        var n = _failures.AddOrUpdate(address, 1, (_, v) => v + 1);
        Cooldown(address, TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, n - 1))));
    }

    /// <summary>Only reconnect on an advertisement received after the cooldown, i.e. a fresh one.</summary>
    private void Cooldown(ulong address, TimeSpan duration) => _cooldownUntil[address] = DateTimeOffset.UtcNow + duration;

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _adapter.AdvertisementReceived -= OnAdvertisement;
        _adapter.StopScan();
        foreach (var c in _clicks.Values) await c.DisposeAsync();
        _clicks.Clear();
        if (Trainer is { } t) await t.DisposeAsync();
        Trainer = null;
        if (HeartRate is { } hr) await hr.DisposeAsync();
        HeartRate = null;
    }
}
