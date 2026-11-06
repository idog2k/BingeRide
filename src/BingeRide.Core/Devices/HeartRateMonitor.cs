using BingeRide.Core.Ble;
using BingeRide.Core.Protocol;

namespace BingeRide.Core.Devices;

/// <summary>A standard BLE heart-rate sensor: a strap, or a watch broadcasting HR.</summary>
public sealed class HeartRateMonitor : IAsyncDisposable
{
    private readonly IBlePeripheral _peripheral;
    private bool _disconnected;

    private HeartRateMonitor(IBlePeripheral peripheral)
    {
        _peripheral = peripheral;
        _peripheral.Disconnected += OnPeripheralDisconnected;
    }

    public ulong Address => _peripheral.Address;
    public string Name => _peripheral.Name is { Length: > 0 } n ? n : "Heart rate";
    public bool IsConnected => !_disconnected && _peripheral.IsConnected;
    public int? LastBpm { get; private set; }

    /// <summary>Raised on the Bluetooth thread for every valid reading.</summary>
    public event EventHandler<int>? HeartRateReceived;
    public event EventHandler? Disconnected;

    public static async Task<HeartRateMonitor> ConnectAsync(IBleAdapter adapter, ulong address, CancellationToken ct = default)
    {
        var peripheral = await adapter.ConnectAsync(address, ct);
        try
        {
            var measurement = await peripheral.GetCharacteristicAsync([HeartRateService.Service], HeartRateService.Measurement, ct)
                              ?? throw new BleException("Heart rate service not found (is the watch broadcasting?)");
            var monitor = new HeartRateMonitor(peripheral);
            await measurement.SubscribeAsync(monitor.OnMeasurement, ct);
            return monitor;
        }
        catch
        {
            await peripheral.DisposeAsync();
            throw;
        }
    }

    internal void OnMeasurement(byte[] data)
    {
        if (!HeartRateService.TryParse(data, out var bpm)) return;
        LastBpm = bpm;
        HeartRateReceived?.Invoke(this, bpm);
    }

    private void OnPeripheralDisconnected(object? sender, EventArgs e)
    {
        if (_disconnected) return;
        _disconnected = true;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _peripheral.Disconnected -= OnPeripheralDisconnected;
        _disconnected = true;
        await _peripheral.DisposeAsync();
    }
}
