using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using MyWahoo.Core.Ble;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace MyWahoo.Windows;

/// <summary>Bluetooth LE through WinRT. No pairing needed for the KICKR or the Zwift Click.</summary>
internal sealed class WinBleAdapter : IBleAdapter
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };

    // Windows reports advertisements and scan responses separately; merge what each address has told us.
    private readonly ConcurrentDictionary<ulong, (string? Name, Dictionary<ushort, byte[]> Mfr, List<Guid> Services)> _seen = new();

    public WinBleAdapter()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) => BleTrace.Log($"scan stopped: {e.Error}");
    }

    public event EventHandler<BleAdvertisement>? AdvertisementReceived;

    public bool IsScanning => _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public void StartScan()
    {
        if (!IsScanning) _watcher.Start();
    }

    public void StopScan()
    {
        if (IsScanning) _watcher.Stop();
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var adv = args.Advertisement;
        var known = _seen.GetOrAdd(args.BluetoothAddress, _ => (null, new Dictionary<ushort, byte[]>(), new List<Guid>()));
        BleAdvertisement merged;
        lock (known.Mfr)
        {
            var name = string.IsNullOrEmpty(adv.LocalName) ? known.Name : adv.LocalName;
            var learnedName = name is not null && known.Name is null;
            foreach (var m in adv.ManufacturerData) known.Mfr[m.CompanyId] = m.Data.ToArray();
            foreach (var s in adv.ServiceUuids.Where(s => !known.Services.Contains(s))) known.Services.Add(s);
            _seen[args.BluetoothAddress] = (name, known.Mfr, known.Services);
            merged = new BleAdvertisement(args.BluetoothAddress, name, args.RawSignalStrengthInDBm,
                known.Services.ToList(), new Dictionary<ushort, byte[]>(known.Mfr), args.Timestamp);
            // One line per device (when its name is first known): answers "why isn't my watch found?".
            if (learnedName)
                BleTrace.Log($"{args.BluetoothAddress:X12} seen: \"{name}\" rssi {args.RawSignalStrengthInDBm} services [{string.Join(",", known.Services)}] mfr [{string.Join(",", known.Mfr.Keys.Select(k => $"0x{k:X4}"))}]");
        }
        AdvertisementReceived?.Invoke(this, merged);
    }

    public async Task<IBlePeripheral> ConnectAsync(ulong address, CancellationToken ct = default)
    {
        BleTrace.Log($"{address:X12} connecting");
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct)
                     ?? throw new BleException($"Device {address:X12} not found");
        try
        {
            var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(ct);
            session.MaintainConnection = true;
            var peripheral = new WinPeripheral(device, session);
            await peripheral.LoadServicesAsync(ct);
            BleTrace.Log($"{address:X12} connected: {device.Name}");
            return peripheral;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }
}

internal sealed class WinPeripheral : IBlePeripheral
{
    private readonly BluetoothLEDevice _device;
    private readonly GattSession _session;
    private readonly List<WinCharacteristic> _subscribed = [];
    private IReadOnlyList<GattDeviceService> _services = [];
    private bool _disconnected;

    public WinPeripheral(BluetoothLEDevice device, GattSession session)
    {
        _device = device;
        _session = session;
        _device.ConnectionStatusChanged += (_, _) =>
        {
            if (_device.ConnectionStatus == BluetoothConnectionStatus.Disconnected) RaiseDisconnected("link lost");
        };
        _session.SessionStatusChanged += (_, e) =>
        {
            if (e.Status == GattSessionStatus.Closed) RaiseDisconnected($"session closed ({e.Error})");
        };
    }

    public ulong Address => _device.BluetoothAddress;
    public string? Name => _device.Name;
    public bool IsConnected => !_disconnected;
    public event EventHandler? Disconnected;

    /// <summary>WinRT often returns a partial service list right after connecting: read uncached, with retries.</summary>
    public async Task LoadServicesAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
            if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
            {
                foreach (var old in _services) old.Dispose();
                _services = result.Services;
                BleTrace.Log($"{Address:X12} services: {string.Join(", ", _services.Select(s => s.Uuid))}");
                return;
            }
            if (attempt >= 3) throw new BleException($"Couldn't read services ({result.Status})");
            await Task.Delay(750, ct);
        }
    }

    public async Task<IGattCharacteristic?> GetCharacteristicAsync(IReadOnlyList<Guid> services, Guid characteristic, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            foreach (var uuid in services)
            {
                var service = _services.FirstOrDefault(s => s.Uuid == uuid);
                if (service is null) continue;
                var result = await service.GetCharacteristicsForUuidAsync(characteristic, BluetoothCacheMode.Uncached).AsTask(ct);
                if (result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0)
                    return new WinCharacteristic(this, result.Characteristics[0]);
            }
            // Only retry when one of the wanted services is missing (possibly a partial list).
            if (services.All(uuid => _services.Any(s => s.Uuid == uuid))) return null;
            await Task.Delay(750, ct);
            await LoadServicesAsync(ct);
        }
        return null;
    }

    internal void Track(WinCharacteristic c)
    {
        lock (_subscribed) _subscribed.Add(c);
    }

    private void RaiseDisconnected(string why)
    {
        if (_disconnected) return;
        _disconnected = true;
        BleTrace.Log($"{Address:X12} disconnected: {why}");
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        _disconnected = true;
        lock (_subscribed)
        {
            foreach (var c in _subscribed) c.Unsubscribe();
            _subscribed.Clear();
        }
        foreach (var s in _services) s.Dispose();
        _session.MaintainConnection = false;
        _session.Dispose();
        _device.Dispose();
        BleTrace.Log($"{Address:X12} closed");
        return ValueTask.CompletedTask;
    }
}

internal sealed class WinCharacteristic(WinPeripheral owner, GattCharacteristic characteristic) : IGattCharacteristic
{
    private Action<byte[]>? _handler;

    public Guid Uuid => characteristic.Uuid;

    public GattProperties Properties
    {
        get
        {
            var p = characteristic.CharacteristicProperties;
            var result = GattProperties.None;
            if (p.HasFlag(GattCharacteristicProperties.Read)) result |= GattProperties.Read;
            if (p.HasFlag(GattCharacteristicProperties.Write)) result |= GattProperties.Write;
            if (p.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)) result |= GattProperties.WriteWithoutResponse;
            if (p.HasFlag(GattCharacteristicProperties.Notify)) result |= GattProperties.Notify;
            if (p.HasFlag(GattCharacteristicProperties.Indicate)) result |= GattProperties.Indicate;
            return result;
        }
    }

    public async Task WriteAsync(byte[] data, bool withResponse, CancellationToken ct = default)
    {
        BleTrace.Data(owner.Address, withResponse ? ">>" : "> ", Uuid, data);
        var option = withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        var result = await characteristic.WriteValueWithResultAsync(data.AsBuffer(), option).AsTask(ct);
        if (result.Status != GattCommunicationStatus.Success)
        {
            BleTrace.Log($"{owner.Address:X12} write failed: {result.Status} {result.ProtocolError}");
            throw new BleException($"Write failed ({result.Status})");
        }
    }

    public async Task SubscribeAsync(Action<byte[]> onValue, CancellationToken ct = default)
    {
        _handler = onValue;
        characteristic.ValueChanged += OnValueChanged;
        var props = characteristic.CharacteristicProperties;
        var cccd = props.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccd).AsTask(ct);
        if (status != GattCommunicationStatus.Success)
        {
            characteristic.ValueChanged -= OnValueChanged;
            throw new BleException($"Subscribe to {Uuid} failed ({status})");
        }
        owner.Track(this);
    }

    public async Task<byte[]> ReadAsync(CancellationToken ct = default)
    {
        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct);
        if (result.Status != GattCommunicationStatus.Success) throw new BleException($"Read failed ({result.Status})");
        return result.Value.ToArray();
    }

    internal void Unsubscribe() => characteristic.ValueChanged -= OnValueChanged;

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = args.CharacteristicValue.ToArray();
        BleTrace.Data(owner.Address, "< ", Uuid, data);
        try
        {
            _handler?.Invoke(data);
        }
        catch (Exception ex)
        {
            BleTrace.Log($"handler error: {ex}");
        }
    }
}
